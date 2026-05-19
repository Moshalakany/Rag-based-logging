using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Embedding;
using LogRag.Api.Sources;
using LogRag.Api.VectorStore;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Ingestion;

public interface IGenericLogParser
{
    ParsedLogEntry Parse(RawLogEntry rawLogEntry);
}

public interface ILogNormalizer
{
    NormalizedLogEntry Normalize(ParsedLogEntry parsedLogEntry);
}

public interface ILogChunker
{
    IReadOnlyList<LogChunk> Chunk(NormalizedLogEntry normalizedLogEntry);
}

public interface ILogEntryFilter
{
    bool ShouldDrop(RawLogEntry rawLogEntry);
}

public interface IIngestionOrchestrator
{
    Task<IngestionRunResult> IngestAsync(CancellationToken cancellationToken);
}

public sealed class RegexLogEntryFilter : ILogEntryFilter
{
    private readonly bool _enabled;
    private readonly IReadOnlyList<Regex> _dropPatterns;

    public RegexLogEntryFilter(IOptions<IngestionOptions> options)
    {
        _enabled = options.Value.EnableNoiseFiltering;
        _dropPatterns = options.Value.DropRawPatterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            .ToArray();
    }

    public bool ShouldDrop(RawLogEntry rawLogEntry)
    {
        if (!_enabled || _dropPatterns.Count == 0)
        {
            return false;
        }

        foreach (var pattern in _dropPatterns)
        {
            if (pattern.IsMatch(rawLogEntry.RawText))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class GenericLogParser : IGenericLogParser
{
    private static readonly string[] CsvColumns = ["timestamp", "severity", "service_name", "trace_id", "message"];
    private static readonly Regex TimestampPrefixedRegex = new(
        "^(?<timestamp>\\d{4}-\\d{2}-\\d{2}(?:[ T]\\d{2}\\s*:\\s*\\d{2}\\s*:\\s*\\d{2}(?:\\.\\d+)?(?:Z|[+-]\\d{2}:\\d{2})?)?)\\s+(?<message>[\\s\\S]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeverityPrefixRegex = new(
        "^(?<severity>TRACE|DEBUG|INFO|WARN|WARNING|ERROR|CRITICAL|FATAL)\\b[:\\- ]*(?<message>[\\s\\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CorrelationRegex = new(
        "Correlation[_ ]ID[:=]\\s*(?<trace>[a-zA-Z0-9\\-:]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RequestIdRegex = new(
        "requestId:\\s*(?<request>[^\\s,]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly IReadOnlyList<(string Name, Regex Regex)> _compiledRegexRules;

    public GenericLogParser(IOptions<ParserOptions> parserOptions)
    {
        _compiledRegexRules = parserOptions.Value.RegexRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule => (rule.Name, new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant)))
            .ToArray();
    }

    public ParsedLogEntry Parse(RawLogEntry rawLogEntry)
    {
        var text = rawLogEntry.RawText.Trim();
        if (TryParseJson(text, out var jsonFields))
        {
            return CreateParsedEntry(rawLogEntry, jsonFields);
        }

        if (TryParseCsv(text, out var csvFields))
        {
            return CreateParsedEntry(rawLogEntry, csvFields);
        }

        foreach (var (_, regex) in _compiledRegexRules)
        {
            var match = regex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var groupName in regex.GetGroupNames())
            {
                if (int.TryParse(groupName, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                var value = match.Groups[groupName].Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields[groupName] = value.Trim().Trim('"');
                }
            }

            if (fields.Count > 0)
            {
                return CreateParsedEntry(rawLogEntry, fields);
            }
        }

        if (TryParseKeyValue(text, out var keyValueFields))
        {
            return CreateParsedEntry(rawLogEntry, keyValueFields);
        }

        if (TryParseTimestampPrefixed(text, out var timestampFields))
        {
            return CreateParsedEntry(rawLogEntry, timestampFields);
        }

        return new ParsedLogEntry(rawLogEntry, new Dictionary<string, string> { ["message"] = rawLogEntry.RawText }, rawLogEntry.RawText);
    }

    private static ParsedLogEntry CreateParsedEntry(RawLogEntry raw, IReadOnlyDictionary<string, string> fields)
    {
        var message = GetFirst(fields, "message", "msg", "log", "text") ?? raw.RawText;
        return new ParsedLogEntry(raw, fields, message);
    }

    private static bool TryParseJson(string text, out IReadOnlyDictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText(),
                };
            }

            fields = map;
            return map.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseCsv(string text, out IReadOnlyDictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>();
        if (!text.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = text.Split(',', CsvColumns.Length, StringSplitOptions.TrimEntries);
        if (parts.Length < 5)
        {
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < CsvColumns.Length; i++)
        {
            map[CsvColumns[i]] = parts[i];
        }

        fields = map;
        return true;
    }

    private static bool TryParseKeyValue(string text, out IReadOnlyDictionary<string, string> fields)
    {
        var matches = Regex.Matches(text, "(?<key>[a-zA-Z_][a-zA-Z0-9_]*)=(?<value>\"[^\"]+\"|\\S+)");
        if (matches.Count == 0)
        {
            fields = new Dictionary<string, string>();
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim().Trim('"');
            map[key] = value;
        }

        if (!map.ContainsKey("message"))
        {
            map["message"] = text;
        }

        fields = map;
        return true;
    }

    private static bool TryParseTimestampPrefixed(string text, out IReadOnlyDictionary<string, string> fields)
    {
        var match = TimestampPrefixedRegex.Match(text);
        if (!match.Success)
        {
            fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        var message = match.Groups["message"].Value.Trim();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["timestamp"] = match.Groups["timestamp"].Value.Trim(),
            ["message"] = message,
        };

        var severityMatch = SeverityPrefixRegex.Match(message);
        if (severityMatch.Success)
        {
            map["severity"] = severityMatch.Groups["severity"].Value.Trim();
            map["message"] = severityMatch.Groups["message"].Value.Trim();
        }

        var correlationMatch = CorrelationRegex.Match(text);
        if (correlationMatch.Success)
        {
            map["trace_id"] = correlationMatch.Groups["trace"].Value.Trim();
        }
        else
        {
            var requestIdMatch = RequestIdRegex.Match(text);
            if (requestIdMatch.Success)
            {
                map["request_id"] = requestIdMatch.Groups["request"].Value.Trim();
            }
        }

        fields = map;
        return true;
    }

    private static string? GetFirst(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

public sealed class LogNormalizer : ILogNormalizer
{
    private static readonly string[] TimestampKeys = ["timestamp", "ts", "@timestamp", "time", "date"];
    private static readonly string[] SeverityKeys = ["severity", "level", "loglevel"];
    private static readonly string[] ServiceKeys = ["service_name", "service", "application", "app"];
    private static readonly string[] TraceKeys = ["trace_id", "traceid", "correlation_id", "request_id"];
    private static readonly string[] MessageKeys = ["message", "msg", "log", "text"];
    private static readonly string[] SyslogTimestampFormats = ["MMM d HH:mm:ss", "MMM dd HH:mm:ss"];

    public NormalizedLogEntry Normalize(ParsedLogEntry parsedLogEntry)
    {
        var fields = new Dictionary<string, string>(parsedLogEntry.Fields, StringComparer.OrdinalIgnoreCase);

        var timestamp = ParseTimestamp(GetFirst(fields, TimestampKeys) ?? parsedLogEntry.Raw.IngestedAtUtc.ToString("O", CultureInfo.InvariantCulture), parsedLogEntry.Raw.IngestedAtUtc);
        var severity = NormalizeSeverity(GetFirst(fields, SeverityKeys) ?? "INFO");
        var serviceName = GetFirst(fields, ServiceKeys) ?? parsedLogEntry.Raw.SourceId;
        var traceId = GetFirst(fields, TraceKeys) ?? "n/a";
        var message = GetFirst(fields, MessageKeys) ?? parsedLogEntry.Message;

        var payload = fields
            .Where(kvp => !TimestampKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            .Where(kvp => !SeverityKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            .Where(kvp => !ServiceKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            .Where(kvp => !TraceKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            .Where(kvp => !MessageKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var logHash = ComputeSha256($"{parsedLogEntry.Raw.SourceId}|{parsedLogEntry.Raw.RawText}");

        return new NormalizedLogEntry(
            parsedLogEntry.Raw.SourceId,
            parsedLogEntry.Raw.SourceType,
            timestamp,
            severity,
            serviceName,
            traceId,
            message,
            logHash,
            payload);
    }

    private static DateTimeOffset ParseTimestamp(string value, DateTimeOffset fallback)
    {
        var normalized = NormalizeTimestampText(value);
        if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        if (DateTime.TryParseExact(normalized, SyslogTimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var syslog))
        {
            var utc = new DateTime(
                fallback.UtcDateTime.Year,
                syslog.Month,
                syslog.Day,
                syslog.Hour,
                syslog.Minute,
                syslog.Second,
                DateTimeKind.Utc);

            return new DateTimeOffset(utc);
        }

        return fallback;
    }

    private static string NormalizeTimestampText(string value)
    {
        var compact = value.Trim().Trim('"');
        compact = Regex.Replace(compact, "\\s*:\\s*", ":");
        compact = Regex.Replace(compact, "\\s+", " ");
        return compact;
    }

    private static string NormalizeSeverity(string severity)
    {
        var normalized = severity.Trim().ToUpperInvariant();
        return normalized switch
        {
            "WARN" => "WARNING",
            "ERR" => "ERROR",
            "CRIT" => "CRITICAL",
            _ => normalized,
        };
    }

    private static string? GetFirst(IReadOnlyDictionary<string, string> fields, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class SlidingWindowLogChunker : ILogChunker
{
    private readonly ChunkingOptions _options;

    public SlidingWindowLogChunker(IOptions<ChunkingOptions> options)
    {
        _options = options.Value;
    }

    public IReadOnlyList<LogChunk> Chunk(NormalizedLogEntry normalizedLogEntry)
    {
        var chunkSize = Math.Max(8, _options.ChunkSizeTokens);
        var overlap = Math.Clamp(_options.OverlapTokens, 0, chunkSize - 1);
        var step = Math.Max(1, chunkSize - overlap);

        var rendered = $"{normalizedLogEntry.TimestampUtc:O} [{normalizedLogEntry.Severity}] {normalizedLogEntry.ServiceName} trace={normalizedLogEntry.TraceId} {normalizedLogEntry.Message}";
        var payloadSuffix = normalizedLogEntry.Payload.Count == 0
            ? ""
            : $" payload={JsonSerializer.Serialize(normalizedLogEntry.Payload)}";

        var tokens = (rendered + payloadSuffix).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var chunks = new List<LogChunk>();
        var chunkIndex = 0;
        for (var start = 0; start < tokens.Length; start += step)
        {
            var slice = tokens.Skip(start).Take(chunkSize);
            var text = string.Join(' ', slice);
            if (text.Length == 0)
            {
                continue;
            }

            chunks.Add(new LogChunk(
                ChunkId: $"{normalizedLogEntry.LogHash}-{chunkIndex:D4}",
                LogHash: normalizedLogEntry.LogHash,
                Text: text,
                TimestampUtc: normalizedLogEntry.TimestampUtc,
                Severity: normalizedLogEntry.Severity,
                ServiceName: normalizedLogEntry.ServiceName,
                TraceId: normalizedLogEntry.TraceId,
                SourceId: normalizedLogEntry.SourceId,
                SourceType: normalizedLogEntry.SourceType,
                Payload: normalizedLogEntry.Payload));

            chunkIndex++;
            if (start + chunkSize >= tokens.Length)
            {
                break;
            }
        }

        return chunks;
    }
}

public sealed class IngestionOrchestrator : IIngestionOrchestrator
{
    private readonly ILogSourceRegistry _sourceRegistry;
    private readonly IGenericLogParser _parser;
    private readonly ILogNormalizer _normalizer;
    private readonly ILogChunker _chunker;
    private readonly ILogEntryFilter _logEntryFilter;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IngestionOptions _ingestionOptions;
    private readonly IOptions<VectorStoreOptions> _vectorStoreOptions;
    private readonly ILogger<IngestionOrchestrator> _logger;

    public IngestionOrchestrator(
        ILogSourceRegistry sourceRegistry,
        IGenericLogParser parser,
        ILogNormalizer normalizer,
        ILogChunker chunker,
        ILogEntryFilter logEntryFilter,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IOptions<IngestionOptions> ingestionOptions,
        IOptions<VectorStoreOptions> vectorStoreOptions,
        ILogger<IngestionOrchestrator> logger)
    {
        _sourceRegistry = sourceRegistry;
        _parser = parser;
        _normalizer = normalizer;
        _chunker = chunker;
        _logEntryFilter = logEntryFilter;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _ingestionOptions = ingestionOptions.Value;
        _vectorStoreOptions = vectorStoreOptions;
        _logger = logger;
    }

    public async Task<IngestionRunResult> IngestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _vectorStore.EnsureCollectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed while ensuring Qdrant collection.", ex);
        }

        var rawLogsRead = 0;
        var filteredLogs = 0;
        var chunksCreated = 0;
        var vectorsUpserted = 0;
        var processingBatchSize = Math.Max(1, _ingestionOptions.ProcessingBatchSize);
        var pendingChunks = new List<LogChunk>(processingBatchSize);

        foreach (var source in _sourceRegistry.GetSources())
        {
            await foreach (var rawLog in source.ReadAsync(cancellationToken))
            {
                rawLogsRead++;
                if (_logEntryFilter.ShouldDrop(rawLog))
                {
                    filteredLogs++;
                    continue;
                }

                var parsed = _parser.Parse(rawLog);
                var normalized = _normalizer.Normalize(parsed);
                var chunks = _chunker.Chunk(normalized);
                if (chunks.Count == 0)
                {
                    continue;
                }

                chunksCreated += chunks.Count;
                pendingChunks.AddRange(chunks);

                if (pendingChunks.Count >= processingBatchSize)
                {
                    vectorsUpserted += await UpsertBatchAsync(pendingChunks, cancellationToken);
                    pendingChunks.Clear();
                }
            }
        }

        if (pendingChunks.Count > 0)
        {
            vectorsUpserted += await UpsertBatchAsync(pendingChunks, cancellationToken);
            pendingChunks.Clear();
        }

        if (chunksCreated == 0)
        {
            _logger.LogInformation("Ingestion completed with no chunks.");
            return new IngestionRunResult(rawLogsRead, 0, 0, DateTimeOffset.UtcNow);
        }

        try
        {
            await _vectorStore.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-_vectorStoreOptions.Value.RetentionDays), cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed while applying Qdrant retention cleanup.", ex);
        }

        _logger.LogInformation(
            "Ingestion completed. RawLogsRead={RawLogsRead}, FilteredLogs={FilteredLogs}, ChunksCreated={ChunksCreated}, VectorsUpserted={VectorsUpserted}",
            rawLogsRead,
            filteredLogs,
            chunksCreated,
            vectorsUpserted);

        return new IngestionRunResult(rawLogsRead, chunksCreated, vectorsUpserted, DateTimeOffset.UtcNow);
    }

    private async Task<int> UpsertBatchAsync(IReadOnlyList<LogChunk> chunks, CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await _embeddingService.EmbedTextsAsync(chunks.Select(chunk => chunk.Text).ToArray(), cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed while generating embeddings from Ollama for batch size {chunks.Count}.", ex);
        }

        var vectorPoints = new List<VectorPoint>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            vectorPoints.Add(new VectorPoint(CreateQdrantPointId(chunks[i].ChunkId), embeddings[i], chunks[i]));
        }

        try
        {
            await _vectorStore.UpsertAsync(vectorPoints, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed while upserting vectors to Qdrant for batch size {chunks.Count}.", ex);
        }

        return vectorPoints.Count;
    }

    private static string CreateQdrantPointId(string chunkId)
    {
        // Qdrant accepts UUID string IDs reliably across versions.
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(chunkId));
        return new Guid(hash).ToString();
    }
}

public sealed class IngestionHostedService : BackgroundService
{
    private readonly IIngestionOrchestrator _orchestrator;
    private readonly SchedulerOptions _options;
    private readonly SemaphoreSlim _ingestLock = new(1, 1);
    private readonly ILogger<IngestionHostedService> _logger;

    public IngestionHostedService(IIngestionOrchestrator orchestrator, IOptions<SchedulerOptions> options, ILogger<IngestionHostedService> logger)
    {
        _orchestrator = orchestrator;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>();
        if (_options.EnableDailyBatch)
        {
            loops.Add(RunDailyBatchLoopAsync(stoppingToken));
        }

        if (_options.EnableStreaming)
        {
            loops.Add(RunStreamingLoopAsync(stoppingToken));
        }

        if (loops.Count == 0)
        {
            _logger.LogInformation("Ingestion scheduler is disabled.");
            return;
        }

        await Task.WhenAll(loops);
    }

    private async Task RunDailyBatchLoopAsync(CancellationToken cancellationToken)
    {
        if (_options.RunBatchOnStartup)
        {
            try
            {
                await RunIngestionAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup ingestion failed. Check if Qdrant and Ollama are running.");
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, _options.DailyBatchIntervalHours)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RunIngestionAsync(cancellationToken);
        }
    }

    private async Task RunStreamingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.StreamingPollSeconds)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RunIngestionAsync(cancellationToken);
        }
    }

    private async Task RunIngestionAsync(CancellationToken cancellationToken)
    {
        if (!await _ingestLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await _orchestrator.IngestAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingestion failed. Ensure Qdrant is running on localhost:6333 and Ollama on localhost:11434");
        }
        finally
        {
            _ingestLock.Release();
        }
    }
}
