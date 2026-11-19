using CommunityToolkit.Mvvm.Input;

namespace WinProvision.Store.ViewModels;

// Explicit properties keep commands visible to the WinRT binding generator.
public sealed partial class AccountSyncViewModel
{
    private RelayCommand? _cancelAuthenticationCommand;

    public IRelayCommand CancelAuthenticationCommand => _cancelAuthenticationCommand ??= new RelayCommand(CancelAuthentication);

    private AsyncRelayCommand? _connectCommand;

    public IAsyncRelayCommand ConnectCommand => _connectCommand ??= new AsyncRelayCommand(ConnectAsync, () => CanStartAuthentication);

    private RelayCommand? _disconnectCommand;

    public IRelayCommand DisconnectCommand => _disconnectCommand ??= new RelayCommand(Disconnect);

    private AsyncRelayCommand? _exportJsonCommand;

    public IAsyncRelayCommand ExportJsonCommand => _exportJsonCommand ??= new AsyncRelayCommand(ExportJsonAsync, () => CanExportProfile);

    private AsyncRelayCommand? _importJsonCommand;

    public IAsyncRelayCommand ImportJsonCommand => _importJsonCommand ??= new AsyncRelayCommand(ImportJsonAsync, () => CanImportProfile);

    private AsyncRelayCommand? _restoreFromCloudCommand;

    public IAsyncRelayCommand RestoreFromCloudCommand => _restoreFromCloudCommand ??= new AsyncRelayCommand(RestoreFromCloudAsync, () => CanImportCloudBackup);

    private AsyncRelayCommand? _saveBackupCommand;

    public IAsyncRelayCommand SaveBackupCommand => _saveBackupCommand ??= new AsyncRelayCommand(SaveBackupAsync, () => CanSaveBackup);

    private AsyncRelayCommand? _refreshCloudFilesCommand;
    public IAsyncRelayCommand RefreshCloudFilesCommand => _refreshCloudFilesCommand ??= new AsyncRelayCommand(RefreshCloudFilesAsync, () => CanRefreshCloudFiles);

    private AsyncRelayCommand? _restoreSelectedCloudFileCommand;
    public IAsyncRelayCommand RestoreSelectedCloudFileCommand => _restoreSelectedCloudFileCommand ??= new AsyncRelayCommand(RestoreSelectedCloudFileAsync, () => CanRestoreSelectedCloudFile);

    private AsyncRelayCommand? _deleteSelectedCloudFileCommand;
    public IAsyncRelayCommand DeleteSelectedCloudFileCommand => _deleteSelectedCloudFileCommand ??= new AsyncRelayCommand(DeleteSelectedCloudFileAsync, () => CanDeleteSelectedCloudFile);

    private AsyncRelayCommand? _deleteAllAppGistsCommand;
    public IAsyncRelayCommand DeleteAllAppGistsCommand => _deleteAllAppGistsCommand ??= new AsyncRelayCommand(DeleteAllAppGistsAsync, () => CanRefreshCloudFiles);
}
