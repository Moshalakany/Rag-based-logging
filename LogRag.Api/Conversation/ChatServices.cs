using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Llm;
using LogRag.Api.Query;
using LogRag.Api.Telemetry;
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

        // Quick gate: skip retrieval entirely for obvious small-talk
        if (IsSmallTalk(request.Question))
        {
            LogRagTelemetry.ChatRequests.Add(1);
            LogRagTelemetry.ChatSmallTalkSkips.Add(1);
            MetricsSnapshot.RecordChat(smallTalk: true);
            const string greeting = "Hello! I'm Momkn intelligent Logs inspector. I help you search, analyze, and understand your application logs. How can I assist you today?";
            _sessionManager.Append(sessionId, "user", request.Question);
            _sessionManager.Append(sessionId, "assistant", greeting);
            yield return new ChatStreamEvent("meta", null, new { session_id = sessionId, chunk_count = 0, small_talk = true });
            yield return new ChatStreamEvent("final", greeting, new { session_id = sessionId, citations = Array.Empty<object>() });
            yield break;
        }

        IReadOnlyList<RetrievedChunk> chunks = Array.Empty<RetrievedChunk>();
        bool qdrantFailed = false;

        LogRagTelemetry.ChatRequests.Add(1);
        
        try
        {
            chunks = await _ragQueryEngine.RetrieveAsync(request.Question, filter, topK, request.CollectionName, cancellationToken);
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
            var collectionInfo = string.IsNullOrWhiteSpace(request.CollectionName) 
                ? "default collection (log_chunks)" 
                : $"collection '{request.CollectionName}'";
            var noMatch = $"No matching logs were found in the {collectionInfo}. If your logs are in a different collection, specify it in the collection field above. To search all data, leave the collection field empty.";
            _sessionManager.Append(sessionId, "user", request.Question);
            _sessionManager.Append(sessionId, "assistant", noMatch);
            yield return new ChatStreamEvent("final", noMatch, new { session_id = sessionId, citations = Array.Empty<object>() });
            yield break;
        }

        var context = _contextBuilder.BuildContext(chunks);
        var answerBuilder = new StringBuilder();
        var llmFailed = false;
        List<string> tokens = new List<string>();
        
        try
        {
            await foreach (var token in _llmClient.StreamAnswerAsync(request.Question, context, history, cancellationToken))
            {
                tokens.Add(token);
                answerBuilder.Append(token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM generation failed for session {SessionId}.", sessionId);
            llmFailed = true;
        }

        foreach (var token in tokens)
        {
            yield return new ChatStreamEvent("token", token, null);
        }

        if (llmFailed)
        {
            const string llmError = "Chat is temporarily unavailable because the language model cannot be reached. Ensure the LLM provider is running.";
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

    /// <summary>
    /// Returns true when the question is obvious small-talk (greetings, identity questions, etc.)
    /// that should not trigger log retrieval.
    /// </summary>
    private static bool IsSmallTalk(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return true;

        var normalized = question.Trim().ToLowerInvariant();

        // Very short greetings
        if (normalized is "hi" or "hello" or "hey" or "yo" or "sup" or "howdy" or "hola" or "good morning" or "good afternoon" or "good evening")
            return true;

        // Greeting-like short phrases (up to ~5 words) that contain greeting words
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 5)
        {
            var greetingWords = new HashSet<string> { "hi", "hello", "hey", "yo", "sup", "howdy", "hola", "morning", "afternoon", "evening" };
            if (words.Any(w => greetingWords.Contains(w)))
                return true;
        }

        // Questions about identity / name / capabilities
        if (normalized.Contains("your name") ||
            normalized.Contains("who are you") ||
            normalized.Contains("what are you") ||
            normalized.Contains("what is your name") ||
            normalized.Contains("what can you do") ||
            normalized.Contains("what do you do") ||
            normalized.Contains("how can you help") ||
            normalized.Contains("are you a bot") ||
            normalized.Contains("are you ai") ||
            normalized.Contains("are you an ai") ||
            normalized.Contains("are you human") ||
            normalized.Contains("tell me about yourself") ||
            normalized.Contains("introduce yourself"))
        {
            return true;
        }

        return false;
    }
}
