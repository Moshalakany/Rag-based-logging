using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Embedding;
using LogRag.Api.VectorStore;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Query;

public interface IRagQueryEngine
{
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, QueryFilter filter, int topK, CancellationToken cancellationToken);
}

public interface IContextBuilder
{
    string BuildContext(IReadOnlyList<RetrievedChunk> chunks);
}

public interface IResponseShaper
{
    string Shape(string answer, IReadOnlyList<RetrievedChunk> citations);
}

public sealed class RagQueryEngine : IRagQueryEngine
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly RetrievalOptions _options;

    public RagQueryEngine(IEmbeddingService embeddingService, IVectorStore vectorStore, IOptions<RetrievalOptions> options)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, QueryFilter filter, int topK, CancellationToken cancellationToken)
    {
        var queryEmbedding = (await _embeddingService.EmbedTextsAsync([question], cancellationToken))[0];
        var candidateCount = Math.Max(topK * 3, topK);
        var candidates = await _vectorStore.SearchAsync(queryEmbedding, filter, candidateCount, cancellationToken);
        var deduplicated = candidates
            .GroupBy(chunk => chunk.LogHash)
            .Select(group => group.OrderByDescending(x => x.Score).First())
            .ToArray();

        var reranked = _options.EnableHeuristicReranker
            ? deduplicated.OrderByDescending(chunk => (chunk.Score * 0.85) + (LexicalOverlap(question, chunk.Text) * 0.15)).ToArray()
            : deduplicated.OrderByDescending(chunk => chunk.Score).ToArray();

        return reranked.Take(Math.Max(1, topK)).ToArray();
    }

    private static double LexicalOverlap(string question, string chunkText)
    {
        var questionTerms = question
            .ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', ':', ';', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToArray();

        if (questionTerms.Length == 0)
        {
            return 0;
        }

        var text = chunkText.ToLowerInvariant();
        var hitCount = questionTerms.Count(term => text.Contains(term, StringComparison.Ordinal));
        return hitCount / (double)questionTerms.Length;
    }
}

public sealed class ContextBuilder : IContextBuilder
{
    public string BuildContext(IReadOnlyList<RetrievedChunk> chunks)
    {
        if (chunks.Count == 0)
        {
            return "No matching logs found.";
        }

        var lines = new List<string>(chunks.Count * 3);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            lines.Add($"[{i + 1}] ts={chunk.TimestampUtc:O} severity={chunk.Severity} service={chunk.ServiceName} trace={chunk.TraceId} source={chunk.SourceId}/{chunk.SourceType}");
            lines.Add(chunk.Text);
            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class MarkdownResponseShaper : IResponseShaper
{
    public string Shape(string answer, IReadOnlyList<RetrievedChunk> citations)
    {
        var citationLines = citations.Select((chunk, index) =>
            $"- [{index + 1}] `{chunk.TimestampUtc:O}` `{chunk.Severity}` `{chunk.ServiceName}` trace `{chunk.TraceId}`").ToArray();

        if (citationLines.Length == 0)
        {
            return answer.Trim();
        }

        return $"{answer.Trim()}{Environment.NewLine}{Environment.NewLine}### Cited log chunks{Environment.NewLine}{string.Join(Environment.NewLine, citationLines)}";
    }
}
