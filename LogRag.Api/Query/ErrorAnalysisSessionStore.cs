using System.Collections.Concurrent;
using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Query;

public interface IErrorAnalysisSessionStore
{
    Task<ErrorAnalysisSession?> GetAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ErrorAnalysisSessionSummary>> ListAsync(CancellationToken cancellationToken);
    Task SaveAsync(ErrorAnalysisSession session, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken);
}

public sealed class JsonErrorAnalysisSessionStore : IErrorAnalysisSessionStore
{
    private readonly string _storageDir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ILogger<JsonErrorAnalysisSessionStore> _logger;

    public JsonErrorAnalysisSessionStore(
        IOptions<ErrorAnalysisOptions> options,
        IHostEnvironment hostEnvironment,
        ILogger<JsonErrorAnalysisSessionStore> logger)
    {
        _logger = logger;
        var dir = options.Value.DataDirectory;
        _storageDir = Path.IsPathRooted(dir)
            ? dir
            : Path.Combine(hostEnvironment.ContentRootPath, dir);

        if (!Directory.Exists(_storageDir))
        {
            Directory.CreateDirectory(_storageDir);
        }
    }

    private string GetFilePath(string sessionId) =>
        Path.Combine(_storageDir, $"{sessionId.Trim()}.json");

    private SemaphoreSlim GetLock(string sessionId) =>
        _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    public async Task<ErrorAnalysisSession?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var path = GetFilePath(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        var sessionLock = GetLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ErrorAnalysisSession>(stream, _jsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load session file for {SessionId}", sessionId);
            return null;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<IReadOnlyList<ErrorAnalysisSessionSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var summaries = new List<ErrorAnalysisSessionSummary>();
        if (!Directory.Exists(_storageDir))
        {
            return summaries;
        }

        var files = Directory.GetFiles(_storageDir, "*.json");
        foreach (var file in files)
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var session = await JsonSerializer.DeserializeAsync<ErrorAnalysisSession>(stream, _jsonOptions, cancellationToken);
                if (session is not null)
                {
                    summaries.Add(new ErrorAnalysisSessionSummary(
                        session.SessionId,
                        session.Name,
                        session.FromUtc,
                        session.ToUtc,
                        session.Status,
                        session.ProgressStage,
                        session.CreatedAtUtc,
                        session.CompletedAtUtc,
                        session.ErrorsFound,
                        session.CorrelatedTracesCount,
                        session.RcaCompletedCount,
                        session.PercentComplete));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read session file {File}", file);
            }
        }

        return summaries.OrderByDescending(s => s.CreatedAtUtc).ToArray();
    }

    public async Task SaveAsync(ErrorAnalysisSession session, CancellationToken cancellationToken)
    {
        var path = GetFilePath(session.SessionId);
        var sessionLock = GetLock(session.SessionId);

        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            var tempPath = $"{path}.tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, session, _jsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save session file for {SessionId}", session.SessionId);
            throw;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        var path = GetFilePath(sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        var sessionLock = GetLock(sessionId);
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            File.Delete(path);
            _locks.TryRemove(sessionId, out _);
            return true;
        }
        finally
        {
            sessionLock.Release();
        }
    }
}
