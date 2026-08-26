using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LogRag.Api.Configuration;
using LogRag.Api.Telemetry;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace LogRag.Api.Embedding;

public interface IEmbeddingService
{
    Task<IReadOnlyList<float[]>> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly EmbeddingOptions _options;
    private readonly OllamaApiClient _client;

    // PERF: Static pooled HttpClient for Ollama API calls. Connection reuse avoids
    // TCP/TLS handshake overhead on every batch. MaxConnectionsPerServer allows
    // concurrent batched requests to use separate connections.
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 8,
        EnableMultipleHttp2Connections = true,
    };

    // PERF: Cache embeddings by SHA256(text) to avoid re-embedding identical chunks
    // across ingestion runs. The cache is bounded to prevent unbounded memory growth.
    private static readonly ConcurrentDictionary<string, float[]> EmbeddingCache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 50_000; // ~50K * 768 * 4 bytes ≈ 150MB max

    public OllamaEmbeddingService(IOptions<EmbeddingOptions> options)
    {
        _options = options.Value;
        // Use shared pooled HttpClient for connection reuse across all embedding calls.
        var httpClient = new HttpClient(SharedHandler)
        {
            BaseAddress = new Uri(_options.BaseUrl),
            Timeout = TimeSpan.FromMinutes(5),
        };
        _client = new OllamaApiClient(httpClient, _options.Model);
    }

    public async Task<IReadOnlyList<float[]>> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        // PERF: Check cache first. For repeated ingestion runs, many chunks are identical.
        var cacheKeys = texts.Select(ComputeTextHash).ToArray();
        var orderedResults = new float[texts.Count][];
        var uncached = new List<(int Index, string Text, string Key)>();

        for (var i = 0; i < texts.Count; i++)
        {
            if (EmbeddingCache.TryGetValue(cacheKeys[i], out var cached))
            {
                orderedResults[i] = cached;
            }
            else
            {
                uncached.Add((i, texts[i], cacheKeys[i]));
            }
        }

        if (uncached.Count > 0)
        {
            using var _ = LogRagTelemetry.MeasureLatency(LogRagTelemetry.EmbedLatency);
            LogRagTelemetry.EmbedRequests.Add(1);
            MetricsSnapshot.RecordEmbed(cacheHit: false);

            // Evict oldest entries if cache exceeds limit
            if (EmbeddingCache.Count > MaxCacheSize)
            {
                TrimCache();
            }

            var indexed = uncached.Select(x => new IndexedText(x.Index, x.Text, x.Key)).ToArray();
            var batches = indexed.Chunk(Math.Max(1, _options.BatchSize)).ToArray();
            var concurrency = Math.Max(1, _options.MaxParallelBatches);

            using var gate = new SemaphoreSlim(concurrency, concurrency);
            var jobs = batches.Select(async batch =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    var response = await _client.EmbedAsync(
                        new EmbedRequest
                        {
                            Model = _options.Model,
                            Input = batch.Select(x => x.Text).ToList(),
                        },
                        cancellationToken);

                    if (response.Embeddings.Count != batch.Length)
                    {
                        throw new InvalidOperationException("Embedding response count does not match request count.");
                    }

                    for (var i = 0; i < batch.Length; i++)
                    {
                        var embedding = response.Embeddings[i];
                        orderedResults[batch[i].Index] = embedding;
                        EmbeddingCache.TryAdd(batch[i].Key, embedding);
                    }
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(jobs);
        }

        if (orderedResults.Any(vector => vector is null))
        {
            throw new InvalidOperationException("One or more embeddings were not produced.");
        }

        return orderedResults!;
    }

    private static string ComputeTextHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void TrimCache()
    {
        // Simple eviction: clear half the cache when limit is exceeded.
        // For a more sophisticated approach, use LRU via ConcurrentLru from System.Threading.Channels.
        var keys = EmbeddingCache.Keys.Take(EmbeddingCache.Count / 2).ToArray();
        foreach (var key in keys)
        {
            EmbeddingCache.TryRemove(key, out _);
        }
    }

    private readonly record struct IndexedText(int Index, string Text, string Key);
}
