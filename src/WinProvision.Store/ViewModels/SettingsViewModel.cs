using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.ComponentModel;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record SettingsThemeOption(string Label, AppThemePreference Preference);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record WindowBackdropOption(string Id, string Label, bool IsSupported, string SupportDescription);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record StartupPageOption(string Label, ApplicationStartupPage Page);

 [WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageVisualPreferenceOption(string id, string label, string description, bool isEnabled) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Description { get; } = description;
    [ObservableProperty] public partial bool IsEnabled { get; set; } = isEnabled;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ApplicationThemeService _themeService;
    private readonly ApplicationPreferencesService _applicationPreferences;
    private readonly PackageVisualPreferencesService _packageVisualPreferences;
    private readonly IReadOnlyDictionary<string, ISettingsCatalogPreferenceProvider> _catalogPreferenceProviders;
    private bool _isInitializing;

    public IReadOnlyList<SettingsThemeOption> ThemeOptions { get; } =
    (WinProvision.Store.ViewModels.SettingsThemeOption[])[
        new("Sistema", AppThemePreference.System),
        new("Claro", AppThemePreference.Light),
        new("Escuro", AppThemePreference.Dark)
    ];

    public IReadOnlyList<WindowBackdropOption> BackdropOptions { get; }
    public IReadOnlyList<PackageVisualPreferenceOption> PackageVisualPreferences { get; }
    public ObservableCollection<NoticeOption> ElevatorNotices { get; } = [];
    public ObservableCollection<NoticeOption> LanServerNotices { get; } = [];
    public ObservableCollection<NoticeOption> LanCredentialNotices { get; } = [];

    public IReadOnlyList<StartupPageOption> StartupPageOptions { get; } =
    (WinProvision.Store.ViewModels.StartupPageOption[])[
        new("Descobrir", ApplicationStartupPage.Discover),
        new("Atualizações", ApplicationStartupPage.Updates),
        new("Aplicativos instalados", ApplicationStartupPage.Installed)
    ];

    [ObservableProperty]
    public partial StartupPageOption? SelectedStartupPageOption { get; set; }

    [ObservableProperty]
    public partial SettingsThemeOption? SelectedThemeOption { get; set; }

    [ObservableProperty]
    public partial WindowBackdropOption? SelectedBackdropOption { get; set; }

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
        WinProvision.Core.Services.OperationPreferencesService? operationPreferences = null,
        PackageVisualPreferencesService? packageVisualPreferences = null,
        LanProvisioningService? lanProvisioning = null,
        IClipboardService? clipboard = null,
        IUiDispatcher? dispatcher = null,
        ToastMessageService? toastMessages = null,
        AppLogSettingsStore? appLogSettings = null,
        IAppLog? appLog = null,
        ElevationPreferencesService? elevationPreferences = null,
        NoticeCatalogService? noticeCatalog = null,
        NoticeDismissalStore? noticeDismissals = null)
    {
        _operationPreferences = operationPreferences ?? new();
        LoadExecutionOptions(toastMessages);
        _cacheSettings = cacheSettings ?? new CacheSettingsService(cache);
        _catalogPreferenceProviders = new ISettingsCatalogPreferenceProvider[]
        {
            new ImageCacheCatalogPreferenceProvider(_cacheSettings)
        }.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        RegisterCacheDescriptionProviders();
        _elevator = elevator ?? new ElevatorSettingsService();
        _backgroundUpdates = backgroundUpdates;
        _themeService = themeService;
        _applicationPreferences = preferences;
        _preferences = preferences;
        _packageVisualPreferences = packageVisualPreferences ?? new PackageVisualPreferencesService(preferences);
        PackageVisualPreferences = _packageVisualPreferences.Fields.Select(field =>
            new PackageVisualPreferenceOption(field.Id, field.Label, field.Description ?? string.Empty,
                _packageVisualPreferences.GetValue(field.Id))).ToArray();
        _installation = installation;
        _desktop = desktop;
        _cache = cache;
        _updater = updater;
        _presentation = presentation;
        _appLogSettings = appLogSettings ?? AppLogRuntime.Settings;
        _appLog = appLog ?? AppLogRuntime.Log;
        _elevationPreferences = elevationPreferences ?? new ElevationPreferencesService();
        _noticeCatalog = noticeCatalog ?? new NoticeCatalogService();
        _noticeDismissals = noticeDismissals ?? new NoticeDismissalStore();
        _settingsClipboard = clipboard;
        ConfigureLanProvisioning(lanProvisioning, clipboard, dispatcher);
        WindowBackdropCatalog backdropCatalog = WindowBackdropCatalog.Default;
        WindowBackdropSupport backdropSupport = WindowBackdropSupport.Current;
        BackdropOptions = backdropCatalog.Backdrops.Select(backdrop =>
        {
            bool supported = backdrop.Kind switch
            {
                "mica" => backdropSupport.Mica,
                "acrylic" => backdropSupport.Acrylic,
                _ => true
            };
            return new WindowBackdropOption(backdrop.Id, backdrop.Label, supported,
                supported ? "Este plano de fundo está disponível." : "Este efeito não é compatível com o sistema atual.");
        }).ToArray();
        _isInitializing = true;
        SelectedThemeOption = ThemeOptions.First(option => option.Preference == themeService.Preference);
        SelectedBackdropOption = BackdropOptions.FirstOrDefault(option =>
            option.Id.Equals(preferences.BackdropPreference, StringComparison.OrdinalIgnoreCase))
            ?? BackdropOptions.First(option => option.Id == backdropCatalog.Document.Default);
        SearchOnDemand = preferences.SearchOnDemand;
        ProvisioningMenuEnabled = preferences.ProvisioningMenuEnabled;
        SelectedStartupPageOption = StartupPageOptions.First(option => option.Page == preferences.StartupPage);
        _isInitializing = false;
        foreach (PackageVisualPreferenceOption option in PackageVisualPreferences)
            option.PropertyChanged += PackageVisualPreferenceChanged;
        preferences.ProvisioningMenuEnabledChanged += OnProvisioningPreferenceChanged;
        ReloadScheduledMaintenance();
        LoadPortabilitySettings();
    }

    private void PackageVisualPreferenceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_isInitializing || args.PropertyName != nameof(PackageVisualPreferenceOption.IsEnabled)
            || sender is not PackageVisualPreferenceOption option)
            return;

        try
        {
            _packageVisualPreferences.SetValue(option.Id, option.IsEnabled);
            _appLog.Write(AppLogLevel.Debug, "App", "Settings.PackageVisualPreferences",
                "Preferência visual alterada.", eventName: "PreferenceChanged");
            StatusText = $"{option.Label}: {(option.IsEnabled ? "Ativado" : "Desativado")} e salvo.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "SettingsViewModel.SetPackageVisualPreference");
            StatusText = $"Não foi possível salvar “{option.Label}”. A preferência anterior foi mantida.";
            option.IsEnabled = _packageVisualPreferences.GetValue(option.Id);
        }
    }

    public void ResetPackageVisualPreferences()
    {
        foreach (PackageVisualPreferenceOption option in PackageVisualPreferences)
            option.IsEnabled = PackageVisualPreferencesCatalog.Default.GetDefaultValue(option.Id);
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

    partial void OnSelectedBackdropOptionChanged(WindowBackdropOption? value)
    {
        if (_isInitializing || value is null || !value.IsSupported)
            return;

        try
        {
            _preferences.SetBackdropPreference(value.Id);
            StatusText = $"O plano de fundo {value.Label} foi aplicado e salvo neste dispositivo.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "SettingsViewModel.SetBackdrop");
            StatusText = "Não foi possível salvar o plano de fundo. A preferência anterior foi mantida.";
            _isInitializing = true;
            SelectedBackdropOption = BackdropOptions.FirstOrDefault(option =>
                option.Id.Equals(_preferences.BackdropPreference, StringComparison.OrdinalIgnoreCase))
                ?? BackdropOptions.First(option => option.Id == WindowBackdropCatalog.Default.Document.Default);
            _isInitializing = false;
        }
    }

    partial void OnSelectedStartupPageOptionChanged(StartupPageOption? value)
    {
        if (_isInitializing || value is null) return;
        try
        {
            _preferences.SetStartupPage(value.Page);
            StatusText = $"O aplicativo abrirá em {value.Label}.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "SettingsViewModel.SetStartupPage");
            StatusText = "Não foi possível salvar a página inicial. A preferência anterior foi mantida.";
            _isInitializing = true;
            SelectedStartupPageOption = StartupPageOptions.First(option => option.Page == _preferences.StartupPage);
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
        _appLog.Write(AppLogLevel.Debug, "App", "Settings.ProvisioningMenu",
            "Preferência de menu alterada.", eventName: "PreferenceChanged");
        _isInitializing = true;
        ProvisioningMenuEnabled = enabled;
        _isInitializing = false;
    }
}
