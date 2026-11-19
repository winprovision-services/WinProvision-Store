using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed record NoticeDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("placement")] string Placement,
    [property: JsonPropertyName("anchor")] string Anchor,
    [property: JsonPropertyName("titleKey")] string TitleKey,
    [property: JsonPropertyName("messageKey")] string MessageKey,
    [property: JsonPropertyName("secondaryKey")] string? SecondaryKey,
    [property: JsonPropertyName("dismissible")] bool Dismissible,
    [property: JsonPropertyName("dismissPersistence")] string DismissPersistence,
    [property: JsonPropertyName("showWhen")] ElevationSettingCondition? ShowWhen,
    [property: JsonPropertyName("actionKey")] string? ActionKey);

public sealed record NoticeCatalogDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("notices")] IReadOnlyList<NoticeDefinition> Notices);

public sealed class NoticeCatalog
{
    private static readonly NoticeDefinition[] FallbackNotices =
    [
        new("elevator-security", "warning", "page-top", "Elevator",
            "notice.elevator.title", "notice.elevator.message", "notice.elevator.secondary",
            false, "none", null, null),
        new("elevator-risk-changed", "warning", "page-top", "Elevator",
            "notice.riskChanged.title", "notice.riskChanged.message", null,
            false, "none",
            new(null, null, "dangerous", true, 1), null),
        new("lan-network-security", "warning", "group-top", "lan-server",
            "notice.lanNetwork.title", "notice.lanNetwork.message", null,
            true, "session", null, null),
        new("lan-credential-security", "warning", "group-top", "lan-credentials",
            "notice.lanCredentials.title", "notice.lanCredentials.message", null,
            true, "session", null, null)
    ];

    private NoticeCatalog(IReadOnlyList<NoticeDefinition> notices) => Notices = notices;

    public IReadOnlyList<NoticeDefinition> Notices { get; }
    public static NoticeCatalog Default { get; } = new(FallbackNotices);

    public static NoticeCatalog Load(string? json, Action<string>? log = null)
    {
        try
        {
            NoticeCatalogDocument? document = JsonSerializer.Deserialize(
                json ?? string.Empty, NoticeCatalogJsonContext.Default.NoticeCatalogDocument);
            if (document is null || document.SchemaVersion != 1 || document.Notices is null)
                throw new JsonException("Catálogo de avisos ausente ou com schemaVersion incompatível.");

            var notices = new List<NoticeDefinition>();
            foreach (NoticeDefinition? supplied in document.Notices)
            {
                if (supplied is null || !IsValid(supplied))
                {
                    log?.Invoke("Aviso inválido ou com características desconhecidas ignorado.");
                    continue;
                }

                notices.Add(supplied.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
                    || supplied.DismissPersistence.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? supplied with { Dismissible = false, DismissPersistence = "none" }
                    : supplied.Dismissible
                        ? supplied
                        : supplied with { DismissPersistence = "none" });
            }

            return new NoticeCatalog(notices
                .GroupBy(notice => notice.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            log?.Invoke($"Catálogo de avisos inválido; usando o padrão embutido. {exception.Message}");
            return Default;
        }
    }

    public IReadOnlyList<NoticeDefinition> GetApplicable(
        string placement,
        string anchor,
        IReadOnlyList<ElevationSettingDefinition> settings,
        IReadOnlyDictionary<string, bool> values,
        NoticeDismissalStore? dismissals = null)
    {
        return Notices.Where(notice =>
            notice.Placement.Equals(placement, StringComparison.OrdinalIgnoreCase)
            && notice.Anchor.Equals(anchor, StringComparison.OrdinalIgnoreCase)
            && (notice.ShowWhen is null
                || ElevationSettingConditionEvaluator.Evaluate(notice.ShowWhen, settings, values).IsSatisfied)
            && dismissals?.IsDismissed(notice) != true).ToArray();
    }

    private static bool IsValid(NoticeDefinition notice)
    {
        bool knownSeverity = notice.Severity is "info" or "warning" or "critical";
        bool knownPlacement = notice.Placement is "page-top" or "group-top";
        bool knownPersistence = notice.DismissPersistence is "none" or "session" or "permanent";
        return IsValidIdentifier(notice.Id)
            && knownSeverity
            && knownPlacement
            && IsValidIdentifier(notice.Anchor)
            && !string.IsNullOrWhiteSpace(notice.TitleKey)
            && !string.IsNullOrWhiteSpace(notice.MessageKey)
            && knownPersistence
            && (notice.ShowWhen is null || ElevationSettingConditionEvaluator.IsValid(notice.ShowWhen));
    }

    private static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}

public sealed class NoticeDismissalStore
{
    private readonly string _path;
    private readonly HashSet<string> _sessionDismissals = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _permanentDismissals = new(StringComparer.OrdinalIgnoreCase);

    public NoticeDismissalStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3", "notice-preferences.json");
        try
        {
            if (!File.Exists(_path))
                return;
            NoticeDismissalData? data = JsonSerializer.Deserialize(
                File.ReadAllText(_path), NoticeCatalogJsonContext.Default.NoticeDismissalData);
            if (data?.PermanentIds is not null)
                _permanentDismissals.UnionWith(data.PermanentIds.Where(IsValidId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            CoreLog.Warning(exception, "NoticeDismissalStore.Load");
        }
    }

    public bool IsDismissed(NoticeDefinition notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
            || !notice.Dismissible
            || notice.DismissPersistence.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;
        return notice.DismissPersistence.Equals("permanent", StringComparison.OrdinalIgnoreCase)
            ? _permanentDismissals.Contains(notice.Id)
            : _sessionDismissals.Contains(notice.Id);
    }

    public void Dismiss(NoticeDefinition notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase) || !notice.Dismissible)
            return;

        if (notice.DismissPersistence.Equals("permanent", StringComparison.OrdinalIgnoreCase))
        {
            if (!_permanentDismissals.Add(notice.Id))
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(
                    new NoticeDismissalData(_permanentDismissals.Order(StringComparer.OrdinalIgnoreCase).ToArray()),
                    NoticeCatalogJsonContext.Default.NoticeDismissalData));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or NotSupportedException or ArgumentException)
            {
                _permanentDismissals.Remove(notice.Id);
                throw;
            }
            return;
        }

        if (notice.DismissPersistence.Equals("session", StringComparison.OrdinalIgnoreCase))
            _sessionDismissals.Add(notice.Id);
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 128
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}

public sealed record NoticeDismissalData(
    [property: JsonPropertyName("permanentIds")] IReadOnlyList<string> PermanentIds);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NoticeCatalogDocument))]
[JsonSerializable(typeof(NoticeDismissalData))]
public partial class NoticeCatalogJsonContext : JsonSerializerContext;
