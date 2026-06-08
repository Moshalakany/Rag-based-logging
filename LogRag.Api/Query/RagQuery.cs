using System.Linq;
using System.Text.RegularExpressions;
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
    private readonly VectorStoreOptions _vectorStoreOptions;

    public RagQueryEngine(
        IEmbeddingService embeddingService, 
        IVectorStore vectorStore, 
        IOptions<RetrievalOptions> options,
        IOptions<VectorStoreOptions> vectorStoreOptions)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _options = options.Value;
        _vectorStoreOptions = vectorStoreOptions.Value;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, QueryFilter filter, int topK, CancellationToken cancellationToken)
    {
        var extractedIds = QueryIdExtractor.Extract(question);
        var exactMatches = new List<RetrievedChunk>();

        if (extractedIds.Count > 0)
        {
            var idFilter = new QueryFilter
            {
                ServiceName = filter.ServiceName,
                Severity = filter.Severity,
                SourceType = filter.SourceType,
                FromUtc = filter.FromUtc,
                ToUtc = filter.ToUtc,
                LinkedIds = extractedIds.ToList()
            };

            var zeroVector = new float[_vectorStoreOptions.VectorSize];
            var matches = await _vectorStore.SearchAsync(zeroVector, idFilter, limit: 100, cancellationToken);
            exactMatches.AddRange(matches);
        }

        var queryEmbedding = (await _embeddingService.EmbedTextsAsync([question], cancellationToken))[0];
        var candidateCount = Math.Max(topK * 3, topK);
        var semanticCandidates = await _vectorStore.SearchAsync(queryEmbedding, filter, candidateCount, cancellationToken);

        // Deduplicate exact matches by LogHash and sort chronologically (ascending)
        var dedupedExact = exactMatches
            .GroupBy(chunk => chunk.LogHash)
            .Select(group => group.First())
            .OrderBy(chunk => chunk.TimestampUtc)
            .ToList();

        // Deduplicate semantic candidates by LogHash
        var dedupedSemantic = semanticCandidates
            .GroupBy(chunk => chunk.LogHash)
            .Select(group => group.OrderByDescending(x => x.Score).First())
            .ToList();

        var merged = new List<RetrievedChunk>(dedupedExact);
        var exactHashes = new HashSet<string>(dedupedExact.Select(x => x.LogHash));

        foreach (var sem in dedupedSemantic)
        {
            if (!exactHashes.Contains(sem.LogHash))
            {
                merged.Add(sem);
            }
        }

        var semanticOnly = merged.Skip(dedupedExact.Count).ToArray();
        var rerankedSemantic = _options.EnableHeuristicReranker
            ? semanticOnly.OrderByDescending(chunk => (chunk.Score * 0.85) + (LexicalOverlap(question, chunk.Text) * 0.15)).ToArray()
            : semanticOnly.OrderByDescending(chunk => chunk.Score).ToArray();

        var finalResult = new List<RetrievedChunk>(dedupedExact);
        finalResult.AddRange(rerankedSemantic);

        return finalResult.Take(Math.Max(topK, Math.Max(10, dedupedExact.Count))).ToArray();
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

public static class QueryIdExtractor
{
    private static readonly Regex GuidRegex = new(@"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b", RegexOptions.Compiled);
    private static readonly Regex NumericIdRegex = new(@"\b\d{5,}\b", RegexOptions.Compiled);
    private static readonly Regex ApmHexIdRegex = new(@"\b[a-fA-F0-9]{16}\b|\b[a-fA-F0-9]{32}\b", RegexOptions.Compiled);
    private static readonly Regex KestrelRequestIdRegex = new(@"\b[a-zA-Z0-9]{8,20}:\d{4,10}\b", RegexOptions.Compiled);

    public static HashSet<string> Extract(string question)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(question)) return ids;

        foreach (Match match in GuidRegex.Matches(question))
        {
            ids.Add(match.Value);
        }

        foreach (Match match in NumericIdRegex.Matches(question))
        {
            ids.Add(match.Value);
        }

        foreach (Match match in ApmHexIdRegex.Matches(question))
        {
            ids.Add(match.Value);
        }

        foreach (Match match in KestrelRequestIdRegex.Matches(question))
        {
            ids.Add(match.Value);
        }

        return ids;
    }
}
