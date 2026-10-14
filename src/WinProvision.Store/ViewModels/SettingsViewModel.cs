using CommunityToolkit.Mvvm.ComponentModel;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record SettingsThemeOption(string Label, AppThemePreference Preference);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ApplicationThemeService _themeService;
    private bool _isInitializing;

    public IReadOnlyList<SettingsThemeOption> ThemeOptions { get; } =
    (WinProvision.Store.ViewModels.SettingsThemeOption[])[
        new("Sistema", AppThemePreference.System),
        new("Claro", AppThemePreference.Light),
        new("Escuro", AppThemePreference.Dark)
    ];

    [ObservableProperty]
    public partial SettingsThemeOption? SelectedThemeOption { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Configurações prontas.";

    public SettingsViewModel(
        ApplicationThemeService themeService,
        ApplicationPreferencesService preferences,
        InstallationPreferencesService installation,
        IDesktopIntegrationService desktop,
        WinProvision.Core.Services.CacheService cache,
        WinProvision.Core.Services.AppUpdateService updater)
    {
        _themeService = themeService;
        _preferences = preferences;
        _installation = installation;
        _desktop = desktop;
        _cache = cache;
        _updater = updater;
        _isInstalledFromSetup = updater.IsCurrentProcessInstalled;
        _isInitializing = true;
        SelectedThemeOption = ThemeOptions.First(option => option.Preference == themeService.Preference);
        _isInitializing = false;
    }

    partial void OnSelectedThemeOptionChanged(SettingsThemeOption? value)
    {
        if (_isInitializing || value is null)
            return;

        try
        {
            _themeService.SetTheme(value.Preference);
            StatusText = "O tema é aplicado imediatamente e salvo neste dispositivo.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "SettingsViewModel.SetTheme");
            StatusText = "Não foi possível salvar o tema. A preferência anterior foi mantida.";
            _isInitializing = true;
            SelectedThemeOption = ThemeOptions.First(option => option.Preference == _themeService.Preference);
            _isInitializing = false;
        }
    }
}
