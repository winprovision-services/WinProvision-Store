using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Xml.Linq;
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
    private static readonly HashSet<string> DefaultOnApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "Word", "Excel", "PowerPoint", "Outlook", "OneNote", "Teams"
    };

    private readonly OfficeDeploymentToolService _officeService;
    private readonly OfficeInstalledProductsDetector _installedDetector;
    private readonly OperationsQueueService _queue;
    private readonly CollectionsViewModel _collections;
    private readonly IPresentationService _presentation;
    private readonly IOfficeConfigurationExportService _configurationExport;
    private readonly IClipboardService _clipboard;
    private readonly IDesktopIntegrationService _desktop;
    private bool _isApplyingAutoUpdates;

    public ObservableCollection<OfficeAppToggleViewModel> AppToggleItems { get; } = new();
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
    public partial bool CleanStoreEdition { get; set; } = true;
    [ObservableProperty]
    public partial bool DownloadBeforeInstall { get; set; }

    [ObservableProperty]
    public partial bool IsAdvancedExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsPreviewOpen { get; set; }

    [ObservableProperty]
    public partial string PreviewXml { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasInstalledProducts { get; private set; }

    [ObservableProperty]
    public partial bool HasNoInstalledProducts { get; private set; } = true;

    [ObservableProperty]
    public partial string InstalledSummary { get; private set; } = "Verifique o estado do Office.";

    [ObservableProperty]
    public partial string UninstallToolTip { get; private set; } = "Instale o Office antes de desinstalar.";

    public bool HasVisioSelected => AppToggleItems.FirstOrDefault(item => item.Id == "Visio")?.IsOn == true;
    public bool HasProjectSelected => AppToggleItems.FirstOrDefault(item => item.Id == "Project")?.IsOn == true;
    public bool HasAdditionalProductSelected => HasVisioSelected || HasProjectSelected;
    public bool IsChannelEnabled { get; private set; }
    public string? ChannelToolTip { get; private set; }
    public bool CanEditUpdatePreference => !_isApplyingAutoUpdates;
    public bool AutoUpdatesEnabled { get; private set; } = true;
    public int SelectedAppCount => AppToggleItems.Count(item => item.IsEnabled && item.IsOn);
    public string SelectionSummary => $"{SelectedAppCount} selecionado(s)";
    public string SelectAppsActionText => AppToggleItems.Where(item => item.IsEnabled).All(item => item.IsOn) ? "Desmarcar todos" : "Selecionar todos";

    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public IAsyncRelayCommand ApplyAutoUpdatesCommand { get; }
    public IAsyncRelayCommand ExportPreviewCommand { get; }
    public IRelayCommand AddToCatalogCommand { get; }
    public IRelayCommand PreviewConfigurationCommand { get; }
    public IRelayCommand CopyPreviewCommand { get; }
    public IRelayCommand ClosePreviewCommand { get; }
    public IRelayCommand OpenWorkFolderCommand { get; }
    public IRelayCommand RefreshInstalledCommand { get; }
    public IRelayCommand SelectAllAppsCommand { get; }
    public string WorkRoot => _officeService.WorkRoot;

    public OfficeViewModel(
        OfficeDeploymentToolService officeService,
        OfficeInstalledProductsDetector installedDetector,
        OperationsQueueService queue,
        CollectionsViewModel collections,
        IPresentationService presentation,
        IOfficeConfigurationExportService configurationExport,
        IClipboardService clipboard,
        IDesktopIntegrationService desktop)
    {
        _officeService = officeService;
        _installedDetector = installedDetector;
        _queue = queue;
        _collections = collections;
        _presentation = presentation;
        _configurationExport = configurationExport;
        _clipboard = clipboard;
        _desktop = desktop;

        InstallCommand = new AsyncRelayCommand(InstallOfficeAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveOfficeAsync);
        ApplyAutoUpdatesCommand = new AsyncRelayCommand(ApplyRequestedAutoUpdatesAsync);
        ExportPreviewCommand = new AsyncRelayCommand(ExportConfigurationAsync);
        AddToCatalogCommand = new RelayCommand(AddToCatalog);
        PreviewConfigurationCommand = new RelayCommand(ShowPreview);
        CopyPreviewCommand = new RelayCommand(CopyPreview);
        ClosePreviewCommand = new RelayCommand(() => IsPreviewOpen = false);
        OpenWorkFolderCommand = new RelayCommand(OpenWorkFolder);
        RefreshInstalledCommand = new RelayCommand(RefreshInstalledProducts);
        SelectAllAppsCommand = new RelayCommand(SelectAllApps);

        foreach (OfficeSelectorOption language in LanguageOptions)
            AdditionalLanguages.Add(new OfficeAppToggleViewModel(language.Id, language.Label, "", false));
        ExcludedComponents.Add(new OfficeAppToggleViewModel("Groove", "OneDrive (Groove)", "", false));
        ExcludedComponents.Add(new OfficeAppToggleViewModel("Lync", "Skype for Business", "", false));

        foreach (var (id, name, icon) in OfficeAppCatalog.CoreApps)
            AppToggleItems.Add(new OfficeAppToggleViewModel(id, name, icon, DefaultOnApps.Contains(id)));
        foreach (var (id, name, icon) in OfficeAppCatalog.AdditionalProducts)
            AppToggleItems.Add(new OfficeAppToggleViewModel(id, name, icon, false, true));

        foreach (OfficeAppToggleViewModel item in AppToggleItems)
            item.PropertyChanged += AppToggle_PropertyChanged;

        Categories.Add(new(OfficeEditionCategory.Personal, "Microsoft 365"));
        Categories.Add(new(OfficeEditionCategory.Corporate365, "Microsoft 365 Empresarial"));
        Categories.Add(new(OfficeEditionCategory.Ltsc, "Office LTSC"));
        Categories.Add(new(OfficeEditionCategory.VisioProject, "Visio / Project"));
        SelectedCategory = Categories[0];
    }

    partial void OnSelectedCategoryChanged(OfficeCategoryOption? value) => LoadPlansForCategory();
    partial void OnSelectedPlanChanged(OfficePlan? value) => ConfigureSelectedPlan();
    partial void OnSelectedChannelChanged(OfficeChannelOption? value) => RefreshAdditionalProductOptions();

    private void AppToggle_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(OfficeAppToggleViewModel.IsOn) or nameof(OfficeAppToggleViewModel.IsEnabled)))
            return;
        OnPropertyChanged(nameof(SelectedAppCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectAppsActionText));
        OnPropertyChanged(nameof(HasVisioSelected));
        OnPropertyChanged(nameof(HasProjectSelected));
        OnPropertyChanged(nameof(HasAdditionalProductSelected));
    }

    public void SelectAllApps()
    {
        bool select = !AppToggleItems.Where(item => item.IsEnabled).All(item => item.IsOn);
        foreach (OfficeAppToggleViewModel item in AppToggleItems)
            if (item.IsEnabled)
                item.IsOn = select;
    }

    public IReadOnlyList<OfficePlan> GetAdditionalProducts()
    {
        var products = new List<OfficePlan>(2);
        if (HasVisioSelected && SelectedVisioProduct is { } visio)
            products.Add(visio);
        if (HasProjectSelected && SelectedProjectProduct is { } project)
            products.Add(project);
        return products;
    }

    private void LoadPlansForCategory()
    {
        Plans.Clear();
        if (SelectedCategory is not { } category)
            return;
        foreach (OfficePlan plan in OfficePlanCatalog.ByCategory(category.Category))
            Plans.Add(plan);

        string? preferredId = category.Category switch
        {
            OfficeEditionCategory.Personal => "O365HomePremRetail",
            OfficeEditionCategory.Corporate365 => "O365ProPlusRetail",
            OfficeEditionCategory.Ltsc => "ProPlus2024Volume",
            _ => null
        };
        SelectedPlan = Plans.FirstOrDefault(plan => plan.ProductId.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
            ?? Plans.FirstOrDefault();
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
            .OrderBy(plan => subscriptionChannel && IsPreferredAdditionalPlan(plan) ? 0 : 1)
            .ThenBy(plan => plan.DisplayName, StringComparer.CurrentCultureIgnoreCase);
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

        OfficeAppToggleViewModel? visio = AppToggleItems.FirstOrDefault(item => item.Id == "Visio");
        OfficeAppToggleViewModel? project = AppToggleItems.FirstOrDefault(item => item.Id == "Project");
        if (visio is not null)
        {
            visio.IsEnabled = VisioProductOptions.Count > 0 && !IsVisioPlan(selectedPlan);
            if (!visio.IsEnabled) visio.IsOn = false;
        }
        if (project is not null)
        {
            project.IsEnabled = ProjectProductOptions.Count > 0 && !IsProjectPlan(selectedPlan);
            if (!project.IsEnabled) project.IsOn = false;
        }
    }

    private static bool IsVisioPlan(OfficePlan plan) => plan.ProductId.StartsWith("Visio", StringComparison.OrdinalIgnoreCase);
    private static bool IsProjectPlan(OfficePlan plan) => plan.ProductId.StartsWith("Project", StringComparison.OrdinalIgnoreCase);
    private static bool HasSameAdditionalProductType(OfficePlan left, OfficePlan right) =>
        (IsVisioPlan(left) && IsVisioPlan(right)) || (IsProjectPlan(left) && IsProjectPlan(right));
    private static bool IsPreferredAdditionalPlan(OfficePlan plan) =>
        plan.ProductId.Equals("VisioProRetail", StringComparison.OrdinalIgnoreCase) ||
        plan.ProductId.Equals("ProjectProRetail", StringComparison.OrdinalIgnoreCase);

    public OfficeInstallRequest? CreateInstallRequest()
    {
        if (SelectedPlan is not { } plan)
        {
            StatusText = "Selecione um plano antes de continuar.";
            return null;
        }

        var request = new OfficeInstallRequest(
            plan,
            Architecture == "32" ? 32 : 64,
            LanguageId,
            AppToggleItems.Where(item => !item.IsAdditionalProduct && !item.IsOn).Select(item => item.Id)
                .Concat(ExcludedComponents.Where(item => item.IsOn).Select(item => item.Id)).ToArray(),
            DisplayNone: DisplayMode != "Visible",
            AdditionalLanguageIds: AdditionalLanguages.Where(item => item.IsOn).Select(item => item.Id).ToArray(),
            DisplayLevel: DisplayMode == "Visible" ? OfficeDisplayLevel.Visible : OfficeDisplayLevel.Silent,
            ChannelOverride: plan.SupportsSelectableChannel ? SelectedChannel?.Id : null,
            AutoUpdatesEnabled: RequestedAutoUpdatesEnabled,
            AdditionalProducts: GetAdditionalProducts(), DownloadBeforeInstall: DownloadBeforeInstall);
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

    public string? BuildPreviewXml()
    {
        OfficeInstallRequest? request = CreateInstallRequest();
        return request is null ? null : OfficeConfigXmlBuilder.Build(request).ToString(SaveOptions.None);
    }

    public void ShowPreview()
    {
        try
        {
            string? xml = BuildPreviewXml();
            if (xml is null)
                return;
            PreviewXml = xml;
            IsPreviewOpen = true;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "OfficeViewModel.ShowPreview");
            StatusText = $"Não foi possível gerar o XML: {exception.Message}";
        }
    }

    private void CopyPreview()
    {
        StatusText = _clipboard.TrySetText(PreviewXml)
            ? "XML copiado para a área de transferência."
            : "Não foi possível copiar o XML. Selecione o conteúdo e use Ctrl+C.";
    }

    private async Task ExportConfigurationAsync()
    {
        if (string.IsNullOrWhiteSpace(PreviewXml))
        {
            StatusText = "Gere a prévia XML antes de exportar.";
            return;
        }
        try
        {
            string? path = await _configurationExport.ExportAsync(PreviewXml);
            if (path is null)
                return;
            StatusText = $"Configuração exportada para {path}.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "OfficeViewModel.ExportConfigurationAsync");
            StatusText = $"Não foi possível exportar o XML: {exception.Message}";
        }
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
        StatusText = $"Instalando {request.Plan.DisplayName}...";
        try
        {
            bool success = await OperationRunner.RunOfficeInstallAsync(_queue, _officeService, request);
            StatusText = success
                ? $"{request.Plan.DisplayName} instalado/reparado com sucesso."
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
            AdditionalProductIds = additional.Select(item => item.ProductId).ToList()
        };
        string appsSummary = string.Join(", ", AppToggleItems.Where(item => item.IsOn).Select(item => item.DisplayName));
        string additionalSummary = string.Join(", ", additional.Select(item => item.DisplayName));
        string[] ids = [plan.ProductId, .. additional.Select(item => item.ProductId)];
        var entry = new AppEntry
        {
            Id = $"office.{string.Join("+", ids)}".ToLowerInvariant(),
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
        bool confirmed = await _presentation.ConfirmAsync(
            "Remover o Office",
            "Isso removerá todas as instalações do Office deste computador. Continuar?",
            "Desinstalar");
        if (!confirmed)
            return;

        var request = new OfficeRemoveRequest(
            RemoveAll: true,
            DisplayLevel: OfficeDisplayLevel.Silent,
            CleanStoreEdition: CleanStoreEdition,
            UseRemoveMSI: true,
            UseAggressiveUninstall: true);
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

    public void RefreshInstalledProducts()
    {
        IReadOnlyList<OfficeInstalledProduct> installed = _installedDetector.GetInstalledProducts();
        OfficeInstalledProduct[] suiteProducts = installed.Where(product => !IsAdditionalProductId(product.ProductId)).ToArray();
        HasInstalledProducts = installed.Count > 0;
        UninstallToolTip = HasInstalledProducts ? "Remover todas as instalações do Office" : "Instale o Office antes de desinstalar.";
        InstalledApps.Clear();
        int installedCount = 0;

        foreach (var (id, name, icon) in OfficeAppCatalog.CoreApps)
        {
            OfficeAppStatusRow row = suiteProducts.Length == 0
                ? new(name, icon, "Não instalado", "None", "\uE711", GetOfficeIconScale(id))
                : suiteProducts.Any(product => product.ExcludedApps.Any(excluded => excluded.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    ? new(name, icon, "Excluído", "Excluded", "\uE7BA", GetOfficeIconScale(id))
                    : new(name, icon, "Instalado", "Installed", "\uE73E", GetOfficeIconScale(id));
            if (row.StatusKind == "Installed")
                installedCount++;
            InstalledApps.Add(row);
        }

        foreach (var (id, name, icon) in OfficeAppCatalog.AdditionalProducts)
        {
            bool isInstalled = installed.Any(product =>
                product.ProductId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                product.ProductId.StartsWith(id, StringComparison.OrdinalIgnoreCase));
            InstalledApps.Add(new(name, icon, isInstalled ? "Instalado" : "Não instalado",
                isInstalled ? "Installed" : "None", isInstalled ? "\uE73E" : "\uE711", GetOfficeIconScale(id)));
            if (isInstalled)
                installedCount++;
        }
        InstalledSummary = $"{installedCount} de {InstalledApps.Count} instalados";
        HasNoInstalledProducts = installed.Count == 0;
    }

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

    private static double GetOfficeIconScale(string id) => id is "Publisher" or "Visio" or "Project" ? 1.8 : 1.0;
    private static bool IsAdditionalProductId(string id) =>
        id.StartsWith("Visio", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Project", StringComparison.OrdinalIgnoreCase);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OfficeAppToggleViewModel : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }
    public string CardDisplayName => Id == "PowerPoint" ? "Power\nPoint" : DisplayName;
    public string IconUrl { get; }
    public double IconScale => Id is "Publisher" or "Visio" or "Project" ? 1.8 : 1.0;
    public bool IsAdditionalProduct { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public OfficeAppToggleViewModel(string id, string displayName, string iconUrl, bool isOnByDefault, bool isAdditionalProduct = false)
    {
        Id = id;
        DisplayName = displayName;
        IconUrl = iconUrl;
        IsAdditionalProduct = isAdditionalProduct;
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
public sealed partial record OfficeAppStatusRow(string DisplayName, string IconUrl, string StatusText, string StatusKind, string StatusGlyph, double IconScale);
