using System.Text.Json;

namespace WinProvision.Core.Services;

public enum NotificationSeverity { Information, Success, Warning, Error }
public enum NotificationLevel { All, Important, ErrorsOnly }
public enum NotificationChannel { InApp, WindowsToast }

public static class NotificationCategories
{
    public const string Progress = "Progress";
    public const string OperationResult = "OperationResult";
    public const string ActionableWarning = "ActionableWarning";
    public const string BlockingError = "BlockingError";
    public const string PassiveInformation = "PassiveInformation";
    public const string AppUpdateAvailable = "AppUpdateAvailable";
}

public sealed record NotificationAction(string Label, string CommandId);

public sealed record Notification(
    string Id,
    string Category,
    NotificationSeverity Severity,
    string Title,
    string Message,
    NotificationAction? Action = null,
    string? DeduplicationKey = null,
    string? Origin = null);

public sealed record NotificationHistoryEntry(
    string Id,
    string Category,
    NotificationSeverity Severity,
    string Title,
    string Message,
    DateTimeOffset CreatedAt,
    string? Origin = null);

public sealed record NotificationPolicy(
    NotificationSeverity DefaultSeverity,
    IReadOnlyList<NotificationChannel> Channels,
    int? AutoDismissSeconds,
    int DedupeWindowSeconds,
    int CooldownSeconds,
    bool Coalesce,
    bool ShowWhenAppFocused,
    bool ShowToastWhenUnfocused,
    bool RequiresAction,
    bool PersistInHistory);

public sealed class NotificationPolicyCatalog
{
    private const int CurrentSchemaVersion = 1;
    private readonly Dictionary<string, NotificationPolicy> _categories;

    private NotificationPolicyCatalog(int schemaVersion, Dictionary<string, NotificationPolicy> categories)
    {
        SchemaVersion = schemaVersion;
        _categories = categories;
    }

    public int SchemaVersion { get; }
    public IReadOnlyDictionary<string, NotificationPolicy> Categories => _categories;

    public NotificationPolicy Resolve(string? category) =>
        !string.IsNullOrWhiteSpace(category) && _categories.TryGetValue(category, out NotificationPolicy? policy)
            ? policy
            : SafeDefault;

    public static NotificationPolicyCatalog Load(string? json, Action<string>? log = null)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json ?? string.Empty);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out JsonElement schema)
                || !schema.TryGetInt32(out int schemaVersion)
                || schemaVersion != CurrentSchemaVersion
                || !root.TryGetProperty("categories", out JsonElement categories)
                || categories.ValueKind != JsonValueKind.Object)
                throw new JsonException("Formato da política de notificações incompatível.");

            var result = new Dictionary<string, NotificationPolicy>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in categories.EnumerateObject())
            {
                if (!KnownCategories.Contains(property.Name)
                    || property.Value.ValueKind != JsonValueKind.Object)
                    continue;

                result[property.Name] = ParsePolicy(property.Value);
            }

            if (result.Count == 0)
                throw new JsonException("A política não contém categorias válidas.");
            return new NotificationPolicyCatalog(schemaVersion, result);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            log?.Invoke($"Política de notificações inválida; usando padrões seguros. {exception.Message}");
            return CreateFallback();
        }
    }

    public static NotificationPolicyCatalog CreateDefault() => CreateFallback();

    public static bool AllowsSeverity(NotificationLevel level, NotificationSeverity severity) => level switch
    {
        NotificationLevel.All => true,
        NotificationLevel.Important => severity is NotificationSeverity.Warning or NotificationSeverity.Error,
        NotificationLevel.ErrorsOnly => severity == NotificationSeverity.Error,
        _ => false
    };

    public static NotificationChannel? SelectChannel(
        NotificationPolicy policy,
        bool appFocused,
        bool windowsNotificationsEnabled,
        bool systemQuietMode = false)
    {
        if (appFocused)
            return policy.ShowWhenAppFocused && policy.Channels.Contains(NotificationChannel.InApp)
                ? NotificationChannel.InApp
                : null;

        return !systemQuietMode
            && windowsNotificationsEnabled
            && policy.ShowToastWhenUnfocused
            && policy.Channels.Contains(NotificationChannel.WindowsToast)
                ? NotificationChannel.WindowsToast
                : null;
    }

    public static int SeverityPriority(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => 3,
        NotificationSeverity.Warning => 2,
        NotificationSeverity.Success => 1,
        _ => 0
    };

    private static NotificationPolicy ParsePolicy(JsonElement element)
    {
        NotificationSeverity severity = ReadEnum(element, "defaultSeverity", NotificationSeverity.Information);
        NotificationChannel[] channels = ReadChannels(element);
        int? autoDismiss = ReadNullableInt(element, "autoDismissSeconds");
        return new NotificationPolicy(
            severity,
            channels,
            autoDismiss is null ? null : Math.Clamp(autoDismiss.Value, 1, 600),
            Math.Clamp(ReadInt(element, "dedupeWindowSeconds", 0), 0, 3600),
            Math.Clamp(ReadInt(element, "cooldownSeconds", 0), 0, 86400),
            ReadBool(element, "coalesce", true),
            ReadBool(element, "showWhenAppFocused", true),
            ReadBool(element, "showToastWhenUnfocused", false),
            ReadBool(element, "requiresAction", false),
            ReadBool(element, "persistInHistory", true));
    }

    private static NotificationChannel[] ReadChannels(JsonElement element)
    {
        if (!element.TryGetProperty("channels", out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return [NotificationChannel.InApp];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => Enum.TryParse(item.GetString(), true, out NotificationChannel channel)
                && Enum.IsDefined(channel) ? (NotificationChannel?)channel : null)
            .OfType<NotificationChannel>()
            .Distinct()
            .ToArray();
    }

    private static T ReadEnum<T>(JsonElement element, string propertyName, T fallback) where T : struct, Enum =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && Enum.TryParse(value.GetString(), true, out T parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    private static int ReadInt(JsonElement element, string propertyName, int fallback) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed
            : fallback;

    private static int? ReadNullableInt(JsonElement element, string propertyName) =>
        !element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.TryGetInt32(out int parsed) ? parsed : null;

    private static bool ReadBool(JsonElement element, string propertyName, bool fallback) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static NotificationPolicyCatalog CreateFallback() => new(CurrentSchemaVersion,
        new Dictionary<string, NotificationPolicy>(StringComparer.OrdinalIgnoreCase)
        {
            [NotificationCategories.Progress] = new(NotificationSeverity.Information, [NotificationChannel.InApp], 4, 0, 0, true, true, false, false, false),
            [NotificationCategories.OperationResult] = new(NotificationSeverity.Success, [NotificationChannel.InApp, NotificationChannel.WindowsToast], 6, 30, 0, true, true, true, false, true),
            [NotificationCategories.ActionableWarning] = new(NotificationSeverity.Warning, [NotificationChannel.InApp], null, 30, 0, true, true, false, true, true),
            [NotificationCategories.BlockingError] = new(NotificationSeverity.Error, [NotificationChannel.InApp, NotificationChannel.WindowsToast], null, 30, 0, true, true, true, false, true),
            [NotificationCategories.PassiveInformation] = new(NotificationSeverity.Information, [NotificationChannel.InApp], 5, 30, 0, true, true, false, false, false),
            [NotificationCategories.AppUpdateAvailable] = new(NotificationSeverity.Information, [NotificationChannel.InApp, NotificationChannel.WindowsToast], 10, 86400, 0, true, true, true, false, true)
        });

    private static NotificationPolicy SafeDefault { get; } =
        new(NotificationSeverity.Information, [NotificationChannel.InApp], 5, 0, 0, true, true, false, false, true);

    private static HashSet<string> KnownCategories { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        NotificationCategories.Progress,
        NotificationCategories.OperationResult,
        NotificationCategories.ActionableWarning,
        NotificationCategories.BlockingError,
        NotificationCategories.PassiveInformation,
        NotificationCategories.AppUpdateAvailable
    };

}

public interface INotificationSink
{
    ValueTask PublishAsync(Notification notification, CancellationToken cancellationToken = default);
}

public sealed class NotificationQueue
{
    private readonly List<NotificationEnvelope> _pending = [];
    private readonly Dictionary<string, DateTimeOffset> _lastPublished = new(StringComparer.OrdinalIgnoreCase);

    public NotificationEnvelope? Active { get; private set; }
    public IReadOnlyList<NotificationEnvelope> Pending => _pending;
    public int PendingNotificationCount => _pending.Sum(item => item.Count);

    public bool Enqueue(Notification notification, NotificationPolicy policy, DateTimeOffset now)
    {
        string key = notification.DeduplicationKey ?? notification.Id;
        if (policy.DedupeWindowSeconds > 0)
        {
            NotificationEnvelope? existing = EnumerateAll().FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)
                && now - item.LastUpdated <= TimeSpan.FromSeconds(policy.DedupeWindowSeconds));
            if (existing is not null)
            {
                existing.LastUpdated = now;
                existing.Notification = notification;
                if (policy.Coalesce) existing.Count++;
                return true;
            }
        }

        if (policy.CooldownSeconds > 0
            && _lastPublished.TryGetValue(key, out DateTimeOffset last)
            && now - last < TimeSpan.FromSeconds(policy.CooldownSeconds))
            return false;

        _lastPublished[key] = now;
        var entry = new NotificationEnvelope(notification, key, now);
        if (Active is null)
        {
            Active = entry;
        }
        else if (NotificationPolicyCatalog.SeverityPriority(notification.Severity)
            > NotificationPolicyCatalog.SeverityPriority(Active.Notification.Severity))
        {
            _pending.Add(Active);
            Active = entry;
        }
        else
        {
            _pending.Add(entry);
        }
        return true;
    }

    public NotificationEnvelope? DismissActive()
    {
        Active = _pending
            .OrderByDescending(item => NotificationPolicyCatalog.SeverityPriority(item.Notification.Severity))
            .ThenBy(item => item.CreatedAt)
            .FirstOrDefault();
        if (Active is not null)
            _pending.Remove(Active);
        return Active;
    }

    private IEnumerable<NotificationEnvelope> EnumerateAll()
    {
        if (Active is not null)
            yield return Active;
        foreach (NotificationEnvelope entry in _pending)
            yield return entry;
    }
}

public sealed class NotificationEnvelope(Notification notification, string key, DateTimeOffset now)
{
    public Notification Notification { get; internal set; } = notification;
    public string Key { get; } = key;
    public DateTimeOffset CreatedAt { get; } = now;
    public DateTimeOffset LastUpdated { get; internal set; } = now;
    public int Count { get; internal set; } = 1;
}
