using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using Microsoft.Extensions.Logging;

namespace LogRag.Api.Sources;

/// <summary>
/// Reads log entries from an HTTP API endpoint.
/// Supports GET and POST. Response can be a JSON array, newline-delimited JSON (NDJSON),
/// or a plain text body (treated as a single log entry).
/// </summary>
public sealed class HttpApiLogSource : ILogSource
{
    private readonly LogSourceDescriptorOptions _descriptor;
    private readonly ILogSourceCheckpointStore _checkpointStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpApiLogSource> _logger;
    private readonly string _checkpointKey;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public HttpApiLogSource(
        LogSourceDescriptorOptions descriptor,
        ILogSourceCheckpointStore checkpointStore,
        HttpClient httpClient,
        ILogger<HttpApiLogSource> logger)
    {
        _descriptor = descriptor;
        _checkpointStore = checkpointStore;
        _httpClient = httpClient;
        _logger = logger;
        _checkpointKey = $"http|{_descriptor.Id}";
    }

    public string Id => _descriptor.Id;
    public string SourceType => _descriptor.SourceType;

    public async IAsyncEnumerable<RawLogEntry> ReadAsync(IngestionTimeWindow? timeWindow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = timeWindow; // Filtered post-read by orchestrator for HTTP sources
        if (string.IsNullOrWhiteSpace(_descriptor.HttpUrl))
        {
            _logger.LogWarning("HTTP URL is not configured for source {Id}", _descriptor.Id);
            yield break;
        }

        var lastTimestamp = _checkpointStore.GetOffset(_checkpointKey);
        var newTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Collect results outside try-catch
        var collectedEntries = new List<RawLogEntry>();

        try
        {
            var method = string.IsNullOrWhiteSpace(_descriptor.HttpMethod)
                ? HttpMethod.Get
                : new HttpMethod(_descriptor.HttpMethod.ToUpperInvariant());

            using var request = new HttpRequestMessage(method, _descriptor.HttpUrl);

            // Add custom headers from config (JSON object)
            if (!string.IsNullOrWhiteSpace(_descriptor.HttpHeaders))
            {
                try
                {
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        _descriptor.HttpHeaders, _jsonOptions);
                    if (headers is not null)
                    {
                        foreach (var (key, value) in headers)
                        {
                            request.Headers.TryAddWithoutValidation(key, value);
                        }
                    }
                }
                catch (JsonException)
                {
                    _logger.LogWarning(
                        "Invalid HTTP headers JSON for source {Id}. Skipping custom headers.",
                        _descriptor.Id);
                }
            }

            // Add request body for POST/PUT
            if (!string.IsNullOrWhiteSpace(_descriptor.HttpBody) &&
                (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch))
            {
                request.Content = new StringContent(
                    _descriptor.HttpBody, Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "HTTP request failed for source {Id}. HTTP {Status}: {Reason}",
                    _descriptor.Id, (int)response.StatusCode, response.ReasonPhrase);
            }
            else
            {
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(body))
                {
                    // Try JSON array first
                    if (body.TrimStart().StartsWith('['))
                    {
                        collectedEntries.AddRange(ParseJsonArray(body));
                    }
                    // Try newline-delimited JSON
                    else if (contentType.Contains("ndjson") || contentType.Contains("x-ndjson") ||
                             (body.Contains("\n{") && body.TrimStart().StartsWith('{')))
                    {
                        collectedEntries.AddRange(ParseNdjson(body));
                    }
                    // Try single JSON object or plain text
                    else
                    {
                        collectedEntries.Add(CreateEntry(body));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "HTTP API request failed for source {Id}", _descriptor.Id);
        }

        _checkpointStore.SaveOffset(_checkpointKey, newTimestamp);

        // Yield collected entries
        foreach (var entry in collectedEntries)
        {
            yield return entry;
        }
    }

    private RawLogEntry CreateEntry(string text)
    {
        return new RawLogEntry(
            Id,
            SourceType,
            text,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["http_source"] = _descriptor.HttpUrl,
            });
    }

    private List<RawLogEntry> ParseJsonArray(string body)
    {
        var results = new List<RawLogEntry>();
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            results.Add(CreateEntry(body));
            return results;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var text = element.ValueKind switch
            {
                JsonValueKind.Object => element.GetRawText(),
                JsonValueKind.String => element.GetString() ?? "",
                _ => element.GetRawText(),
            };
            results.Add(CreateEntry(text));
        }

        return results;
    }

    private IEnumerable<RawLogEntry> ParseNdjson(string body)
    {
        var lines = body.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Each line should be a JSON object
            if (trimmed.StartsWith('{'))
            {
                yield return CreateEntry(trimmed);
            }
            else
            {
                // Non-JSON line — yield as-is
                yield return CreateEntry(trimmed);
            }
        }
    }
}
