using System.Text.RegularExpressions;

namespace LogRag.Api.Ingestion;

/// <summary>
/// Pre-embedding PII redaction to prevent JWT tokens, connection strings,
/// and other sensitive credentials from being stored in the vector database.
/// </summary>
public interface IPiiRedactor
{
    string Redact(string text);
}

public sealed class PiiRedactor : IPiiRedactor
{
    private static readonly (Regex Pattern, string Replacement)[] RedactionRules =
    [
        // JWT tokens (three base64 segments separated by dots)
        (new Regex(
            @"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+",
            RegexOptions.Compiled),
         "[REDACTED_JWT]"),

        // Connection strings containing passwords
        (new Regex(
            @"(?i)(password|pwd)\s*=\s*[^;""'\s]+",
            RegexOptions.Compiled),
         "$1=[REDACTED]"),

        // API keys in headers or query params
        (new Regex(
            @"(?i)(api[_-]?key|apikey|x-api-key)\s*[=:]\s*[A-Za-z0-9_\-]{16,}",
            RegexOptions.Compiled),
         "$1=[REDACTED_KEY]"),

        // Bearer tokens
        (new Regex(
            @"(?i)Bearer\s+[A-Za-z0-9_\-\.]{20,}",
            RegexOptions.Compiled),
         "Bearer [REDACTED_TOKEN]"),
    ];

    public string Redact(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var result = text;
        foreach (var (pattern, replacement) in RedactionRules)
        {
            result = pattern.Replace(result, replacement);
        }

        return result;
    }
}
