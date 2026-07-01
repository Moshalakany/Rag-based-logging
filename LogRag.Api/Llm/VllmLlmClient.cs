using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Llm;

/// <summary>
/// vLLM LLM client using the OpenAI-compatible /v1/chat/completions streaming endpoint.
/// Implements the same ILlmClient interface as OllamaLlmClient.
/// </summary>
public sealed class VllmLlmClient : ILlmClient
{
    private readonly LlmOptions _options;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // PERF: Shared pooled HttpClient for connection reuse.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 4,
        EnableMultipleHttp2Connections = true,
    };

    public VllmLlmClient(IOptions<LlmOptions> options)
    {
        _options = options.Value;
        _httpClient = new HttpClient(SharedHandler)
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string question,
        string context,
        IReadOnlyList<SessionMessage> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var _ = LogRagTelemetry.MeasureLatency(LogRagTelemetry.LlmLatency);
        LogRagTelemetry.LlmRequests.Add(1);
        MetricsSnapshot.RecordLlmRequest();

        var messages = BuildMessages(question, context, history);

        var payload = new
        {
            model = _options.Model,
            messages,
            stream = true,
            temperature = 0.3,
            max_tokens = 2048,
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"vLLM chat request failed. HTTP {(int)response.StatusCode}: {errorBody}");
        }

        // Read all tokens first, then yield outside try-catch
        var tokens = new List<string>();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..].Trim();
            if (data == "[DONE]") break;

            try
            {
                using var document = JsonDocument.Parse(data);
                if (!document.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    continue;
                }

                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;
                if (!delta.TryGetProperty("content", out var content)) continue;

                var token = content.GetString();
                if (!string.IsNullOrEmpty(token))
                    tokens.Add(token);
            }
            catch (JsonException)
            {
                // Malformed SSE line — skip
            }
        }

        foreach (var token in tokens)
        {
            yield return token;
        }
    }

    private List<object> BuildMessages(
        string question, string context, IReadOnlyList<SessionMessage> history)
    {
        var messages = new List<object>();

        // System prompt
        messages.Add(new { role = "system", content = _options.SystemPrompt });

        // Conversation history (limited to MaxHistoryMessages)
        var historyMessages = history.TakeLast(Math.Max(0, _options.MaxHistoryMessages));
        foreach (var msg in historyMessages)
        {
            var role = msg.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant" : "user";
            messages.Add(new { role, content = msg.Content });
        }

        // Current prompt with context
        var prompt = BuildPromptWithContext(question, context);
        messages.Add(new { role = "user", content = prompt });

        return messages;
    }

    private static string BuildPromptWithContext(string question, string context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a helpful assistant for answering questions based on log data and your name is Momkn intelligent Logs inspector.");
        builder.AppendLine("Use the provided context only when it is relevant to the question.");
        builder.AppendLine("If the user asks a simple question about yourself (your name, greetings, capabilities), answer naturally without citing logs.");
        builder.AppendLine("Only cite log chunks when they are directly relevant to the question.");
        builder.AppendLine();
        builder.AppendLine("Log context:");
        builder.AppendLine(context);
        builder.AppendLine();
        builder.AppendLine("Question:");
        builder.AppendLine(question);
        builder.AppendLine();
        builder.AppendLine("Answer with concise natural language and cite timestamps from context when relevant.");
        builder.AppendLine("Ask clarifying questions if the context is insufficient and don't cite sources.");
        builder.AppendLine("Do not make up information that is not in the context.");
        return builder.ToString();
    }
}
