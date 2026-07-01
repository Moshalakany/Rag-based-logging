using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LogRag.Api.Telemetry;

/// <summary>
/// Central observability: OpenTelemetry-compatible tracing (ActivitySource) and
/// metrics (Meter). No external packages required — uses built-in .NET APIs.
/// Metrics are logged to console every 30 seconds by the reporter.
/// </summary>
public static class LogRagTelemetry
{
    public const string ServiceName = "LogRag.Api";

    // ── Tracing ──────────────────────────────────────────────────────────
    public static readonly ActivitySource ActivitySource = new(ServiceName, "1.0.0");

    // ── Meter ────────────────────────────────────────────────────────────
    private static readonly Meter Meter = new(ServiceName, "1.0.0");

    // ── Ingestion counters ───────────────────────────────────────────────
    public static readonly Counter<long> IngestionRuns = Meter.CreateCounter<long>(
        "lograg.ingestion.runs",
        description: "Total number of ingestion runs completed");

    public static readonly Counter<long> IngestionEntriesRead = Meter.CreateCounter<long>(
        "lograg.ingestion.entries.read",
        description: "Total raw log entries read from all sources");

    public static readonly Counter<long> IngestionEntriesFiltered = Meter.CreateCounter<long>(
        "lograg.ingestion.entries.filtered",
        description: "Entries dropped by noise filter");

    public static readonly Counter<long> IngestionChunksCreated = Meter.CreateCounter<long>(
        "lograg.ingestion.chunks.created",
        description: "Total chunks created from normalized entries");

    public static readonly Counter<long> IngestionVectorsUpserted = Meter.CreateCounter<long>(
        "lograg.ingestion.vectors.upserted",
        description: "Total vectors upserted to Qdrant");

    public static readonly Histogram<double> IngestionDuration = Meter.CreateHistogram<double>(
        "lograg.ingestion.duration",
        unit: "s",
        description: "Total duration of ingestion runs in seconds");

    // ── Embedding metrics ────────────────────────────────────────────────
    public static readonly Counter<long> EmbedRequests = Meter.CreateCounter<long>(
        "lograg.embed.requests",
        description: "Total embedding API requests");

    public static readonly Counter<long> EmbedCacheHits = Meter.CreateCounter<long>(
        "lograg.embed.cache_hits",
        description: "Embeddings served from cache");

    public static readonly Histogram<double> EmbedLatency = Meter.CreateHistogram<double>(
        "lograg.embed.latency",
        unit: "s",
        description: "Ollama embedding API latency in seconds");

    // ── Qdrant metrics ───────────────────────────────────────────────────
    public static readonly Counter<long> QdrantUpserts = Meter.CreateCounter<long>(
        "lograg.qdrant.upserts",
        description: "Total Qdrant upsert calls");

    public static readonly Counter<long> QdrantSearches = Meter.CreateCounter<long>(
        "lograg.qdrant.searches",
        description: "Total Qdrant search calls");

    public static readonly Histogram<double> QdrantUpsertLatency = Meter.CreateHistogram<double>(
        "lograg.qdrant.upsert.latency",
        unit: "s",
        description: "Qdrant upsert latency in seconds");

    public static readonly Histogram<double> QdrantSearchLatency = Meter.CreateHistogram<double>(
        "lograg.qdrant.search.latency",
        unit: "s",
        description: "Qdrant search latency in seconds");

    // ── LLM metrics ──────────────────────────────────────────────────────
    public static readonly Counter<long> LlmRequests = Meter.CreateCounter<long>(
        "lograg.llm.requests",
        description: "Total LLM chat requests");

    public static readonly Histogram<double> LlmLatency = Meter.CreateHistogram<double>(
        "lograg.llm.latency",
        unit: "s",
        description: "LLM generation latency in seconds");

    // ── Chat metrics ─────────────────────────────────────────────────────
    public static readonly Counter<long> ChatRequests = Meter.CreateCounter<long>(
        "lograg.chat.requests",
        description: "Total chat requests");

    public static readonly Counter<long> ChatSmallTalkSkips = Meter.CreateCounter<long>(
        "lograg.chat.smalltalk_skips",
        description: "Chat requests that skipped RAG (small talk)");

    /// <summary>
    /// Start a timed span. Returns an IDisposable that records the duration
    /// in the given histogram when disposed.
    /// </summary>
    public static IDisposable MeasureLatency(Histogram<double> histogram)
    {
        return new LatencyTracker(histogram, Stopwatch.StartNew());
    }

    private sealed class LatencyTracker : IDisposable
    {
        private readonly Histogram<double> _histogram;
        private readonly Stopwatch _sw;
        public LatencyTracker(Histogram<double> histogram, Stopwatch sw)
        {
            _histogram = histogram;
            _sw = sw;
        }
        public void Dispose()
        {
            _sw.Stop();
            _histogram.Record(_sw.Elapsed.TotalSeconds);
        }
    }
}
