using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Runtime.CompilerServices;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AppDetailsViewModel : ObservableObject, IDisposable
{
    private readonly CollectionsViewModel _collectionService;
    private readonly IPackageBackend _installedPackagesViewModel;
    private readonly PackageInstallOptionsService _packageInstallOptionsService;
    private readonly IPresentationService _interaction;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;
    private readonly IPackageDetailsProvider _metadata;
    private readonly IconService _icons;
    private AppEntry? _app;
    private string? _availableUpdateVersion;
    private string? _installLocation;
    private bool _forcePerUserInstall;
    private bool _updatingScope;
    private int _scopeLoadGeneration;
    public bool CanInstallForUser { get; private set; }
    public bool CanInstallForMachine { get; private set; }
    public AppEntry? App => _app;
    public string? IconUrl => _app?.IconUrl;
    public string[] Screenshots { get; private set; } = [];
    public bool IsOfficeStoreOffer => _app is not null && IsMicrosoftStoreSource(_app.Source)
        && _app.Tags.Contains("Office", StringComparer.OrdinalIgnoreCase);
    public bool CanAddCurrentAppToPackages => _app is not null && IsSupportedCollectionSource(_app.Source);
    public bool AlreadyAdded => _app is not null && _collectionService.Items?.Any(a => string.Equals(a.Id, _app.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(a.App.Source, _app.Source, StringComparison.OrdinalIgnoreCase)) == true;
    public async Task<AppEntry> LoadCatalogDetailsAsync(AppEntry app, CancellationToken cancellationToken)
    {
        try { return await _metadata.LoadCatalogAsync(app, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { WinProvisionLog.Write($"Catalog details failed: {ex}"); return app; }
    }
    [ObservableProperty] public partial string AppNameText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PackageSourceText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PackageManagerText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PackageVersionText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DescriptionText { get; set; } = string.Empty;
    private IEnumerable<string> _tags = [];
    public IEnumerable<string> Tags { get => _tags; set => SetProperty(ref _tags, value); }
    [ObservableProperty] public partial bool IsTagsListVisible { get; set; } = false;
    [ObservableProperty] public partial string PackageIdText { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeveloperText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AuthorText { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastUpdatedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string InstallerTypeText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsInstallerUrlLinkVisible { get; set; } = false;
    [ObservableProperty] public partial string? InstallerUrlLinkTag { get; set; } = null;
    [ObservableProperty] public partial string Sha256Text { get; set; } = string.Empty;
    [ObservableProperty] public partial string DependenciesText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReleaseNotesText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsUpdateVersionPanelVisible { get; set; } = false;
    [ObservableProperty] public partial string CurrentUpdateVersionText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AvailableUpdateVersionText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SelectedLocationText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool InstallAllUsersCheckBoxIsChecked { get; set; } = false;
    [ObservableProperty] public partial bool InteractiveInstallCheckBoxIsChecked { get; set; } = false;
    [ObservableProperty] public partial bool IgnoreSecurityHashCheckBoxIsChecked { get; set; } = false;
    [ObservableProperty] public partial bool IsWinGetVersionOptionsVisible { get; set; } = false;
    [ObservableProperty] public partial bool IsLoadingVersions { get; set; } = false;
    [ObservableProperty] public partial bool CanLoadVersions { get; set; } = true;
    [ObservableProperty] public partial string SelectedInstallVersion { get; set; } = "Mais recente";
    private IEnumerable<string> _installVersions = (string[])["Mais recente"];
    public IEnumerable<string> InstallVersions { get => _installVersions; set => SetProperty(ref _installVersions, value); }
    [ObservableProperty] public partial bool InstallAllUsersCheckBoxIsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool InteractiveInstallCheckBoxIsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool IsInstallArchitecturePanelVisible { get; set; } = false;
    [ObservableProperty] public partial string ManifestLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ManifestLinkTag { get; set; } = null;
    [ObservableProperty] public partial bool IsManifestLinkVisible { get; set; } = false;
    [ObservableProperty] public partial bool IsManifestNotFoundVisible { get; set; } = false;
    [ObservableProperty] public partial bool IsHomepageLinkVisible { get; set; } = false;
    [ObservableProperty] public partial bool IsHomepageNotAvailableTextVisible { get; set; } = false;
    [ObservableProperty] public partial string HomepageLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? HomepageLinkTag { get; set; } = null;
    [ObservableProperty] public partial string LicenseText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLicenseLinkVisible { get; set; } = false;
    [ObservableProperty] public partial string LicenseLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? LicenseLinkTag { get; set; } = null;
    [ObservableProperty] public partial string InstallerSizeText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsReleaseNotesUrlLinkVisible { get; set; } = false;
    [ObservableProperty] public partial bool IsReleaseNotesUrlNotAvailableTextVisible { get; set; } = false;
    [ObservableProperty] public partial string ReleaseNotesUrlLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ReleaseNotesUrlLinkTag { get; set; } = null;
    [ObservableProperty] public partial bool IsLoadingProgressBarVisible { get; set; } = false;
    [ObservableProperty] public partial string InstallerUrlLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? DownloadInstallerLinkTag { get; set; } = null;
    [ObservableProperty] public partial bool IsDownloadInstallerLinkVisible { get; set; } = false;
    [ObservableProperty] public partial bool PersistPackageOptionsCheckBoxIsChecked { get; set; } = false;
    [ObservableProperty] public partial bool IsOptionsBodyPanelVisible { get; set; } = false;
    [ObservableProperty] public partial bool PersistPackageOptionsCheckBoxIsEnabled { get; set; } = true;
    [ObservableProperty] public partial string PersistPackageOptionsHintText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool InstallButtonIsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool InstallChevronButtonIsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool UpdateButtonIsEnabled { get; set; } = true;
    [ObservableProperty] public partial string InstallCommandPreviewTextBox { get; set; } = string.Empty;
    [ObservableProperty] public partial bool CopyInstallCommandButtonIsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool UninstallButtonIsEnabled { get; set; } = true;
    private IEnumerable<string> _architectures = [];
    public IEnumerable<string> Architectures { get => _architectures; set => SetProperty(ref _architectures, value); }
    [ObservableProperty] public partial string SelectedArchitecture { get; set; } = "Automática";
    [ObservableProperty] public partial bool IsUpdateButtonVisible { get; set; }
    [ObservableProperty] public partial bool IsInstallSplitGroupVisible { get; set; }
    [ObservableProperty] public partial bool IsUninstallSplitGroupVisible { get; set; }
    [ObservableProperty] public partial bool IsSelfPackage { get; set; }
    [ObservableProperty] public partial string SelfInstalledVersionText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ShowManagementOptions { get; set; } = true;
    public AppDetailsViewModel(CollectionsViewModel collections, IPackageBackend backend, PackageInstallOptionsService options, IPresentationService presentation, IUiDispatcher dispatcher, IClipboardService clipboard, IPackageDetailsProvider metadata, IconService icons)
    {
        _collectionService = collections; _installedPackagesViewModel = backend;
        _packageInstallOptionsService = options; _interaction = presentation;
        _dispatcher = dispatcher; _clipboard = clipboard; _metadata = metadata; _icons = icons;
        InstallCommand = new AsyncRelayCommand(InstallAsync);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync);
        UninstallCommand = new AsyncRelayCommand(UninstallAsync);
        SaveOptionsCommand = new RelayCommand(SaveOptions);
        OpenAppUpdatesCommand = new RelayCommand(_interaction.ShowAppUpdates);
    }
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand UpdateCommand { get; }
    public IAsyncRelayCommand UninstallCommand { get; }
    public IRelayCommand SaveOptionsCommand { get; }
    public IRelayCommand OpenAppUpdatesCommand { get; }
    public void SetApp(AppEntry app, string? currentVersion, string? availableVersion, bool preserveOptions = false)
    {
        if (_app is not null) _app.PropertyChanged -= App_PropertyChanged;
        _app = app;
        _scopeLoadGeneration++;
        IsSelfPackage = app.IsSelf;
        SelfInstalledVersionText = app.IsSelf ? $"Instalado — versão {AppUpdateService.GetCurrentVersion().ToString(3)}" : string.Empty;
        if (app.IsSelf) app.IsInstalled = true;
        ShowManagementOptions = !app.IsSelf;
        OnPropertyChanged(nameof(App));
        OnPropertyChanged(nameof(IconUrl));
        _availableUpdateVersion = availableVersion;

        InstallButtonIsEnabled = InstallChevronButtonIsEnabled = UpdateButtonIsEnabled = UninstallButtonIsEnabled = true;

        // ── 1. Inicialização síncrona com os dados já disponíveis no AppEntry ──

        AppNameText = app.Name;

        string sourceLabel = app.CatalogSourceLabel;
        PackageSourceText = sourceLabel;
        PackageManagerText = sourceLabel;

        string displayedVersion = availableVersion ?? app.Version;
        PackageVersionText = string.IsNullOrWhiteSpace(displayedVersion) ? "Não informada" : displayedVersion;

        DescriptionText = string.IsNullOrWhiteSpace(app.Description)
            ? "Nenhuma descrição disponível."
            : app.Description;

        Screenshots = _icons.ResolveScreenshotUrls(app)
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        // Tags
        Tags = app.Tags;
        IsTagsListVisible = app.Tags.Count > 0;

        // ID do Pacote
        PackageIdText = app.Id;

        // Manifesto URL
        SetupManifestUrl(app.Id, app.Source, app.WingetManifestAvailable
            ?? (string.Equals(app.CatalogOrigin, "curated", StringComparison.OrdinalIgnoreCase) ? false : null));

        // Página Inicial
        SetupHomepage(app.Homepage);

        // Desenvolvedor e Autor
        DeveloperText = !string.IsNullOrWhiteSpace(app.Publisher) ? app.Publisher : "—";
        AuthorText = app.Author ?? DeveloperText;

        // Licença
        SetupLicense(app.License, app.LicenseUrl);

        // Última atualização
        bool isOfficeStoreOffer = IsOfficeStoreOffer;
        IsLoadingProgressBarVisible = !isOfficeStoreOffer;
        LastUpdatedText = isOfficeStoreOffer ? "Microsoft Store" : "Carregando...";

        // Tipo de instalador e detalhes
        InstallerTypeText = isOfficeStoreOffer ? "Produto Microsoft 365" : "Carregando...";
        IsInstallerUrlLinkVisible = false;
        InstallerUrlLinkTag = null;
        Sha256Text = isOfficeStoreOffer ? "Não aplicável" : "Carregando...";

        // Tamanho do instalador
        SetupSize(app.InstallerSizeBytes);

        // Dependências e Notas
        DependenciesText = "Nenhuma dependência especificada";
        ReleaseNotesText = "Não disponível";
        SetupReleaseNotesUrl(app.ReleaseNotesUrl);
        if (!isOfficeStoreOffer) ApplyCatalogMetadata(app);

        // Update comparison panel
        IsUpdateVersionPanelVisible = currentVersion is not null && availableVersion is not null
            ;
        CurrentUpdateVersionText = currentVersion ?? string.Empty;
        AvailableUpdateVersionText = availableVersion ?? string.Empty;

        // Status & Opções
        if (!preserveOptions)
        {
            StatusText = string.Empty;
            _installLocation = null;
            _forcePerUserInstall = false;
            SelectedLocationText = "Local padrão do sistema";
            InstallAllUsersCheckBoxIsChecked = false;
            InteractiveInstallCheckBoxIsChecked = false;
            IgnoreSecurityHashCheckBoxIsChecked = false;
            InstallVersions = (string[])["Mais recente"];
            SelectedInstallVersion = "Mais recente";
            IsLoadingVersions = false;
            CanLoadVersions = true;
        }
        bool supportsWinGetOptions = !IsMicrosoftStoreSource(app.Source);
        IsWinGetVersionOptionsVisible = string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase);
        InstallAllUsersCheckBoxIsEnabled = false;
        CanInstallForUser = CanInstallForMachine = false;
        InteractiveInstallCheckBoxIsEnabled = supportsWinGetOptions;
        var supportedArchitectures = (app.Architectures ?? [])
            .Where(value => value is not null)
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "x86" or "x64" or "arm" or "arm64")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string previousArchitecture = SelectedArchitecture;
        Architectures = new[] { "Automática" }.Concat(supportedArchitectures).ToArray();
        if (preserveOptions)
            SelectedArchitecture = Architectures.Contains(previousArchitecture, StringComparer.OrdinalIgnoreCase)
                ? previousArchitecture : "Automática";
        if (!preserveOptions)
        {
            SelectedArchitecture = "Automática";
            LoadSavedPackageOptions(app);
        }
        IsInstallArchitecturePanelVisible = supportsWinGetOptions && supportedArchitectures.Length > 1
            ;

        _app.PropertyChanged += App_PropertyChanged;
        UpdateInstallActionsVisibility();
        UpdateInstallCommandPreview();
        if (isOfficeStoreOffer)
        {
            DependenciesText = "Gerenciado pela Microsoft Store";
            ReleaseNotesText = "Consulte os detalhes do produto na Microsoft Store.";
        }

    }
    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppEntry.IsInstalled)) _dispatcher.Post(UpdateInstallActionsVisibility);
    }
    public void Clear() { if (_app is not null) _app.PropertyChanged -= App_PropertyChanged; _app = null; OnPropertyChanged(nameof(App)); OnPropertyChanged(nameof(IconUrl)); }
    public void SetLocation(string location) { _installLocation = location; SelectedLocationText = location; UpdateInstallCommandPreview(); }
    public async Task LoadInstallVersionsAsync(CancellationToken token)
    {
        if (_app is not { } app || !IsWinGetVersionOptionsVisible || IsLoadingVersions) return;
        IsLoadingVersions = true;
        CanLoadVersions = false;
        try
        {
            string[] versions = await _metadata.LoadVersionsAsync(app, token);
            if (_app != app || token.IsCancellationRequested) return;
            InstallVersions = new[] { "Mais recente" }.Concat(versions).ToArray();
            if (!InstallVersions.Contains(SelectedInstallVersion, StringComparer.OrdinalIgnoreCase))
                SelectedInstallVersion = "Mais recente";
            StatusText = versions.Length == 0 ? "O catálogo não informou versões disponíveis." : string.Empty;
            await LoadSelectedVersionScopeAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (_app == app) StatusText = $"Não foi possível consultar versões: {ex.Message}"; }
        finally { if (_app == app) { IsLoadingVersions = false; CanLoadVersions = true; } }
    }
    public async Task LoadSelectedVersionScopeAsync(CancellationToken token)
    {
        if (_app is not { } app || !IsWinGetVersionOptionsVisible) return;
        string selected = SelectedInstallVersion;
        string architecture = SelectedArchitecture;
        int generation = ++_scopeLoadGeneration;
        bool requestedMachine = InstallAllUsersCheckBoxIsChecked;
        bool requestedUser = _forcePerUserInstall;
        InstallButtonIsEnabled = InstallChevronButtonIsEnabled = false;
        InstallAllUsersCheckBoxIsEnabled = false;
        CanInstallForUser = CanInstallForMachine = false;
        try
        {
            var installers = await _metadata.LoadVersionInstallersAsync(app,
                selected == "Mais recente" ? null : selected, token);
            if (_app != app || generation != _scopeLoadGeneration
                || SelectedInstallVersion != selected || SelectedArchitecture != architecture
                || token.IsCancellationRequested) return;
            if (architecture != "Automática")
                installers = installers.Where(item => item.Architecture is null or "neutral"
                    || string.Equals(item.Architecture, architecture, StringComparison.OrdinalIgnoreCase)).ToArray();
            bool user = installers.Any(item => string.Equals(item.Scope, "user", StringComparison.OrdinalIgnoreCase));
            bool machine = installers.Any(item => string.Equals(item.Scope, "machine", StringComparison.OrdinalIgnoreCase));
            CanInstallForUser = user;
            CanInstallForMachine = machine;
            _updatingScope = true;
            try
            {
                if (machine && !user) { InstallAllUsersCheckBoxIsChecked = true; _forcePerUserInstall = false; }
                else if (user && !machine) { InstallAllUsersCheckBoxIsChecked = false; _forcePerUserInstall = true; }
                else if (!user && !machine) { InstallAllUsersCheckBoxIsChecked = false; _forcePerUserInstall = false; }
                else { InstallAllUsersCheckBoxIsChecked = requestedMachine; _forcePerUserInstall = requestedUser; }
                InstallAllUsersCheckBoxIsEnabled = user && machine;
            }
            finally { _updatingScope = false; }
            UpdateInstallCommandPreview();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (_app == app) StatusText = $"Não foi possível consultar escopos: {ex.Message}"; }
        finally
        {
            if (_app == app && generation == _scopeLoadGeneration)
                InstallButtonIsEnabled = InstallChevronButtonIsEnabled = true;
        }
    }
    public void SetUserScope(bool allUsers)
    {
        if (_updatingScope || allUsers && !CanInstallForMachine || !allUsers && !CanInstallForUser) return;
        InstallAllUsersCheckBoxIsChecked = allUsers;
        _forcePerUserInstall = !allUsers;
        UpdateInstallCommandPreview();
    }
    public void CopyInstallCommand()
    { StatusText = _clipboard.TrySetText(InstallCommandPreviewTextBox) ? "Comando WinGet copiado para a área de transferência." : "Não foi possível copiar o comando WinGet."; }
    private void UpdateInstallActionsVisibility()
    {
        if (_app is null) return;

        bool isUpdateContext = !string.IsNullOrWhiteSpace(_availableUpdateVersion);
        IsUpdateButtonVisible = !_app.IsSelf && isUpdateContext;
        IsInstallSplitGroupVisible = !_app.IsSelf && !(isUpdateContext || _app.IsInstalled);
        IsUninstallSplitGroupVisible = !_app.IsSelf && !(isUpdateContext || !_app.IsInstalled);

    }
    private void SetupManifestUrl(string id, string source, bool? available)
    {
        IsManifestNotFoundVisible = available == false;
        if (available == false)
        {
            ManifestLinkText = "Não encontrado";
            ManifestLinkTag = null;
            IsManifestLinkVisible = false;
            return;
        }
        if (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase))
        {
            string url = $"https://apps.microsoft.com/detail/{id}";
            ManifestLinkText = url;
            ManifestLinkTag = url;
            IsManifestLinkVisible = true;
            return;
        }

        // WinGet manifest no GitHub
        string firstChar = id.Length > 0 ? char.ToLowerInvariant(id[0]).ToString() : "a";
        string parts = id.Replace('.', '/');
        string manifestUrl = $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{firstChar}/{parts}";
        ManifestLinkText = manifestUrl;
        ManifestLinkTag = manifestUrl;
        IsManifestLinkVisible = true;
    }
    private void SetupHomepage(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        IsHomepageLinkVisible = hasUrl;
        IsHomepageNotAvailableTextVisible = !(hasUrl);

        if (hasUrl)
        {
            HomepageLinkText = url ?? string.Empty;
            HomepageLinkTag = url;
        }
    }
    private void SetupLicense(string? name, string? url)
    {
        bool hasName = !string.IsNullOrWhiteSpace(name);
        bool hasUrl = !string.IsNullOrWhiteSpace(url);

        LicenseText = hasName ? name! : (hasUrl ? string.Empty : "Não informada");
        IsLicenseLinkVisible = hasUrl;

        if (hasUrl)
        {
            LicenseLinkText = url ?? string.Empty;
            LicenseLinkTag = url;
        }
    }
    private void SetupSize(long? sizeBytes)
    {
        if (sizeBytes is > 0)
        {
            double mb = sizeBytes.Value / 1024d / 1024d;
            InstallerSizeText = mb >= 1024
                ? $"(~ {mb / 1024:0.0} GB)"
                : $"(~ {mb:0} MB)";
        }
        else
        {
            InstallerSizeText = "(Tamanho desconhecido)";
        }
    }
    private void SetupReleaseNotesUrl(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        IsReleaseNotesUrlLinkVisible = hasUrl;
        IsReleaseNotesUrlNotAvailableTextVisible = !(hasUrl);

        if (hasUrl)
        {
            ReleaseNotesUrlLinkText = url ?? string.Empty;
            ReleaseNotesUrlLinkTag = url;
        }
    }
    public async Task LoadExtendedDetailsAsync(AppEntry app, CancellationToken cancellationToken)
    {
        if (string.Equals(app.CatalogOrigin, "curated", StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCatalogMetadata(app);
            IsLoadingProgressBarVisible = false;
            return;
        }
        IsLoadingProgressBarVisible = true;

        try
        {
            var info = await _metadata.LoadExtendedAsync(app, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _app != app) return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (_app != app) return;

                if (!string.IsNullOrWhiteSpace(info.Version))
                    PackageVersionText = info.Version;

                if (!string.IsNullOrWhiteSpace(info.Publisher))
                    DeveloperText = info.Publisher;

                if (!string.IsNullOrWhiteSpace(info.Author))
                    AuthorText = info.Author;

                if (!string.IsNullOrWhiteSpace(info.Homepage))
                    SetupHomepage(info.Homepage);

                if (!string.IsNullOrWhiteSpace(info.License) || !string.IsNullOrWhiteSpace(info.LicenseUrl))
                    SetupLicense(info.License ?? app.License, info.LicenseUrl ?? app.LicenseUrl);

                LastUpdatedText = !string.IsNullOrWhiteSpace(info.ReleaseDate)
                    ? info.ReleaseDate
                    : "Não informada";

                InstallerTypeText = !string.IsNullOrWhiteSpace(info.InstallerType)
                    ? info.InstallerType
                    : "exe";

                if (!string.IsNullOrWhiteSpace(info.InstallerUrl))
                {
                    IsInstallerUrlLinkVisible = true;
                    InstallerUrlLinkText = info.InstallerUrl;
                    InstallerUrlLinkTag = info.InstallerUrl;
                    DownloadInstallerLinkTag = info.InstallerUrl;
                    IsDownloadInstallerLinkVisible = true;
                }
                else
                {
                    IsInstallerUrlLinkVisible = false;
                    IsDownloadInstallerLinkVisible = false;
                }

                Sha256Text = !string.IsNullOrWhiteSpace(info.Sha256)
                    ? info.Sha256.ToUpperInvariant()
                    : "Não especificado";

                if (!string.IsNullOrWhiteSpace(info.Dependencies))
                    DependenciesText = info.Dependencies;

                if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
                    ReleaseNotesText = info.ReleaseNotes;

                if (!string.IsNullOrWhiteSpace(info.ReleaseNotesUrl))
                    SetupReleaseNotesUrl(info.ReleaseNotesUrl);

                if (info.Tags.Count > 0)
                {
                    Tags = info.Tags;
                    IsTagsListVisible = true;
                }

                if (!string.IsNullOrWhiteSpace(info.Description) && string.IsNullOrWhiteSpace(app.Description))
                {
                    DescriptionText = info.Description;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelado normalmente ao mudar de pacote
        }
        catch (Exception ex)
        {
            WinProvisionLog.Write($"Package metadata failed: {ex}");
            // Fallback gracioso mantendo os metadados existentes
            await _dispatcher.InvokeAsync(() =>
            {
                if (_app == app && !cancellationToken.IsCancellationRequested)
                {
                    ApplyCatalogMetadata(app);
                }
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_app == app && !cancellationToken.IsCancellationRequested) IsLoadingProgressBarVisible = false;
            });
        }
    }
    private void ApplyCatalogMetadata(AppEntry app)
    {
        var date = app.ReleaseDate ?? app.CatalogSyncedUtc;
        LastUpdatedText = date?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "Não disponível";
        string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        var installer = app.Installers.FirstOrDefault(item =>
            string.Equals(item.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            ?? app.Installers.FirstOrDefault();
        InstallerTypeText = installer?.Type?.ToLowerInvariant() switch
        {
            "inno" => "Inno Setup",
            "nullsoft" => "Nullsoft (NSIS)",
            "msi" or "wix" => "Windows Installer (MSI)",
            null or "" => "Não informado",
            _ => installer!.Type!
        };
        InstallerUrlLinkText = installer?.Url ?? string.Empty;
        InstallerUrlLinkTag = DownloadInstallerLinkTag = installer?.Url;
        IsInstallerUrlLinkVisible = IsDownloadInstallerLinkVisible =
            Uri.TryCreate(installer?.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
        Sha256Text = string.IsNullOrWhiteSpace(installer?.Sha256) ? "Não disponível" : installer.Sha256.ToUpperInvariant();
        ReleaseNotesText = string.IsNullOrWhiteSpace(app.ReleaseNotes) ? "Não disponível" : app.ReleaseNotes;
    }
    public void SaveOptions()
    {
        try
        {
            if (_app is not null && IsSupportedCollectionSource(_app.Source) && !IsMicrosoftStoreSource(_app.Source))
            {
                if (PersistPackageOptionsCheckBoxIsChecked == true)
                {
                    string? architecture = SelectedArchitecture as string;
                    _packageInstallOptionsService.Save(_app.Id, _app.Source, new PackageInstallOptions(
                        InteractiveInstallCheckBoxIsChecked == true,
                        InstallAllUsersCheckBoxIsChecked == true ? "machine" : _forcePerUserInstall ? "user" : null,
                        architecture == "Automática" ? null : architecture,
                        false,
                        _installLocation));
                    StatusText = "Opções salvas para este pacote e futuras instalações e atualizações.";
                }
                else
                {
                    _packageInstallOptionsService.Remove(_app.Id, _app.Source);
                    StatusText = "Opções mantidas apenas para esta instalação.";
                }
            }
            else
            {
                StatusText = IsMicrosoftStoreSource(_app?.Source)
                    ? "As opções da Microsoft Store são definidas pelo próprio WinGet."
                    : "As opções reutilizáveis estão disponíveis apenas para pacotes WinGet.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Não foi possível salvar as opções deste pacote: {ex.Message}";
        }

    }
    private void LoadSavedPackageOptions(AppEntry app)
    {
        var saved = _packageInstallOptionsService.Get(app.Id, app.Source);
        bool supported = IsSupportedCollectionSource(app.Source) && !IsMicrosoftStoreSource(app.Source);
        PersistPackageOptionsCheckBoxIsEnabled = supported;
        PersistPackageOptionsCheckBoxIsChecked = saved is not null;
        PersistPackageOptionsHintText = supported
            ? "Escopo, arquitetura e interação podem ser reutilizados em atualizações. A pasta vale para novas instalações; versão e hash valem só para a operação atual."
            : IsMicrosoftStoreSource(app.Source)
                ? "Pacotes da Microsoft Store usam as opções definidas pelo próprio WinGet."
                : "Opções reutilizáveis estão disponíveis apenas para pacotes WinGet.";

        InstallAllUsersCheckBoxIsChecked = saved?.Scope == "machine";
        InteractiveInstallCheckBoxIsChecked = saved?.Interactive == true;
        if (saved?.Scope == "user")
        {
            InstallAllUsersCheckBoxIsChecked = false;
            _forcePerUserInstall = true;
        }
        else
        {
            _forcePerUserInstall = false;
        }

        if (saved?.Architecture is { } architecture
            && Architectures.Contains(architecture))
            SelectedArchitecture = architecture;
        else
            SelectedArchitecture = "Automática";

        _installLocation = saved?.InstallLocation;
        SelectedLocationText = string.IsNullOrWhiteSpace(_installLocation)
            ? "Local padrão do sistema"
            : _installLocation;
        UpdateInstallCommandPreview();
    }
    private static bool IsMicrosoftStoreSource(string? source) =>
        string.Equals(source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "Microsoft Store", StringComparison.OrdinalIgnoreCase);
    private static bool IsSupportedCollectionSource(string? source) =>
        string.Equals(source?.Trim(), "winget", StringComparison.OrdinalIgnoreCase)
        || source?.Trim().StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase) == true
        || string.Equals(source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "Microsoft Store", StringComparison.OrdinalIgnoreCase);
    public void AddCurrentAppToPackages()
    {
        if (_app is null) return;
        if (!CanAddCurrentAppToPackages)
        {
            StatusText = "Somente pacotes WinGet e Microsoft Store podem ser adicionados à coleção.";
            return;
        }

        int count = _collectionService.Items?.Count ?? 0;
        _collectionService.Add(_app);
        int added = (_collectionService.Items?.Count ?? 0) - count;
        string tabTitle = _collectionService.SelectedProfile?.Title ?? "Perfil Padrão";
        StatusText = added > 0
            ? $"{_app.Name} adicionado à coleção '{tabTitle}'."
            : $"{_app.Name} já está na coleção '{tabTitle}'.";
    }
    public async Task InstallAsync()
    {
        if (_app is null || _app.IsSelf) return;

        var app = _app;
        InstallButtonIsEnabled = false;
        InstallChevronButtonIsEnabled = false;
        StatusText = $"{app.Name} entrou na fila de instalação.";

        try
        {
            var result = await _installedPackagesViewModel.InstallAsync(app, CurrentOptions());

            if (result.Success)
            {
                app.IsInstalled = true;
                if (_app == app)
                    StatusText = $"{app.Name} instalado.";
            }
            else if (_app == app)
            {
                StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", app.Name);
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "AppDetailsViewModel.InstallAsync");

            if (_app == app)
                StatusText = $"Não foi possível instalar {app.Name}. Tente novamente.";
        }
        finally
        {
            if (_app == app) { InstallButtonIsEnabled = true; InstallChevronButtonIsEnabled = true; }
        }
    }
    public async Task UpdateAsync()
    {
        if (_app is null || _app.IsSelf) return;

        var app = _app;
        UpdateButtonIsEnabled = false;
        StatusText = $"{app.Name} entrou na fila de atualização.";

        try
        {
            var result = await _installedPackagesViewModel.UpdateAsync(new UpgradablePackage { Id = app.Id, Name = app.Name, IconUrl = app.IconUrl, Source = app.Source }, CurrentOptions());

            if (_app == app)
            {
                StatusText = result.Success
                    ? $"{app.Name} atualizado."
                    : WingetErrorTranslator.ToMessage(result.FailureReason, "atualizar", app.Name);
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "AppDetailsViewModel.UpdateAsync");

            if (_app == app)
                StatusText = $"Não foi possível atualizar {app.Name}. Tente novamente.";
        }
        finally
        {
            if (_app == app) UpdateButtonIsEnabled = true;
        }
    }
    public void UpdateInstallCommandPreview()
    {
        if (_app is null)
        {
            InstallCommandPreviewTextBox = string.Empty;
            CopyInstallCommandButtonIsEnabled = false;
            return;
        }

        string source = IsMicrosoftStoreSource(_app.Source) ? "msstore" : "winget";
        string? architecture = SelectedArchitecture as string;
        var args = new List<string>
        {
            IsUpdateButtonVisible ? "winget upgrade --id" : "winget install --id",
            QuotePowerShellArgument(_app.Id),
            "--exact --source",
            QuotePowerShellArgument(source)
        };

        bool interactive = source == "msstore" || InteractiveInstallCheckBoxIsChecked == true;
        args.Add(interactive ? "--interactive" : "--silent --disable-interactivity");
        if (source == "winget")
        {
            if (InstallAllUsersCheckBoxIsChecked == true)
                args.Add("--scope machine");
            else if (_forcePerUserInstall)
                args.Add("--scope user");
        }
        if (!string.IsNullOrWhiteSpace(architecture) && architecture != "Automática")
            args.Add($"--architecture {architecture}");
        if (!string.IsNullOrWhiteSpace(_installLocation))
        {
            args.Add("--location");
            args.Add(QuotePowerShellArgument(_installLocation));
        }
        if (IsWinGetVersionOptionsVisible && !string.IsNullOrWhiteSpace(SelectedInstallVersion)
            && SelectedInstallVersion != "Mais recente")
        {
            args.Add("--version");
            args.Add(QuotePowerShellArgument(SelectedInstallVersion));
        }
        if (IsWinGetVersionOptionsVisible && IgnoreSecurityHashCheckBoxIsChecked)
            args.Add("--ignore-security-hash");
        args.Add("--accept-source-agreements --accept-package-agreements");

        InstallCommandPreviewTextBox = string.Join(" ", args);
        CopyInstallCommandButtonIsEnabled = true;
    }
    private static string QuotePowerShellArgument(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    public async Task UninstallAsync()
    {
        if (_app is null || _app.IsSelf) return;
        var app = _app;
        UninstallButtonIsEnabled = false;
        StatusText = $"Aguardando confirmação para desinstalar {app.Name}…";

        try
        {
            var confirmResult = await _interaction.ConfirmAsync(
                "Desinstalar aplicativo",
                InstalledPackagesService.IsOffice(app.Id, app.Name)
                    ? "Todas as versões e edições do Microsoft Office serão removidas do computador."
                    : $"Desinstalar {app.Name}?", "Desinstalar", "Cancelar");
            if (!confirmResult || _app != app)
                return;

            StatusText = $"{app.Name} entrou na fila de desinstalação.";
            bool removed = (await _installedPackagesViewModel.UninstallAsync(app)).Success;
            if (removed)
            {
                app.IsInstalled = false;
                if (_app == app)
                    StatusText = $"{app.Name} removido.";
            }
            else if (_app == app)
            {
                StatusText = $"Não foi possível desinstalar {app.Name}.";
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "AppDetailsViewModel.UninstallAsync");

            if (_app == app)
                StatusText = $"Não foi possível iniciar a desinstalação de {app.Name}: {recoveryException.Message}";
        }
        finally
        {
            if (_app == app) UninstallButtonIsEnabled = true;
        }
    }
    public PackageInstallOptions CurrentOptions() => new(
        InteractiveInstallCheckBoxIsChecked || IsMicrosoftStoreSource(_app?.Source),
        InstallAllUsersCheckBoxIsChecked ? "machine" : _forcePerUserInstall ? "user" : null,
        SelectedArchitecture == "Automática" ? null : SelectedArchitecture,
        IsWinGetVersionOptionsVisible && IgnoreSecurityHashCheckBoxIsChecked,
        _installLocation,
        Version: IsWinGetVersionOptionsVisible && !string.IsNullOrWhiteSpace(SelectedInstallVersion)
            && SelectedInstallVersion != "Mais recente" ? SelectedInstallVersion : null);
    public void Dispose() => Clear();

}
