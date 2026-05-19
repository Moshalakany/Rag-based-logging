using LogRag.Api.Configuration;
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

    public OllamaEmbeddingService(IOptions<EmbeddingOptions> options)
    {
        _options = options.Value;
        _client = new OllamaApiClient(new Uri(_options.BaseUrl), _options.Model);
    }

    public async Task<IReadOnlyList<float[]>> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var orderedResults = new float[texts.Count][];
        var indexed = texts.Select((text, index) => new IndexedText(index, text)).ToArray();
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
                    orderedResults[batch[i].Index] = response.Embeddings[i];
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(jobs);

        if (orderedResults.Any(vector => vector is null))
        {
            throw new InvalidOperationException("One or more embeddings were not produced.");
        }

        return orderedResults!;
    }

    private readonly record struct IndexedText(int Index, string Text);
}
