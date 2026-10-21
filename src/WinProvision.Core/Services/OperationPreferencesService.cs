using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public enum InstallerNameStyle { Original, PackageVersion, PackageVersionArchitecture }
public enum NotificationCategory { Updates, Errors, Success, BatchSummary }
public sealed record OperationPreferences
{
    public bool NotifyUpdates { get; init; } = true;
    public bool NotifyErrors { get; init; } = true;
    public bool NotifySuccess { get; init; } = true;
    public bool NotifyBatchSummary { get; init; } = true;
    public int ParallelOperations { get; init; } = 1;
    public int ParallelDownloads { get; init; } = 3;
    public string? DownloadDirectory { get; init; }
    public InstallerNameStyle InstallerNames { get; init; } = InstallerNameStyle.PackageVersion;
    public bool WarnInstallerHostChange { get; init; } = true;
    public bool Allows(NotificationCategory category) => category switch
    {
        NotificationCategory.Updates => NotifyUpdates, NotificationCategory.Errors => NotifyErrors,
        NotificationCategory.Success => NotifySuccess, NotificationCategory.BatchSummary => NotifyBatchSummary, _ => false
    };
    public bool AllowsSuccessfulOperation(bool isBatch) => NotifySuccess && (!isBatch || NotifyBatchSummary);
    public void Validate()
    {
        if (ParallelOperations is < 1 or > 4 || ParallelDownloads is < 1 or > 8 || !Enum.IsDefined(InstallerNames))
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
    public static OperationConcurrencyLimiter Packages { get; } = new(() => new OperationPreferencesService().Load().ParallelOperations);
    public static OperationConcurrencyLimiter Downloads { get; } = new(() => new OperationPreferencesService().Load().ParallelDownloads);
}
