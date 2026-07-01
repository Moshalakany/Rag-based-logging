using System.Text.Json.Serialization;

namespace LogRag.Api.Domain;

public sealed record RawLogEntry(
    string SourceId,
    string SourceType,
    string RawText,
    DateTimeOffset IngestedAtUtc,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record ParsedLogEntry(
    RawLogEntry Raw,
    IReadOnlyDictionary<string, string> Fields,
    string Message);

public sealed record NormalizedLogEntry(
    string SourceId,
    string SourceType,
    DateTimeOffset TimestampUtc,
    string Severity,
    string ServiceName,
    string TraceId,
    string Message,
    string LogHash,
    IReadOnlyDictionary<string, string> Payload);

public sealed record LogChunk(
    string ChunkId,
    string LogHash,
    string Text,
    DateTimeOffset TimestampUtc,
    string Severity,
    string ServiceName,
    string TraceId,
    string SourceId,
    string SourceType,
    IReadOnlyDictionary<string, string> Payload);

public sealed record VectorPoint(
    string Id,
    float[] Vector,
    LogChunk Chunk);

public sealed class QueryFilter
{
    [JsonPropertyName("service_name")]
    public string? ServiceName { get; init; }

    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("from_utc")]
    public DateTimeOffset? FromUtc { get; init; }

    [JsonPropertyName("to_utc")]
    public DateTimeOffset? ToUtc { get; init; }

    [JsonPropertyName("linked_ids")]
    public IReadOnlyList<string>? LinkedIds { get; init; }
}

public sealed record RetrievedChunk(
    string Id,
    double Score,
    string LogHash,
    string Text,
    DateTimeOffset TimestampUtc,
    string Severity,
    string ServiceName,
    string TraceId,
    string SourceId,
    string SourceType,
    IReadOnlyDictionary<string, string> Payload);

public sealed class ChatRequestDto
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("question")]
    public string Question { get; init; } = "";

    [JsonPropertyName("top_k")]
    public int? TopK { get; init; }

    [JsonPropertyName("filter")]
    public QueryFilter? Filter { get; init; }

    /// <summary>
    /// Optional: search a specific collection instead of the default.
    /// Used to query time-windowed collections like log_chunks_20260620.
    /// </summary>
    [JsonPropertyName("collection_name")]
    public string? CollectionName { get; init; }
}

public sealed record ChatStreamEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("metadata")] object? Metadata);

public sealed record SessionMessage(
    string Role,
    string Content,
    DateTimeOffset TimestampUtc);

public sealed record IngestionRunResult(
    [property: JsonPropertyName("raw_logs_read")] int RawLogsRead,
    [property: JsonPropertyName("chunks_created")] int ChunksCreated,
    [property: JsonPropertyName("vectors_upserted")] int VectorsUpserted,
    [property: JsonPropertyName("completed_at_utc")] DateTimeOffset CompletedAtUtc,
    [property: JsonPropertyName("collection_name")] string? CollectionName = null,
    [property: JsonPropertyName("window_from_utc")] string? WindowFromUtc = null,
    [property: JsonPropertyName("window_to_utc")] string? WindowToUtc = null);

/// <summary>
/// Time window for scoped ingestion. Both bounds are optional.
/// If both are null (IsEmpty), behavior falls back to checkpoint-based full ingestion.
/// </summary>
public sealed record IngestionTimeWindow(
    [property: JsonPropertyName("from_utc")] DateTimeOffset? FromUtc,
    [property: JsonPropertyName("to_utc")] DateTimeOffset? ToUtc)
{
    public bool IsEmpty => FromUtc is null && ToUtc is null;

    /// <summary>
    /// Returns true if the given timestamp falls within the window.
    /// An absent bound means unbounded on that side.
    /// </summary>
    public bool Contains(DateTimeOffset timestamp)
    {
        if (FromUtc is not null && timestamp < FromUtc.Value) return false;
        if (ToUtc is not null && timestamp > ToUtc.Value) return false;
        return true;
    }
}

/// <summary>
/// Request body for POST /ingest with optional time window.
/// </summary>
public sealed class IngestRequestDto
{
    [JsonPropertyName("from_utc")]
    public DateTimeOffset? FromUtc { get; init; }

    [JsonPropertyName("to_utc")]
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>
    /// Optional: target collection name. If omitted and time window is set,
    /// auto-generates log_chunks_{yyyyMMdd}. If omitted and no time window,
    /// uses the default collection from config.
    /// </summary>
    [JsonPropertyName("collection_name")]
    public string? CollectionName { get; init; }
}
