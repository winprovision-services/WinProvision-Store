using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            value = HeaderPattern().Replace(value, "$1[REDACTED]");
            value = AssignmentPattern().Replace(value, "$1[REDACTED]");
            return ConnectionCodePattern().Replace(value, "$1[REDACTED]");
        }
        catch (RegexMatchTimeoutException) { return "[diagnostic omitted: redaction timeout]"; }
    }

    public static IReadOnlyList<string> RedactArguments(IReadOnlyList<string> arguments, out IReadOnlyList<string> secretValues)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        var safe = new string[arguments.Count];
        bool redactNext = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index] ?? string.Empty;
            if (redactNext)
            {
                if (argument.Length > 0) secrets.Add(argument);
                safe[index] = "[REDACTED]";
                redactNext = false;
                continue;
            }

            if (SensitiveArgumentPattern().IsMatch(argument))
            {
                int separator = argument.IndexOfAny(['=', ':']);
                if (separator >= 0)
                {
                    string value = argument[(separator + 1)..].Trim('"', '\'');
                    if (value.Length > 0) secrets.Add(value);
                    safe[index] = argument[..(separator + 1)] + "[REDACTED]";
                }
                else
                {
                    safe[index] = argument;
                    redactNext = true;
                }
            }
            else if (IsAuthorizationHeader(argument))
            {
                Match header = AuthorizationValuePattern().Match(argument);
                if (header.Success)
                    secrets.Add(header.Groups["value"].Value);
                safe[index] = "[REDACTED]";
            }
            else
            {
                safe[index] = Redact(argument);
            }
        }

        secretValues = secrets.OrderByDescending(value => value.Length).ToArray();
        return safe;
    }

    public static string Redact(string value, IReadOnlyList<string>? knownSecrets)
    {
        string safe = Redact(value);
        if (knownSecrets is null) return safe;
        foreach (string secret in knownSecrets)
        {
            if (secret.Length > 0)
                safe = safe.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }
        return safe;
    }

    public static JsonElement RedactJson(JsonElement value, IReadOnlyList<string>? knownSecrets = null)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(value.GetRawText());
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text) && text is not null)
                node = JsonValue.Create(Redact(text, knownSecrets));
            else
                RedactNode(node, knownSecrets);
            using JsonDocument document = JsonDocument.Parse(node?.ToJsonString() ?? "{}");
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using JsonDocument document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    public static AppLogEvent RedactEvent(AppLogEvent logEvent, IReadOnlyList<string>? knownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        AppLogException? exception = logEvent.Exception is null ? null : logEvent.Exception with
        {
            Type = Redact(logEvent.Exception.Type, knownSecrets),
            Message = Redact(logEvent.Exception.Message, knownSecrets),
            StackTrace = logEvent.Exception.StackTrace is { } stack ? Redact(stack, knownSecrets) : null
        };
        return logEvent with
        {
            Category = Redact(logEvent.Category, knownSecrets),
            Source = Redact(logEvent.Source, knownSecrets),
            Event = Redact(logEvent.Event, knownSecrets),
            Message = Redact(logEvent.Message, knownSecrets),
            Data = logEvent.Data.ValueKind == JsonValueKind.Undefined
                ? EmptyObject()
                : RedactJson(logEvent.Data, knownSecrets),
            Exception = exception
        };
    }

    private static JsonElement EmptyObject()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static void RedactNode(JsonNode? node, IReadOnlyList<string>? knownSecrets)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? child) in obj.ToArray())
            {
                if (SensitiveFieldPattern().IsMatch(key))
                    obj[key] = "[REDACTED]";
                else if (child is JsonValue childValue
                    && childValue.TryGetValue<string>(out string? text) && text is not null)
                    obj[key] = Redact(text, knownSecrets);
                else
                    RedactNode(child, knownSecrets);
            }
            return;
        }
        if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue itemValue
                    && itemValue.TryGetValue<string>(out string? text) && text is not null)
                    array[index] = Redact(text, knownSecrets);
                else
                    RedactNode(array[index], knownSecrets);
            }
        }
    }

    private static bool IsAuthorizationHeader(string value) =>
        value.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        && (value.Contains("bearer ", StringComparison.OrdinalIgnoreCase)
            || value.Contains("basic ", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[0-9A-Z]{16}|sk-(?:proj-)?[A-Za-z0-9_-]{32,})\b", RegexOptions.CultureInvariant, 200)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(\b(?:Authorization)\b\s*[:=]\s*(?:Bearer|Basic)\s+)[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex HeaderPattern();

    [GeneratedRegex("(\\b(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|private[_-]?key|password|passwd|connection[_-]?code|pairing[_-]?code|auth[_-]?code|authorization|secret|token|key)\\b[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s\\\"'&,;<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"(\bconnection\s+code\b\s*[:=]\s*)[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex ConnectionCodePattern();

    [GeneratedRegex(@"^(?:--?)(?:access[-_]?token|refresh[-_]?token|token|password|passwd|secret|client[-_]?secret|api[-_]?key|private[-_]?key|connection[-_]?code|pairing[-_]?code|auth[-_]?code|authorization)(?:$|[=:])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex SensitiveArgumentPattern();

    [GeneratedRegex(@"(?:Authorization\s*[:=]\s*)(?:Bearer|Basic)\s+(?<value>[^\s,;]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex AuthorizationValuePattern();

    [GeneratedRegex(@"^(?:access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|private[_-]?key|password|passwd|connection[_-]?code|pairing[_-]?code|auth[_-]?code|authorization|secret|token|key|.*(?:token|secret|password|passwd|authorization|connectioncode|pairingcode|authcode|apikey|privatekey))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 200)]
    private static partial Regex SensitiveFieldPattern();
}
