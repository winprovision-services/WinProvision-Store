using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Services;

public sealed partial class WinUiBackupSyncService : IAsyncDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);
    private readonly CollectionsViewModel _collections;
    private readonly IAccountBackupService _backup;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<Task> _activeSyncs = [];
    private readonly Timer _debounceTimer;
    private string? _pendingSnapshot;
    private bool _disposed;

    public event Action<WinUiBackupSyncResult>? SyncAttempted;

    public WinUiBackupSyncService(
        CollectionsViewModel collections,
        IAccountBackupService backup,
        bool enableAutomaticSync = true)
    {
        _collections = collections;
        _backup = backup;
        _debounceTimer = new Timer(DebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (enableAutomaticSync)
            _collections.BackupSnapshotChanged += Collections_BackupSnapshotChanged;
    }

    public Task<WinUiBackupSyncResult> RunSyncAsync(ProfileBackupSet snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string json = WinProvision.Core.Services.AotJson.Serialize(snapshot, WinProvisionJsonOptions.Profile);
        _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return SyncSerializedAsync(json, cancellationToken);
    }

    private void Collections_BackupSnapshotChanged(ProfileBackupSet snapshot)
    {
        if (snapshot.Tabs.Count == 0 && snapshot.Provisioning is null)
            return;

        string json;
        try
        {
            json = WinProvision.Core.Services.AotJson.Serialize(snapshot, WinProvisionJsonOptions.Profile);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "WinUiBackupSyncService.Snapshot");
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;
            _pendingSnapshot = json;
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void DebounceElapsed(object? state)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            Task sync = RunScheduledSyncAsync();
            _activeSyncs.Add(sync);
            _ = ObserveScheduledSyncAsync(sync);
        }
    }

    private async Task ObserveScheduledSyncAsync(Task sync)
    {
        try
        {
            await sync.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "WinUiBackupSyncService.ScheduledSync");
        }
        finally
        {
            lock (_gate)
                _activeSyncs.Remove(sync);
        }
    }

    private async Task RunScheduledSyncAsync()
    {
        string? json;
        lock (_gate)
        {
            json = _pendingSnapshot;
            _pendingSnapshot = null;
        }

        if (json is null)
            return;

        WinUiBackupSyncResult result = await SyncSerializedAsync(json, CancellationToken.None).ConfigureAwait(false);
        if (result.AlreadyRunning)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _pendingSnapshot ??= json;
                    _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    private async Task<WinUiBackupSyncResult> SyncSerializedAsync(string json, CancellationToken cancellationToken)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new(false, false, false, "Já existe uma sincronização em andamento.", true);

        bool localSucceeded = false;
        bool cloudAttempted = false;
        bool cloudSucceeded = false;
        string? errorMessage = null;
        try
        {
            ProfileBackupSet snapshot = WinProvision.Core.Services.AotJson.Deserialize<ProfileBackupSet>(json, WinProvisionJsonOptions.Profile)
                ?? throw new InvalidDataException("O snapshot do backup está vazio.");
            await _backup.SaveLocalAsync(snapshot, cancellationToken).ConfigureAwait(false);
            localSucceeded = true;

            if (_backup.IsConnected)
            {
                cloudAttempted = true;
                GitHubBackupUploadResult result = await _backup.UploadCloudAsync(snapshot, cancellationToken).ConfigureAwait(false);
                cloudSucceeded = result.Success;
                errorMessage = result.ErrorMessage;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "WinUiBackupSyncService.Sync");
            errorMessage = exception.Message;
        }
        finally
        {
            _syncLock.Release();
        }

        var syncResult = new WinUiBackupSyncResult(localSucceeded, cloudAttempted, cloudSucceeded, errorMessage);
        SyncAttempted?.Invoke(syncResult);
        return syncResult;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _collections.BackupSnapshotChanged -= Collections_BackupSnapshotChanged;
        }

        await _debounceTimer.DisposeAsync().ConfigureAwait(false);
        Task[] activeSyncs;
        lock (_gate)
            activeSyncs = _activeSyncs.ToArray();
        await Task.WhenAll(activeSyncs).ConfigureAwait(false);
        _syncLock.Dispose();
    }
}

public sealed record WinUiBackupSyncResult(
    bool LocalSucceeded,
    bool CloudAttempted,
    bool CloudSucceeded,
    string? ErrorMessage = null,
    bool AlreadyRunning = false);
