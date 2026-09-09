using System.Text.Json;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace LogRag.Api.Query;

public sealed class MongoErrorAnalysisSessionStore : IErrorAnalysisSessionStore
{
    static MongoErrorAnalysisSessionStore()
    {
        var pack = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true)
        };
        ConventionRegistry.Register("camelCase", pack, _ => true);

        try
        {
            BsonSerializer.RegisterSerializer(new DateTimeOffsetSerializer(BsonType.String));
        }
        catch (BsonSerializationException) { }

        try
        {
            BsonSerializer.RegisterSerializer(new NullableSerializer<DateTimeOffset>(new DateTimeOffsetSerializer(BsonType.String)));
        }
        catch (BsonSerializationException) { }
    }

    private readonly IMongoCollection<ErrorAnalysisSession> _collection;
    private readonly ILogger<MongoErrorAnalysisSessionStore> _logger;

    public MongoErrorAnalysisSessionStore(
        IOptions<ErrorAnalysisOptions> options,
        IHostEnvironment hostEnvironment,
        ILogger<MongoErrorAnalysisSessionStore> logger)
    {
        _logger = logger;
        var mongoConfig = options.Value.MongoDb;
        var client = new MongoClient(mongoConfig.ConnectionString);
        var database = client.GetDatabase(mongoConfig.DatabaseName);
        _collection = database.GetCollection<ErrorAnalysisSession>(mongoConfig.CollectionName);

        var dataDir = options.Value.DataDirectory;
        var storageDir = Path.IsPathRooted(dataDir)
            ? dataDir
            : Path.Combine(hostEnvironment.ContentRootPath, dataDir);

        MigrateExistingJsonFiles(storageDir);
    }

    private void MigrateExistingJsonFiles(string dataDir)
    {
        try
        {
            if (!Directory.Exists(dataDir)) return;
            var files = Directory.GetFiles(dataDir, "*.json");
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var session = JsonSerializer.Deserialize<ErrorAnalysisSession>(json, jsonOptions);
                    if (session is not null && !string.IsNullOrWhiteSpace(session.SessionId))
                    {
                        var filter = Builders<ErrorAnalysisSession>.Filter.Eq(s => s.SessionId, session.SessionId);
                        _collection.ReplaceOne(filter, session, new ReplaceOptions { IsUpsert = true });
                        _logger.LogInformation("Migrated/Synced session {SessionId} from JSON file {File} into MongoDB.", session.SessionId, Path.GetFileName(file));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to migrate session file {File} into MongoDB", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking JSON session directory for migration: {DataDir}", dataDir);
        }
    }

    public async Task<ErrorAnalysisSession?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var filter = Builders<ErrorAnalysisSession>.Filter.Eq(s => s.SessionId, sessionId.Trim());
            return await _collection.Find(filter).FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get error analysis session {SessionId} from MongoDB", sessionId);
            return null;
        }
    }

    public async Task<IReadOnlyList<ErrorAnalysisSessionSummary>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var projection = Builders<ErrorAnalysisSession>.Projection.Exclude(s => s.Traces);
            var sessions = await _collection
                .Find(Builders<ErrorAnalysisSession>.Filter.Empty)
                .Project<ErrorAnalysisSession>(projection)
                .SortByDescending(s => s.CreatedAtUtc)
                .ToListAsync(cancellationToken);

            return sessions.Select(s => new ErrorAnalysisSessionSummary(
                s.SessionId,
                s.Name,
                s.FromUtc,
                s.ToUtc,
                s.Status,
                s.ProgressStage,
                s.CreatedAtUtc,
                s.CompletedAtUtc,
                s.ErrorsFound,
                s.CorrelatedTracesCount,
                s.RcaCompletedCount,
                s.PercentComplete)).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list error analysis sessions from MongoDB");
            return Array.Empty<ErrorAnalysisSessionSummary>();
        }
    }

    public async Task SaveAsync(ErrorAnalysisSession session, CancellationToken cancellationToken)
    {
        try
        {
            var filter = Builders<ErrorAnalysisSession>.Filter.Eq(s => s.SessionId, session.SessionId);
            await _collection.ReplaceOneAsync(filter, session, new ReplaceOptions { IsUpsert = true }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save error analysis session {SessionId} to MongoDB", session.SessionId);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var filter = Builders<ErrorAnalysisSession>.Filter.Eq(s => s.SessionId, sessionId.Trim());
            var result = await _collection.DeleteOneAsync(filter, cancellationToken);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete error analysis session {SessionId} from MongoDB", sessionId);
            return false;
        }
    }
}
