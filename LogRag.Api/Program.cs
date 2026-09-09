using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Conversation;
using LogRag.Api.Domain;
using LogRag.Api.Embedding;
using LogRag.Api.Ingestion;
using LogRag.Api.Llm;
using LogRag.Api.Query;
using LogRag.Api.Sources;
using LogRag.Api.Telemetry;
using LogRag.Api.VectorStore;


var builder = WebApplication.CreateBuilder(args);















builder.Services.Configure<LogSourcesOptions>(builder.Configuration.GetSection("LogSources"));
builder.Services.Configure<ParserOptions>(builder.Configuration.GetSection("Parser"));
builder.Services.Configure<ChunkingOptions>(builder.Configuration.GetSection("Chunking"));
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));
builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection("Scheduler"));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection("Embedding"));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<VectorStoreOptions>(builder.Configuration.GetSection("VectorStore"));
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection("Retrieval"));
builder.Services.Configure<ErrorAnalysisOptions>(builder.Configuration.GetSection("ErrorAnalysis"));

builder.Services.AddHttpClient(); // For ElasticsearchLogSource + HttpApiLogSource
builder.Services.AddSingleton<ILogSourceRegistry, OptionsLogSourceRegistry>();
builder.Services.AddSingleton<IGenericLogParser, GenericLogParser>();
builder.Services.AddSingleton<ILogNormalizer, LogNormalizer>();
builder.Services.AddSingleton<ILogChunker, SlidingWindowLogChunker>();
builder.Services.AddSingleton<ILogEntryFilter, RegexLogEntryFilter>();
builder.Services.AddSingleton<IPiiRedactor, PiiRedactor>();
builder.Services.AddSingleton<IIngestionOrchestrator, IngestionOrchestrator>();
builder.Services.AddSingleton<IErrorAnalysisSessionStore, JsonErrorAnalysisSessionStore>();
builder.Services.AddSingleton<IErrorAnalysisEngine, ErrorAnalysisEngine>();
builder.Services.AddHostedService<IngestionHostedService>();
builder.Services.AddHostedService<MetricsConsoleReporter>();

// ── Embedding service: Ollama or vLLM ──
var embeddingProvider = builder.Configuration.GetValue<string>("Embedding:Provider") ?? "ollama";
if (string.Equals(embeddingProvider, "vllm", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IEmbeddingService, VllmEmbeddingService>();
}
else
{
    builder.Services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
}

builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>();
builder.Services.AddSingleton<IRagQueryEngine, RagQueryEngine>();
builder.Services.AddSingleton<IContextBuilder, ContextBuilder>();
builder.Services.AddSingleton<IResponseShaper, MarkdownResponseShaper>();

// ── LLM client: Ollama or vLLM ──
var llmProvider = builder.Configuration.GetValue<string>("Llm:Provider") ?? "ollama";
if (string.Equals(llmProvider, "vllm", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ILlmClient, VllmLlmClient>();
}
else
{
    builder.Services.AddSingleton<ILlmClient, OllamaLlmClient>();
}

builder.Services.AddSingleton<ISessionManager, InMemorySessionManager>();
builder.Services.AddSingleton<IChatService, ChatService>();

var app = builder.Build();

app.MapGet("/health", (ILogSourceRegistry registry) =>
{
    var sources = registry.GetSources().Select(s => new
    {
        id = s.Id,
        type = s.SourceType,
        sourceKind = s.GetType().Name.Replace("LogSource", "").ToLowerInvariant()
    });
    return Results.Ok(new { status = "ok", sources });
});

app.MapGet("/metrics", () =>
{
    return Results.Text(MetricsSnapshot.GetSnapshot(), contentType: "text/plain");
});

app.MapPost("/ingest", async (HttpContext httpContext, IIngestionOrchestrator orchestrator, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    IngestRequestDto? requestBody = null;
    IngestionTimeWindow? timeWindow = null;

    // Try to parse request body for optional time window
    if (httpContext.Request.ContentLength > 0)
    {
        try
        {
            requestBody = await httpContext.Request.ReadFromJsonAsync<IngestRequestDto>(cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            // Body is not valid JSON — ignore, use defaults (full ingestion)
        }
    }

    if (requestBody is { FromUtc: not null } || requestBody is { ToUtc: not null })
    {
        timeWindow = new IngestionTimeWindow(requestBody!.FromUtc, requestBody.ToUtc);
    }

    var collectionName = requestBody?.CollectionName;

    try
    {
        // PERF: Use a separate long-running token so the ingestion
        // completes even if the HTTP client (Postman/curl) times out.
        // Embedding 2000+ chunks on CPU takes 5-15 minutes.
        using var ingestCts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        var result = await orchestrator.IngestAsync(timeWindow, collectionName, ingestCts.Token);

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        loggerFactory.CreateLogger("IngestEndpoint")
            .LogError(ex, "Ingestion request failed.");
        return Results.Json(
            new
            {
                error = "Ingestion failed",
                detail = ex.Message,
                inner = ex.InnerException?.Message,
            },
            statusCode: IsDependencyUnavailable(ex)
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/chat", async (LogRag.Api.Domain.ChatRequestDto request, HttpContext httpContext, IChatService chatService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = "question is required" }, cancellationToken: cancellationToken);
        return;
    }

    httpContext.Response.StatusCode = StatusCodes.Status200OK;
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";
    httpContext.Response.Headers.Connection = "keep-alive";

    try
    {
        await foreach (var evt in chatService.StreamChatAsync(request, cancellationToken))
        {
            var payload = JsonSerializer.Serialize(evt);
            await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (Exception ex)
    {
        loggerFactory.CreateLogger("ChatEndpoint")
            .LogError(ex, "Chat request failed.");

        var fallback = new LogRag.Api.Domain.ChatStreamEvent(
            Type: "final",
            Content: "Chat failed due to a backend dependency error. Ensure Qdrant (localhost:6333) and Ollama (localhost:11434) are running.",
            Metadata: new { error = "chat_dependency_failure" });

        var payload = JsonSerializer.Serialize(fallback);
        await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
        await httpContext.Response.Body.FlushAsync(cancellationToken);
    }
});

// ── Error Correlation & Root Cause Analysis Endpoints ──

void MapErrorAnalysisEndpoints(WebApplication webApp, string prefix)
{
    webApp.MapPost($"{prefix}/sessions", async (CreateErrorAnalysisRequest request, IErrorAnalysisEngine engine, CancellationToken cancellationToken) =>
    {
        var session = await engine.StartSessionAsync(request, cancellationToken);
        return Results.Ok(session);
    });

    webApp.MapGet($"{prefix}/sessions", async (IErrorAnalysisEngine engine, CancellationToken cancellationToken) =>
    {
        var sessions = await engine.ListSessionsAsync(cancellationToken);
        return Results.Ok(sessions);
    });

    webApp.MapGet($"{prefix}/sessions/{{id}}", async (string id, IErrorAnalysisEngine engine, CancellationToken cancellationToken) =>
    {
        var session = await engine.GetSessionAsync(id, cancellationToken);
        return session is not null ? Results.Ok(session) : Results.NotFound(new { error = $"Session '{id}' not found." });
    });

    webApp.MapGet($"{prefix}/sessions/{{id}}/stream", async (string id, HttpContext httpContext, IErrorAnalysisEngine engine, CancellationToken cancellationToken) =>
    {
        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (var evt in engine.StreamProgressAsync(id, cancellationToken))
            {
                var payload = JsonSerializer.Serialize(evt);
                await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected
        }
    });

    webApp.MapPost($"{prefix}/sessions/{{id}}/stop", async (string id, IErrorAnalysisEngine engine, CancellationToken cancellationToken) =>
    {
        var stopped = await engine.StopSessionAsync(id, cancellationToken);
        return stopped ? Results.Ok(new { status = "stopped" }) : Results.NotFound(new { error = $"Session '{id}' not found." });
    });

    webApp.MapDelete($"{prefix}/sessions/{{id}}", async (string id, IErrorAnalysisEngine engine, CancellationToken cancellationToken) =>
    {
        var deleted = await engine.DeleteSessionAsync(id, cancellationToken);
        return deleted ? Results.Ok(new { status = "deleted" }) : Results.NotFound(new { error = $"Session '{id}' not found." });
    });

    webApp.MapPost($"{prefix}/sessions/{{id}}/chat", async (string id, ChatRequestDto request, HttpContext httpContext, IErrorAnalysisEngine engine, IChatService chatService, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
    {
        var session = await engine.GetSessionAsync(id, cancellationToken);
        if (session is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await httpContext.Response.WriteAsJsonAsync(new { error = $"Session '{id}' not found." }, cancellationToken: cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Question))
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await httpContext.Response.WriteAsJsonAsync(new { error = "question is required" }, cancellationToken: cancellationToken);
            return;
        }

        // Direct the question to this session's dedicated Qdrant collection
        var scopedRequest = new ChatRequestDto
        {
            Question = request.Question,
            TopK = request.TopK,
            Filter = request.Filter,
            CollectionName = session.CollectionName,
            SessionId = $"error_analysis_{id}"
        };

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers.Connection = "keep-alive";

        try
        {
            await foreach (var evt in chatService.StreamChatAsync(scopedRequest, cancellationToken))
            {
                var payload = JsonSerializer.Serialize(evt);
                await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
                await httpContext.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("SessionChatEndpoint").LogError(ex, "Session chat request failed.");
            var fallback = new ChatStreamEvent(
                Type: "final",
                Content: "Chat failed due to a backend dependency error. Ensure Qdrant and LLM are running.",
                Metadata: new { error = "session_chat_failure" });
            var payload = JsonSerializer.Serialize(fallback);
            await httpContext.Response.WriteAsync($"data: {payload}\n\n", cancellationToken);
            await httpContext.Response.Body.FlushAsync(cancellationToken);
        }
    });
}

MapErrorAnalysisEndpoints(app, "/error-analysis");
MapErrorAnalysisEndpoints(app, "/api/error-analysis");

app.Run();

static bool IsDependencyUnavailable(Exception ex)
{
    for (var current = ex; current is not null; current = current.InnerException)
    {
        if (current is HttpRequestException || current is System.Net.Sockets.SocketException)
        {
            return true;
        }
    }

    return false;
}
