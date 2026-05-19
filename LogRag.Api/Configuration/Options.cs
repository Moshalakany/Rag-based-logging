namespace LogRag.Api.Configuration;

public sealed class LogSourcesOptions
{
    public List<LogSourceDescriptorOptions> Sources { get; init; } = [];
}

public sealed class LogSourceDescriptorOptions
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "file";
    public string Path { get; init; } = "";
    public string SourceType { get; init; } = "app";
}

public sealed class ParserOptions
{
    public List<RegexRuleOptions> RegexRules { get; init; } = [];
}

public sealed class RegexRuleOptions
{
    public string Name { get; init; } = "";
    public string Pattern { get; init; } = "";
}

public sealed class ChunkingOptions
{
    public int ChunkSizeTokens { get; init; } = 512;
    public int OverlapTokens { get; init; } = 64;
}

public sealed class IngestionOptions
{
    public int ProcessingBatchSize { get; init; } = 256;
    public bool EnableNoiseFiltering { get; init; } = true;
    public List<string> DropRawPatterns { get; init; } =
    [
        @"\{""PayloadSenderV2""\}\s+Cancellation requested",
        @"Elastic\.Apm\.Metrics\.MetricSet",
    ];
    public bool EnableSourceCheckpoints { get; init; } = true;
    public string CheckpointFilePath { get; init; } = @"data\ingestion-checkpoints.json";
}

public sealed class SchedulerOptions
{
    public bool EnableDailyBatch { get; init; } = true;
    public bool EnableStreaming { get; init; } = true;
    public bool RunBatchOnStartup { get; init; } = true;
    public int DailyBatchIntervalHours { get; init; } = 24;
    public int StreamingPollSeconds { get; init; } = 15;
}

public sealed class EmbeddingOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "nomic-embed-text";
    public int BatchSize { get; init; } = 16;
    public int MaxParallelBatches { get; init; } = 4;
}

public sealed class LlmOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "llama3";
    public int MaxHistoryMessages { get; init; } = 8;
    public string SystemPrompt { get; init; } =
        "You are a log analyst assistant. Answer only from provided log context. " +
        "Quote relevant entries with timestamps. Use natural language. " +
        "If context is empty or irrelevant, clearly say no matching logs were found.";
}

public sealed class VectorStoreOptions
{
    public string BaseUrl { get; init; } = "http://localhost:6333";
    public string? ApiKey { get; init; }
    public string CollectionName { get; init; } = "log_chunks";
    public int VectorSize { get; init; } = 768;
    public string Distance { get; init; } = "Cosine";
    public int RetentionDays { get; init; } = 30;
}

public sealed class RetrievalOptions
{
    public int DefaultTopK { get; init; } = 8;
    public bool EnableHeuristicReranker { get; init; } = true;
}
