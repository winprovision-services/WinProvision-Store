using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public interface IOperationTextProvider
{
    event EventHandler? Changed;
    string Language { get; }
    string GetText(string key);
    string Format(string key, IReadOnlyDictionary<string, string?>? arguments = null);
    string FormatPlural(string key, int count, IReadOnlyDictionary<string, string?>? arguments = null);
    void SetLanguage(string language);
}

/// <summary>Resolves operation UI text from language resources and user overrides.</summary>
public sealed class OperationTextProvider : IOperationTextProvider
{
    private const string ResourcePrefix = "WinProvision.Store.Assets.Localization.operations.";
    private const string DefaultLanguage = "pt-BR";
    private static readonly Regex PlaceholderPattern = new("\\{([^{}]+)\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedPlaceholders = new(StringComparer.Ordinal)
        { "packageName", "version", "completed", "total", "failed", "canceled", "position", "value" };
    private readonly object _gate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private readonly Action<string>? _testLog;
    private IReadOnlyDictionary<string, string> _user = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _language = new Dictionary<string, string>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _ptBr = new Dictionary<string, string>(StringComparer.Ordinal);
    private string _languageId = DefaultLanguage;

    public static string UserOverlayDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStoreWinUI3", "Localization");
    public static string UserOverlayPath(string language) => Path.Combine(UserOverlayDirectory, $"operations.{NormalizeLanguage(language)}.json");

    public OperationTextProvider() => SetLanguage(DefaultLanguage, raiseChanged: false);
    private OperationTextProvider(Action<string>? testLog) => _testLog = testLog;

    public event EventHandler? Changed;
    public string Language { get { lock (_gate) return _languageId; } }

    public void SetLanguage(string language) => SetLanguage(language, raiseChanged: true);

    private void SetLanguage(string language, bool raiseChanged)
    {
        string normalized = NormalizeLanguage(language);
        IReadOnlyDictionary<string, string> ptBr = LoadEmbedded(DefaultLanguage);
        IReadOnlyDictionary<string, string> translations = string.Equals(normalized, DefaultLanguage, StringComparison.OrdinalIgnoreCase)
            ? ptBr : LoadEmbedded(normalized);
        IReadOnlyDictionary<string, string> user = LoadUser(normalized);
        lock (_gate)
        {
            _languageId = normalized;
            _ptBr = ptBr;
            _language = translations;
            _user = user;
        }
        if (raiseChanged) Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static OperationTextProvider CreateForTests(string ptBrJson, string? selectedJson,
        string? userJson, string language, Action<string>? log = null)
    {
        var provider = new OperationTextProvider(log);
        provider._ptBr = ParseJson(ptBrJson);
        provider._language = selectedJson is null ? new Dictionary<string, string>(StringComparer.Ordinal) : ParseJson(selectedJson);
        provider._languageId = NormalizeLanguage(language);
        if (!string.IsNullOrWhiteSpace(userJson))
        {
            try { provider._user = ParseJson(userJson); }
            catch (JsonException exception)
            {
                provider.LogOnce("test-user", $"Sobreposição de idioma inválida; usando recursos embutidos. {exception.Message}");
            }
        }
        return provider;
    }

    public string GetText(string key)
    {
        lock (_gate)
        {
            if (_user.TryGetValue(key, out string? userText)) return userText;
            if (_language.TryGetValue(key, out string? languageText)) return languageText;
            if (_ptBr.TryGetValue(key, out string? fallback)) return fallback;
        }
        LogOnce($"missing:{key}", $"Chave de texto de operações ausente: {key}");
        return key;
    }

    /// <summary>Parses a language overlay into an immutable candidate without changing the live provider.</summary>
    public bool TryBuildUserOverlay(string language, string? json, out IReadOnlyDictionary<string, string> candidate)
    {
        if (json is null)
        {
            candidate = new Dictionary<string, string>(StringComparer.Ordinal);
            return true;
        }
        try
        {
            candidate = ParseJson(json);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            LogOnce($"reload-user:{NormalizeLanguage(language)}", $"Sobreposição de idioma inválida; mantendo o último idioma válido: {exception.Message}");
            candidate = new Dictionary<string, string>(StringComparer.Ordinal);
            return false;
        }
    }

    /// <summary>Publishes a previously validated overlay. Call on the UI dispatcher.</summary>
    public void ReplaceUserOverlay(string language, IReadOnlyDictionary<string, string> candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (!string.Equals(_languageId, NormalizeLanguage(language), StringComparison.OrdinalIgnoreCase)) return;
            _user = new Dictionary<string, string>(candidate, StringComparer.Ordinal);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string FormatPlural(string key, int count, IReadOnlyDictionary<string, string?>? arguments = null)
    {
        string pluralKey = $"{key}.{(count == 1 ? "one" : "other")}";
        var values = new Dictionary<string, string?>(arguments ?? new Dictionary<string, string?>(), StringComparer.Ordinal)
        {
            ["completed"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return Format(pluralKey, values);
    }

    public string Format(string key, IReadOnlyDictionary<string, string?>? arguments = null)
    {
        string text = GetText(key);
        var values = arguments ?? new Dictionary<string, string?>();
        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            string name = match.Groups[1].Value;
            if (!AllowedPlaceholders.Contains(name))
            {
                LogOnce($"placeholder-unknown:{key}:{name}", $"Placeholder desconhecido '{name}' no texto de operações '{key}'.");
                continue;
            }
            if (!values.TryGetValue(name, out string? value))
            {
                LogOnce($"placeholder-missing:{key}:{name}", $"Valor do placeholder '{name}' ausente no texto de operações '{key}'.");
                continue;
            }
            text = text.Replace(match.Value, value, StringComparison.Ordinal);
        }
        return text;
    }

    private IReadOnlyDictionary<string, string> LoadEmbedded(string language)
    {
        string resource = ResourcePrefix + language + ".json";
        try
        {
            using Stream stream = typeof(OperationTextProvider).Assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException($"Recurso de operações não encontrado: {resource}");
            return ParseDictionary(stream);
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            LogOnce($"embedded:{language}", $"Não foi possível carregar o idioma de operações {language}; o fallback será usado.", exception);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private IReadOnlyDictionary<string, string> LoadUser(string language)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3", "Localization", $"operations.{language}.json");
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            using FileStream stream = File.OpenRead(path);
            return ParseDictionary(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            LogOnce($"user:{path}", $"Sobreposição de idioma de operações inválida; usando recursos embutidos: {path}", exception);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseDictionary(Stream stream)
    {
        using JsonDocument document = JsonDocument.Parse(stream);
        return ParseDocument(document);
    }

    private static IReadOnlyDictionary<string, string> ParseJson(string json)
    {
        json = json.TrimStart('\uFEFF');
        using JsonDocument document = JsonDocument.Parse(json);
        return ParseDocument(document);
    }

    private static IReadOnlyDictionary<string, string> ParseDocument(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("O arquivo de textos deve ser um objeto JSON.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) throw new JsonException($"O valor de '{property.Name}' deve ser texto.");
            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        return values;
    }

    private static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language)) return DefaultLanguage;
        string candidate = language.Trim();
        try { return System.Globalization.CultureInfo.GetCultureInfo(candidate).Name; }
        catch (System.Globalization.CultureNotFoundException) { return DefaultLanguage; }
    }

    private void LogOnce(string id, string message, Exception? exception = null)
    {
        lock (_gate)
            if (!_logged.Add(id)) return;
        if (_testLog is not null) _testLog(message);
        else if (exception is null) CoreLog.Warning(new InvalidDataException(message), "Operations.Localization");
        else CoreLog.Warning(exception, "Operations.Localization");
    }
}
