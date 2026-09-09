namespace LogRag.Api.Configuration;

public sealed class MongoDbOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "LogRag";
    public string CollectionName { get; set; } = "ErrorAnalysisSessions";
}

public sealed class ErrorAnalysisOptions
{
    public string StoreType { get; set; } = "MongoDb"; // "MongoDb" or "JsonFile"
    public MongoDbOptions MongoDb { get; set; } = new();
    public string DataDirectory { get; set; } = "data/error-sessions";
    public int MaxRcaConcurrency { get; set; } = 1;
    public int MaxTracesToAnalyze { get; set; } = 0; // 0 = unlimited
    public bool AutoDropCollectionOnStop { get; set; } = true;
    public string RcaSystemPrompt { get; set; } =
        "You are an expert distributed systems site reliability engineer (SRE) and log analyst. " +
        "You will be provided with a chronological sequence of log entries from multiple interconnected services sharing the same Correlation ID. " +
        "One or more errors occurred in this flow. " +
        "Analyze the logs carefully to identify what went wrong, which service failed first (the culprit component), " +
        "the root cause, and how to resolve or prevent it. Be concise, technical, and precise.";
}

