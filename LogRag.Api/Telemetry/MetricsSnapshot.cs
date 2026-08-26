using System.Diagnostics.Metrics;
using System.Text;

namespace LogRag.Api.Telemetry;

/// <summary>
/// Lightweight metrics snapshot for console logging and the /metrics endpoint.
/// Reads cumulative counter values from the Meter.
/// </summary>
public sealed class MetricsSnapshot
{
    private static readonly Meter Meter = new(LogRagTelemetry.ServiceName);
    private static long _lastIngestionRuns, _lastEntriesRead, _lastFiltered, _lastChunks, _lastVectors;
    private static long _lastEmbedReqs, _lastEmbedHits;
    private static long _lastQdrantUpserts, _lastQdrantSearches;
    private static long _lastLlmReqs, _lastChatReqs, _lastSmallTalk;

    public static string GetSnapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("═══ LogRag Metrics ═══");
        sb.AppendLine($"  Ingestion runs:       {_lastIngestionRuns}");
        sb.AppendLine($"  Entries read:         {_lastEntriesRead}");
        sb.AppendLine($"  Entries filtered:     {_lastFiltered}");
        sb.AppendLine($"  Chunks created:       {_lastChunks}");
        sb.AppendLine($"  Vectors upserted:     {_lastVectors}");
        sb.AppendLine($"  Embed requests:       {_lastEmbedReqs}  (cache hits: {_lastEmbedHits})");
        sb.AppendLine($"  Qdrant upserts:       {_lastQdrantUpserts}");
        sb.AppendLine($"  Qdrant searches:      {_lastQdrantSearches}");
        sb.AppendLine($"  LLM requests:         {_lastLlmReqs}");
        sb.AppendLine($"  Chat requests:        {_lastChatReqs}  (small-talk skips: {_lastSmallTalk})");
        return sb.ToString();
    }

    // Called by the actual instrumented code to update tracked values
    public static void RecordIngestion(int entriesRead, int filtered, int chunks, int vectors)
    {
        Interlocked.Increment(ref _lastIngestionRuns);
        Interlocked.Add(ref _lastEntriesRead, entriesRead);
        Interlocked.Add(ref _lastFiltered, filtered);
        Interlocked.Add(ref _lastChunks, chunks);
        Interlocked.Add(ref _lastVectors, vectors);
    }

    public static void RecordEmbed(bool cacheHit)
    {
        Interlocked.Increment(ref _lastEmbedReqs);
        if (cacheHit) Interlocked.Increment(ref _lastEmbedHits);
    }

    public static void RecordQdrantUpsert() => Interlocked.Increment(ref _lastQdrantUpserts);
    public static void RecordQdrantSearch() => Interlocked.Increment(ref _lastQdrantSearches);
    public static void RecordLlmRequest() => Interlocked.Increment(ref _lastLlmReqs);
    public static void RecordChat(bool smallTalk)
    {
        Interlocked.Increment(ref _lastChatReqs);
        if (smallTalk) Interlocked.Increment(ref _lastSmallTalk);
    }
}
