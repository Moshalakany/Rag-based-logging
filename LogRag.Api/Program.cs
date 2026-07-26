using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Conversation;
using LogRag.Api.Embedding;
using LogRag.Api.Ingestion;
using LogRag.Api.Llm;
using LogRag.Api.Query;
using LogRag.Api.Sources;
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

builder.Services.AddSingleton<ILogSourceRegistry, OptionsLogSourceRegistry>();
builder.Services.AddSingleton<IGenericLogParser, GenericLogParser>();
builder.Services.AddSingleton<ILogNormalizer, LogNormalizer>();
builder.Services.AddSingleton<ILogChunker, SlidingWindowLogChunker>();
builder.Services.AddSingleton<ILogEntryFilter, RegexLogEntryFilter>();
builder.Services.AddSingleton<IPiiRedactor, PiiRedactor>();
builder.Services.AddSingleton<IIngestionOrchestrator, IngestionOrchestrator>();
builder.Services.AddHostedService<IngestionHostedService>();

builder.Services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>();
builder.Services.AddSingleton<IRagQueryEngine, RagQueryEngine>();
builder.Services.AddSingleton<IContextBuilder, ContextBuilder>();
builder.Services.AddSingleton<IResponseShaper, MarkdownResponseShaper>();
builder.Services.AddSingleton<ILlmClient, OllamaLlmClient>();

builder.Services.AddSingleton<ISessionManager, InMemorySessionManager>();
builder.Services.AddSingleton<IChatService, ChatService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/ingest", async (IIngestionOrchestrator orchestrator, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await orchestrator.IngestAsync(cancellationToken);
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
