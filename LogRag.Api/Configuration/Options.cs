namespace LogRag.Api.Configuration;

public sealed class LogSourcesOptions
{
    public List<LogSourceDescriptorOptions> Sources { get; init; } = [];
}

public sealed class LogSourceDescriptorOptions
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "file";    // file | elasticsearch | sql | http
    public string Path { get; init; } = "";         // file path (file type)
    public string SourceType { get; init; } = "app";

    // ── Elasticsearch ──
    public string ElasticsearchUrl { get; init; } = "";
    public string ElasticsearchIndex { get; init; } = "";
    public string ElasticsearchQuery { get; init; } = "";  // JSON query body
    public string ElasticsearchApiKey { get; init; } = "";
    public int ElasticsearchScrollSize { get; init; } = 1000;

    // ── SQL ──
    public string SqlConnectionString { get; init; } = "";
    public string SqlQuery { get; init; } = "";            // e.g. "SELECT * FROM logs WHERE ..."
    public string SqlTimestampColumn { get; init; } = "timestamp";
    public string SqlCursorColumn { get; init; } = "id";   // for checkpointing

    // ── HTTP ──
    public string HttpUrl { get; init; } = "";
    public string HttpMethod { get; init; } = "GET";
    public string HttpHeaders { get; init; } = "";         // JSON object string
    public string HttpBody { get; init; } = "";            // request body template
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
    public int ProcessingBatchSize { get; init; } = 1000;
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

    /// <summary>
    /// If > 0, scheduled ingestion only pulls logs from the last N hours
    /// instead of from the last checkpoint. 0 = disabled (use checkpoint).
    /// </summary>
    public int IngestionTimeWindowHours { get; init; } = 0;
}

public sealed class EmbeddingOptions
{
    public string Provider { get; init; } = "ollama";   // "ollama" | "vllm"
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "nomic-embed-text";
    public int BatchSize { get; init; } = 64;
    public int MaxParallelBatches { get; init; } = 2;
    public string? ApiKey { get; init; }                  // vLLM API key (if needed)
}

public sealed class LlmOptions
{
    public string Provider { get; init; } = "ollama";     // "ollama" | "vllm"
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "llama3";
    public int MaxHistoryMessages { get; init; } = 8;
    public string? ApiKey { get; init; }                  // vLLM API key (if needed)
    public string SystemPrompt { get; init; } =
        "You are a log analyst assistant. Answer only from provided log context when it is relevant to the question. " +
        "If the user asks a simple question about yourself (your name, greetings, capabilities), answer naturally without citing logs. " +
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
