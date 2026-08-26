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
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, QueryFilter filter, int topK, string? collectionName, CancellationToken cancellationToken);
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

    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string question, QueryFilter filter, int topK, string? collectionName, CancellationToken cancellationToken)
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
            var matches = await _vectorStore.SearchAsync(zeroVector, idFilter, limit: 200, collectionName, cancellationToken);
            exactMatches.AddRange(matches);
        }

        // Deduplicate exact matches by LogHash and sort chronologically
        var dedupedExact = exactMatches
            .GroupBy(chunk => chunk.LogHash)
            .Select(group => group.First())
            .OrderBy(chunk => chunk.TimestampUtc)
            .ToList();

        // CRITICAL: If user asked about a specific ID (extracted from question)
        // and we found ZERO exact matches, return empty — don't pollute with
        // random semantic results that don't have the requested ID.
        if (extractedIds.Count > 0 && dedupedExact.Count == 0)
        {
            return [];
        }

        // PERF: When we have exact ID matches, bias heavily toward them.
        // Semantic search only fills remaining slots, and only if there's room.
        if (dedupedExact.Count >= topK)
        {
            return dedupedExact.Take(topK).ToList();
        }

        var remaining = topK - dedupedExact.Count;
        var queryEmbedding = (await _embeddingService.EmbedTextsAsync([question], cancellationToken))[0];

        // If we have exact matches, use their linked_ids to narrow the semantic search
        QueryFilter semanticFilter;
        if (dedupedExact.Count > 0)
        {
            // Collect all linked IDs from exact matches to bias semantic search
            var allLinkedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in dedupedExact)
            {
                if (chunk.Payload.TryGetValue("linked_ids", out var idsStr) && !string.IsNullOrWhiteSpace(idsStr))
                {
                    foreach (var id in idsStr.Split(',', StringSplitOptions.TrimEntries))
                        allLinkedIds.Add(id);
                }
            }
            semanticFilter = new QueryFilter
            {
                ServiceName = filter.ServiceName,
                Severity = filter.Severity,
                SourceType = filter.SourceType,
                FromUtc = filter.FromUtc,
                ToUtc = filter.ToUtc,
                LinkedIds = allLinkedIds.Count > 0 ? allLinkedIds.ToList() : null,
            };
        }
        else
        {
            semanticFilter = filter;
        }

        var candidateCount = Math.Max(remaining * 3, 10);
        var semanticCandidates = await _vectorStore.SearchAsync(queryEmbedding, semanticFilter, candidateCount, collectionName, cancellationToken);

        var dedupedSemantic = semanticCandidates
            .Where(c => !dedupedExact.Any(e => e.LogHash == c.LogHash))
            .OrderByDescending(c => c.Score)
            .Take(remaining)
            .ToList();

        var merged = new List<RetrievedChunk>(dedupedExact);
        merged.AddRange(dedupedSemantic);

        return merged;
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

public static partial class QueryIdExtractor
{
    // PERF: Source-generated regex — compiled at build time, 3-5x faster.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial Regex GuidRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b\d{5,}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial Regex NumericIdRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b[a-fA-F0-9]{16}\b|\b[a-fA-F0-9]{32}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial Regex ApmHexIdRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b[a-zA-Z0-9]{8,20}:\d{4,10}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial Regex KestrelRequestIdRegex();

    public static HashSet<string> Extract(string question)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(question)) return ids;

        foreach (Match match in GuidRegex().Matches(question))
        {
            ids.Add(match.Value);
        }

        foreach (Match match in NumericIdRegex().Matches(question))
        {
            ids.Add(match.Value);
        }

        foreach (Match match in ApmHexIdRegex().Matches(question))
        {
            ids.Add(match.Value);
        }

        foreach (Match match in KestrelRequestIdRegex().Matches(question))
        {
            ids.Add(match.Value);
        }

        return ids;
    }
}
