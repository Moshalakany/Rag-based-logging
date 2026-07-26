using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Embedding;
using LogRag.Api.Sources;
using LogRag.Api.VectorStore;
using Microsoft.Extensions.Options;

namespace LogRag.Api.Ingestion;

public interface IGenericLogParser
{
    ParsedLogEntry Parse(RawLogEntry rawLogEntry);
}

public interface ILogNormalizer
{
    NormalizedLogEntry Normalize(ParsedLogEntry parsedLogEntry);
}

public interface ILogChunker
{
    IReadOnlyList<LogChunk> Chunk(NormalizedLogEntry normalizedLogEntry);
}

public interface ILogEntryFilter
{
    bool ShouldDrop(RawLogEntry rawLogEntry);
}

public interface IIngestionOrchestrator
{
    Task<IngestionRunResult> IngestAsync(CancellationToken cancellationToken);
}

public sealed class RegexLogEntryFilter : ILogEntryFilter
{
    private readonly bool _enabled;
    private readonly IReadOnlyList<Regex> _dropPatterns;

    public RegexLogEntryFilter(IOptions<IngestionOptions> options)
    {
        _enabled = options.Value.EnableNoiseFiltering;
        _dropPatterns = options.Value.DropRawPatterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            .ToArray();
    }

    public bool ShouldDrop(RawLogEntry rawLogEntry)
    {
        if (!_enabled || _dropPatterns.Count == 0)
        {
            return false;
        }

        foreach (var pattern in _dropPatterns)
        {
            if (pattern.IsMatch(rawLogEntry.RawText))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class GenericLogParser : IGenericLogParser
{
    private static readonly string[] CsvColumns = ["timestamp", "severity", "service_name", "trace_id", "message"];
    private static readonly Regex TimestampPrefixedRegex = new(
        "^(?<timestamp>\\d{4}-\\d{2}-\\d{2}(?:[ T]\\d{2}\\s*:\\s*\\d{2}\\s*:\\s*\\d{2}(?:\\.\\d+)?(?:Z|[+-]\\d{2}:\\d{2})?)?)\\s+(?<message>[\\s\\S]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SeverityPrefixRegex = new(
        "^(?<severity>TRACE|DEBUG|INFO|WARN|WARNING|ERROR|CRITICAL|FATAL)\\b[:\\- ]*(?<message>[\\s\\S]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex CorrelationRegex = new(
        "Correlation[_ ]ID[:=]\\s*(?<trace>[a-zA-Z0-9\\-:]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex RequestIdRegex = new(
        "requestId:\\s*(?<request>[^\\s,]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly IReadOnlyList<(string Name, Regex Regex)> _compiledRegexRules;

    public GenericLogParser(IOptions<ParserOptions> parserOptions)
    {
        _compiledRegexRules = parserOptions.Value.RegexRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Pattern))
            .Select(rule => (rule.Name, new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline)))
            .ToArray();
    }

    public ParsedLogEntry Parse(RawLogEntry rawLogEntry)
    {
        var text = rawLogEntry.RawText.Trim();
        if (TryParseJson(text, out var jsonFields))
        {
            return CreateParsedEntry(rawLogEntry, jsonFields);
        }

        // Handle the dominant Momkn format: "2026-05-19 18: 13: 07.823 {JSON...}"
        if (TryParseTimestampPrefixedJson(text, out var tpjFields))
        {
            return CreateParsedEntry(rawLogEntry, tpjFields);
        }

        // Handle hybrid/mixed key-value timestamp-prefixed formats
        if (TryParseTimestampPrefixedKeyValue(text, out var tpkvFields))
        {
            return CreateParsedEntry(rawLogEntry, tpkvFields);
        }

        if (TryParseCsv(text, out var csvFields))
        {
            return CreateParsedEntry(rawLogEntry, csvFields);
        }

        foreach (var (_, regex) in _compiledRegexRules)
        {
            var match = regex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var groupName in regex.GetGroupNames())
            {
                if (int.TryParse(groupName, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                var value = match.Groups[groupName].Value;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields[groupName] = value.Trim().Trim('"');
                }
            }

            if (fields.Count > 0)
            {
                return CreateParsedEntry(rawLogEntry, fields);
            }
        }

        if (TryParseKeyValue(text, out var keyValueFields))
        {
            return CreateParsedEntry(rawLogEntry, keyValueFields);
        }

        if (TryParseTimestampPrefixed(text, out var timestampFields))
        {
            return CreateParsedEntry(rawLogEntry, timestampFields);
        }

        return new ParsedLogEntry(rawLogEntry, new Dictionary<string, string> { ["message"] = rawLogEntry.RawText }, rawLogEntry.RawText);
    }

    private static ParsedLogEntry CreateParsedEntry(RawLogEntry raw, IReadOnlyDictionary<string, string> fields)
    {
        var message = GetFirst(fields, "message", "msg", "log", "text") ?? raw.RawText;
        return new ParsedLogEntry(raw, fields, message);
    }

    private static bool TryParseJson(string text, out IReadOnlyDictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!text.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => property.Value.GetRawText(),
                };
            }

            ExtractNestedProperties(map);

            fields = map;
            return map.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly Regex TimestampThenJsonRegex = new(
        "^(?<timestamp>\\d{4}-\\d{2}-\\d{2}(?:[ T]\\d{2}\\s*:\\s*\\d{2}\\s*:\\s*\\d{2}(?:\\.\\d+)?(?:Z|[+-]\\d{2}:\\d{2})?)?)\\s+(?<json>\\{[\\s\\S]+\\})\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool TryParseTimestampPrefixedJson(string text, out IReadOnlyDictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var match = TimestampThenJsonRegex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var timestampPrefix = match.Groups["timestamp"].Value.Trim();
        var jsonBody = match.Groups["json"].Value.Trim();

        try
        {
            using var document = JsonDocument.Parse(jsonBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["timestamp"] = timestampPrefix,
            };

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    // For nested objects/arrays (e.g. MessageBody, ExtraData), serialize as JSON string
                    _ => property.Value.GetRawText(),
                };
                map[property.Name] = value;
            }

            // Extract message from MessageBody if it's a string, or keep JSON for complex bodies
            if (map.TryGetValue("MessageBody", out var msgBody) && !string.IsNullOrWhiteSpace(msgBody))
            {
                map["message"] = msgBody;
            }

            // Use LogLevel as severity if present
            if (map.TryGetValue("LogLevel", out var logLevel) && !string.IsNullOrWhiteSpace(logLevel))
            {
                map["severity"] = logLevel;
            }

            // Use LogDate + LogTime for more precise timestamp if available
            if (map.TryGetValue("LogDate", out var logDate) && map.TryGetValue("LogTime", out var logTime)
                && !string.IsNullOrWhiteSpace(logDate) && !string.IsNullOrWhiteSpace(logTime))
            {
                map["timestamp"] = $"{logDate} {logTime}";
            }

            ExtractNestedProperties(map);

            fields = map;
            return map.Count > 1; // Must have more than just timestamp
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseTimestampPrefixedKeyValue(string text, out IReadOnlyDictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tsMatch = TimestampPrefixedRegex.Match(text);
        if (!tsMatch.Success)
        {
            return false;
        }

        var timestampPrefix = tsMatch.Groups["timestamp"].Value.Trim();
        var remaining = tsMatch.Groups["message"].Value.Trim();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["timestamp"] = timestampPrefix
        };

        // Regex to match quoted keys followed by colon or unquoted keys followed by equals
        var keyRegex = new Regex(@"(?:""(?<key>[a-zA-Z0-9_\-]+)""\s*:\s*|\b(?<key>[a-zA-Z0-9_\-]+)\s*=\s*)", RegexOptions.Compiled);
        var matches = keyRegex.Matches(remaining);

        if (matches.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var key = match.Groups["key"].Value;
            int valueStart = match.Index + match.Length;

            if (valueStart >= remaining.Length)
            {
                continue;
            }

            string value;
            char firstChar = remaining[valueStart];

            if (firstChar == '{' || firstChar == '[')
            {
                value = ExtractBalancedBlock(remaining, valueStart, firstChar);
            }
            else if (firstChar == '"')
            {
                value = ExtractQuotedString(remaining, valueStart);
            }
            else
            {
                value = ExtractUnquotedValue(remaining, valueStart);
            }

            map[key] = value;
        }

        ExtractNestedProperties(map);

        fields = map;
        return map.Count > 1;
    }

    private static string ExtractBalancedBlock(string text, int startIndex, char openChar)
    {
        char closeChar = openChar == '{' ? '}' : ']';
        int braceCount = 0;
        bool inQuotes = false;
        int i = startIndex;

        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == openChar)
                {
                    braceCount++;
                }
                else if (c == closeChar)
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        i++; // Include the closing brace
                        break;
                    }
                }
            }
        }

        int length = Math.Min(i, text.Length) - startIndex;
        return text.Substring(startIndex, length);
    }

    private static string ExtractQuotedString(string text, int startIndex)
    {
        int i = startIndex + 1;
        for (; i < text.Length; i++)
        {
            if (text[i] == '"' && text[i - 1] != '\\')
            {
                i++; // Include the closing quote
                break;
            }
        }
        int length = Math.Min(i, text.Length) - startIndex;
        return text.Substring(startIndex, length).Trim('"');
    }

    private static string ExtractUnquotedValue(string text, int startIndex)
    {
        int i = startIndex;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ',' || c == ' ' || c == ']' || c == '}')
            {
                break;
            }
        }
        return text.Substring(startIndex, i - startIndex).Trim();
    }

    private static void ExtractNestedProperties(Dictionary<string, string> map)
    {
        string[] candidateJsonKeys = ["MessageBody", "message", "ExtraData", "payload"];
        foreach (var key in candidateJsonKeys)
        {
            if (map.TryGetValue(key, out var jsonStr) && !string.IsNullOrWhiteSpace(jsonStr))
            {
                var trimmed = jsonStr.Trim();
                if (trimmed.StartsWith('{'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        ExtractJsonElementProperties(doc.RootElement, map);
                    }
                    catch (JsonException) { /* Skip invalid JSON */ }
                }
            }
        }
    }

    private static void ExtractJsonElementProperties(JsonElement element, Dictionary<string, string> map)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (!map.TryGetValue(prop.Name, out var existing) || string.IsNullOrWhiteSpace(existing) || existing == "null")
            {
                string valStr = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? "",
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => prop.Value.GetRawText()
                };
                
                if (!string.IsNullOrWhiteSpace(valStr))
                {
                    map[prop.Name] = valStr;
                }
            }

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                ExtractJsonElementProperties(prop.Value, map);
            }
        }
    }

    private static bool TryParseCsv(string text, out IReadOnlyDictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>();
        if (!text.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = text.Split(',', CsvColumns.Length, StringSplitOptions.TrimEntries);
        if (parts.Length < 5)
        {
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < CsvColumns.Length; i++)
        {
            map[CsvColumns[i]] = parts[i];
        }

        fields = map;
        return true;
    }

    private static bool TryParseKeyValue(string text, out IReadOnlyDictionary<string, string> fields)
    {
        var matches = Regex.Matches(text, "(?<key>[a-zA-Z_][a-zA-Z0-9_]*)=(?<value>\"[^\"]+\"|\\S+)");
        if (matches.Count == 0)
        {
            fields = new Dictionary<string, string>();
            return false;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim().Trim('"');
            map[key] = value;
        }

        if (!map.ContainsKey("message"))
        {
            map["message"] = text;
        }

        fields = map;
        return true;
    }

    private static bool TryParseTimestampPrefixed(string text, out IReadOnlyDictionary<string, string> fields)
    {
        var match = TimestampPrefixedRegex.Match(text);
        if (!match.Success)
        {
            fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        var message = match.Groups["message"].Value.Trim();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["timestamp"] = match.Groups["timestamp"].Value.Trim(),
            ["message"] = message,
        };

        var severityMatch = SeverityPrefixRegex.Match(message);
        if (severityMatch.Success)
        {
            map["severity"] = severityMatch.Groups["severity"].Value.Trim();
            map["message"] = severityMatch.Groups["message"].Value.Trim();
        }

        var correlationMatch = CorrelationRegex.Match(text);
        if (correlationMatch.Success)
        {
            map["trace_id"] = correlationMatch.Groups["trace"].Value.Trim();
        }
        else
        {
            var requestIdMatch = RequestIdRegex.Match(text);
            if (requestIdMatch.Success)
            {
                map["request_id"] = requestIdMatch.Groups["request"].Value.Trim();
            }
        }

        fields = map;
        return true;
    }

    private static string? GetFirst(IReadOnlyDictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

public sealed class LogNormalizer : ILogNormalizer
{
    private static readonly string[] TimestampKeys = ["timestamp", "ts", "@timestamp", "time", "date", "logdate", "logtime"];
    private static readonly string[] SeverityKeys = ["severity", "level", "loglevel"];
    private static readonly string[] ServiceKeys = ["service_name", "service", "application", "app"];
    private static readonly string[] TraceKeys = ["trace_id", "traceid", "correlation_id", "correlationid", "request_id"];
    private static readonly string[] MessageKeys = ["message", "msg", "log", "text", "messagebody"];
    private static readonly string[] SyslogTimestampFormats = ["MMM d HH:mm:ss", "MMM dd HH:mm:ss"];

    // Momkn business field keys (promoted to first-class fields, excluded from generic payload)
    private static readonly string[] MomknFieldKeys = [
        "correlationid", "module", "method", "source", "destination",
        "brn", "billingaccount", "denominationid", "accountid", "ip",
        "entity", "statuscode"
    ];

    public NormalizedLogEntry Normalize(ParsedLogEntry parsedLogEntry)
    {
        var fields = new Dictionary<string, string>(parsedLogEntry.Fields, StringComparer.OrdinalIgnoreCase);

        var timestamp = ParseTimestamp(GetFirst(fields, TimestampKeys) ?? parsedLogEntry.Raw.IngestedAtUtc.ToString("O", CultureInfo.InvariantCulture), parsedLogEntry.Raw.IngestedAtUtc);
        var severity = NormalizeSeverity(GetFirst(fields, SeverityKeys) ?? "INFO");
        var serviceName = GetFirst(fields, ServiceKeys) ?? parsedLogEntry.Raw.SourceId;
        var traceId = GetFirst(fields, TraceKeys) ?? "n/a";
        var message = GetFirst(fields, MessageKeys) ?? parsedLogEntry.Message;

        // Extract Momkn business fields
        var correlationId = GetFieldValue(fields, "CorrelationId") ?? traceId;
        var module = GetFieldValue(fields, "Module") ?? "";
        var method = GetFieldValue(fields, "Method") ?? "";
        var logSource = GetFieldValue(fields, "Source") ?? "";
        var destination = GetFieldValue(fields, "Destination") ?? "";
        var brn = GetFieldValue(fields, "BRN");
        var billingAccount = GetFieldValue(fields, "BillingAccount");
        var denominationId = GetFieldValue(fields, "DenominationId");
        var ip = GetFieldValue(fields, "IP");

        // Extract AccountId from ExtraData if present (supports arbitrary array/object nesting safely)
        string? accountId = null;
        if (fields.TryGetValue("ExtraData", out var extraData) && !string.IsNullOrWhiteSpace(extraData) && extraData != "null")
        {
            try
            {
                using var doc = JsonDocument.Parse(extraData);
                accountId = FindAccountIdInJsonElement(doc.RootElement);
            }
            catch (JsonException) { /* ExtraData is not valid JSON, skip */ }
        }

        // Extract StatusCode from MessageBody if it's a JSON object containing StatusCode
        int? statusCode = null;
        if (!string.IsNullOrWhiteSpace(message) && message.TrimStart().StartsWith('{'))
        {
            try
            {
                using var msgDoc = JsonDocument.Parse(message);
                if (msgDoc.RootElement.ValueKind == JsonValueKind.Object && msgDoc.RootElement.TryGetProperty("StatusCode", out var sc))
                {
                    statusCode = sc.TryGetInt32(out var code) ? code : null;
                }
            }
            catch (JsonException) { /* Not JSON, skip */ }
        }

        var allPromotedKeys = TimestampKeys
            .Concat(SeverityKeys)
            .Concat(ServiceKeys)
            .Concat(TraceKeys)
            .Concat(MessageKeys)
            .Concat(MomknFieldKeys)
            .Append("ExtraData")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var payload = fields
            .Where(kvp => !allPromotedKeys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var logHash = ComputeSha256($"{parsedLogEntry.Raw.SourceId}|{parsedLogEntry.Raw.RawText}");

        return new NormalizedLogEntry(
            parsedLogEntry.Raw.SourceId,
            parsedLogEntry.Raw.SourceType,
            timestamp,
            severity,
            serviceName,
            traceId,
            message,
            logHash,
            payload,
            CorrelationId: correlationId,
            Module: module,
            Method: method,
            LogSource: logSource,
            Destination: destination,
            BRN: brn,
            BillingAccount: billingAccount,
            DenominationId: denominationId,
            AccountId: accountId,
            IP: ip,
            StatusCode: statusCode);
    }

    private static string? FindAccountIdInJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindAccountIdInJsonElement(item);
                if (found != null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("Key", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() == "AccountId"
                && element.TryGetProperty("Value", out var val))
            {
                return val.ValueKind == JsonValueKind.String ? val.GetString() : val.GetRawText();
            }

            foreach (var prop in element.EnumerateObject())
            {
                var found = FindAccountIdInJsonElement(prop.Value);
                if (found != null)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static string? GetFieldValue(IReadOnlyDictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) && value != "null"
            ? value
            : null;
    }

    private static DateTimeOffset ParseTimestamp(string value, DateTimeOffset fallback)
    {
        var normalized = NormalizeTimestampText(value);
        if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        if (DateTime.TryParseExact(normalized, SyslogTimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var syslog))
        {
            var utc = new DateTime(
                fallback.UtcDateTime.Year,
                syslog.Month,
                syslog.Day,
                syslog.Hour,
                syslog.Minute,
                syslog.Second,
                DateTimeKind.Utc);

            return new DateTimeOffset(utc);
        }

        return fallback;
    }

    private static string NormalizeTimestampText(string value)
    {
        var compact = value.Trim().Trim('"');
        compact = Regex.Replace(compact, "\\s*:\\s*", ":");
        compact = Regex.Replace(compact, "\\s+", " ");
        return compact;
    }

    private static string NormalizeSeverity(string severity)
    {
        var normalized = severity.Trim().ToUpperInvariant();
        return normalized switch
        {
            "WARN" => "WARNING",
            "ERR" => "ERROR",
            "CRIT" => "CRITICAL",
            _ => normalized,
        };
    }

    private static string? GetFirst(IReadOnlyDictionary<string, string> fields, IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed class SlidingWindowLogChunker : ILogChunker
{
    private readonly ChunkingOptions _options;

    public SlidingWindowLogChunker(IOptions<ChunkingOptions> options)
    {
        _options = options.Value;
    }

    public IReadOnlyList<LogChunk> Chunk(NormalizedLogEntry normalizedLogEntry)
    {
        var chunkSize = Math.Max(8, _options.ChunkSizeTokens);
        var overlap = Math.Clamp(_options.OverlapTokens, 0, chunkSize - 1);
        var step = Math.Max(1, chunkSize - overlap);

        var rendered = $"{normalizedLogEntry.TimestampUtc:O} [{normalizedLogEntry.Severity}] {normalizedLogEntry.ServiceName} trace={normalizedLogEntry.TraceId} {normalizedLogEntry.Message}";
        var payloadSuffix = normalizedLogEntry.Payload.Count == 0
            ? ""
            : $" payload={JsonSerializer.Serialize(normalizedLogEntry.Payload)}";

        var tokens = (rendered + payloadSuffix).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var chunks = new List<LogChunk>();
        var chunkIndex = 0;
        for (var start = 0; start < tokens.Length; start += step)
        {
            var slice = tokens.Skip(start).Take(chunkSize);
            var text = string.Join(' ', slice);
            if (text.Length == 0)
            {
                continue;
            }

            chunks.Add(new LogChunk(
                ChunkId: $"{normalizedLogEntry.LogHash}-{chunkIndex:D4}",
                LogHash: normalizedLogEntry.LogHash,
                Text: text,
                TimestampUtc: normalizedLogEntry.TimestampUtc,
                Severity: normalizedLogEntry.Severity,
                ServiceName: normalizedLogEntry.ServiceName,
                TraceId: normalizedLogEntry.TraceId,
                SourceId: normalizedLogEntry.SourceId,
                SourceType: normalizedLogEntry.SourceType,
                Payload: normalizedLogEntry.Payload,
                CorrelationId: normalizedLogEntry.CorrelationId,
                Module: normalizedLogEntry.Module,
                Method: normalizedLogEntry.Method,
                LogSource: normalizedLogEntry.LogSource,
                Destination: normalizedLogEntry.Destination,
                BRN: normalizedLogEntry.BRN,
                BillingAccount: normalizedLogEntry.BillingAccount,
                DenominationId: normalizedLogEntry.DenominationId,
                AccountId: normalizedLogEntry.AccountId,
                IP: normalizedLogEntry.IP,
                StatusCode: normalizedLogEntry.StatusCode));

            chunkIndex++;
            if (start + chunkSize >= tokens.Length)
            {
                break;
            }
        }

        return chunks;
    }
}

public sealed class IngestionOrchestrator : IIngestionOrchestrator
{
    private readonly ILogSourceRegistry _sourceRegistry;
    private readonly IGenericLogParser _parser;
    private readonly ILogNormalizer _normalizer;
    private readonly ILogChunker _chunker;
    private readonly ILogEntryFilter _logEntryFilter;
    private readonly IPiiRedactor _piiRedactor;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IngestionOptions _ingestionOptions;
    private readonly IOptions<VectorStoreOptions> _vectorStoreOptions;
    private readonly ILogger<IngestionOrchestrator> _logger;

    public IngestionOrchestrator(
        ILogSourceRegistry sourceRegistry,
        IGenericLogParser parser,
        ILogNormalizer normalizer,
        ILogChunker chunker,
        ILogEntryFilter logEntryFilter,
        IPiiRedactor piiRedactor,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IOptions<IngestionOptions> ingestionOptions,
        IOptions<VectorStoreOptions> vectorStoreOptions,
        ILogger<IngestionOrchestrator> logger)
    {
        _sourceRegistry = sourceRegistry;
        _parser = parser;
        _normalizer = normalizer;
        _chunker = chunker;
        _logEntryFilter = logEntryFilter;
        _piiRedactor = piiRedactor;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _ingestionOptions = ingestionOptions.Value;
        _vectorStoreOptions = vectorStoreOptions;
        _logger = logger;
    }

    public async Task<IngestionRunResult> IngestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _vectorStore.EnsureCollectionAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed while ensuring Qdrant collection.", ex);
        }

        var rawLogsRead = 0;
        var filteredLogs = 0;
        var chunksCreated = 0;
        var vectorsUpserted = 0;
        var processingBatchSize = Math.Max(1, _ingestionOptions.ProcessingBatchSize);
        var pendingChunks = new List<LogChunk>(processingBatchSize);

        foreach (var source in _sourceRegistry.GetSources())
        {
            await foreach (var rawLog in source.ReadAsync(cancellationToken))
            {
                rawLogsRead++;
                if (_logEntryFilter.ShouldDrop(rawLog))
                {
                    filteredLogs++;
                    continue;
                }

                var parsed = _parser.Parse(rawLog);
                var normalized = _normalizer.Normalize(parsed);
                var chunks = _chunker.Chunk(normalized);
                if (chunks.Count == 0)
                {
                    continue;
                }

                // Apply PII redaction before embedding
                var redactedChunks = chunks.Select(c => c with { Text = _piiRedactor.Redact(c.Text) }).ToList();

                chunksCreated += redactedChunks.Count;
                pendingChunks.AddRange(redactedChunks);

                if (pendingChunks.Count >= processingBatchSize)
                {
                    vectorsUpserted += await UpsertBatchAsync(pendingChunks, cancellationToken);
                    pendingChunks.Clear();
                }
            }
        }

        if (pendingChunks.Count > 0)
        {
            vectorsUpserted += await UpsertBatchAsync(pendingChunks, cancellationToken);
            pendingChunks.Clear();
        }

        if (chunksCreated == 0)
        {
            _logger.LogInformation("Ingestion completed with no chunks.");
            return new IngestionRunResult(rawLogsRead, 0, 0, DateTimeOffset.UtcNow);
        }

        try
        {
            await _vectorStore.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-_vectorStoreOptions.Value.RetentionDays), cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed while applying Qdrant retention cleanup.", ex);
        }

        _logger.LogInformation(
            "Ingestion completed. RawLogsRead={RawLogsRead}, FilteredLogs={FilteredLogs}, ChunksCreated={ChunksCreated}, VectorsUpserted={VectorsUpserted}",
            rawLogsRead,
            filteredLogs,
            chunksCreated,
            vectorsUpserted);

        return new IngestionRunResult(rawLogsRead, chunksCreated, vectorsUpserted, DateTimeOffset.UtcNow);
    }

    private async Task<int> UpsertBatchAsync(IReadOnlyList<LogChunk> chunks, CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await _embeddingService.EmbedTextsAsync(chunks.Select(chunk => chunk.Text).ToArray(), cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed while generating embeddings from Ollama for batch size {chunks.Count}.", ex);
        }

        var vectorPoints = new List<VectorPoint>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            vectorPoints.Add(new VectorPoint(CreateQdrantPointId(chunks[i].ChunkId), embeddings[i], chunks[i]));
        }

        try
        {
            await _vectorStore.UpsertAsync(vectorPoints, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed while upserting vectors to Qdrant for batch size {chunks.Count}.", ex);
        }

        return vectorPoints.Count;
    }

    private static string CreateQdrantPointId(string chunkId)
    {
        // Qdrant accepts UUID string IDs reliably across versions.
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(chunkId));
        return new Guid(hash).ToString();
    }
}

public sealed class IngestionHostedService : BackgroundService
{
    private readonly IIngestionOrchestrator _orchestrator;
    private readonly SchedulerOptions _options;
    private readonly SemaphoreSlim _ingestLock = new(1, 1);
    private readonly ILogger<IngestionHostedService> _logger;

    public IngestionHostedService(IIngestionOrchestrator orchestrator, IOptions<SchedulerOptions> options, ILogger<IngestionHostedService> logger)
    {
        _orchestrator = orchestrator;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>();
        if (_options.EnableDailyBatch)
        {
            loops.Add(RunDailyBatchLoopAsync(stoppingToken));
        }

        if (_options.EnableStreaming)
        {
            loops.Add(RunStreamingLoopAsync(stoppingToken));
        }

        if (loops.Count == 0)
        {
            _logger.LogInformation("Ingestion scheduler is disabled.");
            return;
        }

        await Task.WhenAll(loops);
    }

    private async Task RunDailyBatchLoopAsync(CancellationToken cancellationToken)
    {
        if (_options.RunBatchOnStartup)
        {
            try
            {
                await RunIngestionAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup ingestion failed. Check if Qdrant and Ollama are running.");
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, _options.DailyBatchIntervalHours)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RunIngestionAsync(cancellationToken);
        }
    }

    private async Task RunStreamingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.StreamingPollSeconds)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RunIngestionAsync(cancellationToken);
        }
    }

    private async Task RunIngestionAsync(CancellationToken cancellationToken)
    {
        if (!await _ingestLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            await _orchestrator.IngestAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingestion failed. Ensure Qdrant is running on localhost:6333 and Ollama on localhost:11434");
        }
        finally
        {
            _ingestLock.Release();
        }
    }
}
