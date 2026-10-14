using System.Text.RegularExpressions;

namespace WinProvision.Core.Services;

/// <summary>Removes common credential formats before diagnostics are persisted or exported.</summary>
public static partial class SensitiveDataRedactor
{
    public static string Redact(string value)
    {
        try
        {
            value = TokenPattern().Replace(value, "[REDACTED]");
            value = BearerPattern().Replace(value, "$1[REDACTED]");
            return AssignmentPattern().Replace(value, "$1[REDACTED]");
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic omitted: redaction timeout]"; }
    }

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|sk-(?:proj-)?[A-Za-z0-9_-]{32,})\b", RegexOptions.CultureInvariant, 200)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex BearerPattern();

    [GeneratedRegex("(\\b(?:access_token|refresh_token|client_secret|api[_-]?key|password|token)\\b[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s\\\"'&,;<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex AssignmentPattern();
}
