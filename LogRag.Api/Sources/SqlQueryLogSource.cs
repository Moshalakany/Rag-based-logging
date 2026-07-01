using System.Runtime.CompilerServices;
using System.Text;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LogRag.Api.Sources;

/// <summary>
/// Reads log entries from a SQL database using a configurable query with cursor-based checkpointing.
/// Supports SQL Server via Microsoft.Data.SqlClient.
/// </summary>
public sealed class SqlQueryLogSource : ILogSource
{
    private readonly LogSourceDescriptorOptions _descriptor;
    private readonly ILogSourceCheckpointStore _checkpointStore;
    private readonly ILogger<SqlQueryLogSource> _logger;
    private readonly string _checkpointKey;

    public SqlQueryLogSource(
        LogSourceDescriptorOptions descriptor,
        ILogSourceCheckpointStore checkpointStore,
        ILogger<SqlQueryLogSource> logger)
    {
        _descriptor = descriptor;
        _checkpointStore = checkpointStore;
        _logger = logger;
        _checkpointKey = $"sql|{_descriptor.Id}";
    }

    public string Id => _descriptor.Id;
    public string SourceType => _descriptor.SourceType;

    public async IAsyncEnumerable<RawLogEntry> ReadAsync(IngestionTimeWindow? timeWindow, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_descriptor.SqlConnectionString))
        {
            _logger.LogWarning("SQL connection string is not configured for source {Id}", _descriptor.Id);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(_descriptor.SqlQuery))
        {
            _logger.LogWarning("SQL query is not configured for source {Id}", _descriptor.Id);
            yield break;
        }

        var lastCursor = _checkpointStore.GetOffset(_checkpointKey);
        long maxCursor = lastCursor;

        var cursorColumn = string.IsNullOrWhiteSpace(_descriptor.SqlCursorColumn)
            ? "id"
            : _descriptor.SqlCursorColumn;

        // Build the cursor-based query
        var query = BuildCursorQuery(_descriptor.SqlQuery, cursorColumn, timeWindow);

        // Collect results outside try-catch
        var collectedEntries = new List<RawLogEntry>();

        try
        {
            await using var connection = new SqlConnection(_descriptor.SqlConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@cursor", lastCursor);

            // PERF: Time-window filter pushed to SQL for server-side filtering
            if (timeWindow is { IsEmpty: false })
            {
                if (timeWindow.FromUtc is not null)
                    command.Parameters.AddWithValue("@timeFrom", timeWindow.FromUtc.Value.UtcDateTime);
                if (timeWindow.ToUtc is not null)
                    command.Parameters.AddWithValue("@timeTo", timeWindow.ToUtc.Value.UtcDateTime);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // Get column names for building text representation
            var columnNames = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columnNames.Add(reader.GetName(i));
            }

            var cursorOrdinal = reader.GetOrdinal(cursorColumn);

            while (await reader.ReadAsync(cancellationToken))
            {
                var text = BuildRowText(reader, columnNames);
                long currentCursor = 0;

                if (!await reader.IsDBNullAsync(cursorOrdinal, cancellationToken))
                {
                    currentCursor = ConvertToLong(reader.GetValue(cursorOrdinal));
                }

                maxCursor = Math.Max(maxCursor, currentCursor);

                var attributes = new Dictionary<string, string>
                {
                    ["sql_source"] = _descriptor.Id,
                    ["sql_cursor"] = currentCursor.ToString(),
                };

                collectedEntries.Add(new RawLogEntry(Id, SourceType, text, DateTimeOffset.UtcNow, attributes));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "SQL query failed for source {Id}: {Message}", _descriptor.Id, ex.Message);
        }

        _checkpointStore.SaveOffset(_checkpointKey, maxCursor);

        // Yield collected entries
        foreach (var entry in collectedEntries)
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Wraps the user's SQL query with a cursor-based WHERE clause and optional time window.
    /// </summary>
    private static string BuildCursorQuery(string userQuery, string cursorColumn, IngestionTimeWindow? timeWindow)
    {
        var trimmed = userQuery.Trim().TrimEnd(';').TrimEnd();
        var tsColumn = "timestamp";

        var clauses = new List<string> { $"[{cursorColumn}] > @cursor" };

        // PERF: Time-window filter pushed to SQL for server-side filtering
        if (timeWindow is { IsEmpty: false })
        {
            if (timeWindow.FromUtc is not null)
                clauses.Add($"[{tsColumn}] >= @timeFrom");
            if (timeWindow.ToUtc is not null)
                clauses.Add($"[{tsColumn}] <= @timeTo");
        }

        var whereClause = string.Join(" AND ", clauses);
        var orderClause = $" ORDER BY [{cursorColumn}] ASC";

        var hasWhere = trimmed.Contains("WHERE ", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.Contains("WHERE\n", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.Contains("WHERE\t", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.Contains("WHERE\r", StringComparison.OrdinalIgnoreCase);

        if (hasWhere)
        {
            // Insert the cursor + time-window conditions after existing WHERE
            var whereIndex = FindWhereIndex(trimmed);
            if (whereIndex >= 0)
            {
                var beforeWhere = trimmed[..(whereIndex + 6)]; // "WHERE "
                var afterWhere = trimmed[(whereIndex + 6)..];
                return $"{beforeWhere} {whereClause} AND {afterWhere}{orderClause}";
            }
        }

        return $"{trimmed} WHERE {whereClause}{orderClause}";
    }

    private static int FindWhereIndex(string query)
    {
        // Case-insensitive search for "WHERE" as a standalone word
        var upper = query.ToUpperInvariant();
        var idx = upper.IndexOf("WHERE", StringComparison.Ordinal);
        while (idx >= 0)
        {
            // Check it's a word boundary
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(query[idx - 1]) && query[idx - 1] != '_';
            var afterOk = idx + 5 >= query.Length || !char.IsLetterOrDigit(query[idx + 5]) && query[idx + 5] != '_';
            if (beforeOk && afterOk)
            {
                return idx;
            }
            idx = upper.IndexOf("WHERE", idx + 1, StringComparison.Ordinal);
        }
        return -1;
    }

    private static string BuildRowText(SqlDataReader reader, List<string> columnNames)
    {
        var sb = new StringBuilder();
        sb.Append("{ ");
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('"');
            sb.Append(columnNames[i]);
            sb.Append("\": ");

            if (reader.IsDBNull(i))
            {
                sb.Append("null");
            }
            else
            {
                var value = reader.GetValue(i);
                switch (value)
                {
                    case string s:
                        sb.Append('"');
                        sb.Append(s.Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r"));
                        sb.Append('"');
                        break;
                    case DateTime dt:
                        sb.Append('"');
                        sb.Append(dt.ToString("O"));
                        sb.Append('"');
                        break;
                    case DateTimeOffset dto:
                        sb.Append('"');
                        sb.Append(dto.ToString("O"));
                        sb.Append('"');
                        break;
                    case bool b:
                        sb.Append(b ? "true" : "false");
                        break;
                    default:
                        sb.Append(value.ToString());
                        break;
                }
            }
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static long ConvertToLong(object value)
    {
        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            decimal d => (long)d,
            double d => (long)d,
            float f => (long)f,
            string s when long.TryParse(s, out var parsed) => parsed,
            _ => 0
        };
    }
}
