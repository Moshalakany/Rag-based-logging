using System.Text;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace LogRag.Api.Llm;

public interface ILlmClient
{
    IAsyncEnumerable<string> StreamAnswerAsync(string question, string context, IReadOnlyList<SessionMessage> history, CancellationToken cancellationToken);
}

public sealed class OllamaLlmClient : ILlmClient
{
    private readonly LlmOptions _options;
    private readonly OllamaApiClient _client;

    public OllamaLlmClient(IOptions<LlmOptions> options)
    {
        _options = options.Value;
        _client = new OllamaApiClient(new Uri(_options.BaseUrl), _options.Model);
    }

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string question,
        string context,
        IReadOnlyList<SessionMessage> history,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var historyBlock = BuildHistory(history, _options.MaxHistoryMessages);
        var prompt = BuildPrompt(question, context, historyBlock);

        var request = new GenerateRequest
        {
            Model = _options.Model,
            System = _options.SystemPrompt,
            Prompt = prompt,
            Stream = true,
        };

        await foreach (var chunk in _client.GenerateAsync(request, cancellationToken))
        {
            if (!string.IsNullOrEmpty(chunk?.Response))
            {
                yield return chunk.Response;
            }
        }
    }

    private static string BuildHistory(IReadOnlyList<SessionMessage> history, int maxMessages)
    {
        if (history.Count == 0 || maxMessages <= 0)
        {
            return string.Empty;
        }

        var selected = history.TakeLast(maxMessages);
        var builder = new StringBuilder();
        foreach (var message in selected)
        {
            builder.Append('[').Append(message.Role.ToUpperInvariant()).Append("] ").AppendLine(message.Content);
        }

        return builder.ToString().Trim();
    }

    private static string BuildPrompt(string question, string context, string history)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are a helpful assistant for answering questions based on log data and your name is Momkn intelligent Logs inspector.");
        builder.AppendLine("Use the provided context only.");
        if (!string.IsNullOrWhiteSpace(history))
        {
            builder.AppendLine("Conversation history:");
            builder.AppendLine(history);
            builder.AppendLine();
        }

        builder.AppendLine("Log context:");
        builder.AppendLine(context);
        builder.AppendLine();
        builder.AppendLine("Question:");
        builder.AppendLine(question);
        builder.AppendLine();
        builder.AppendLine("Answer with concise natural language and cite timestamps from context.");
        builder.AppendLine("Ask clarifying questions if the context is insufficient and don't cite sources.");
        builder.AppendLine("Do not make up information that is not in the context.");
        return builder.ToString();
    }
}
