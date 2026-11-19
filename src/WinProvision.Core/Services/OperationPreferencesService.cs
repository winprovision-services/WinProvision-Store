using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public static class OperationConcurrencyLimits
{
    public const int MinimumParallelOperations = 1;
    public const int MaximumParallelOperations = 4;
    public static IReadOnlyList<ElevationSettingChoice> ParallelOperationChoices { get; } =
        Enumerable.Range(MinimumParallelOperations, MaximumParallelOperations - MinimumParallelOperations + 1)
            .Select(value => new ElevationSettingChoice(value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"operations.option.{value}"))
            .ToArray();
}

public enum InstallerNameStyle { Original, PackageVersion, PackageVersionArchitecture }
public sealed record OperationPreferences
{
    public NotificationLevel NotificationLevel { get; init; } = NotificationLevel.All;
    public bool BackgroundWindowsNotificationsEnabled { get; init; } = true;
    public int ParallelOperations { get; init; } = 1;
    public int ParallelDownloads { get; init; } = 3;
    public string? DownloadDirectory { get; init; }
    public InstallerNameStyle InstallerNames { get; init; } = InstallerNameStyle.PackageVersion;
    public bool WarnInstallerHostChange { get; init; } = true;
    public void Validate()
    {
        if (ParallelOperations < OperationConcurrencyLimits.MinimumParallelOperations
            || ParallelOperations > OperationConcurrencyLimits.MaximumParallelOperations || ParallelDownloads is < 1 or > 8
            || !Enum.IsDefined(InstallerNames) || !Enum.IsDefined(NotificationLevel))
            throw new ArgumentException("Selecione limites e formato de nome válidos.");
        if (!string.IsNullOrWhiteSpace(DownloadDirectory) && (!Path.IsPathFullyQualified(DownloadDirectory)
            || DownloadDirectory.StartsWith(@"\\?\", StringComparison.Ordinal) || DownloadDirectory.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new ArgumentException("Escolha uma pasta com caminho absoluto.");
        if (!string.IsNullOrWhiteSpace(DownloadDirectory)) _ = Path.GetFullPath(DownloadDirectory);
    }
}

public sealed class OperationPreferencesService
{
    private readonly string _path;
    public OperationPreferencesService(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStoreWinUI3", "operation-preferences.json");
    public OperationPreferences Load()
    {
        try
        {
            var options = JsonSerializer.Deserialize(File.ReadAllText(_path), OperationPreferencesJson.Default.OperationPreferences) ?? new();
            options.Validate(); return options with { DownloadDirectory = string.IsNullOrWhiteSpace(options.DownloadDirectory) ? null : options.DownloadDirectory.Trim() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            if (ex is not FileNotFoundException and not DirectoryNotFoundException) CoreLog.Warning(ex, "Operations.Preferences");
            return new();
        }
    }
    public void Save(OperationPreferences options)
    {
        options.Validate();
        options = options with { DownloadDirectory = string.IsNullOrWhiteSpace(options.DownloadDirectory) ? null : options.DownloadDirectory.Trim() };
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(options, OperationPreferencesJson.Default.OperationPreferences));
    }

    public string GetCatalogValue(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        JsonObject document = SerializePreferences(Load());
        if (!document.TryGetPropertyValue(storageKey, out JsonNode? value))
            throw new ArgumentException("A preferência declarada não existe no armazenamento.", nameof(storageKey));
        if (value is null)
            return string.Empty;
        if (value is not JsonValue jsonValue)
            throw new InvalidDataException("A preferência não tem um valor simples compatível.");
        if (jsonValue.TryGetValue(out string? text))
            return text ?? string.Empty;
        if (jsonValue.TryGetValue(out bool boolean))
            return boolean.ToString();
        if (jsonValue.TryGetValue(out int integer))
            return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        throw new InvalidDataException("A preferência não tem um tipo de valor compatível.");
    }

    public void SetCatalogValue(string storageKey, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentNullException.ThrowIfNull(value);
        JsonObject document = SerializePreferences(Load());
        if (!document.TryGetPropertyValue(storageKey, out JsonNode? previous))
            throw new ArgumentException("A preferência declarada não existe no armazenamento.", nameof(storageKey));

        JsonNode? updated = previous switch
        {
            null => string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value),
            JsonValue jsonValue when jsonValue.TryGetValue(out string? _) => JsonValue.Create(value),
            JsonValue jsonValue when jsonValue.TryGetValue(out bool _) =>
                bool.TryParse(value, out bool boolean)
                    ? JsonValue.Create(boolean)
                    : throw new ArgumentException("O valor booleano da preferência é inválido.", nameof(value)),
            JsonValue jsonValue when jsonValue.TryGetValue(out int _) =>
                int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int integer)
                    ? JsonValue.Create(integer)
                    : throw new ArgumentException("O valor numérico da preferência é inválido.", nameof(value)),
            _ => throw new InvalidDataException("A preferência não tem um tipo de valor compatível.")
        };
        document[storageKey] = updated;
        OperationPreferences options = document.Deserialize(OperationPreferencesJson.Default.OperationPreferences)
            ?? throw new InvalidDataException("Não foi possível aplicar a preferência declarada.");
        Save(options);
    }

    private static JsonObject SerializePreferences(OperationPreferences preferences) =>
        JsonSerializer.SerializeToNode(preferences, OperationPreferencesJson.Default.OperationPreferences)?.AsObject()
        ?? throw new InvalidDataException("Não foi possível ler as preferências de operações.");
}

[JsonSerializable(typeof(OperationPreferences))]
internal partial class OperationPreferencesJson : JsonSerializerContext;

/// <summary>Process-wide limits. Reducing the limit never interrupts running work.</summary>
public sealed class OperationConcurrencyLimiter(Func<int> limit)
{
    private readonly object _gate = new();
    private int _active;
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task signal;
            lock (_gate)
            {
                if (_active < Math.Max(1, limit())) { _active++; return new Lease(this); }
                signal = _changed.Task;
            }
            // Periodic recheck also observes a limit increased by another host/settings instance.
            await Task.WhenAny(signal, Task.Delay(250, token)).ConfigureAwait(false);
        }
    }
    private sealed class Lease(OperationConcurrencyLimiter owner) : IDisposable
    {
        private OperationConcurrencyLimiter? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } current) return;
            lock (current._gate)
            {
                current._active--; var signal = current._changed; current._changed = NewSignal(); signal.TrySetResult();
            }
        }
    }
}

public static class OperationLimits
{
    private static readonly OperationPreferencesService Preferences = new();

    public static OperationConcurrencyLimiter Packages { get; } =
        new(() => Preferences.Load().ParallelOperations);

    public static OperationConcurrencyLimiter Downloads { get; } =
        new(() => Preferences.Load().ParallelDownloads);
}
