using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Telemetry;
using Microsoft.Extensions.Options;

namespace LogRag.Api.VectorStore;

public interface IVectorStore
{
    Task EnsureCollectionAsync(string? collectionName, CancellationToken cancellationToken);
    Task DeleteCollectionAsync(string collectionName, CancellationToken cancellationToken);
    Task UpsertAsync(IReadOnlyList<VectorPoint> points, string? collectionName, CancellationToken cancellationToken);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryVector, QueryFilter filter, int limit, string? collectionName, CancellationToken cancellationToken);
    Task DeleteOlderThanAsync(DateTimeOffset cutoffUtc, string? collectionName, CancellationToken cancellationToken);
    /// <summary>
    /// Wait for all pending async operations to be indexed (call after batch ingestion with wait=false).
    /// </summary>
    Task RefreshCollectionAsync(string? collectionName, CancellationToken cancellationToken);
}

public sealed class QdrantVectorStore : IVectorStore
{
    private readonly HttpClient _httpClient;
    private readonly VectorStoreOptions _options;
    private readonly SemaphoreSlim _collectionInitLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _initializedCollections = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(HttpClient httpClient, IOptions<VectorStoreOptions> options, ILogger<QdrantVectorStore> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
    }

    public async Task EnsureCollectionAsync(string? collectionName, CancellationToken cancellationToken)
    {
        var name = ResolveCollectionName(collectionName);
        if (_initializedCollections.ContainsKey(name))
        {
            return;
        }

        await _collectionInitLock.WaitAsync(cancellationToken);
        try
        {
            if (_initializedCollections.ContainsKey(name))
            {
                return;
            }

            var createPayload = new
            {
                vectors = new
                {
                    size = _options.VectorSize,
                    distance = _options.Distance,
                },
            };

            using var request = BuildRequest(HttpMethod.Put, $"/collections/{name}", createPayload);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Conflict)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"Failed to ensure Qdrant collection '{name}'. HTTP {(int)response.StatusCode}: {body}");
            }

            // PERF: Create payload indexes for frequently filtered fields.
            // These are idempotent — safe to call on every startup.
            await CreatePayloadIndexesAsync(name, cancellationToken);
            _initializedCollections.TryAdd(name, true);
        }
        finally
        {
            _collectionInitLock.Release();
        }
    }

    public async Task DeleteCollectionAsync(string collectionName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            return;
        }

        var name = collectionName.Trim();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/collections/{name}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            _initializedCollections.TryRemove(name, out _);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Qdrant collection '{CollectionName}' dropped successfully.", name);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Failed to delete Qdrant collection '{CollectionName}'. HTTP {StatusCode}: {Body}", name, (int)response.StatusCode, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Exception while deleting Qdrant collection '{CollectionName}'.", name);
        }
    }


    /// <summary>
    /// PERF: Create Qdrant payload indexes for fields used in filter queries.
    /// Without these, Qdrant does full payload scans on every filtered search.
    /// </summary>
    private async Task CreatePayloadIndexesAsync(string collectionName, CancellationToken cancellationToken)
    {
        string[] keywordFields = [
            "severity", "service_name", "source_id", "source_type",
            "correlation_id", "module", "method", "log_source", "destination",
            "brn", "billing_account", "denomination_id", "account_id", "ip",
            "trace_id", "log_hash", "linked_ids"
        ];

        foreach (var field in keywordFields)
        {
            try
            {
                var indexPayload = new { field_name = field, field_schema = "keyword" };
                using var req = BuildRequest(HttpMethod.Put, $"/collections/{collectionName}/index", indexPayload);
                using var resp = await _httpClient.SendAsync(req, cancellationToken);
                // 409 Conflict = index already exists — fine
                if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.Conflict)
                {
                    var body = await resp.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Failed to create Qdrant index on '{Field}'. HTTP {StatusCode}: {Body}", field, (int)resp.StatusCode, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create Qdrant index for field {Field}. Filtering may be slow.", field);
            }
        }

        // Datetime index for timestamp range queries
        try
        {
            var tsPayload = new { field_name = "timestamp", field_schema = "datetime" };
            using var req = BuildRequest(HttpMethod.Put, $"/collections/{collectionName}/index", tsPayload);
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Qdrant index for timestamp.");
        }

        // Integer index for status_code range queries
        try
        {
            var statusIndexPayload = new { field_name = "status_code", field_schema = "integer" };
            using var req = BuildRequest(HttpMethod.Put, $"/collections/{collectionName}/index", statusIndexPayload);
            using var resp = await _httpClient.SendAsync(req, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create Qdrant index for status_code.");
        }
    }

    public async Task RefreshCollectionAsync(string? collectionName, CancellationToken cancellationToken)
    {
        var name = ResolveCollectionName(collectionName);
        using var request = BuildRequest(HttpMethod.Get, $"/collections/{name}", null);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Qdrant collection refresh failed. HTTP {(int)response.StatusCode}: {body}");
        }
    }

    public async Task UpsertAsync(IReadOnlyList<VectorPoint> points, string? collectionName, CancellationToken cancellationToken)

    {
        if (points.Count == 0)
        {
            return;
        }

        var payload = new
        {
            points = points.Select(point => new
            {
                id = point.Id,
                vector = point.Vector,
                payload = new Dictionary<string, object?>
                {
                    ["timestamp"] = point.Chunk.TimestampUtc.ToString("O"),
                    ["severity"] = point.Chunk.Severity,
                    ["service_name"] = point.Chunk.ServiceName,
                    ["trace_id"] = point.Chunk.TraceId,
                    ["source_id"] = point.Chunk.SourceId,
                    ["source_type"] = point.Chunk.SourceType,
                    ["message"] = point.Chunk.Text,
                    ["log_hash"] = point.Chunk.LogHash,
                    // Momkn business fields
                    ["correlation_id"] = point.Chunk.CorrelationId,
                    ["module"] = point.Chunk.Module,
                    ["method"] = point.Chunk.Method,
                    ["log_source"] = point.Chunk.LogSource,
                    ["destination"] = point.Chunk.Destination,
                    ["brn"] = point.Chunk.BRN,
                    ["billing_account"] = point.Chunk.BillingAccount,
                    ["denomination_id"] = point.Chunk.DenominationId,
                    ["account_id"] = point.Chunk.AccountId,
                    ["ip"] = point.Chunk.IP,
                    ["status_code"] = point.Chunk.StatusCode,
                    ["extra"] = point.Chunk.Payload,
                    ["linked_ids"] = point.Chunk.Payload.TryGetValue("linked_ids", out var idsStr)
                        ? idsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        : Array.Empty<string>(),
                },
            }),
        };

        var name = ResolveCollectionName(collectionName);
        // PERF: Use wait=false for batch ingestion. Qdrant indexes asynchronously.
        using var _ = LogRagTelemetry.MeasureLatency(LogRagTelemetry.QdrantUpsertLatency);
        LogRagTelemetry.QdrantUpserts.Add(1);
        MetricsSnapshot.RecordQdrantUpsert();
        using var request = BuildRequest(HttpMethod.Put, $"/collections/{name}/points?wait=false", payload);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Qdrant upsert failed. HTTP {(int)response.StatusCode}: {body}");
        }
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(float[] queryVector, QueryFilter filter, int limit, string? collectionName, CancellationToken cancellationToken)
    {
        var name = ResolveCollectionName(collectionName);
        using var _ = LogRagTelemetry.MeasureLatency(LogRagTelemetry.QdrantSearchLatency);
        LogRagTelemetry.QdrantSearches.Add(1);
        MetricsSnapshot.RecordQdrantSearch();
        var filterClause = BuildFilterClause(filter);
        var requestPayload = new Dictionary<string, object?>
        {
            ["vector"] = queryVector,
            ["limit"] = Math.Max(1, limit),
            ["with_payload"] = true,
        };

        if (filterClause is not null)
        {
            requestPayload["filter"] = filterClause;
        }

        using var request = BuildRequest(HttpMethod.Post, $"/collections/{name}/points/search", requestPayload);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException($"Qdrant search failed. HTTP {(int)response.StatusCode}: {body}");
                }

        var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
        if (document is null)
        {
            return [];
        }

        var results = new List<RetrievedChunk>();
        if (!document.RootElement.TryGetProperty("result", out var resultArray) || resultArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var row in resultArray.EnumerateArray())
        {
            var id = row.GetProperty("id").ToString();
            var score = row.TryGetProperty("score", out var scoreElement) ? scoreElement.GetDouble() : 0;
            if (!row.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var timestamp = DateTimeOffset.TryParse(GetString(p, "timestamp"), out var parsed) ? parsed : DateTimeOffset.UtcNow;
            var extra = p.TryGetProperty("extra", out var extraElement) && extraElement.ValueKind == JsonValueKind.Object
                ? extraElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int? statusCode = null;
            if (p.TryGetProperty("status_code", out var scEl) && scEl.ValueKind == JsonValueKind.Number)
            {
                statusCode = scEl.GetInt32();
            }

            results.Add(new RetrievedChunk(
                Id: id,
                Score: score,
                LogHash: GetString(p, "log_hash") ?? "",
                Text: GetString(p, "message") ?? "",
                TimestampUtc: timestamp,
                Severity: GetString(p, "severity") ?? "INFO",
                ServiceName: GetString(p, "service_name") ?? "unknown",
                TraceId: GetString(p, "trace_id") ?? "n/a",
                SourceId: GetString(p, "source_id") ?? "unknown",
                SourceType: GetString(p, "source_type") ?? "unknown",
                Payload: extra,
                CorrelationId: GetString(p, "correlation_id") ?? "n/a",
                Module: GetString(p, "module") ?? "",
                Method: GetString(p, "method") ?? "",
                LogSource: GetString(p, "log_source") ?? "",
                Destination: GetString(p, "destination") ?? "",
                BRN: GetString(p, "brn"),
                BillingAccount: GetString(p, "billing_account"),
                DenominationId: GetString(p, "denomination_id"),
                AccountId: GetString(p, "account_id"),
                IP: GetString(p, "ip"),
                StatusCode: statusCode));
        }

        return results;
    }

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoffUtc, string? collectionName, CancellationToken cancellationToken)
    {
        var name = ResolveCollectionName(collectionName);
        var payload = new
        {
            filter = new
            {
                must = new object[]
                {
                    new
                    {
                        key = "timestamp",
                        range = new
                        {
                            lt = cutoffUtc.ToString("O"),
                        },
                    },
                },
            },
        };

        using var request = BuildRequest(HttpMethod.Post, $"/collections/{name}/points/delete?wait=true", payload);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Qdrant delete failed. HTTP {(int)response.StatusCode}: {body}");
        }
    }

    private string ResolveCollectionName(string? collectionName)
    {
        return string.IsNullOrWhiteSpace(collectionName) ? _options.CollectionName : collectionName;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, object? content)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Add("api-key", _options.ApiKey);
        }

        if (content is not null)
        {
            var json = JsonSerializer.Serialize(content, _serializerOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static string? GetString(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var property) ? property.ToString() : null;
    }

    private static object? BuildFilterClause(QueryFilter filter)
    {
        var must = new List<object>();

        if (!string.IsNullOrWhiteSpace(filter.ServiceName))
        {
            must.Add(new { key = "service_name", match = new { value = filter.ServiceName } });
        }

        if (!string.IsNullOrWhiteSpace(filter.Severity))
        {
            must.Add(new { key = "severity", match = new { value = filter.Severity.ToUpperInvariant() } });
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceType))
        {
            must.Add(new { key = "source_type", match = new { value = filter.SourceType } });
        }

        // Momkn business field filters
        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            must.Add(new { key = "correlation_id", match = new { value = filter.CorrelationId } });
        }

        if (!string.IsNullOrWhiteSpace(filter.Module))
        {
            must.Add(new { key = "module", match = new { value = filter.Module } });
        }

        if (!string.IsNullOrWhiteSpace(filter.Method))
        {
            must.Add(new { key = "method", match = new { value = filter.Method } });
        }

        if (!string.IsNullOrWhiteSpace(filter.BRN))
        {
            must.Add(new { key = "brn", match = new { value = filter.BRN } });
        }

        if (!string.IsNullOrWhiteSpace(filter.DenominationId))
        {
            must.Add(new { key = "denomination_id", match = new { value = filter.DenominationId } });
        }

        if (!string.IsNullOrWhiteSpace(filter.AccountId))
        {
            must.Add(new { key = "account_id", match = new { value = filter.AccountId } });
        }

        if (filter.FromUtc is not null || filter.ToUtc is not null)
        {
            must.Add(new
            {
                key = "timestamp",
                range = new Dictionary<string, string>()
                {
                    ["gte"] = filter.FromUtc?.ToString("O") ?? "",
                    ["lte"] = filter.ToUtc?.ToString("O") ?? "",
                }.Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value)).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            });
        }

        if (filter.LinkedIds is not null && filter.LinkedIds.Count > 0)
        {
            var shouldClauses = filter.LinkedIds.Select(id => new { key = "linked_ids", match = new { value = id } }).ToArray();
            must.Add(new { should = shouldClauses });
        }

        return must.Count == 0 ? null : new { must };
    }
}
