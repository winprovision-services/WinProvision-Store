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
    public partial bool SearchOnDemand { get; set; }

    [ObservableProperty]
    public partial bool ProvisioningMenuEnabled { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Configurações prontas.";

    public SettingsViewModel(
        ApplicationThemeService themeService,
        ApplicationPreferencesService preferences,
        InstallationPreferencesService installation,
        IDesktopIntegrationService desktop,
        WinProvision.Core.Services.CacheService cache,
        WinProvision.Core.Services.AppUpdateService updater,
        IPresentationService presentation,
        IBackgroundUpdateStartupService? backgroundUpdates = null,
        IElevatorSettingsService? elevator = null,
        ICacheSettingsService? cacheSettings = null,
        WinProvision.Core.Services.OperationPreferencesService? operationPreferences = null)
    {
        _operationPreferences = operationPreferences ?? new();
        LoadExecutionOptions();
        _cacheSettings = cacheSettings ?? new CacheSettingsService(cache);
        _elevator = elevator ?? new ElevatorSettingsService();
        _backgroundUpdates = backgroundUpdates;
        _themeService = themeService;
        _preferences = preferences;
        _installation = installation;
        _desktop = desktop;
        _cache = cache;
        _updater = updater;
        _presentation = presentation;
        _isInstalledFromSetup = updater.IsCurrentProcessInstalled;
        _isInitializing = true;
        SelectedThemeOption = ThemeOptions.First(option => option.Preference == themeService.Preference);
        SearchOnDemand = preferences.SearchOnDemand;
        ProvisioningMenuEnabled = preferences.ProvisioningMenuEnabled;
        _isInitializing = false;
        preferences.ProvisioningMenuEnabledChanged += OnProvisioningPreferenceChanged;
        ReloadScheduledMaintenance();
        LoadPortabilitySettings();
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

    partial void OnSearchOnDemandChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            _preferences.SetSearchOnDemand(value);
            StatusText = value
                ? "A vitrine carregará aplicativos somente após uma pesquisa."
                : "A vitrine completa será carregada ao abrir Descobrir.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "SettingsViewModel.SetSearchOnDemand");
            StatusText = "Não foi possível salvar esta opção. A preferência anterior foi mantida.";
            _isInitializing = true;
            SearchOnDemand = _preferences.SearchOnDemand;
            _isInitializing = false;
        }
    }

    partial void OnProvisioningMenuEnabledChanged(bool value)
    {
        if (_isInitializing) return;
        try
        {
            _preferences.SetProvisioningMenuEnabled(value);
            StatusText = value
                ? "O menu Provisionamento está disponível na navegação."
                : "O menu Provisionamento foi ocultado da navegação.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "SettingsViewModel.SetProvisioningMenuEnabled");
            StatusText = "Não foi possível salvar esta opção. A preferência anterior foi mantida.";
            _isInitializing = true;
            ProvisioningMenuEnabled = _preferences.ProvisioningMenuEnabled;
            _isInitializing = false;
        }
    }

    private void OnProvisioningPreferenceChanged(bool enabled)
    {
        if (ProvisioningMenuEnabled == enabled) return;
        _isInitializing = true;
        ProvisioningMenuEnabled = enabled;
        _isInitializing = false;
    }
}
