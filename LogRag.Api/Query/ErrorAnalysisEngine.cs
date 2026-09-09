using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Embedding;
using LogRag.Api.Ingestion;
using LogRag.Api.Llm;
using LogRag.Api.Sources;
using LogRag.Api.VectorStore;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Query;

public interface IErrorAnalysisEngine
{
    Task<ErrorAnalysisSession> StartSessionAsync(CreateErrorAnalysisRequest request, CancellationToken cancellationToken);
    Task<bool> StopSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<ErrorAnalysisSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ErrorAnalysisSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<ErrorAnalysisProgressEvent> StreamProgressAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class ErrorAnalysisEngine : IErrorAnalysisEngine
{
    private readonly ILogSourceRegistry _sourceRegistry;
    private readonly IGenericLogParser _parser;
    private readonly ILogNormalizer _normalizer;
    private readonly ILogEntryFilter _filter;
    private readonly ILogChunker _chunker;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly ILlmClient _llmClient;
    private readonly IErrorAnalysisSessionStore _sessionStore;
    private readonly ErrorAnalysisOptions _options;
    private readonly ILogger<ErrorAnalysisEngine> _logger;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRuns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ErrorAnalysisProgressEvent>>> _subscribers = new(StringComparer.OrdinalIgnoreCase);

    public ErrorAnalysisEngine(
        ILogSourceRegistry sourceRegistry,
        IGenericLogParser parser,
        ILogNormalizer normalizer,
        ILogEntryFilter filter,
        ILogChunker chunker,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        ILlmClient llmClient,
        IErrorAnalysisSessionStore sessionStore,
        IOptions<ErrorAnalysisOptions> options,
        ILogger<ErrorAnalysisEngine> logger)
    {
        _sourceRegistry = sourceRegistry;
        _parser = parser;
        _normalizer = normalizer;
        _filter = filter;
        _chunker = chunker;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _llmClient = llmClient;
        _sessionStore = sessionStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ErrorAnalysisSession> StartSessionAsync(CreateErrorAnalysisRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        var sessionName = !string.IsNullOrWhiteSpace(request.Name)
            ? request.Name.Trim()
            : $"Error-Analysis-{DateTimeOffset.UtcNow:yyyyMMdd-HHmm}";

        var session = new ErrorAnalysisSession
        {
            SessionId = sessionId,
            Name = sessionName,
            FromUtc = request.FromUtc,
            ToUtc = request.ToUtc,
            Status = ErrorAnalysisSessionStatus.Running,
            ProgressStage = ErrorAnalysisProgressStage.Initializing,
            ProgressMessage = "Initializing error analysis session...",
            PercentComplete = 5,
            CollectionName = $"error_session_{sessionId}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _sessionStore.SaveAsync(session, cancellationToken);

        var cts = new CancellationTokenSource();
        _activeRuns[sessionId] = cts;

        // Background execution
        _ = Task.Run(() => RunAnalysisCycleAsync(session, request.MaxTracesToAnalyze, cts.Token));

        return session;
    }

    public async Task<bool> StopSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_activeRuns.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        var session = await _sessionStore.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.Status = ErrorAnalysisSessionStatus.Stopped;
        session.ProgressStage = ErrorAnalysisProgressStage.Stopped;
        session.ProgressMessage = "Session was manually stopped.";
        session.CompletedAtUtc = DateTimeOffset.UtcNow;
        session.PercentComplete = 100;

        if (_options.AutoDropCollectionOnStop && !string.IsNullOrWhiteSpace(session.CollectionName))
        {
            try
            {
                await _vectorStore.DeleteCollectionAsync(session.CollectionName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to drop Qdrant collection on session stop for {SessionId}", sessionId);
            }
        }

        await _sessionStore.SaveAsync(session, cancellationToken);
        BroadcastProgress(session, 100);

        if (_subscribers.TryRemove(sessionId, out var sessionSubs))
        {
            foreach (var sub in sessionSubs.Values)
            {
                sub.Writer.TryComplete();
            }
        }

        return true;
    }

    public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_activeRuns.TryRemove(sessionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        var session = await _sessionStore.GetAsync(sessionId, cancellationToken);
        if (session is not null && !string.IsNullOrWhiteSpace(session.CollectionName))
        {
            try
            {
                await _vectorStore.DeleteCollectionAsync(session.CollectionName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to drop Qdrant collection on session delete for {SessionId}", sessionId);
            }
        }

        if (_subscribers.TryRemove(sessionId, out var sessionSubs))
        {
            foreach (var sub in sessionSubs.Values)
            {
                sub.Writer.TryComplete();
            }
        }

        return await _sessionStore.DeleteAsync(sessionId, cancellationToken);
    }

    public Task<ErrorAnalysisSession?> GetSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        _sessionStore.GetAsync(sessionId, cancellationToken);

    public Task<IReadOnlyList<ErrorAnalysisSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken) =>
        _sessionStore.ListAsync(cancellationToken);

    public async IAsyncEnumerable<ErrorAnalysisProgressEvent> StreamProgressAsync(
        string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = await _sessionStore.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            yield break;
        }

        var currentPct = CalculateProgressPercent(session);

        // Emit current state snapshot immediately with accurate percent
        yield return new ErrorAnalysisProgressEvent(
            session.SessionId,
            session.ProgressStage,
            session.ProgressMessage,
            session.TotalRawScanned,
            session.ErrorsFound,
            session.CorrelatedTracesCount,
            session.VectorsUpserted,
            session.RcaCompletedCount,
            session.Traces.Count,
            currentPct,
            DateTimeOffset.UtcNow);

        if (session.Status != ErrorAnalysisSessionStatus.Running)
        {
            yield break;
        }

        var subId = Guid.NewGuid();
        var subChannel = Channel.CreateBounded<ErrorAnalysisProgressEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var sessionSubs = _subscribers.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, Channel<ErrorAnalysisProgressEvent>>());
        sessionSubs[subId] = subChannel;

        try
        {
            while (!cancellationToken.IsCancellationRequested && await subChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (subChannel.Reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            sessionSubs.TryRemove(subId, out _);
        }
    }

    public static int CalculateProgressPercent(ErrorAnalysisSession session)
    {
        if (session.PercentComplete > 0)
        {
            return session.PercentComplete;
        }

        if (session.Status is ErrorAnalysisSessionStatus.Completed or ErrorAnalysisSessionStatus.Stopped or ErrorAnalysisSessionStatus.Failed)
        {
            return 100;
        }

        return session.ProgressStage switch
        {
            ErrorAnalysisProgressStage.Initializing => 5,
            ErrorAnalysisProgressStage.Scanning => 15,
            ErrorAnalysisProgressStage.Correlating => 35,
            ErrorAnalysisProgressStage.Vectorizing => 50,
            ErrorAnalysisProgressStage.Analyzing => 60 + (int)(38.0 * session.RcaCompletedCount / Math.Max(1, session.CorrelatedTracesCount > 0 ? session.CorrelatedTracesCount : session.Traces.Count)),
            ErrorAnalysisProgressStage.Completed => 100,
            _ => 5
        };
    }

    private async Task RunAnalysisCycleAsync(ErrorAnalysisSession session, int? maxTracesParam, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Starting error analysis cycle for session {SessionId}", session.SessionId);

            // ── STAGE 1: Scan & Detect Errors Across All Sources ──
            session.ProgressStage = ErrorAnalysisProgressStage.Scanning;
            session.ProgressMessage = "Scanning logs across configured sources in the specified date range...";
            BroadcastProgress(session, 10);
            await _sessionStore.SaveAsync(session, CancellationToken.None);

            var timeWindow = (session.FromUtc.HasValue || session.ToUtc.HasValue)
                ? new IngestionTimeWindow(session.FromUtc, session.ToUtc)
                : null;

            var sources = _sourceRegistry.GetSources();
            var allNormalizedLogs = new List<NormalizedLogEntry>();
            var errorCorrelationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int totalScanned = 0;
            int totalErrors = 0;

            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogInformation("Scanning source {SourceId} for session {SessionId}", source.Id, session.SessionId);

                await foreach (var raw in source.ReadAllAsync(timeWindow, ct))
                {
                    totalScanned++;
                    if (totalScanned % 500 == 0)
                    {
                        session.TotalRawScanned = totalScanned;
                        session.ErrorsFound = totalErrors;
                        session.ProgressMessage = $"Scanned {totalScanned} log lines, detected {totalErrors} errors...";
                        BroadcastProgress(session, Math.Min(30, 10 + (totalScanned / 100)));
                    }

                    if (_filter.ShouldDrop(raw))
                    {
                        continue;
                    }

                    var parsed = _parser.Parse(raw);
                    var normalized = _normalizer.Normalize(parsed);

                    // Verify time window if present
                    if (session.FromUtc.HasValue && normalized.TimestampUtc < session.FromUtc.Value)
                    {
                        continue;
                    }
                    if (session.ToUtc.HasValue && normalized.TimestampUtc > session.ToUtc.Value)
                    {
                        continue;
                    }

                    var isError = IsErrorEntry(normalized);
                    var correlationId = ResolveCorrelationId(normalized);

                    if (isError)
                    {
                        totalErrors++;
                        errorCorrelationIds.Add(correlationId);
                    }

                    allNormalizedLogs.Add(normalized);
                }
            }

            session.TotalRawScanned = totalScanned;
            session.ErrorsFound = totalErrors;

            _logger.LogInformation(
                "Scan complete for session {SessionId}: Scanned={TotalScanned}, Errors={Errors}, UniqueCorrelationIds={CidCount}",
                session.SessionId, totalScanned, totalErrors, errorCorrelationIds.Count);

            if (errorCorrelationIds.Count == 0)
            {
                session.ProgressStage = ErrorAnalysisProgressStage.Completed;
                session.ProgressMessage = (session.FromUtc.HasValue || session.ToUtc.HasValue)
                    ? $"No error traces matched the time window ({session.FromUtc:yyyy-MM-dd} to {session.ToUtc:yyyy-MM-dd}). {totalScanned} total logs were scanned. Note: Configured sample logs are dated May 2026. Try clearing the dates or selecting May 2026."
                    : $"Analysis complete. No errors were found across {totalScanned} scanned logs.";
                session.Status = ErrorAnalysisSessionStatus.Completed;
                session.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _sessionStore.SaveAsync(session, CancellationToken.None);
                BroadcastProgress(session, 100);
                return;
            }

            // ── STAGE 2: Correlate & Group Logs ──
            ct.ThrowIfCancellationRequested();
            session.ProgressStage = ErrorAnalysisProgressStage.Correlating;
            session.ProgressMessage = $"Gathering all related log entries across all components for {errorCorrelationIds.Count} error correlation traces...";
            BroadcastProgress(session, 35);
            await _sessionStore.SaveAsync(session, CancellationToken.None);

            // Filter all logs that belong to any error correlation ID
            var groupedTraces = allNormalizedLogs
                .Where(log => errorCorrelationIds.Contains(ResolveCorrelationId(log)))
                .GroupBy(ResolveCorrelationId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var maxTraces = maxTracesParam.GetValueOrDefault(_options.MaxTracesToAnalyze);
            if (maxTraces > 0 && groupedTraces.Count > maxTraces)
            {
                groupedTraces = groupedTraces.Take(maxTraces).ToList();
            }

            var traces = new List<ErrorCorrelationTrace>(groupedTraces.Count);
            var logsToVectorize = new List<NormalizedLogEntry>();

            foreach (var group in groupedTraces)
            {
                var sortedLogs = group.OrderBy(l => l.TimestampUtc).ToList();
                logsToVectorize.AddRange(sortedLogs);

                var trace = new ErrorCorrelationTrace
                {
                    CorrelationId = group.Key,
                    ServicesInvolved = sortedLogs.Select(l => l.ServiceName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    StartTimeUtc = sortedLogs.First().TimestampUtc,
                    EndTimeUtc = sortedLogs.Last().TimestampUtc,
                    ErrorCount = sortedLogs.Count(IsErrorEntry),
                    Logs = sortedLogs.Select(l => new TraceLogItem(
                        l.TimestampUtc,
                        l.Severity,
                        l.ServiceName,
                        l.SourceId,
                        l.CorrelationId,
                        l.Message,
                        l.StatusCode,
                        $"{l.TimestampUtc:yyyy-MM-dd HH:mm:ss.fff} [{l.ServiceName}] [{l.Severity}] {l.Message}"
                    )).ToList()
                };

                traces.Add(trace);
            }

            session.Traces = traces;
            session.CorrelatedTracesCount = traces.Count;
            session.ProgressMessage = $"Found {traces.Count} distinct error correlation groups with {logsToVectorize.Count} total associated log entries.";
            BroadcastProgress(session, 45);
            await _sessionStore.SaveAsync(session, CancellationToken.None);

            // ── STAGE 3: Vectorize Into Dedicated Session Collection ──
            ct.ThrowIfCancellationRequested();
            session.ProgressStage = ErrorAnalysisProgressStage.Vectorizing;
            session.ProgressMessage = $"Creating isolated collection '{session.CollectionName}' and embedding {logsToVectorize.Count} correlated logs...";
            BroadcastProgress(session, 50);
            await _sessionStore.SaveAsync(session, CancellationToken.None);

            await _vectorStore.EnsureCollectionAsync(session.CollectionName, ct);

            // Chunk & Embed
            var chunks = new List<LogChunk>();
            foreach (var log in logsToVectorize)
            {
                chunks.AddRange(_chunker.Chunk(log));
            }

            int vectorsUpserted = 0;
            const int embedBatchSize = 32;

            for (int i = 0; i < chunks.Count; i += embedBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = chunks.Skip(i).Take(embedBatchSize).ToList();
                var texts = batch.Select(c => c.Text).ToList();

                var embeddings = await _embeddingService.EmbedTextsAsync(texts, ct);

                var points = new List<VectorPoint>(batch.Count);
                for (int j = 0; j < batch.Count; j++)
                {
                    points.Add(new VectorPoint(CreateQdrantPointId(batch[j].ChunkId), embeddings[j], batch[j]));
                }

                await _vectorStore.UpsertAsync(points, session.CollectionName, ct);
                vectorsUpserted += points.Count;

                session.VectorsUpserted = vectorsUpserted;
                var embedPct = 50 + (int)(15.0 * vectorsUpserted / Math.Max(1, chunks.Count));
                session.ProgressMessage = $"Vectorized {vectorsUpserted}/{chunks.Count} chunks into {session.CollectionName}...";
                BroadcastProgress(session, embedPct);
            }

            await _vectorStore.RefreshCollectionAsync(session.CollectionName, ct);

            // ── STAGE 4: AI Root Cause Analysis per Correlation Trace ──
            ct.ThrowIfCancellationRequested();
            session.ProgressStage = ErrorAnalysisProgressStage.Analyzing;
            session.ProgressMessage = $"Running AI Root Cause Analysis on {traces.Count} correlation traces...";
            BroadcastProgress(session, 65);
            await _sessionStore.SaveAsync(session, CancellationToken.None);

            int rcaDone = 0;
            foreach (var trace in traces)
            {
                ct.ThrowIfCancellationRequested();

                session.ProgressMessage = $"Analyzing trace {rcaDone + 1}/{traces.Count} (Correlation ID: {trace.CorrelationId})...";
                BroadcastProgress(session, 65 + (int)(30.0 * rcaDone / Math.Max(1, traces.Count)));

                var rcaResult = await AnalyzeTraceWithLlmAsync(trace, ct);
                trace.RcaResult = rcaResult;
                rcaDone++;
                session.RcaCompletedCount = rcaDone;

                // Save incremental progress after each RCA
                await _sessionStore.SaveAsync(session, CancellationToken.None);
            }

            // ── STAGE 5: Complete ──
            session.Status = ErrorAnalysisSessionStatus.Completed;
            session.ProgressStage = ErrorAnalysisProgressStage.Completed;
            session.ProgressMessage = $"Analysis successfully completed! Processed {traces.Count} error traces across {totalScanned} scanned logs.";
            session.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _sessionStore.SaveAsync(session, CancellationToken.None);
            BroadcastProgress(session, 100);

            _logger.LogInformation("Session {SessionId} completed successfully.", session.SessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Session {SessionId} was canceled.", session.SessionId);
            session.Status = ErrorAnalysisSessionStatus.Stopped;
            session.ProgressStage = ErrorAnalysisProgressStage.Stopped;
            session.ProgressMessage = "Session was stopped by the user.";
            session.CompletedAtUtc = DateTimeOffset.UtcNow;

            if (_options.AutoDropCollectionOnStop && !string.IsNullOrWhiteSpace(session.CollectionName))
            {
                try
                {
                    await _vectorStore.DeleteCollectionAsync(session.CollectionName, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to drop collection on stop for session {SessionId}", session.SessionId);
                }
            }

            await _sessionStore.SaveAsync(session, CancellationToken.None);
            BroadcastProgress(session, 100);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session {SessionId} failed unexpectedly", session.SessionId);
            session.Status = ErrorAnalysisSessionStatus.Failed;
            session.ProgressStage = ErrorAnalysisProgressStage.Failed;
            session.ErrorMessage = ex.Message;
            session.ProgressMessage = $"Analysis failed: {ex.Message}";
            session.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _sessionStore.SaveAsync(session, CancellationToken.None);
            BroadcastProgress(session, 100);
        }
        finally
        {
            _activeRuns.TryRemove(session.SessionId, out _);
            if (_subscribers.TryRemove(session.SessionId, out var sessionSubs))
            {
                foreach (var sub in sessionSubs.Values)
                {
                    sub.Writer.TryComplete();
                }
            }
        }
    }

    private async Task<TraceRcaResult> AnalyzeTraceWithLlmAsync(ErrorCorrelationTrace trace, CancellationToken ct)
    {
        var traceLogBuilder = new StringBuilder();
        traceLogBuilder.AppendLine($"Correlation ID: {trace.CorrelationId}");
        traceLogBuilder.AppendLine($"Services Involved: {string.Join(" -> ", trace.ServicesInvolved)}");
        traceLogBuilder.AppendLine($"Timeline Window: {trace.StartTimeUtc:yyyy-MM-dd HH:mm:ss.fff} UTC -> {trace.EndTimeUtc:yyyy-MM-dd HH:mm:ss.fff} UTC");
        traceLogBuilder.AppendLine("Log sequence:");

        foreach (var log in trace.Logs.Take(60)) // Cap per-trace entries to prevent context blowup
        {
            var codePart = log.StatusCode.HasValue ? $" [Status: {log.StatusCode}]" : "";
            traceLogBuilder.AppendLine($"[{log.TimestampUtc:HH:mm:ss.fff}] [{log.ServiceName}] [{log.Severity}]{codePart}: {log.Message}");
        }

        var context = traceLogBuilder.ToString();
        var question =
            "Analyze the failure chain for this transaction.\n" +
            "Provide your findings in these exact sections:\n" +
            "### Culprit Component\n<the first service that failed>\n\n" +
            "### Root Cause\n<technical explanation of what failed and why>\n\n" +
            "### Impact\n<how other services and the user were affected>\n\n" +
            "### Suggested Fix\n<concrete technical steps to fix or prevent this>\n\n" +
            "### Summary\n<concise 1-sentence summary>";

        var responseBuilder = new StringBuilder();
        try
        {
            await foreach (var chunk in _llmClient.StreamAnswerAsync(question, context, [], ct))
            {
                responseBuilder.Append(chunk);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM Root Cause Analysis failed for CorrelationId {Cid}", trace.CorrelationId);
            return new TraceRcaResult(
                Summary: $"Automated LLM analysis error: {ex.Message}",
                CulpritComponent: trace.ServicesInvolved.FirstOrDefault() ?? "Unknown",
                RootCause: $"Analysis encountered error: {ex.Message}",
                Impact: "Unknown due to analysis error",
                SuggestedFix: "Review raw logs manually.",
                CompletedAtUtc: DateTimeOffset.UtcNow);
        }

        var responseText = responseBuilder.ToString();
        return ParseLlmRcaResponse(responseText, trace);
    }

    private static TraceRcaResult ParseLlmRcaResponse(string text, ErrorCorrelationTrace trace)
    {
        string ExtractSection(string header)
        {
            var pattern = $@"###\s*{Regex.Escape(header)}[^\n]*\n([\s\S]*?)(?=(###|\Z))";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        var culprit = ExtractSection("Culprit Component");
        var rootCause = ExtractSection("Root Cause");
        var impact = ExtractSection("Impact");
        var fix = ExtractSection("Suggested Fix");
        var summary = ExtractSection("Summary");

        if (string.IsNullOrWhiteSpace(culprit))
        {
            culprit = trace.ServicesInvolved.FirstOrDefault() ?? "Unknown";
        }

        if (string.IsNullOrWhiteSpace(rootCause))
        {
            rootCause = text.Trim();
        }

        if (string.IsNullOrWhiteSpace(summary))
        {
            var firstSentence = rootCause.Split(['\n', '.'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            summary = !string.IsNullOrWhiteSpace(firstSentence) ? firstSentence.Trim() : "Error occurred during cross-component transaction.";
        }

        if (string.IsNullOrWhiteSpace(impact))
        {
            impact = $"Transaction aborted across {string.Join(", ", trace.ServicesInvolved)}.";
        }

        if (string.IsNullOrWhiteSpace(fix))
        {
            fix = "Inspect component configurations, connection timeouts, and upstream error handling.";
        }

        return new TraceRcaResult(
            Summary: summary,
            CulpritComponent: culprit,
            RootCause: rootCause,
            Impact: impact,
            SuggestedFix: fix,
            CompletedAtUtc: DateTimeOffset.UtcNow);
    }

    private void BroadcastProgress(ErrorAnalysisSession session, int percent)
    {
        session.PercentComplete = percent;
        var evt = new ErrorAnalysisProgressEvent(
            session.SessionId,
            session.ProgressStage,
            session.ProgressMessage,
            session.TotalRawScanned,
            session.ErrorsFound,
            session.CorrelatedTracesCount,
            session.VectorsUpserted,
            session.RcaCompletedCount,
            session.Traces.Count,
            percent,
            DateTimeOffset.UtcNow);

        if (_subscribers.TryGetValue(session.SessionId, out var sessionSubs))
        {
            foreach (var sub in sessionSubs.Values)
            {
                sub.Writer.TryWrite(evt);
            }
        }
    }

    private static bool IsErrorEntry(NormalizedLogEntry entry)
    {
        var sev = entry.Severity.ToUpperInvariant();
        if (sev is "ERROR" or "CRITICAL" or "FATAL")
        {
            return true;
        }

        if (entry.StatusCode.HasValue && entry.StatusCode.Value >= 500)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entry.Message))
        {
            var msg = entry.Message;
            if (msg.Contains("Exception:", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("System.Exception", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("NullReferenceException", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("SqlException", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("HttpRequestException", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("StackTrace", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("failed with status 5", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveCorrelationId(NormalizedLogEntry entry)
    {
        var cid = entry.CorrelationId?.Trim();
        if (!string.IsNullOrWhiteSpace(cid) && !string.Equals(cid, "n/a", StringComparison.OrdinalIgnoreCase) && cid != "-")
        {
            return cid;
        }

        var tid = entry.TraceId?.Trim();
        if (!string.IsNullOrWhiteSpace(tid) && !string.Equals(tid, "n/a", StringComparison.OrdinalIgnoreCase) && tid != "-")
        {
            return tid;
        }

        return $"uncorrelated-{entry.ServiceName.ToLowerInvariant()}-{entry.LogHash[..Math.Min(8, entry.LogHash.Length)]}";
    }

    private static string CreateQdrantPointId(string chunkId)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(chunkId));
        return new Guid(hash).ToString();
    }
}
