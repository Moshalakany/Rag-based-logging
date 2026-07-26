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
    IReadOnlyDictionary<string, string> Payload,
    // Momkn business fields
    string CorrelationId = "n/a",
    string Module = "",
    string Method = "",
    string LogSource = "",
    string Destination = "",
    string? BRN = null,
    string? BillingAccount = null,
    string? DenominationId = null,
    string? AccountId = null,
    string? IP = null,
    int? StatusCode = null);

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
    IReadOnlyDictionary<string, string> Payload,
    // Momkn business fields
    string CorrelationId = "n/a",
    string Module = "",
    string Method = "",
    string LogSource = "",
    string Destination = "",
    string? BRN = null,
    string? BillingAccount = null,
    string? DenominationId = null,
    string? AccountId = null,
    string? IP = null,
    int? StatusCode = null);

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

    // Momkn-specific filters
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("module")]
    public string? Module { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("brn")]
    public string? BRN { get; init; }

    [JsonPropertyName("denomination_id")]
    public string? DenominationId { get; init; }

    [JsonPropertyName("account_id")]
    public string? AccountId { get; init; }
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
    IReadOnlyDictionary<string, string> Payload,
    // Momkn business fields
    string CorrelationId = "n/a",
    string Module = "",
    string Method = "",
    string LogSource = "",
    string Destination = "",
    string? BRN = null,
    string? BillingAccount = null,
    string? DenominationId = null,
    string? AccountId = null,
    string? IP = null,
    int? StatusCode = null);

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
    [property: JsonPropertyName("completed_at_utc")] DateTimeOffset CompletedAtUtc);
