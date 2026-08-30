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
            var initialMatches = await _vectorStore.SearchAsync(zeroVector, idFilter, limit: 200, collectionName, cancellationToken);
            exactMatches.AddRange(initialMatches);

            // Perform 2nd-hop graph expansion: extract all linked_ids across retrieved initial matches
            var allLinkedIds = new HashSet<string>(extractedIds, StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in initialMatches)
            {
                if (chunk.Payload.TryGetValue("linked_ids", out var idsStr) && !string.IsNullOrWhiteSpace(idsStr))
                {
                    foreach (var id in idsStr.Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrWhiteSpace(id) && id.Length > 2)
                        {
                            allLinkedIds.Add(id);
                        }
                    }
                }
            }

            if (allLinkedIds.Count > extractedIds.Count)
            {
                var expandedFilter = new QueryFilter
                {
                    ServiceName = filter.ServiceName,
                    Severity = filter.Severity,
                    SourceType = filter.SourceType,
                    FromUtc = filter.FromUtc,
                    ToUtc = filter.ToUtc,
                    LinkedIds = allLinkedIds.ToList()
                };

                var expandedMatches = await _vectorStore.SearchAsync(zeroVector, expandedFilter, limit: 200, collectionName, cancellationToken);
                exactMatches.AddRange(expandedMatches);
            }
        }

        // Deduplicate exact matches by LogHash and sort chronologically
        var dedupedExact = exactMatches
            .GroupBy(chunk => chunk.LogHash)
            .Select(group => group.First())
            .OrderBy(chunk => chunk.TimestampUtc)
            .ToList();

        // If user asked about specific ID(s) and zero matches were found, return empty
        if (extractedIds.Count > 0 && dedupedExact.Count == 0)
        {
            return [];
        }

        // If we have exact graph matches, return them (up to topK or max 50 for complete context)
        if (dedupedExact.Count > 0)
        {
            var limit = Math.Max(topK, 25);
            return dedupedExact.Take(limit).ToList();
        }

        var queryEmbedding = (await _embeddingService.EmbedTextsAsync([question], cancellationToken))[0];
        var candidateCount = Math.Max(topK * 3, 10);
        var semanticCandidates = await _vectorStore.SearchAsync(queryEmbedding, filter, candidateCount, collectionName, cancellationToken);

        return semanticCandidates
            .OrderByDescending(c => c.Score)
            .Take(topK)
            .ToList();
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
        @"\b[a-zA-Z0-9]{8}-[a-zA-Z0-9]{4}-[a-zA-Z0-9]{4}-[a-zA-Z0-9]{4}-[a-zA-Z0-9]{8,16}\b",
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
