using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using Microsoft.Extensions.Logging;

namespace LogRag.Api.Sources;

/// <summary>
/// Reads log entries from an Elasticsearch index using the scroll API.
/// Supports checkpointing via @timestamp to resume from the last ingested point.
/// </summary>
public sealed class ElasticsearchLogSource : ILogSource
{
    private readonly LogSourceDescriptorOptions _descriptor;
    private readonly ILogSourceCheckpointStore _checkpointStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<ElasticsearchLogSource> _logger;
    private readonly string _checkpointKey;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public ElasticsearchLogSource(
        LogSourceDescriptorOptions descriptor,
        ILogSourceCheckpointStore checkpointStore,
        HttpClient httpClient,
        ILogger<ElasticsearchLogSource> logger)
    {
        _descriptor = descriptor;
        _checkpointStore = checkpointStore;
        _httpClient = httpClient;
        _logger = logger;
        _checkpointKey = $"es|{descriptor.Id}|{descriptor.ElasticsearchIndex}";

        // Configure base URL
        if (!string.IsNullOrWhiteSpace(descriptor.ElasticsearchUrl))
        {
            _httpClient.BaseAddress = new Uri(descriptor.ElasticsearchUrl.TrimEnd('/'));
        }

        // Set API key if configured
        if (!string.IsNullOrWhiteSpace(descriptor.ElasticsearchApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "ApiKey", descriptor.ElasticsearchApiKey);
        }
    }

    public string Id => _descriptor.Id;
    public string SourceType => _descriptor.SourceType;

    public async IAsyncEnumerable<RawLogEntry> ReadAsync(IngestionTimeWindow? timeWindow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = timeWindow; // Time filter pushed to ES query via BuildScrollQuery
        if (string.IsNullOrWhiteSpace(_descriptor.ElasticsearchUrl))
        {
            _logger.LogWarning("Elasticsearch URL is not configured for source {Id}", _descriptor.Id);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(_descriptor.ElasticsearchIndex))
        {
            _logger.LogWarning("Elasticsearch index is not configured for source {Id}", _descriptor.Id);
            yield break;
        }

        // Get last checkpoint (stored as Unix milliseconds)
        var lastTimestampMs = _checkpointStore.GetOffset(_checkpointKey);
        long maxTimestampMs = lastTimestampMs;

        // Collect results outside of try-catch so yield is allowed
        var collectedEntries = new List<RawLogEntry>();

        // Build the scroll query
        var queryBody = BuildScrollQuery(lastTimestampMs, timeWindow);
        var scrollTimeout = "2m";

        // Step 1: Initiate scroll
        var scrollPath = $"/{_descriptor.ElasticsearchIndex}/_search?scroll={scrollTimeout}";
        var initPayload = new
        {
            size = Math.Max(1, _descriptor.ElasticsearchScrollSize),
            query = queryBody,
            sort = new[] { new { _timestamp = new { order = "asc" } } }
        };

        string? scrollId;
        try
        {
            var initJson = JsonSerializer.Serialize(initPayload, _jsonOptions);
            using var initResponse = await _httpClient.PostAsync(
                scrollPath,
                new StringContent(initJson, Encoding.UTF8, "application/json"),
                cancellationToken);

            if (!initResponse.IsSuccessStatusCode)
            {
                var errorBody = await initResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Elasticsearch scroll init failed for source {Id}. HTTP {Status}: {Body}",
                    _descriptor.Id, (int)initResponse.StatusCode, errorBody);
                yield break;
            }

            var initResult = await initResponse.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            scrollId = initResult?.RootElement.GetProperty("_scroll_id").GetString();

            if (initResult is not null)
            {
                foreach (var entry in ExtractHits(initResult.RootElement))
                {
                    maxTimestampMs = Math.Max(maxTimestampMs, entry.timestampMs);
                    collectedEntries.Add(entry.rawEntry);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Elasticsearch scroll init failed for source {Id}", _descriptor.Id);
            yield break;
        }

        // Step 2: Continue scrolling
        while (!string.IsNullOrWhiteSpace(scrollId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var scrollPayload = new
                {
                    scroll = scrollTimeout,
                    scroll_id = scrollId
                };

                var scrollJson = JsonSerializer.Serialize(scrollPayload, _jsonOptions);
                using var scrollResponse = await _httpClient.PostAsync(
                    "/_search/scroll",
                    new StringContent(scrollJson, Encoding.UTF8, "application/json"),
                    cancellationToken);

                if (!scrollResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "Elasticsearch scroll fetch failed for source {Id}. HTTP {Status}",
                        _descriptor.Id, (int)scrollResponse.StatusCode);
                    break;
                }

                var scrollResult = await scrollResponse.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
                scrollId = scrollResult?.RootElement.GetProperty("_scroll_id").GetString();

                if (scrollResult is null) break;

                var hasHits = false;
                foreach (var entry in ExtractHits(scrollResult.RootElement))
                {
                    hasHits = true;
                    maxTimestampMs = Math.Max(maxTimestampMs, entry.timestampMs);
                    collectedEntries.Add(entry.rawEntry);
                }

                if (!hasHits) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Elasticsearch scroll fetch failed for source {Id}", _descriptor.Id);
                break;
            }
        }

        // Step 3: Clear scroll and save checkpoint
        if (!string.IsNullOrWhiteSpace(scrollId))
        {
            try
            {
                var clearPayload = new { scroll_id = new[] { scrollId } };
                var clearJson = JsonSerializer.Serialize(clearPayload, _jsonOptions);
                using var clearResponse = await _httpClient.DeleteAsync(
                    "/_search/scroll",
                    cancellationToken);
                // Best-effort; don't fail if this errors
            }
            catch
            {
                // Non-critical cleanup
            }
        }

        _checkpointStore.SaveOffset(_checkpointKey, maxTimestampMs);

        // Yield all collected entries
        foreach (var entry in collectedEntries)
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Build the Elasticsearch query body. Uses the configured query if provided,
    /// otherwise falls back to match_all with a @timestamp range filter.
    /// </summary>
    private object BuildScrollQuery(long lastTimestampMs, IngestionTimeWindow? timeWindow)
    {
        var mustClauses = new List<object>();

        // Checkpoint-based timestamp filter
        if (lastTimestampMs > 0)
        {
            mustClauses.Add(new
            {
                range = new
                {
                    @timestamp = new { gt = lastTimestampMs }
                }
            });
        }

        // PERF: Time-window filter pushed to ES for server-side filtering
        if (timeWindow is { IsEmpty: false })
        {
            var rangeConstraints = new Dictionary<string, object>();
            if (timeWindow.FromUtc is not null)
                rangeConstraints["gte"] = timeWindow.FromUtc.Value.ToString("O");
            if (timeWindow.ToUtc is not null)
                rangeConstraints["lte"] = timeWindow.ToUtc.Value.ToString("O");
            if (rangeConstraints.Count > 0)
            {
                mustClauses.Add(new
                {
                    range = new
                    {
                        @timestamp = rangeConstraints
                    }
                });
            }
        }

        // Custom user query or match_all
        if (!string.IsNullOrWhiteSpace(_descriptor.ElasticsearchQuery))
        {
            try
            {
                var customQuery = JsonSerializer.Deserialize<JsonElement>(_descriptor.ElasticsearchQuery);
                mustClauses.Add(customQuery);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Invalid Elasticsearch query JSON for source {Id}. Using match_all.", _descriptor.Id);
                mustClauses.Add(new { match_all = new { } });
            }
        }
        else
        {
            mustClauses.Add(new { match_all = new { } });
        }

        return mustClauses.Count == 1
            ? mustClauses[0]
            : new { @bool = new { must = mustClauses.ToArray() } };
    }

    private IEnumerable<(RawLogEntry rawEntry, long timestampMs)> ExtractHits(JsonElement root)
    {
        if (!root.TryGetProperty("hits", out var hitsElement) ||
            hitsElement.ValueKind != JsonValueKind.Object ||
            !hitsElement.TryGetProperty("hits", out var hitsArray) ||
            hitsArray.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var hit in hitsArray.EnumerateArray())
        {
            if (!hit.TryGetProperty("_source", out var source) ||
                source.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = source.GetRawText();
            var hitId = hit.TryGetProperty("_id", out var idEl) ? idEl.GetString() ?? "" : "";
            var score = hit.TryGetProperty("_score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number
                ? scoreEl.GetDouble().ToString(CultureInfo.InvariantCulture)
                : "0";

            // Extract @timestamp as Unix milliseconds for checkpointing
            var timestampMs = ExtractTimestampMs(source);
            var attributes = new Dictionary<string, string>
            {
                ["es_index"] = _descriptor.ElasticsearchIndex,
                ["es_id"] = hitId,
                ["es_score"] = score,
            };

            yield return (
                new RawLogEntry(Id, SourceType, text, DateTimeOffset.UtcNow, attributes),
                timestampMs);
        }
    }

    private static long ExtractTimestampMs(JsonElement source)
    {
        // Try @timestamp field
        if (source.TryGetProperty("@timestamp", out var tsElement))
        {
            var tsStr = tsElement.ValueKind == JsonValueKind.String
                ? tsElement.GetString()
                : tsElement.GetRawText();

            if (!string.IsNullOrWhiteSpace(tsStr) &&
                DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUnixTimeMilliseconds();
            }
        }

        // Try timestamp field
        if (source.TryGetProperty("timestamp", out var ts2Element))
        {
            var tsStr = ts2Element.ValueKind == JsonValueKind.String
                ? ts2Element.GetString()
                : ts2Element.GetRawText();

            if (!string.IsNullOrWhiteSpace(tsStr) &&
                DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUnixTimeMilliseconds();
            }
        }

        return 0;
    }
}
