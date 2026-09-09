namespace LogRag.Api.Domain;

public static class ErrorAnalysisSessionStatus
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Stopped = "Stopped";
    public const string Failed = "Failed";
}

public static class ErrorAnalysisProgressStage
{
    public const string Initializing = "Initializing";
    public const string Scanning = "Scanning";
    public const string Correlating = "Correlating";
    public const string Vectorizing = "Vectorizing";
    public const string Analyzing = "Analyzing";
    public const string Completed = "Completed";
    public const string Stopped = "Stopped";
    public const string Failed = "Failed";
}

public sealed record TraceLogItem(
    DateTimeOffset TimestampUtc,
    string Severity,
    string ServiceName,
    string SourceId,
    string CorrelationId,
    string Message,
    int? StatusCode,
    string RawText);

public sealed record TraceRcaResult(
    string Summary,
    string CulpritComponent,
    string RootCause,
    string Impact,
    string SuggestedFix,
    DateTimeOffset CompletedAtUtc);

public sealed class ErrorCorrelationTrace
{
    public string CorrelationId { get; set; } = "";
    public List<string> ServicesInvolved { get; set; } = [];
    public DateTimeOffset StartTimeUtc { get; set; }
    public DateTimeOffset EndTimeUtc { get; set; }
    public int ErrorCount { get; set; }
    public List<TraceLogItem> Logs { get; set; } = [];
    public TraceRcaResult? RcaResult { get; set; }
}

public sealed class ErrorAnalysisSession
{
    public string SessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? FromUtc { get; set; }
    public DateTimeOffset? ToUtc { get; set; }
    public string Status { get; set; } = ErrorAnalysisSessionStatus.Running;
    public string ProgressStage { get; set; } = ErrorAnalysisProgressStage.Initializing;
    public string ProgressMessage { get; set; } = "";
    public int PercentComplete { get; set; }
    public string CollectionName { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int TotalRawScanned { get; set; }
    public int ErrorsFound { get; set; }
    public int CorrelatedTracesCount { get; set; }
    public int VectorsUpserted { get; set; }
    public int RcaCompletedCount { get; set; }
    public string? ErrorMessage { get; set; }
    public List<ErrorCorrelationTrace> Traces { get; set; } = [];
}

public sealed record ErrorAnalysisSessionSummary(
    string SessionId,
    string Name,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    string Status,
    string ProgressStage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int ErrorsFound,
    int CorrelatedTracesCount,
    int RcaCompletedCount,
    int PercentComplete = 0);

public sealed record CreateErrorAnalysisRequest(
    string? Name,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int? MaxTracesToAnalyze);

public sealed record ErrorAnalysisProgressEvent(
    string SessionId,
    string Stage,
    string Message,
    int TotalRawScanned,
    int ErrorsFound,
    int CorrelatedTracesCount,
    int VectorsUpserted,
    int RcaCompletedCount,
    int RcaTotalCount,
    int PercentComplete,
    DateTimeOffset TimestampUtc);
