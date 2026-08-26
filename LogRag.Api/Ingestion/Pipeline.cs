using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LogRag.Api.Configuration;
using LogRag.Api.Domain;
using LogRag.Api.Embedding;
using LogRag.Api.Sources;
using LogRag.Api.Telemetry;
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
    Task<IngestionRunResult> IngestAsync(IngestionTimeWindow? timeWindow, string? collectionName, CancellationToken cancellationToken);
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

public sealed partial class GenericLogParser : IGenericLogParser
{
    private static readonly string[] CsvColumns = ["timestamp", "severity", "service_name", "trace_id", "message"];

    // PERF: Source-generated regex — compiled at build time, 3-5x faster than RegexOptions.Compiled.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2}(?:[ T]\d{2}\s*:\s*\d{2}\s*:\s*\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?)?)\s+(?<message>[\s\S]+)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefixedRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^(?<severity>TRACE|DEBUG|INFO|WARN|WARNING|ERROR|CRITICAL|FATAL)\b[:\- ]*(?<message>[\s\S]*)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial Regex SeverityPrefixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"Correlation[_ ]ID[:=]\s*(?<trace>[a-zA-Z0-9\-:]+)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial Regex CorrelationRegex();

    [System.Text.RegularExpressions.GeneratedRegex(
        @"requestId:\s*(?<request>[^\s,]+)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial Regex RequestIdRegex();

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
        var tsMatch = TimestampPrefixedRegex().Match(text);

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
        var match = TimestampPrefixedRegex().Match(text);
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

        var severityMatch = SeverityPrefixRegex().Match(message);
        if (severityMatch.Success)
        {
            map["severity"] = severityMatch.Groups["severity"].Value.Trim();
            map["message"] = severityMatch.Groups["message"].Value.Trim();
        }

        var correlationMatch = CorrelationRegex().Match(text);
        if (correlationMatch.Success)
        {
            map["trace_id"] = correlationMatch.Groups["trace"].Value.Trim();
        }
        else
        {
            var requestIdMatch = RequestIdRegex().Match(text);
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

    // PERF: BPE tokenizers like nomic-embed-text average ~4 characters per token.
    // Using character-based estimation is significantly more accurate than word-split.
    private const double CharsPerToken = 4.0;

    public SlidingWindowLogChunker(IOptions<ChunkingOptions> options)
    {
        _options = options.Value;
    }

    public IReadOnlyList<LogChunk> Chunk(NormalizedLogEntry normalizedLogEntry)
    {
        var targetTokens = Math.Max(8, _options.ChunkSizeTokens);
        var overlapTokens = Math.Clamp(_options.OverlapTokens, 0, targetTokens - 1);
        var targetChars = (int)(targetTokens * CharsPerToken);
        var overlapChars = (int)(overlapTokens * CharsPerToken);
        var stepChars = Math.Max(1, targetChars - overlapChars);

        var rendered = $"{normalizedLogEntry.TimestampUtc:O} [{normalizedLogEntry.Severity}] {normalizedLogEntry.ServiceName} trace={normalizedLogEntry.TraceId} {normalizedLogEntry.Message}";
        var payloadSuffix = normalizedLogEntry.Payload.Count == 0
            ? ""
            : $" payload={JsonSerializer.Serialize(normalizedLogEntry.Payload)}";

        var fullText = rendered + payloadSuffix;
        if (fullText.Length == 0)
        {
            return [];
        }

        // PERF: For short texts that fit in one chunk, skip the sliding window entirely.
        if (fullText.Length <= targetChars)
        {
            return [CreateLogChunk(normalizedLogEntry, fullText, 0)];
        }

        var chunks = new List<LogChunk>();
        var chunkIndex = 0;
        var start = 0;

        while (start < fullText.Length)
        {
            var end = Math.Min(start + targetChars, fullText.Length);

            // PERF: Try to break at a sentence boundary for semantic coherence.
            // Look backwards from the target end for a period, newline, or double-newline.
            if (end < fullText.Length)
            {
                var breakPoint = FindBestBreakPoint(fullText, start, end);
                if (breakPoint > start)
                {
                    end = breakPoint;
                }
            }

            var text = fullText[start..end].Trim();
            if (text.Length > 0)
            {
                chunks.Add(CreateLogChunk(normalizedLogEntry, text, chunkIndex));
                chunkIndex++;
            }


            if (end >= fullText.Length)
            {
                break;
            }

            start += stepChars;
            // Don't let start get stuck
            if (start >= fullText.Length || end - start < 4)
            {
                break;
            }
        }

        return chunks;
    }

    /// <summary>
    /// Find the best position to break text — prefers sentence boundaries
    /// (period+space, newline) for semantic coherence of chunks.
    /// </summary>
    private static int FindBestBreakPoint(string text, int start, int maxEnd)
    {
        var searchStart = start + (maxEnd - start) / 4; // Search the last 75% of the window
        if (searchStart >= maxEnd) searchStart = start;

        // Priority 1: double newline (paragraph break)
        for (var i = maxEnd - 1; i >= searchStart; i--)
        {
            if (i + 1 < text.Length && text[i] == '\n' && text[i + 1] == '\n')
            {
                return i + 2;
            }
        }

        // Priority 2: period + space (sentence boundary)
        for (var i = maxEnd - 1; i >= searchStart; i--)
        {
            if (text[i] == '.' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                return i + 2;
            }
        }

        // Priority 3: single newline
        for (var i = maxEnd - 1; i >= searchStart; i--)
        {
            if (text[i] == '\n')
            {
                return i + 1;
            }
        }

        // Priority 4: space (word boundary)
        for (var i = maxEnd - 1; i >= searchStart; i--)
        {
            if (text[i] == ' ')
            {
                return i + 1;
            }
        }

        return maxEnd; // No good break found, use the character limit
    }

    private static LogChunk CreateLogChunk(NormalizedLogEntry entry, string text, int chunkIndex)
    {
        return new LogChunk(
            ChunkId: $"{entry.LogHash}-{chunkIndex:D4}",
            LogHash: entry.LogHash,
            Text: text,
            TimestampUtc: entry.TimestampUtc,
            Severity: entry.Severity,
            ServiceName: entry.ServiceName,
            TraceId: entry.TraceId,
            SourceId: entry.SourceId,
            SourceType: entry.SourceType,
            Payload: entry.Payload,
            CorrelationId: entry.CorrelationId,
            Module: entry.Module,
            Method: entry.Method,
            LogSource: entry.LogSource,
            Destination: entry.Destination,
            BRN: entry.BRN,
            BillingAccount: entry.BillingAccount,
            DenominationId: entry.DenominationId,
            AccountId: entry.AccountId,
            IP: entry.IP,
            StatusCode: entry.StatusCode);
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

    public async Task<IngestionRunResult> IngestAsync(IngestionTimeWindow? timeWindow, string? collectionName, CancellationToken cancellationToken)
    {
        using var activity = LogRagTelemetry.ActivitySource.StartActivity("ingest", ActivityKind.Internal);
        activity?.SetTag("collection", collectionName ?? "default");
        var sw = Stopwatch.StartNew();

        // Auto-generate collection name for time-windowed ingestion
        var resolvedCollection = collectionName;
        if (string.IsNullOrWhiteSpace(resolvedCollection) && timeWindow is { IsEmpty: false } && timeWindow.FromUtc is not null)
        {
            resolvedCollection = $"log_chunks_{timeWindow.FromUtc.Value:yyyyMMdd}";
        }

        try
        {
            await _vectorStore.EnsureCollectionAsync(resolvedCollection, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed while ensuring Qdrant collection.", ex);
        }

        var rawLogsRead = 0;
        var filteredLogs = 0;
        var chunksCreated = 0;
        var vectorsUpserted = 0;

        var normalizedEntries = new List<NormalizedLogEntry>();
        var entryIds = new List<HashSet<string>>();
        var dsu = new DisjointSet();

        // PERF: Read all log sources in parallel (IO-bound, independent files).
        // Each source gets a 120s timeout — prevents hanging on unreachable endpoints.
        var sources = _sourceRegistry.GetSources();
        var readLock = new object();
        var sourceTimeout = TimeSpan.FromSeconds(120);
        var readTasks = sources.Select(async source =>
        {
            using var sourceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sourceCts.CancelAfter(sourceTimeout);
            try
            {
                await foreach (var rawLog in source.ReadAsync(timeWindow, sourceCts.Token))
                {
                    Interlocked.Increment(ref rawLogsRead);
                    if (_logEntryFilter.ShouldDrop(rawLog))
                    {
                        Interlocked.Increment(ref filteredLogs);
                        continue;
                    }

                    var parsed = _parser.Parse(rawLog);
                    var normalized = _normalizer.Normalize(parsed);

                    var ids = LogIdExtractor.ExtractIds(normalized);

                    // Thread-safe collection append
                    lock (readLock)
                    {
                        if (ids.Count > 1)
                        {
                            var idList = ids.ToList();
                            for (int i = 1; i < idList.Count; i++)
                            {
                                dsu.Union(idList[0], idList[i]);
                            }
                        }
                        normalizedEntries.Add(normalized);
                        entryIds.Add(ids);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Source {SourceId} timed out or was cancelled after {Timeout}s",
                    source.Id, sourceTimeout.TotalSeconds);
            }
        });

        await Task.WhenAll(readTasks);

        // PERF: For sources without server-side time filtering (file, http),
        // filter entries at the orchestrator level after normalization.
        // ES and SQL already filtered server-side, but this is a cheap safety net.
        if (timeWindow is { IsEmpty: false })
        {
            // DIAGNOSTIC: log sample timestamps so user can verify parsing
            if (normalizedEntries.Count > 0)
            {
                var samples = normalizedEntries.Take(Math.Min(5, normalizedEntries.Count))
                    .Select(e => e.TimestampUtc.ToString("O"))
                    .ToArray();
                _logger.LogInformation(
                    "Time-window filter: window=[{From}, {To}], total entries={Total}. Sample timestamps: {Samples}",
                    timeWindow.FromUtc?.ToString("O") ?? "beginning",
                    timeWindow.ToUtc?.ToString("O") ?? "now",
                    normalizedEntries.Count,
                    string.Join(", ", samples));
            }

            var inWindow = new List<(NormalizedLogEntry Entry, HashSet<string> Ids)>();
            for (int i = 0; i < normalizedEntries.Count; i++)
            {
                if (timeWindow.Contains(normalizedEntries[i].TimestampUtc))
                {
                    inWindow.Add((normalizedEntries[i], entryIds[i]));
                }
            }

            if (inWindow.Count < normalizedEntries.Count)
            {
                _logger.LogInformation(
                    "Time-window filter dropped {Dropped}/{Total} entries outside [{From}, {To}]",
                    normalizedEntries.Count - inWindow.Count,
                    normalizedEntries.Count,
                    timeWindow.FromUtc?.ToString("O") ?? "beginning",
                    timeWindow.ToUtc?.ToString("O") ?? "now");


                normalizedEntries = inWindow.Select(x => x.Entry).ToList();
                entryIds = inWindow.Select(x => x.Ids).ToList();
            }
            else
            {
                _logger.LogInformation(
                    "Time-window filter: all {Count} entries within window, proceeding.",
                    normalizedEntries.Count);
            }
        }

        if (normalizedEntries.Count == 0)
        {
            _logger.LogInformation("Ingestion completed with no chunks.");
            return new IngestionRunResult(rawLogsRead, 0, 0, DateTimeOffset.UtcNow);
        }

        var components = dsu.GetComponents();
        var processingBatchSize = Math.Max(1, _ingestionOptions.ProcessingBatchSize);
        var pendingChunks = new List<LogChunk>(processingBatchSize);

        for (int i = 0; i < normalizedEntries.Count; i++)
        {
            var entry = normalizedEntries[i];
            var ids = entryIds[i];

            var allLinked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var root = dsu.Find(id);
                if (components.TryGetValue(root, out var set))
                {
                    foreach (var member in set)
                    {
                        allLinked.Add(member);
                    }
                }
            }

            var updatedPayload = new Dictionary<string, string>(entry.Payload, StringComparer.OrdinalIgnoreCase);
            if (allLinked.Count > 0)
            {
                updatedPayload["linked_ids"] = string.Join(",", allLinked);
            }

            var traceId = entry.TraceId;
            if (string.Equals(traceId, "n/a", StringComparison.OrdinalIgnoreCase) && allLinked.Count > 0)
            {
                var firstValid = allLinked.FirstOrDefault(id => id.Contains('-') || id.Length >= 16) ?? allLinked.FirstOrDefault();
                if (firstValid != null)
                {
                    traceId = firstValid;
                }
            }

            var updatedEntry = new NormalizedLogEntry(
                entry.SourceId,
                entry.SourceType,
                entry.TimestampUtc,
                entry.Severity,
                entry.ServiceName,
                traceId,
                entry.Message,
                entry.LogHash,
                updatedPayload);

            var chunks = _chunker.Chunk(updatedEntry);
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
                vectorsUpserted += await UpsertBatchAsync(pendingChunks, resolvedCollection, cancellationToken);
                pendingChunks.Clear();
            }
        }

        if (pendingChunks.Count > 0)
        {
            vectorsUpserted += await UpsertBatchAsync(pendingChunks, resolvedCollection, cancellationToken);
            pendingChunks.Clear();
        }

        try
        {
            await _vectorStore.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-_vectorStoreOptions.Value.RetentionDays), resolvedCollection, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed while applying Qdrant retention cleanup.", ex);
        }

        // PERF: After batch ingestion with wait=false, ensure all async upserts are visible
        // before the next query arrives.
        try
        {
            await _vectorStore.RefreshCollectionAsync(resolvedCollection, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Qdrant collection refresh after ingestion failed (non-fatal).");
        }

        _logger.LogInformation(
            "Ingestion completed. RawLogsRead={RawLogsRead}, FilteredLogs={FilteredLogs}, ChunksCreated={ChunksCreated}, VectorsUpserted={VectorsUpserted}",
            rawLogsRead,
            filteredLogs,
            chunksCreated,
            vectorsUpserted);

        // Record telemetry
        sw.Stop();
        LogRagTelemetry.IngestionRuns.Add(1);
        LogRagTelemetry.IngestionEntriesRead.Add(rawLogsRead);
        LogRagTelemetry.IngestionEntriesFiltered.Add(filteredLogs);
        LogRagTelemetry.IngestionChunksCreated.Add(chunksCreated);
        LogRagTelemetry.IngestionVectorsUpserted.Add(vectorsUpserted);
        LogRagTelemetry.IngestionDuration.Record(sw.Elapsed.TotalSeconds);
        MetricsSnapshot.RecordIngestion(rawLogsRead, filteredLogs, chunksCreated, vectorsUpserted);
        activity?.SetTag("entries", rawLogsRead);
        activity?.SetTag("chunks", chunksCreated);
        activity?.SetTag("duration_s", sw.Elapsed.TotalSeconds);

        return new IngestionRunResult(
            rawLogsRead,
            chunksCreated,
            vectorsUpserted,
            DateTimeOffset.UtcNow,
            resolvedCollection,
            timeWindow?.FromUtc?.ToString("O"),
            timeWindow?.ToUtc?.ToString("O"));
    }

    private async Task<int> UpsertBatchAsync(IReadOnlyList<LogChunk> chunks, string? collectionName, CancellationToken cancellationToken)
    {
        _logger.LogInformation("UpsertBatch: starting embed for {Count} chunks", chunks.Count);
        IReadOnlyList<float[]> embeddings;
        try
        {
            embeddings = await _embeddingService.EmbedTextsAsync(chunks.Select(chunk => chunk.Text).ToArray(), cancellationToken);
            _logger.LogInformation("UpsertBatch: embed complete, got {Count} vectors", embeddings.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertBatch: embed failed for {Count} chunks", chunks.Count);
            throw new InvalidOperationException($"Failed while generating embeddings from Ollama for batch size {chunks.Count}.", ex);
        }

        var vectorPoints = new List<VectorPoint>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            vectorPoints.Add(new VectorPoint(CreateQdrantPointId(chunks[i].ChunkId), embeddings[i], chunks[i]));
        }

        _logger.LogInformation("UpsertBatch: upserting {Count} points to collection {Collection}", vectorPoints.Count, collectionName ?? "default");
        try
        {
            await _vectorStore.UpsertAsync(vectorPoints, collectionName, cancellationToken);
            _logger.LogInformation("UpsertBatch: upsert complete for {Count} points", vectorPoints.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpsertBatch: upsert failed for {Count} points", vectorPoints.Count);
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
            var timeWindow = _options.IngestionTimeWindowHours > 0
                ? new IngestionTimeWindow(
                    FromUtc: DateTimeOffset.UtcNow.AddHours(-_options.IngestionTimeWindowHours),
                    ToUtc: null) // up to now
                : null;
            await _orchestrator.IngestAsync(timeWindow, null, cancellationToken);
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

public static partial class LogIdExtractor
{
    // PERF: Source-generated regex — compiled at build time, 3-5x faster.
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial Regex GuidRegex();

    // Catch common patterns like RequestId: 0HNLLJNFUULGS:00000001, requestId: 1017975, Correlation_ID: 90c5f56a..., BRN: 1017975
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:request_?id|correlation_?id|transaction_?id|trace_?id|brn)[:= ]+\s*([a-zA-Z0-9/\-._:]+)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueIdRegex();

    // Ocelot APM fields: Id: 2a5c25b568ce5cfb, TraceId: be254bae4286c4095a8cf68154ea94c2
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b(?:Id|TraceId|TransactionId|ParentId)[:= ]+\s*([a-fA-F0-9]{16,32})\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial Regex ApmIdRegex();

    // Kestrel/Ocelot request IDs: 0HNLLJNFUULGS:00000001
    [System.Text.RegularExpressions.GeneratedRegex(
        @"\b[a-zA-Z0-9]{8,20}:\d{4,10}\b",
        System.Text.RegularExpressions.RegexOptions.Compiled)]
    private static partial Regex KestrelRequestIdRegex();

    public static HashSet<string> ExtractIds(NormalizedLogEntry entry)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Add trace id if it's not n/a
        if (!string.IsNullOrWhiteSpace(entry.TraceId) && !entry.TraceId.Equals("n/a", StringComparison.OrdinalIgnoreCase))
        {
            ids.Add(entry.TraceId);
        }

        // 2. Scan message
        ExtractFromText(entry.Message, ids);

        // 3. Scan payload keys & values
        foreach (var kvp in entry.Payload)
        {
            if (IsIdKey(kvp.Key))
            {
                ids.Add(kvp.Value);
            }
            ExtractFromText(kvp.Value, ids);
        }

        // 4. Scan raw message/extra data if they are stored as JSON strings
        foreach (var val in entry.Payload.Values)
        {
            if (val.Contains("Key", StringComparison.OrdinalIgnoreCase) && val.Contains("Value", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(val, @"""Key""\s*:\s*""(?:RequestId|CorrelationId|TransactionId|BRN)""\s*,\s*""Value""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    ids.Add(match.Groups[1].Value);
                }
            }
        }

        return ids;
    }

    private static void ExtractFromText(string text, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (Match match in GuidRegex().Matches(text))
        {
            ids.Add(match.Value);
        }

        foreach (Match match in KeyValueIdRegex().Matches(text))
        {
            var val = match.Groups[1].Value.Trim().Trim('"');
            if (IsValidIdValue(val))
            {
                ids.Add(val);
            }
        }

        foreach (Match match in ApmIdRegex().Matches(text))
        {
            var val = match.Groups[1].Value.Trim().Trim('"');
            if (IsValidIdValue(val))
            {
                ids.Add(val);
            }
        }

        foreach (Match match in KestrelRequestIdRegex().Matches(text))
        {
            ids.Add(match.Value);
        }
    }

    private static bool IsIdKey(string key)
    {
        return key.Contains("trace", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("correlation", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("request", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("transaction", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("brn", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidIdValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Equals("null", StringComparison.OrdinalIgnoreCase) || 
            value.Equals("undefined", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("n/a", StringComparison.OrdinalIgnoreCase)) return false;

        if (double.TryParse(value, out _) && value.Length < 4) return false;

        return true;
    }
}

public class DisjointSet
{
    private readonly Dictionary<string, string> _parent = new(StringComparer.OrdinalIgnoreCase);

    public string Find(string i)
    {
        if (!_parent.TryGetValue(i, out var p))
        {
            _parent[i] = i;
            return i;
        }

        if (p.Equals(i, StringComparison.OrdinalIgnoreCase))
        {
            return i;
        }

        var root = Find(p);
        _parent[i] = root;
        return root;
    }

    public void Union(string i, string j)
    {
        var rootI = Find(i);
        var rootJ = Find(j);
        if (!rootI.Equals(rootJ, StringComparison.OrdinalIgnoreCase))
        {
            _parent[rootI] = rootJ;
        }
    }

    public Dictionary<string, HashSet<string>> GetComponents()
    {
        var components = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _parent.Keys)
        {
            var root = Find(key);
            if (!components.TryGetValue(root, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                components[root] = set;
            }
            set.Add(key);
        }
        return components;
    }
}
