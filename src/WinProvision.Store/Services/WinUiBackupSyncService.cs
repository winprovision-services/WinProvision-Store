using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;

namespace WinProvision.Store.Services;

public sealed partial class WinUiBackupSyncService : IAsyncDisposable
{
    private readonly IAccountBackupService _backup;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _disposed;

    public WinUiBackupSyncService(IAccountBackupService backup)
    {
        _backup = backup;
    }

    public Task<WinUiBackupSyncResult> RunSyncAsync(ProfileBackupSet snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(_disposed, this);
        string json = AotJson.Serialize(snapshot, WinProvisionJsonOptions.Profile);
        return SyncSerializedAsync(json, cancellationToken);
    }

    private async Task<WinUiBackupSyncResult> SyncSerializedAsync(string json, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);

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
                CloudBackupUploadResult result = await _backup.UploadCloudAsync(snapshot, cancellationToken).ConfigureAwait(false);
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
        return syncResult;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

}

public sealed record WinUiBackupSyncResult(
    bool LocalSucceeded,
    bool CloudAttempted,
    bool CloudSucceeded,
    string? ErrorMessage = null,
    bool AlreadyRunning = false);
