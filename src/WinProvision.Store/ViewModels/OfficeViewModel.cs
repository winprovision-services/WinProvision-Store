using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;
public enum OfficeAppsViewMode { List, Cards, Icons }

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OfficeViewModel : ObservableObject
{
    [ObservableProperty]
    public partial OfficeAppsViewMode AppsViewMode { get; set; } = OfficeAppsViewMode.Cards;
    private readonly OfficeDeploymentToolService _officeService;
    private readonly OfficeInstalledProductsDetector _installedDetector;
    private readonly OperationsQueueService _queue;
    private readonly CollectionsViewModel _collections;
    private readonly IPresentationService _presentation;
    private readonly IDesktopIntegrationService _desktop;
    private readonly ApplicationPreferencesService _preferences;
    private readonly Dictionary<string, bool> _appSelectionOverrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _isApplyingAutoUpdates;
    private static readonly HashSet<string> UnknownDetectedChannels = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _detectedApplications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private OfficePageCatalogService? _pageCatalog;
    private IReadOnlyList<ElevationSettingDefinition> _pageDefinitions = [];
    private bool _restoringOfficePreferences;

    public ObservableCollection<OfficeAppToggleViewModel> AppToggleItems { get; } = new();
    public ObservableCollection<OfficeAppToggleViewModel> CoreAppItems { get; } = new();
    public ObservableCollection<OfficeCategoryOption> Categories { get; } = new();
    public ObservableCollection<OfficePlan> Plans { get; } = new();
    public ObservableCollection<OfficeChannelOption> Channels { get; } = new();
    public ObservableCollection<OfficePlan> VisioProductOptions { get; } = new();
    public ObservableCollection<OfficePlan> ProjectProductOptions { get; } = new();
    public ObservableCollection<OfficeAppStatusRow> InstalledApps { get; } = new();
    public ObservableCollection<OfficeAppToggleViewModel> AdditionalLanguages { get; } = new();
    public ObservableCollection<OfficeAppToggleViewModel> ExcludedComponents { get; } = new();

    public IReadOnlyList<OfficeSelectorOption> ArchitectureOptions { get; } = (WinProvision.Store.ViewModels.OfficeSelectorOption[])[new("64", "64 bits"), new("32", "32 bits")];
    public IReadOnlyList<OfficeSelectorOption> LanguageOptions { get; } =
        (WinProvision.Store.ViewModels.OfficeSelectorOption[])[new("pt-br", "Português (Brasil)"), new("en-us", "Inglês (Estados Unidos)"), new("es-es", "Espanhol"), new("fr-fr", "Francês"), new("de-de", "Alemão")];
    public IReadOnlyList<OfficeSelectorOption> DisplayOptions { get; } = (WinProvision.Store.ViewModels.OfficeSelectorOption[])[new("Silent", "Oculta"), new("Visible", "Visível")];
    public IReadOnlyList<SettingsCatalogGroupOption> CatalogGroups { get; private set; } = [];
    public IReadOnlyList<SettingsCatalogGroupOption> AppCatalogGroups { get; private set; } = [];
    public IReadOnlyList<SettingsCatalogGroupOption> ActionCatalogGroups { get; private set; } = [];
    public IReadOnlyList<SettingsCatalogGroupOption> StatusCatalogGroups { get; private set; } = [];
    public IReadOnlyList<SettingsCatalogSettingOption> AutoUpdateCatalogSettings { get; private set; } = [];
    public IReadOnlyList<SettingsCatalogSettingOption> ExclusionCatalogSettings { get; private set; } = [];
    public IReadOnlyList<SettingsCatalogSettingOption> ApplicationSettings { get; private set; } = [];
    public IReadOnlyList<SettingsCatalogSettingOption> StatusSettings { get; private set; } = [];
    public ObservableCollection<OfficeCatalogActionOption> CatalogActions { get; } = [];
    public bool HasInstalledForConditions => HasInstalledProducts;
    public string CatalogStatusValue => HasInstalledProducts ? "installed" : "not-installed";
    public string PageTitle => _pageCatalog?.ResolveText("office.page.title") ?? "Office";
    public IReadOnlyList<ElevationSettingDefinition> CatalogDefinitions => _pageDefinitions;
    public IReadOnlyDictionary<string, string> CatalogValues { get; private set; } = new Dictionary<string, string>();
    public bool IsApplicationDetected(string appId) => _detectedApplications.Contains(appId);

    [ObservableProperty]
    public partial OfficeCategoryOption? SelectedCategory { get; set; }

    [ObservableProperty]
    public partial OfficePlan? SelectedPlan { get; set; }

    [ObservableProperty]
    public partial OfficeChannelOption? SelectedChannel { get; set; }

    [ObservableProperty]
    public partial OfficePlan? SelectedVisioProduct { get; set; }

    [ObservableProperty]
    public partial OfficePlan? SelectedProjectProduct { get; set; }

    [ObservableProperty]
    public partial string Architecture { get; set; } = "64";

    [ObservableProperty]
    public partial string LanguageId { get; set; } = "pt-br";

    [ObservableProperty]
    public partial string DisplayMode { get; set; } = "Silent";

    [ObservableProperty]
    public partial bool RequestedAutoUpdatesEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool DownloadBeforeInstall { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasInstalledProducts { get; private set; }

    [ObservableProperty]
    public partial bool HasNoInstalledProducts { get; private set; } = true;

    [ObservableProperty]
    public partial bool HasUnsupportedInstallations { get; private set; }

    [ObservableProperty]
    public partial string UnsupportedInstallationsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanInstallSelectedProducts { get; private set; } = true;

    private IReadOnlyList<OfficeInstalledProduct> _installedProducts = [];

    [ObservableProperty]
    public partial string InstalledSummary { get; private set; } = "Verifique o estado do Office.";

    [ObservableProperty]
    public partial string LastCheckedText { get; private set; } = "Ainda não verificado";

    [ObservableProperty]
    public partial string DetectedProductsText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string DetectedStoreProductsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UninstallToolTip { get; private set; } = "Instale o Office antes de desinstalar.";

    public bool HasVisioSelected => AppToggleItems.Any(item => item.ProductGroup == "visio" && item.IsOn);
    public bool HasProjectSelected => AppToggleItems.Any(item => item.ProductGroup == "project" && item.IsOn);
    public bool HasAdditionalProductSelected => AppToggleItems.Any(item => item.IsAdditionalProduct && item.IsOn);
    public bool IsChannelEnabled { get; private set; }
    public string? ChannelToolTip { get; private set; }
    public bool CanEditUpdatePreference => !_isApplyingAutoUpdates && !HasUnsupportedInstallations;
    public bool CanRemoveOffice => HasInstalledProducts && !HasUnsupportedInstallations;
    public string PrimaryActionText => HasInstalledProducts ? "Aplicar alterações" : "Instalar";
    public bool AutoUpdatesEnabled { get; private set; } = true;
    public int SelectedAppCount => CoreAppItems.Count(item => item.IsEnabled && item.IsOn);
    public string SelectionSummary => $"{SelectedAppCount} selecionado(s)";
    public string SelectAppsActionText => CoreAppItems.Where(item => item.IsEnabled).All(item => item.IsOn) ? "Desmarcar todos" : "Selecionar todos";

    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand ApplyAutoUpdatesCommand { get; }
    public IRelayCommand AddToCatalogCommand { get; }
    public IRelayCommand OpenWorkFolderCommand { get; }
    public IRelayCommand RefreshInstalledCommand { get; }
    public IRelayCommand SelectAllAppsCommand { get; }
    public string WorkRoot => _officeService.WorkRoot;
    public IRelayCommand SelectAppsCommand => SelectAllAppsCommand;
    public IAsyncRelayCommand ApplyActionCommand => InstallCommand;
    public IAsyncRelayCommand RemoveActionCommand => RemoveCommand;
    public bool HasActiveOfficeOperation => _queue.Operations.Any(item => !item.IsFinished
        && (item.ManagerOverride ?? item.Manager).Equals("office", StringComparison.OrdinalIgnoreCase));

    public OfficeViewModel(
        OfficeDeploymentToolService officeService,
        OfficeInstalledProductsDetector installedDetector,
        OperationsQueueService queue,
        CollectionsViewModel collections,
        IPresentationService presentation,
        IDesktopIntegrationService desktop,
        ApplicationPreferencesService preferences)
    {
        _officeService = officeService;
        _installedDetector = installedDetector;
        _queue = queue;
        _collections = collections;
        _presentation = presentation;
        _desktop = desktop;
        _preferences = preferences;

        InstallCommand = new AsyncRelayCommand(InstallOfficeAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveOfficeAsync);
        ApplyAutoUpdatesCommand = new AsyncRelayCommand(ApplyRequestedAutoUpdatesAsync);
        AddToCatalogCommand = new RelayCommand(AddToCatalog);
        OpenWorkFolderCommand = new RelayCommand(OpenWorkFolder);
        RefreshInstalledCommand = new RelayCommand(RefreshInstalledProducts);
        SelectAllAppsCommand = new RelayCommand(SelectAllApps);

        foreach (OfficeSelectorOption language in LanguageOptions)
            AdditionalLanguages.Add(new OfficeAppToggleViewModel(language.Id, language.Label, "", false));
        foreach (OfficeCatalogApp app in OfficeCatalogRuntime.Current.Apps.Where(item => item.Kind == "exclude"))
            CoreAppItems.Add(new OfficeAppToggleViewModel(app.Id, app.Name,
                $"{OfficeAppCatalog.IconBaseUrl}/{app.Icon}", app.DefaultSelected, fallbackIconUrl: app.IconFallback,
                cardDisplayName: app.CardDisplayName));
        foreach (OfficeCatalogApp app in OfficeCatalogRuntime.Current.Apps.Where(item => item.Kind == "addOn"))
            AppToggleItems.Add(new OfficeAppToggleViewModel(app.Id, app.Name,
                $"{OfficeAppCatalog.IconBaseUrl}/{app.Icon}", app.DefaultSelected, true, app.IconFallback,
                app.ProductGroup, app.CardDisplayName));

        foreach (OfficeAppToggleViewModel item in AppToggleItems)
            item.PropertyChanged += AppToggle_PropertyChanged;
        foreach (OfficeAppToggleViewModel item in CoreAppItems)
            item.PropertyChanged += AppToggle_PropertyChanged;
        foreach (OfficeAppToggleViewModel item in AdditionalLanguages)
            item.PropertyChanged += AppToggle_PropertyChanged;
        foreach (var item in OfficeAppCatalog.AdvancedApps)
        {
            var setting = new OfficeAppToggleViewModel(item.Id, item.DisplayName, string.Empty,
                _preferences.OfficeExcludedComponents.Contains(item.Id, StringComparer.OrdinalIgnoreCase));
            ExcludedComponents.Add(setting);
            setting.PropertyChanged += AppToggle_PropertyChanged;
        }

        Categories.Add(new(OfficeEditionCategory.Personal, "Microsoft 365"));
        Categories.Add(new(OfficeEditionCategory.Corporate365, "Microsoft 365 Empresarial"));
        Categories.Add(new(OfficeEditionCategory.Ltsc, "Office LTSC"));
        Categories.Add(new(OfficeEditionCategory.VisioProject, "Visio / Project"));
        SelectedCategory = Categories[0];
    }

    public void LoadPageCatalog(OfficePageCatalogService catalog)
    {
        _pageCatalog = catalog;
        _pageDefinitions = catalog.Catalog.GetSettingsForPage("office");
        CatalogGroups = catalog.Catalog.GetGroups("office").Select(group =>
        {
            SettingsCatalogSettingOption[] settings = catalog.Catalog.GetSettings(group.Key).Select(definition =>
                new SettingsCatalogSettingOption(definition, catalog.ResolveText(definition.TitleTextKey),
                    catalog.ResolveText(definition.DescriptionTextKey), null,
                    GetCatalogValue(definition), catalog.Text)).ToArray();
            return new SettingsCatalogGroupOption(catalog.ResolveText(group.TitleTextKey), null, null, null, settings, group.Icon);
        }).ToArray();
        AppCatalogGroups = CatalogGroups.Where(group => group.Settings.Any(setting => setting.Key == "office.apps")).ToArray();
        ActionCatalogGroups = CatalogGroups.Where(group => group.Settings.Any(setting => setting.IsAction)).ToArray();
        StatusCatalogGroups = CatalogGroups.Where(group => group.Settings.Any(setting => setting.Key == "office.status")).ToArray();
        AutoUpdateCatalogSettings = CatalogGroups.SelectMany(group => group.Settings)
            .Where(setting => setting.Definition.StorageKey == "AutoUpdatesEnabled").ToArray();
        ExclusionCatalogSettings = CatalogGroups.SelectMany(group => group.Settings)
            .Where(setting => setting.Definition.StorageKey == "ExcludedComponents").ToArray();
        ApplicationSettings = CatalogGroups.SelectMany(group => group.Settings)
            .Where(setting => setting.Key == "office.apps").ToArray();
        StatusSettings = CatalogGroups.SelectMany(group => group.Settings)
            .Where(setting => setting.Definition.Kind == "status").ToArray();
        CatalogActions.Clear();
        foreach (SettingsCatalogSettingOption option in ActionCatalogGroups.SelectMany(group => group.Settings))
            CatalogActions.Add(new(option, option.Definition.ActionKey switch
            {
                "office.install" => InstallCommand,
                "office.remove" => RemoveCommand,
                _ => null
            }));
        foreach (OfficeAppToggleViewModel app in CoreAppItems)
            app.IsOn = OfficeCatalogRuntime.Current.Apps.FirstOrDefault(item => item.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase))?.DefaultSelected == true;
        foreach (ElevationSettingDefinition definition in _pageDefinitions.Where(item => item.StorageKey == "ExcludedComponents"))
        {
            ExcludedComponents.Clear();
            foreach (ElevationSettingChoice option in definition.Options ?? [])
            {
                if (OfficeCatalogRuntime.Current.LegacyIgnoredOdtIds.Contains(option.Value, StringComparer.OrdinalIgnoreCase)) continue;
                ExcludedComponents.Add(new OfficeAppToggleViewModel(option.Value, catalog.ResolveText(option.LabelTextKey), string.Empty, false));
            }
            foreach (OfficeAppToggleViewModel item in ExcludedComponents)
                item.PropertyChanged += AppToggle_PropertyChanged;
        }
        RefreshCatalogValues();
        OnPropertyChanged(nameof(CatalogGroups));
        OnPropertyChanged(nameof(AppCatalogGroups));
        OnPropertyChanged(nameof(ActionCatalogGroups));
        OnPropertyChanged(nameof(StatusCatalogGroups));
        OnPropertyChanged(nameof(AutoUpdateCatalogSettings));
        OnPropertyChanged(nameof(ExclusionCatalogSettings));
        OnPropertyChanged(nameof(ApplicationSettings));
        OnPropertyChanged(nameof(StatusSettings));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(CatalogDefinitions));
    }

    private string GetCatalogValue(ElevationSettingDefinition definition) => definition.StorageKey switch
    {
        "ProductId" => SelectedPlan?.ProductId ?? definition.DefaultTextValue,
        "Channel" => SelectedChannel?.Id ?? definition.DefaultTextValue,
        "Architecture" => Architecture,
        "LanguageId" => LanguageId,
        "DisplayMode" => DisplayMode,
        "AutoUpdatesEnabled" => RequestedAutoUpdatesEnabled.ToString(),
        _ => definition.DefaultTextValue
    };

    public void RefreshCatalogValues()
    {
        var values = _pageDefinitions.ToDictionary(definition => definition.Key, GetCatalogValue, StringComparer.OrdinalIgnoreCase);
        foreach (ElevationSettingDefinition definition in _pageDefinitions.Where(setting => setting.Kind == "status"))
            values[definition.Key] = CatalogStatusValue;
        CatalogValues = values;
        foreach (SettingsCatalogSettingOption option in CatalogGroups.SelectMany(group => group.Settings))
        {
            option.Value = CatalogValues.GetValueOrDefault(option.Key, option.Definition.DefaultTextValue);
            option.RefreshPresentation(CatalogValues, _pageDefinitions);
        }
        OnPropertyChanged(nameof(CatalogValues));
        foreach (OfficeCatalogActionOption action in CatalogActions)
        {
            action.Refresh(_pageDefinitions, CatalogValues, HasUnsupportedInstallations);
        }
    }

    public void ApplyCatalogValue(SettingsCatalogSettingOption option, string value)
    {
        if (!option.Options.Any(candidate => candidate.Value.Equals(value, StringComparison.Ordinal)))
        {
            RefreshCatalogValues();
            StatusText = "O valor selecionado não está disponível nesta configuração.";
            return;
        }
        switch (option.Definition.StorageKey)
        {
            case "ProductId":
                OfficePlan? plan = OfficePlanCatalog.All.FirstOrDefault(candidate => candidate.ProductId.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (plan is null) break;
                SelectedCategory = Categories.FirstOrDefault(category => OfficePlanCatalog.ByCategory(category.Category).Any(item => item.ProductId == value));
                SelectedPlan = plan;
                break;
            case "Channel": SelectedChannel = Channels.FirstOrDefault(candidate => candidate.Id.Equals(value, StringComparison.OrdinalIgnoreCase)); break;
            case "Architecture": Architecture = value; break;
            case "LanguageId": LanguageId = value; break;
            case "DisplayMode": DisplayMode = value; break;
            case "AutoUpdatesEnabled": RequestedAutoUpdatesEnabled = bool.TryParse(value, out bool updates) && updates; break;
        }
        RefreshCatalogValues();
    }

    partial void OnSelectedCategoryChanged(OfficeCategoryOption? value) => LoadPlansForCategory();
    partial void OnSelectedPlanChanged(OfficePlan? value) { ConfigureSelectedPlan(); RefreshCatalogValues(); SaveOfficeSettings(); }
    partial void OnSelectedChannelChanged(OfficeChannelOption? value) { RefreshAdditionalProductOptions(); RefreshCatalogValues(); SaveOfficeSettings(); }
    partial void OnArchitectureChanged(string value) { RefreshCatalogValues(); SaveOfficeSettings(); }
    partial void OnLanguageIdChanged(string value) { RefreshCatalogValues(); SaveOfficeSettings(); }
    partial void OnDisplayModeChanged(string value) => SaveOfficeSettings();
    partial void OnDownloadBeforeInstallChanged(bool value) => SaveOfficeSettings();
    partial void OnRequestedAutoUpdatesEnabledChanged(bool value) => SaveOfficeSettings();
    partial void OnSelectedVisioProductChanged(OfficePlan? value) { RefreshInstallAvailability(); SaveOfficeSettings(); }
    partial void OnSelectedProjectProductChanged(OfficePlan? value) { RefreshInstallAvailability(); SaveOfficeSettings(); }

    private void AppToggle_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(OfficeAppToggleViewModel.IsOn) && sender is OfficeAppToggleViewModel app)
        {
            if (CoreAppItems.Contains(app) || AppToggleItems.Contains(app))
                _appSelectionOverrides[app.Id] = app.IsOn;
            SaveOfficeSettings();
        }
        if (args.PropertyName is not (nameof(OfficeAppToggleViewModel.IsOn) or nameof(OfficeAppToggleViewModel.IsEnabled)))
            return;
        OnPropertyChanged(nameof(SelectedAppCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectAppsActionText));
        OnPropertyChanged(nameof(HasVisioSelected));
        OnPropertyChanged(nameof(HasProjectSelected));
        OnPropertyChanged(nameof(HasAdditionalProductSelected));
        RefreshInstallAvailability();
        RefreshCatalogValues();
    }

    public void SelectAllApps()
    {
        bool select = !CoreAppItems.Where(item => item.IsEnabled).All(item => item.IsOn);
        foreach (OfficeAppToggleViewModel item in CoreAppItems)
            if (item.IsEnabled)
                item.IsOn = select;
    }

    public IReadOnlyList<OfficePlan> GetAdditionalProducts()
    {
        var products = new List<OfficePlan>();
        foreach (OfficeAppToggleViewModel addOn in AppToggleItems.Where(item => item.IsAdditionalProduct && item.IsOn))
            if (SelectedAdditionalProduct(addOn.ProductGroup) is { } product) products.Add(product);
        return products;
    }

    private OfficePlan? SelectedAdditionalProduct(string? group) => group switch
    {
        "visio" => SelectedVisioProduct,
        "project" => SelectedProjectProduct,
        _ => null
    };

    private void LoadPlansForCategory()
    {
        Plans.Clear();
        if (SelectedCategory is not { } category)
            return;
        foreach (OfficePlan plan in OfficePlanCatalog.ByCategory(category.Category).OrderBy(item => item.Group, StringComparer.Ordinal).ThenBy(item => item.Order))
            Plans.Add(plan);
        SelectedPlan = Plans.FirstOrDefault(plan => plan.IsDefault) ?? Plans.FirstOrDefault();
    }

    private void ConfigureSelectedPlan()
    {
        Channels.Clear();
        if (SelectedPlan is not { } plan)
        {
            IsChannelEnabled = false;
            ChannelToolTip = null;
            SelectedChannel = null;
            OnPropertyChanged(nameof(IsChannelEnabled));
            OnPropertyChanged(nameof(ChannelToolTip));
            RefreshCoreAppApplicability();
            return;
        }

        if (plan.SupportsSelectableChannel)
        {
            foreach (var channel in OfficeChannelCatalog.SubscriptionChannels)
                Channels.Add(new(channel.Id, channel.DisplayName));
            ChannelToolTip = "O canal selecionado será aplicado ao produto principal e aos produtos adicionais compatíveis.";
            IsChannelEnabled = true;
            SelectedChannel = Channels.FirstOrDefault(option => option.Id == plan.Channel) ?? Channels.FirstOrDefault();
        }
        else
        {
            Channels.Add(new(plan.Channel ?? "-", plan.Channel ?? "Fixo pela licença"));
            ChannelToolTip = "O canal de atualização é determinado por esta edição do Office LTSC.";
            IsChannelEnabled = false;
            SelectedChannel = Channels[0];
        }
        OnPropertyChanged(nameof(IsChannelEnabled));
        OnPropertyChanged(nameof(ChannelToolTip));
        RefreshAdditionalProductOptions();
        RefreshInstallAvailability();
    }

    private void RefreshAdditionalProductOptions()
    {
        if (SelectedPlan is not { } selectedPlan)
            return;
        string? previousVisioId = SelectedVisioProduct?.ProductId;
        string? previousProjectId = SelectedProjectProduct?.ProductId;
        VisioProductOptions.Clear();
        ProjectProductOptions.Clear();
        string? channel = selectedPlan.SupportsSelectableChannel ? SelectedChannel?.Id ?? selectedPlan.Channel : selectedPlan.Channel;
        bool subscriptionChannel = OfficeChannelCatalog.SubscriptionChannels.Any(item =>
            item.Id.Equals(channel, StringComparison.OrdinalIgnoreCase));

        IEnumerable<OfficePlan> compatible = OfficePlanCatalog.All.Where(plan =>
            plan.Category == OfficeEditionCategory.VisioProject &&
            !plan.ProductId.Equals(selectedPlan.ProductId, StringComparison.OrdinalIgnoreCase) &&
            !HasSameAdditionalProductType(plan, selectedPlan) &&
            ((subscriptionChannel && plan.SupportsSelectableChannel) || string.Equals(plan.Channel, channel, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(plan => subscriptionChannel && plan.IsDefault ? 0 : 1)
            .ThenBy(plan => plan.Group, StringComparer.Ordinal).ThenBy(plan => plan.Order);
        foreach (OfficePlan plan in compatible)
        {
            if (IsVisioPlan(plan))
                VisioProductOptions.Add(plan);
            else if (IsProjectPlan(plan))
                ProjectProductOptions.Add(plan);
        }

        SelectedVisioProduct = VisioProductOptions.FirstOrDefault(plan => plan.ProductId.Equals(previousVisioId, StringComparison.OrdinalIgnoreCase))
            ?? VisioProductOptions.FirstOrDefault();
        SelectedProjectProduct = ProjectProductOptions.FirstOrDefault(plan => plan.ProductId.Equals(previousProjectId, StringComparison.OrdinalIgnoreCase))
            ?? ProjectProductOptions.FirstOrDefault();

        foreach (OfficeAppToggleViewModel addOn in AppToggleItems)
        {
            bool hasPlans = addOn.ProductGroup switch
            {
                "visio" => VisioProductOptions.Count > 0,
                "project" => ProjectProductOptions.Count > 0,
                _ => false
            };
            addOn.IsEnabled = hasPlans && addOn.ProductGroup != selectedPlan.Group;
            if (!addOn.IsEnabled) addOn.IsOn = false;
        }
        RefreshCoreAppApplicability();
        RefreshInstallAvailability();
    }

    private void RefreshCoreAppApplicability()
    {
        IReadOnlyList<string> included = SelectedPlan?.IncludedApplicationIds ?? Array.Empty<string>();
        IReadOnlyList<string> conditional = SelectedPlan?.ConditionalApplicationIds ?? Array.Empty<string>();
        bool discardedSelection = false;
        foreach (OfficeAppToggleViewModel app in CoreAppItems)
        {
            bool isAvailable = included.Contains(app.Id, StringComparer.OrdinalIgnoreCase)
                || conditional.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
            if (!isAvailable && app.IsOn)
            {
                app.IsOn = false;
                _appSelectionOverrides.Remove(app.Id);
                discardedSelection = true;
            }
            app.IsEnabled = isAvailable;
        }
        if (discardedSelection)
            StatusText = "A seleção de aplicativos foi ajustada ao produto escolhido.";
    }

    private void RefreshInstallAvailability()
    {
        CanInstallSelectedProducts = !HasUnsupportedInstallations && SelectedPlan is not null;
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    private static bool IsVisioPlan(OfficePlan plan) => plan.Group == "visio";
    private static bool IsProjectPlan(OfficePlan plan) => plan.Group == "project";
    private static bool HasSameAdditionalProductType(OfficePlan left, OfficePlan right) =>
        (IsVisioPlan(left) && IsVisioPlan(right)) || (IsProjectPlan(left) && IsProjectPlan(right));
    private static bool IsPreferredAdditionalPlan(OfficePlan plan) => plan.IsDefault;

    public OfficeInstallRequest? CreateInstallRequest()
    {
        if (SelectedPlan is not { } plan)
        {
            StatusText = "Selecione um plano antes de continuar.";
            return null;
        }

        bool useViperDownload = _preferences.UseViperOfficeDownload;
        if (DownloadBeforeInstall && useViperDownload
            && (AdditionalLanguages.Any(item => item.IsOn) || GetAdditionalProducts().Count > 0))
        {
            StatusText = "O download paralelo Viper aceita um produto e um idioma por vez. Remova produtos/idiomas adicionais ou selecione ODT.";
            return null;
        }

        var request = new OfficeInstallRequest(
            plan,
            Architecture == "32" ? 32 : 64,
            LanguageId,
            CoreAppItems.Where(item => item.IsEnabled && !item.IsOn).Select(item => item.Id)
                .Concat(ExcludedComponents.Where(item => item.IsOn).Select(item => item.Id)).ToArray(),
            DisplayNone: DisplayMode != "Visible",
            AdditionalLanguageIds: AdditionalLanguages.Where(item => item.IsOn).Select(item => item.Id).ToArray(),
            DisplayLevel: DisplayMode == "Visible" ? OfficeDisplayLevel.Visible : OfficeDisplayLevel.Silent,
            ChannelOverride: plan.SupportsSelectableChannel ? SelectedChannel?.Id : null,
            AutoUpdatesEnabled: RequestedAutoUpdatesEnabled,
            AdditionalProducts: GetAdditionalProducts(), DownloadBeforeInstall: DownloadBeforeInstall,
            UseViperDownload: useViperDownload);
        if (DownloadBeforeInstall)
            request = request with { SourcePath = _officeService.GetPayloadCachePath(request) };

        string? error = OfficeConfigXmlBuilder.ValidateRequest(request);
        if (error is not null)
        {
            StatusText = error;
            return null;
        }
        return request;
    }

    private void OpenWorkFolder()
    {
        try
        {
            Directory.CreateDirectory(WorkRoot);
            _desktop.OpenFolder(WorkRoot);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "OfficeViewModel.OpenWorkFolder");
            StatusText = $"Não foi possível abrir a pasta de trabalho: {exception.Message}";
        }
    }

    private async Task InstallOfficeAsync()
    {
        OfficeInstallRequest? request = CreateInstallRequest();
        if (request is null)
            return;
        RefreshInstalledProducts();
        bool applyChanges = HasInstalledProducts;
        string summary = $"Produto: {request.Plan.DisplayName}\nArquitetura: {request.Architecture} bits\nIdioma: {request.LanguageId}\nCanal: {request.ChannelOverride ?? request.Plan.Channel ?? "determinado pela licença"}\nAplicativos excluídos: {(request.ExcludedApps.Count == 0 ? "nenhum" : string.Join(", ", request.ExcludedApps))}\nProdutos adicionais: {(request.AdditionalProducts?.Count > 0 ? string.Join(", ", request.AdditionalProducts.Select(item => item.DisplayName)) : "nenhum")}\n\nO Office Click-to-Run poderá fechar aplicativos do Office durante a configuração.";
        if (!await _presentation.ConfirmAsync(applyChanges ? "Aplicar alterações ao Office" : "Instalar o Office", summary, applyChanges ? "Aplicar" : "Instalar"))
            return;
        StatusText = $"{(applyChanges ? "Aplicando alterações em" : "Instalando")} {request.Plan.DisplayName}...";
        try
        {
            bool success = await OperationRunner.RunOfficeInstallAsync(_queue, _officeService, request, applyChanges: applyChanges);
            StatusText = success
                ? $"{request.Plan.DisplayName} instalado com sucesso."
                : "Falha ao instalar o Office. Veja a fila de operações para detalhes.";
            RefreshInstalledProducts();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Instalação do Office cancelada.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "OfficeViewModel.InstallOfficeAsync");
            StatusText = "Não foi possível instalar o Office. Tente novamente.";
        }
    }

    private void AddToCatalog()
    {
        OfficeInstallRequest? request = CreateInstallRequest();
        if (request is null)
            return;
        OfficePlan plan = request.Plan;
        OfficePlan[] additional = request.AdditionalProducts?.ToArray() ?? [];
        var options = new OfficeInstallOptions
        {
            ProductId = plan.ProductId,
            Architecture = request.Architecture,
            LanguageId = request.LanguageId,
            AdditionalLanguageIds = request.AdditionalLanguageIds?.ToList() ?? [],
            ExcludedApps = request.ExcludedApps.ToList(),
            Silent = request.DisplayLevel == OfficeDisplayLevel.Silent,
            ChannelOverride = request.ChannelOverride,
            AutoUpdatesEnabled = request.AutoUpdatesEnabled,
            DownloadBeforeInstall = request.DownloadBeforeInstall,
            UseViperDownload = request.UseViperDownload,
            AdditionalProductIds = additional.Select(item => item.ProductId).ToList()
        };
        string appsSummary = string.Join(", ", AppToggleItems.Where(item => item.IsOn).Select(item => item.DisplayName));
        string additionalSummary = string.Join(", ", additional.Select(item => item.DisplayName));
        string[] ids = [plan.ProductId, .. additional.Select(item => item.ProductId)];
        var entry = new AppEntry
        {
            Id = $"office.{string.Join("+", ids)}".ToLowerInvariant(),
            Source = "office",
            Name = $"Office — {plan.DisplayName}" + (additional.Length == 0 ? "" : $" + {additionalSummary}"),
            Publisher = "Microsoft",
            Version = request.ChannelOverride ?? plan.Channel ?? "-",
            Description = $"Plano salvo pela página Office. Apps incluídos: {(appsSummary.Length == 0 ? "nenhum aplicativo selecionado" : appsSummary)}. Produtos adicionais: {additionalSummary}.",
            IconUrl = OfficeAppCatalog.Microsoft365IconUrl,
            Tags = { "Office", plan.Category.ToString() },
            Office = options
        };
        try
        {
            _collections.Add(entry);
            StatusText = $"Configuração de {plan.DisplayName} salva na coleção ativa. Use Coleções para revisar o perfil antes da instalação.";
        }
        catch (InvalidOperationException exception)
        {
            StatusText = exception.Message;
        }
    }

    private async Task RemoveOfficeAsync()
    {
        RefreshInstalledProducts();
        if (!HasInstalledProducts)
            return;
        if (HasActiveOfficeOperation)
        {
            StatusText = "Aguarde a operação Office em andamento antes de remover produtos.";
            return;
        }
        string scope = " A operação removerá todos os produtos Click-to-Run detectados.";
        bool hasStoreEditions = _installedDetector.GetStoreOfficeInstallations().Count > 0;
        if (hasStoreEditions)
            scope += " Você também pode remover o aplicativo Office da Microsoft Store desta conta.";
        OfficeRemovalConfirmation? confirmation = await _presentation.ConfirmOfficeRemovalAsync(
            "Remover o Office",
            $"{scope} Documentos e arquivos pessoais não são alvo desta operação; configurações e cache do Office podem ser removidos. Continuar?",
            "Desinstalar", hasStoreEditions);
        if (confirmation is null)
            return;

        var request = new OfficeRemoveRequest(
            RemoveAll: true,
            DisplayLevel: OfficeDisplayLevel.Silent,
            CleanStoreEdition: confirmation.CleanStoreEdition,
            UseAggressiveUninstall: false);
        StatusText = "Removendo o Office... Acompanhe o progresso na fila.";
        try
        {
            bool success = await OperationRunner.RunOfficeRemoveAsync(_queue, _officeService, request, "Remoção completa do Office (RemoveAll)");
            StatusText = success ? "Office removido com sucesso." : "Falha na remoção. Veja a fila de operações para detalhes.";
            RefreshInstalledProducts();
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "OfficeViewModel.RemoveOfficeAsync");
            StatusText = "Não foi possível remover o Office. Tente novamente.";
        }
    }

    public void ApplyOfficeCatalog(OfficeCatalogDocument catalog)
    {
        OfficeAppToggleViewModel[] previousItems = CoreAppItems.Concat(AppToggleItems).ToArray();
        foreach (OfficeAppToggleViewModel item in previousItems) item.PropertyChanged -= AppToggle_PropertyChanged;
        OfficeCatalogRuntime.UseValidated(catalog);
        CoreAppItems.Clear();
        AppToggleItems.Clear();
        foreach (OfficeCatalogApp app in catalog.Apps.Where(item => item.Kind is "exclude" or "addOn"))
        {
            bool selected = app.Kind == "exclude"
                ? _preferences.OfficeExcludedComponents.Contains(app.Id, StringComparer.OrdinalIgnoreCase)
                : _preferences.OfficeAdditionalLanguages.Contains(app.Id, StringComparer.OrdinalIgnoreCase)
                    || (_appSelectionOverrides.TryGetValue(app.Id, out bool wasSelected) ? wasSelected : app.DefaultSelected);
            var item = new OfficeAppToggleViewModel(app.Id, app.Name, $"{OfficeAppCatalog.IconBaseUrl}/{app.Icon}", selected,
                app.Kind == "addOn", app.IconFallback, app.ProductGroup, app.CardDisplayName);
            if (app.Kind == "exclude") CoreAppItems.Add(item); else AppToggleItems.Add(item);
            item.PropertyChanged += AppToggle_PropertyChanged;
        }
        _restoringOfficePreferences = true;
        try
        {
            OfficePlan? savedPlan = OfficePlanCatalog.ByProductId(_preferences.OfficeProductId);
            SelectedCategory = Categories.FirstOrDefault(category => savedPlan is not null
                && OfficePlanCatalog.ByCategory(category.Category).Any(plan => plan.ProductId == savedPlan.ProductId)) ?? Categories[0];
            LoadPlansForCategory();
            if (savedPlan is not null) SelectedPlan = Plans.FirstOrDefault(plan => plan.ProductId == savedPlan.ProductId) ?? SelectedPlan;
            ConfigureSelectedPlan();
            SelectedChannel = Channels.FirstOrDefault(option => option.Id.Equals(_preferences.OfficeChannel, StringComparison.OrdinalIgnoreCase)) ?? SelectedChannel;
            Architecture = _preferences.OfficeArchitecture;
            LanguageId = _preferences.OfficeLanguageId;
            DisplayMode = _preferences.OfficeDisplayMode;
            DownloadBeforeInstall = _preferences.OfficeDownloadBeforeInstall;
            RequestedAutoUpdatesEnabled = _preferences.OfficeAutoUpdatesEnabled;
            RefreshAdditionalProductOptions();
            SelectedVisioProduct = VisioProductOptions.FirstOrDefault(plan => plan.ProductId == _preferences.OfficeVisioProductId) ?? SelectedVisioProduct;
            SelectedProjectProduct = ProjectProductOptions.FirstOrDefault(plan => plan.ProductId == _preferences.OfficeProjectProductId) ?? SelectedProjectProduct;
            foreach (OfficeAppToggleViewModel language in AdditionalLanguages)
                language.IsOn = _preferences.OfficeAdditionalLanguages.Contains(language.Id, StringComparer.OrdinalIgnoreCase);
        }
        finally { _restoringOfficePreferences = false; }
        RefreshInstalledProducts();
        SaveOfficeSettings();
    }

    private void SaveOfficeSettings()
    {
        if (_restoringOfficePreferences) return;
        try
        {
            _preferences.SetOfficeSettings(SelectedPlan?.ProductId ?? "O365HomePremRetail", SelectedChannel?.Id ?? "Current",
                Architecture, LanguageId, DisplayMode, DownloadBeforeInstall, RequestedAutoUpdatesEnabled,
                AdditionalLanguages.Where(item => item.IsOn).Select(item => item.Id),
                ExcludedComponents.Where(item => item.IsOn).Select(item => item.Id),
                SelectedVisioProduct?.ProductId, SelectedProjectProduct?.ProductId);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "OfficeViewModel.SaveOfficeSettings");
        }
    }

    public void RefreshInstalledProducts()
    {
        IReadOnlyList<OfficeInstalledProduct> installed = _installedDetector.GetInstalledProducts();
        IReadOnlyList<string> unsupported = _installedDetector.GetUnsupportedInstallations();
        IReadOnlyList<string> storeProducts = _installedDetector.GetStoreOfficeInstallations();
        LastCheckedText = $"Verificado às {DateTime.Now:HH:mm}";
        _installedProducts = installed;
        _detectedApplications = _installedDetector.GetInstalledApplicationIds(installed);
        HasUnsupportedInstallations = unsupported.Count > 0 || storeProducts.Count > 0;
        UnsupportedInstallationsText = unsupported.Count == 0
            ? string.Empty
            : $"Instalação MSI não compatível com as opções desta página: {string.Join(", ", unsupported)}. A instalação e a alteração de configuração estão desativadas; a remoção usará a ferramenta oficial de recuperação.";
        OnPropertyChanged(nameof(CanEditUpdatePreference));
        RefreshInstallAvailability();
        HasInstalledProducts = installed.Count > 0 || unsupported.Count > 0 || storeProducts.Count > 0;
        DetectedStoreProductsText = storeProducts.Count == 0 ? string.Empty : $"Microsoft Store: {string.Join(", ", storeProducts)}";
        UninstallToolTip = HasInstalledProducts ? "Remover os produtos Office detectados" : "Instale o Office antes de desinstalar.";
        OnPropertyChanged(nameof(CanRemoveOffice));
        OnPropertyChanged(nameof(PrimaryActionText));
        InstalledApps.Clear();
        foreach (var (id, name, icon) in OfficeAppCatalog.CoreApps)
        {
            bool isInstalled = IsApplicationDetected(id);
            OfficeAppStatusRow row = new(name, icon, isInstalled ? CatalogState("installed") : CatalogState("notInstalled"),
                isInstalled ? "Installed" : "None");
            InstalledApps.Add(row);
        }

        foreach (var (id, name, icon) in OfficeAppCatalog.AdditionalProducts)
        {
            bool isInstalled = IsApplicationDetected(id);
            InstalledApps.Add(new(name, icon, isInstalled ? CatalogState("installed") : CatalogState("notInstalled"),
                isInstalled ? "Installed" : "None"));
        }
        if (installed.Count == 0)
        {
            InstalledSummary = unsupported.Count == 0 ? CatalogState("notInstalled") : "Instalação detectada fora do suporte Click-to-Run";
            DetectedProductsText = unsupported.Count == 0 ? "Nenhum produto Click-to-Run detectado" : string.Join(", ", unsupported);
        }
        else
        {
            OfficeInstalledProduct first = installed[0];
            string version = installed.Select(product => product.VersionToReport).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Desconhecida";
            string rawChannel = first.UpdateChannel is { Length: > 0 } value && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
                ? uri.Segments.Last().TrimEnd('/') : first.UpdateChannel ?? string.Empty;
            string channel = OfficeChannelCatalog.ResolveDetectedName(rawChannel);
            if (channel == "Canal personalizado" && !string.IsNullOrWhiteSpace(rawChannel) && UnknownDetectedChannels.Add(rawChannel))
                WinProvisionLog.Warning(new InvalidDataException($"Canal Office detectado não está catalogado: {rawChannel}"), "OfficeViewModel.UnknownUpdateChannel");
            string architecture = first.Platform?.ToLowerInvariant() switch { "x64" or "64" => "64 bits", "x86" or "32" => "32 bits", _ => "Desconhecida" };
            string language = first.ClientCulture ?? "Desconhecido";
            InstalledSummary = $"{CatalogState("installed")} · Versão {version} · {channel} · {architecture} · {language}";
            DetectedProductsText = string.Join(", ", installed.Select(product => product.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase));
        }
        HasNoInstalledProducts = installed.Count == 0;
        RefreshCatalogValues();
    }

    private static string CatalogState(string key) => OfficeCatalogRuntime.Current.States.TryGetValue(key, out OfficeCatalogState? state)
        ? state.Label : key;

    private async Task ApplyRequestedAutoUpdatesAsync() => await SetAutoUpdatesAsync(RequestedAutoUpdatesEnabled);

    private async Task SetAutoUpdatesAsync(bool enabled)
    {
        if (_isApplyingAutoUpdates || enabled == AutoUpdatesEnabled)
            return;
        bool previous = AutoUpdatesEnabled;
        if (!HasInstalledProducts)
        {
            AutoUpdatesEnabled = enabled;
            StatusText = "Preferência definida para a próxima instalação.";
            return;
        }

        _isApplyingAutoUpdates = true;
        OnPropertyChanged(nameof(CanEditUpdatePreference));
        try
        {
            bool success = await OperationRunner.RunOfficeSetAutoUpdateAsync(_queue, _officeService, enabled);
            AutoUpdatesEnabled = success ? enabled : previous;
            StatusText = success
                ? "Configuração enviada ao Office. Políticas da organização têm precedência."
                : "Não foi possível aplicar a política de atualização. Veja a fila de operações para detalhes.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "OfficeViewModel.SetAutoUpdatesAsync");
            AutoUpdatesEnabled = previous;
            StatusText = "Não foi possível alterar as atualizações do Office.";
        }
        finally
        {
            _isApplyingAutoUpdates = false;
            OnPropertyChanged(nameof(CanEditUpdatePreference));
            RequestedAutoUpdatesEnabled = AutoUpdatesEnabled;
        }
    }

}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OfficeCatalogActionOption(SettingsCatalogSettingOption setting, System.Windows.Input.ICommand? command) : ObservableObject
{
    public SettingsCatalogSettingOption Setting { get; } = setting;
    public System.Windows.Input.ICommand? Command { get; } = command;
    public string Title => Setting.Title;
    public string Description => Setting.DisplayDescription ?? string.Empty;
    public string ActionText => Setting.ActionButtonText;
    [ObservableProperty] public partial bool IsVisible { get; private set; }
    [ObservableProperty] public partial bool IsEnabled { get; private set; }

    public void Refresh(IReadOnlyList<ElevationSettingDefinition> definitions, IReadOnlyDictionary<string, string> values, bool unsupported)
    {
        ElevationSettingPresentation state = ElevationSettingsCatalog.ResolvePresentation(Setting.Definition, values,
            settings: definitions);
        IsVisible = state.IsVisible && (state.IsEnabled || Setting.Definition.Kind == "info");
        IsEnabled = Command is not null && !unsupported;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OfficeAppToggleViewModel : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }
    public string CardDisplayName => _cardDisplayName ?? DisplayName;
    public string IconUrl { get; }
    public string? FallbackIconUrl { get; }
    public bool IsAdditionalProduct { get; }
    public string? ProductGroup { get; }
    private readonly string? _cardDisplayName;

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public OfficeAppToggleViewModel(string id, string displayName, string iconUrl, bool isOnByDefault, bool isAdditionalProduct = false,
        string? fallbackIconUrl = null, string? productGroup = null, string? cardDisplayName = null)
    {
        Id = id;
        DisplayName = displayName;
        IconUrl = iconUrl;
        FallbackIconUrl = fallbackIconUrl;
        IsAdditionalProduct = isAdditionalProduct;
        ProductGroup = productGroup;
        _cardDisplayName = cardDisplayName;
        IsOn = isOnByDefault;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record OfficeCategoryOption(OfficeEditionCategory Category, string Label);
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record OfficeChannelOption(string Id, string Label);
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record OfficeSelectorOption(string Id, string Label);
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record OfficeAppStatusRow(string DisplayName, string IconUrl, string StatusText, string StatusKind);
