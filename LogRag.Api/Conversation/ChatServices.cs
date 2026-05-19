using System.Collections.Concurrent;
using System.Text;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Llm;
using LogRag.Api.Query;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Conversation;

public interface ISessionManager
{
    string ResolveSessionId(string? sessionId);
    IReadOnlyList<SessionMessage> GetHistory(string sessionId);
    void Append(string sessionId, string role, string content);
}

public interface IChatService
{
    IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(ChatRequestDto request, CancellationToken cancellationToken);
}

public sealed class InMemorySessionManager : ISessionManager
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<SessionMessage>> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxMessages;

    public InMemorySessionManager(IOptions<LlmOptions> llmOptions)
    {
        _maxMessages = Math.Max(4, llmOptions.Value.MaxHistoryMessages * 4);
    }

    public string ResolveSessionId(string? sessionId)
    {
        return string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId.Trim();
    }

    public IReadOnlyList<SessionMessage> GetHistory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var queue))
        {
            return queue.ToArray();
        }

        return [];
    }

    public void Append(string sessionId, string role, string content)
    {
        var queue = _sessions.GetOrAdd(sessionId, _ => new ConcurrentQueue<SessionMessage>());
        queue.Enqueue(new SessionMessage(role, content, DateTimeOffset.UtcNow));

        while (queue.Count > _maxMessages && queue.TryDequeue(out _))
        {
        }
    }
}

public sealed class ChatService : IChatService
{
    private readonly ISessionManager _sessionManager;
    private readonly IRagQueryEngine _ragQueryEngine;
    private readonly IContextBuilder _contextBuilder;
    private readonly ILlmClient _llmClient;
    private readonly IResponseShaper _responseShaper;
    private readonly RetrievalOptions _retrievalOptions;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        ISessionManager sessionManager,
        IRagQueryEngine ragQueryEngine,
        IContextBuilder contextBuilder,
        ILlmClient llmClient,
        IResponseShaper responseShaper,
        IOptions<RetrievalOptions> retrievalOptions,
        ILogger<ChatService> logger)
    {
        _sessionManager = sessionManager;
        _ragQueryEngine = ragQueryEngine;
        _contextBuilder = contextBuilder;
        _llmClient = llmClient;
        _responseShaper = responseShaper;
        _retrievalOptions = retrievalOptions.Value;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequestDto request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sessionId = _sessionManager.ResolveSessionId(request.SessionId);
        var topK = Math.Max(1, request.TopK.GetValueOrDefault(_retrievalOptions.DefaultTopK));
        var filter = request.Filter ?? new QueryFilter();

        var history = _sessionManager.GetHistory(sessionId);
        IReadOnlyList<RetrievedChunk> chunks = Array.Empty<RetrievedChunk>();
        bool qdrantFailed = false;
        
        try
        {
            chunks = await _ragQueryEngine.RetrieveAsync(request.Question, filter, topK, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chat retrieval failed for session {SessionId}.", sessionId);
            qdrantFailed = true;
        }

        if (qdrantFailed)
        {
            const string dependencyError = "Chat is temporarily unavailable because the vector database cannot be reached. Ensure Qdrant is running on localhost:6333.";
            yield return new ChatStreamEvent("meta", null, new { session_id = sessionId, chunk_count = 0, error = "vector_store_unavailable" });
            yield return new ChatStreamEvent("final", dependencyError, new { session_id = sessionId, citations = Array.Empty<object>(), error = "vector_store_unavailable" });
            yield break;
        }

        yield return new ChatStreamEvent("meta", null, new { session_id = sessionId, chunk_count = chunks.Count });

        if (chunks.Count == 0)
        {
            const string noMatch = "No matching logs were found for the current question and filters.";
            _sessionManager.Append(sessionId, "user", request.Question);
            _sessionManager.Append(sessionId, "assistant", noMatch);
            yield return new ChatStreamEvent("final", noMatch, new { session_id = sessionId, citations = Array.Empty<object>() });
            yield break;
        }

        var context = _contextBuilder.BuildContext(chunks);
        var answerBuilder = new StringBuilder();
        var llmFailed = false;
        
        var enumerator = _llmClient.StreamAnswerAsync(request.Question, context, history, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LLM generation failed for session {SessionId}.", sessionId);
                    llmFailed = true;
                    break;
                }

                if (!hasNext) break;

                var token = enumerator.Current;
                answerBuilder.Append(token);
                yield return new ChatStreamEvent("token", token, null);
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        if (llmFailed)
        {
            const string llmError = "Chat is temporarily unavailable because the language model cannot be reached. Ensure Ollama is running on localhost:11434.";
            yield return new ChatStreamEvent("final", llmError, new { session_id = sessionId, citations = Array.Empty<object>(), error = "llm_unavailable" });
            yield break;
        }

        var finalMarkdown = _responseShaper.Shape(answerBuilder.ToString(), chunks);
        _sessionManager.Append(sessionId, "user", request.Question);
        _sessionManager.Append(sessionId, "assistant", finalMarkdown);

        var citationMetadata = chunks.Select((chunk, index) => new
        {
            id = index + 1,
            timestamp = chunk.TimestampUtc.ToString("O"),
            chunk.Severity,
            service_name = chunk.ServiceName,
            trace_id = chunk.TraceId,
            source_id = chunk.SourceId,
        }).ToArray();

        yield return new ChatStreamEvent("final", finalMarkdown, new { session_id = sessionId, citations = citationMetadata });
    }
}
