using CommunityToolkit.Mvvm.Input;

namespace WinProvision.Store.ViewModels;

// Explicit properties keep commands visible to the WinRT binding generator.
public sealed partial class SettingsViewModel
{
    private AsyncRelayCommand? _checkUpdateCommand;

    public IAsyncRelayCommand CheckUpdateCommand => _checkUpdateCommand ??= new AsyncRelayCommand(CheckUpdateAsync, () => CanCheckUpdate);

    private RelayCommand? _cancelUpdateCommand;
    public IRelayCommand CancelUpdateCommand => _cancelUpdateCommand ??= new RelayCommand(CancelUpdate, () => HasCancelableUpdateOperation);

    private AsyncRelayCommand? _clearCacheCommand;

    public IAsyncRelayCommand ClearCacheCommand => _clearCacheCommand ??= new AsyncRelayCommand(ClearCacheAsync);

    private AsyncRelayCommand? _refreshCatalogCommand;
    public IAsyncRelayCommand RefreshCatalogCommand => _refreshCatalogCommand ??= new AsyncRelayCommand(RefreshCatalogAsync);
    private AsyncRelayCommand? _openReleaseCommand;
    public IAsyncRelayCommand OpenReleaseCommand => _openReleaseCommand ??= new AsyncRelayCommand(OpenReleaseAsync);

    private RelayCommand? _darkThemeCommand;

    public IRelayCommand DarkThemeCommand => _darkThemeCommand ??= new RelayCommand(DarkTheme);

    private AsyncRelayCommand? _downloadUpdateCommand;

    public IAsyncRelayCommand DownloadUpdateCommand => _downloadUpdateCommand ??= new AsyncRelayCommand(DownloadUpdateAsync, () => CanDownloadUpdate);

    private AsyncRelayCommand? _applyUpdateCommand;
    public IAsyncRelayCommand ApplyUpdateCommand => _applyUpdateCommand ??= new AsyncRelayCommand(ApplyUpdateAsync, () => CanApplyUpdate);

    private RelayCommand? _lightThemeCommand;

    public IRelayCommand LightThemeCommand => _lightThemeCommand ??= new RelayCommand(LightTheme);

    private RelayCommand? _openLogsCommand;

    public IRelayCommand OpenLogsCommand => _openLogsCommand ??= new RelayCommand(OpenLogs);

    private RelayCommand? _copyLogsPathCommand;

    public IRelayCommand CopyLogsPathCommand => _copyLogsPathCommand ??= new RelayCommand(CopyLogsPath);

    private AsyncRelayCommand? _exportLogsCommand;

    public IAsyncRelayCommand ExportLogsCommand => _exportLogsCommand ??= new AsyncRelayCommand(ExportLogsAsync);

    private AsyncRelayCommand? _cleanOldLogsCommand;

    public IAsyncRelayCommand CleanOldLogsCommand => _cleanOldLogsCommand ??= new AsyncRelayCommand(CleanOldLogsAsync);

    private AsyncRelayCommand? _resetPreferencesCommand;

    public IAsyncRelayCommand ResetPreferencesCommand => _resetPreferencesCommand ??= new AsyncRelayCommand(ResetPreferencesAsync);

    private RelayCommand? _systemThemeCommand;

    public IRelayCommand SystemThemeCommand => _systemThemeCommand ??= new RelayCommand(SystemTheme);
}
