using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogRag.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Embedding;

/// <summary>
/// vLLM embedding service using the OpenAI-compatible /v1/embeddings endpoint.
/// Supports the same interface, caching, and batching as the Ollama service.
/// </summary>
public sealed class VllmEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingOptions _options;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    // PERF: Shared pooled HttpClient for connection reuse.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 8,
        EnableMultipleHttp2Connections = true,
    };

    // PERF: Cache embeddings by SHA256(text) to avoid re-embedding identical chunks.
    private static readonly ConcurrentDictionary<string, float[]> EmbeddingCache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 50_000;

    public VllmEmbeddingService(IOptions<EmbeddingOptions> options)
    {
        _options = options.Value;
        _httpClient = new HttpClient(SharedHandler)
        {
            BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedTextsAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0) return [];

        // Check cache first
        var cacheKeys = texts.Select(ComputeTextHash).ToArray();
        var orderedResults = new float[texts.Count][];
        var uncached = new List<(int Index, string Text, string Key)>();

        for (var i = 0; i < texts.Count; i++)
        {
            if (EmbeddingCache.TryGetValue(cacheKeys[i], out var cached))
                orderedResults[i] = cached;
            else
                uncached.Add((i, texts[i], cacheKeys[i]));
        }

        if (uncached.Count > 0)
        {
            if (EmbeddingCache.Count > MaxCacheSize) TrimCache();

            var indexed = uncached.Select(x => new IndexedText(x.Index, x.Text, x.Key)).ToArray();
            var batches = indexed.Chunk(Math.Max(1, _options.BatchSize)).ToArray();
            var concurrency = Math.Max(1, _options.MaxParallelBatches);

            using var gate = new SemaphoreSlim(concurrency, concurrency);
            var jobs = batches.Select(async batch =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var embeddings = await EmbedBatchAsync(
                        batch.Select(x => x.Text).ToArray(), cancellationToken);

                    if (embeddings.Length != batch.Length)
                        throw new InvalidOperationException(
                            $"vLLM returned {embeddings.Length} embeddings for {batch.Length} inputs");

                    for (var i = 0; i < batch.Length; i++)
                    {
                        orderedResults[batch[i].Index] = embeddings[i];
                        EmbeddingCache.TryAdd(batch[i].Key, embeddings[i]);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(jobs);
        }

        if (orderedResults.Any(v => v is null))
            throw new InvalidOperationException("One or more embeddings were not produced.");

        return orderedResults!;
    }

    private async Task<float[][]> EmbedBatchAsync(
        string[] texts, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _options.Model,
            input = texts,
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync("/v1/embeddings", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"vLLM embeddings request failed. HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(responseBody);

        if (!document.RootElement.TryGetProperty("data", out var dataArray) ||
            dataArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"vLLM embeddings response missing 'data' array. Body: {responseBody[..Math.Min(200, responseBody.Length)]}");
        }

        var results = new List<float[]>(dataArray.GetArrayLength());
        foreach (var item in dataArray.EnumerateArray())
        {
            if (item.TryGetProperty("embedding", out var embeddingArray) &&
                embeddingArray.ValueKind == JsonValueKind.Array)
            {
                var embedding = new float[embeddingArray.GetArrayLength()];
                var idx = 0;
                foreach (var val in embeddingArray.EnumerateArray())
                {
                    embedding[idx++] = val.GetSingle();
                }
                results.Add(embedding);
            }
        }

        return results.ToArray();
    }

    private static string ComputeTextHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void TrimCache()
    {
        var keys = EmbeddingCache.Keys.Take(EmbeddingCache.Count / 2).ToArray();
        foreach (var key in keys)
            EmbeddingCache.TryRemove(key, out _);
    }

    private readonly record struct IndexedText(int Index, string Text, string Key);
}
