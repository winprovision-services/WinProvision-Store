using CommunityToolkit.Mvvm.Input;

namespace WinProvision.Store.ViewModels;

// Explicit properties keep commands visible to the WinRT binding generator.
public sealed partial class SettingsViewModel
{
    private AsyncRelayCommand? _checkUpdateCommand;

    public IAsyncRelayCommand CheckUpdateCommand => _checkUpdateCommand ??= new AsyncRelayCommand(CheckUpdateAsync, () => CanCheckUpdate);

    private System.Windows.Input.ICommand? _checkUpdateCancelCommand;

    public System.Windows.Input.ICommand CheckUpdateCancelCommand => _checkUpdateCancelCommand ??= IAsyncRelayCommandExtensions.CreateCancelCommand(CheckUpdateCommand);

    private AsyncRelayCommand? _clearCacheCommand;

    public IAsyncRelayCommand ClearCacheCommand => _clearCacheCommand ??= new AsyncRelayCommand(ClearCacheAsync);

    private AsyncRelayCommand? _refreshCatalogCommand;
    public IAsyncRelayCommand RefreshCatalogCommand => _refreshCatalogCommand ??= new AsyncRelayCommand(RefreshCatalogAsync);
    private AsyncRelayCommand? _openReleaseCommand;
    public IAsyncRelayCommand OpenReleaseCommand => _openReleaseCommand ??= new AsyncRelayCommand(OpenReleaseAsync);

    private RelayCommand? _darkThemeCommand;

    public IRelayCommand DarkThemeCommand => _darkThemeCommand ??= new RelayCommand(DarkTheme);

    private AsyncRelayCommand? _installUpdateCommand;

    public IAsyncRelayCommand InstallUpdateCommand => _installUpdateCommand ??= new AsyncRelayCommand(InstallUpdateAsync, () => CanInstallUpdate);

    private System.Windows.Input.ICommand? _installUpdateCancelCommand;

    public System.Windows.Input.ICommand InstallUpdateCancelCommand => _installUpdateCancelCommand ??= IAsyncRelayCommandExtensions.CreateCancelCommand(InstallUpdateCommand);

    private RelayCommand? _lightThemeCommand;

    public IRelayCommand LightThemeCommand => _lightThemeCommand ??= new RelayCommand(LightTheme);

    private RelayCommand? _openLogsCommand;

    public IRelayCommand OpenLogsCommand => _openLogsCommand ??= new RelayCommand(OpenLogs);

    private RelayCommand? _resetPreferencesCommand;

    public IRelayCommand ResetPreferencesCommand => _resetPreferencesCommand ??= new RelayCommand(ResetPreferences);

    private RelayCommand? _systemThemeCommand;

    public IRelayCommand SystemThemeCommand => _systemThemeCommand ??= new RelayCommand(SystemTheme);
}
