using System.ComponentModel;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, string[]> VersionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly CollectionsViewModel _collectionService;
    private readonly IPackageBackend _installedPackagesViewModel;
    private readonly PackageInstallOptionsService _packageInstallOptionsService;
    private readonly IPresentationService _interaction;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;
    private readonly IPackageDetailsProvider _metadata;
    private readonly IconService _icons;
    private readonly ApplicationPreferencesService _applicationPreferences;
    private readonly PackageVisualPreferencesService _visuals;
    private readonly IUpdatesIgnoreStore _ignoredUpdates;
    private AppEntry? _app;
    private string? _availableUpdateVersion;
    private string? _currentUpdateVersion;
    private string? _installLocation;
    private bool _forcePerUserInstall;
    private bool _updatingScope;
    private int _scopeLoadGeneration;
    [ObservableProperty] public partial PackageDetailsRecord? PackageDetails { get; set; }
    [ObservableProperty] public partial PackageManagerDescriptor PackageManager { get; set; } = new();
    [ObservableProperty] public partial bool IsPackageVersionVisible { get; set; }
    [ObservableProperty] public partial bool IsDescriptionVisible { get; set; }
    [ObservableProperty] public partial bool IsDeveloperVisible { get; set; }
    [ObservableProperty] public partial bool IsAuthorVisible { get; set; }
    [ObservableProperty] public partial bool IsLastUpdatedVisible { get; set; }
    [ObservableProperty] public partial bool IsManifestRowVisible { get; set; }
    [ObservableProperty] public partial bool IsHomepageRowVisible { get; set; }
    [ObservableProperty] public partial bool IsLicenseRowVisible { get; set; }
    [ObservableProperty] public partial bool IsInstallerTypeRowVisible { get; set; }
    [ObservableProperty] public partial bool IsInstallerUrlRowVisible { get; set; }
    [ObservableProperty] public partial bool IsSha256RowVisible { get; set; }
    [ObservableProperty] public partial bool IsInstallerSizeVisible { get; set; }
    [ObservableProperty] public partial bool IsInstallerDetailsVisible { get; set; }
    [ObservableProperty] public partial bool IsDependenciesVisible { get; set; }
    [ObservableProperty] public partial bool IsReleaseNotesVisible { get; set; }
    [ObservableProperty] public partial bool IsCustomVersionOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsInteractiveOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsIgnoreHashOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsCustomArgumentsOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsScopeOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsLocationOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsCloseAppsOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsUninstallPreviousOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsManagerOptionsVisible { get; set; }
    [ObservableProperty] public partial bool IsVersionOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsAdminOptionVisible { get; set; }
    [ObservableProperty] public partial bool IsLocationTabVisible { get; set; }
    [ObservableProperty] public partial bool IsCommandPreviewVisible { get; set; }
    [ObservableProperty] public partial bool IsGlobalOptionsVisible { get; set; }
    public bool CanInstallForUser { get; private set; }
    public bool CanInstallForMachine { get; private set; }
    public AppEntry? App => _app;
    public string? IconUrl => _visuals.GetPackageIcon(_app?.IconUrl).SourceUrl;
    public bool ArePackageIconsEnabled => _visuals.ShouldLoad(PackageImageKind.PackageIcon);
    public bool IsPackageIconVisible => _visuals.ShouldLoad(PackageImageKind.PackageIcon)
        && !string.IsNullOrWhiteSpace(_app?.IconUrl);
    public bool IsScreenshotPanelVisible => _visuals.ShouldLoad(PackageImageKind.Screenshot);
    private string[] _screenshots = [];
    public string[] Screenshots
    {
        get => _screenshots;
        private set
        {
            if (ReferenceEquals(_screenshots, value)) return;
            _screenshots = value;
            OnPropertyChanged();
        }
    }
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
    [ObservableProperty] public partial bool IsSha256CopyEnabled { get; set; }
    [ObservableProperty] public partial string DependenciesText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReleaseNotesText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsUpdateVersionPanelVisible { get; set; } = false;
    [ObservableProperty] public partial string CurrentUpdateVersionText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AvailableUpdateVersionText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SelectedLocationText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool InstallAllUsersCheckBoxIsChecked { get; set; } = false;
    [ObservableProperty] public partial bool InteractiveInstallCheckBoxIsChecked { get; set; } = false;
    [ObservableProperty] public partial bool RequiresElevationCheckBoxIsChecked { get; set; }
    [ObservableProperty] public partial bool IgnoreSecurityHashCheckBoxIsChecked { get; set; } = false;
    [ObservableProperty] public partial bool IsVersionSelectorVisible { get; set; } = false;
    [ObservableProperty] public partial bool IsVersionLoadErrorVisible { get; set; }
    [ObservableProperty] public partial bool IsLoadingVersions { get; set; } = false;
    [ObservableProperty] public partial bool CanLoadVersions { get; set; } = true;
    [ObservableProperty] public partial string SelectedInstallVersion { get; set; } = "Mais recente";
    [ObservableProperty] public partial string CustomInstallVersion { get; set; } = string.Empty;
    public int[] SkipMinorUpdatesLevels { get; } = [2, 3, 4];
    public bool CanEditScope => IsOptionsOperationNotUninstall && InstallAllUsersCheckBoxIsEnabled;
    [ObservableProperty] public partial bool FollowGlobalOptions { get; set; } = true;
    public bool IsCustomOptionsMode => !FollowGlobalOptions || !IsManagerOptionsVisible;
    public bool CanPersistPackageOptions => PersistPackageOptionsCheckBoxIsEnabled && !FollowGlobalOptions;
    private IEnumerable<string> _installVersions = (string[])["Mais recente"];
    public IEnumerable<string> InstallVersions { get => _installVersions; set => SetProperty(ref _installVersions, value); }
    [ObservableProperty] public partial bool InstallAllUsersCheckBoxIsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool InteractiveInstallCheckBoxIsEnabled { get; set; } = true;
    [ObservableProperty] public partial bool IsInstallArchitecturePanelVisible { get; set; } = false;
    [ObservableProperty] public partial string ManifestLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? ManifestLinkTag { get; set; } = null;
    [ObservableProperty] public partial bool IsManifestLinkVisible { get; set; } = false;
    [ObservableProperty] public partial bool IsHomepageLinkVisible { get; set; } = false;
    [ObservableProperty] public partial string HomepageLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? HomepageLinkTag { get; set; } = null;
    [ObservableProperty] public partial string LicenseText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLicenseLinkVisible { get; set; } = false;
    [ObservableProperty] public partial string LicenseLinkText { get; set; } = string.Empty;
    [ObservableProperty] public partial string? LicenseLinkTag { get; set; } = null;
    [ObservableProperty] public partial string InstallerSizeText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsReleaseNotesUrlLinkVisible { get; set; } = false;
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
    [ObservableProperty] public partial string PreInstallScript { get; set; } = string.Empty;
    [ObservableProperty] public partial string PostInstallScript { get; set; } = string.Empty;
    [ObservableProperty] public partial string CustomArguments { get; set; } = string.Empty;
    [ObservableProperty] public partial string OverrideArguments { get; set; } = string.Empty;
    [ObservableProperty] public partial bool RunInstallHooks { get; set; }
    [ObservableProperty] public partial bool RunUpdateHooks { get; set; }
    [ObservableProperty] public partial bool RunUninstallHooks { get; set; }
    [ObservableProperty] public partial bool IsInstallHooksOptionsVisible { get; set; }
    [ObservableProperty] public partial string SelectedOptionsOperation { get; set; } = "Instalar";
    [ObservableProperty] public partial bool AutoUpdatePackageCheckBoxIsChecked { get; set; }
    [ObservableProperty] public partial bool IgnoreFutureUpdatesCheckBoxIsChecked { get; set; }
    [ObservableProperty] public partial bool SkipMinorUpdatesCheckBoxIsChecked { get; set; }
    [ObservableProperty] public partial int SkipMinorUpdatesLevel { get; set; } = 2;
    [ObservableProperty] public partial string UpdateArguments { get; set; } = string.Empty;
    [ObservableProperty] public partial string UninstallArguments { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreUpdateScript { get; set; } = string.Empty;
    [ObservableProperty] public partial string PostUpdateScript { get; set; } = string.Empty;
    [ObservableProperty] public partial string PreUninstallScript { get; set; } = string.Empty;
    [ObservableProperty] public partial string PostUninstallScript { get; set; } = string.Empty;
    [ObservableProperty] public partial bool AbortInstallOnPreScriptFailure { get; set; } = true;
    [ObservableProperty] public partial bool AbortUpdateOnPreScriptFailure { get; set; } = true;
    [ObservableProperty] public partial bool AbortUninstallOnPreScriptFailure { get; set; } = true;
    [ObservableProperty] public partial string ProcessesToCloseText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ForceCloseProcesses { get; set; }
    [ObservableProperty] public partial bool UninstallPrevious { get; set; }
    public string[] OptionsOperations { get; } = ["Instalar", "Atualizar", "Desinstalar"];
    public bool IsInstallOptionsOperation => SelectedOptionsOperation == "Instalar";
    public bool IsUpdateOptionsOperation => SelectedOptionsOperation == "Atualizar";
    public bool IsUninstallOptionsOperation => SelectedOptionsOperation == "Desinstalar";
    public bool IsSkipMinorLevelEnabled => SkipMinorUpdatesCheckBoxIsChecked;
    public bool IsOptionsOperationNotUninstall => !IsUninstallOptionsOperation;
    public bool IsOptionsOperationInstall => IsInstallOptionsOperation;
    public bool IsVersionOperationVisible => PackageManager.Capabilities.SupportsVersionSelection(SelectedOptionsOperation);
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
    public AppDetailsViewModel(CollectionsViewModel collections, IPackageBackend backend, PackageInstallOptionsService options, IPresentationService presentation, IUiDispatcher dispatcher, IClipboardService clipboard, IPackageDetailsProvider metadata, IconService icons, ApplicationPreferencesService applicationPreferences, IUpdatesIgnoreStore ignoredUpdates, PackageVisualPreferencesService? visuals = null)
    {
        _collectionService = collections; _installedPackagesViewModel = backend;
        _packageInstallOptionsService = options; _interaction = presentation;
        _dispatcher = dispatcher; _clipboard = clipboard; _metadata = metadata; _icons = icons;
        _applicationPreferences = applicationPreferences; _ignoredUpdates = ignoredUpdates;
        _visuals = visuals ?? new PackageVisualPreferencesService(applicationPreferences);
        _visuals.PackageIconsChanged += OnPackageIconsChanged;
        _visuals.ScreenshotsChanged += OnScreenshotsChanged;
        InstallCommand = new AsyncRelayCommand(
            InstallAsync,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
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
        PackageManager = _metadata.GetManager(app);
        IsInstallHooksOptionsVisible = PackageManager.Capabilities.PreInstallScripts || PackageManager.Capabilities.PostInstallScripts;
        IsInteractiveOptionVisible = PackageManager.Capabilities.InteractiveInstall;
        IsAdminOptionVisible = PackageManager.Capabilities.RunAsAdministrator;
        IsIgnoreHashOptionVisible = PackageManager.Capabilities.SkipHashCheck;
        IsCustomArgumentsOptionVisible = PackageManager.Capabilities.CustomArgs;
        IsCustomVersionOptionVisible = PackageManager.Capabilities.CustomVersion;
        IsScopeOptionVisible = PackageManager.Capabilities.SelectScope;
        IsLocationOptionVisible = PackageManager.Capabilities.SelectInstallLocation;
        IsCloseAppsOptionVisible = PackageManager.Capabilities.CloseRunningApps;
        IsUninstallPreviousOptionVisible = PackageManager.Capabilities.UninstallPrevious;
        PackageManagerCapabilities capabilities = PackageManager.Capabilities ?? new PackageManagerCapabilities();
        IsGlobalOptionsVisible = capabilities.GlobalOptions;
        IsManagerOptionsVisible = capabilities.InteractiveInstall || capabilities.RunAsAdministrator || capabilities.SkipHashCheck
            || capabilities.CustomVersion || capabilities.VersionSelector || capabilities.CustomArgs
            || capabilities.PreInstallScripts || capabilities.PostInstallScripts || capabilities.CloseRunningApps
            || capabilities.SelectArchitecture || capabilities.SelectScope || capabilities.SelectInstallLocation;
        IsVersionOptionVisible = capabilities.CustomVersion || capabilities.VersionSelector;
        IsLocationTabVisible = capabilities.SelectArchitecture || capabilities.SelectScope || capabilities.SelectInstallLocation;
        IsCommandPreviewVisible = !string.IsNullOrWhiteSpace(PackageManager.CommandLabel);
        OnPropertyChanged(nameof(IsCustomOptionsMode));
        OnPropertyChanged(nameof(App));
        OnPropertyChanged(nameof(IconUrl));
        OnPropertyChanged(nameof(ArePackageIconsEnabled));
        OnPropertyChanged(nameof(IsPackageIconVisible));
        _currentUpdateVersion = currentVersion;
        _availableUpdateVersion = availableVersion;
        SelectedOptionsOperation = availableVersion is not null ? "Atualizar" : app.IsInstalled ? "Desinstalar" : "Instalar";
        AutoUpdatePackageCheckBoxIsChecked = _applicationPreferences.IsAutoUpdatePackageSelected(app.Id, app.Source ?? string.Empty);
        IgnoreFutureUpdatesCheckBoxIsChecked = _ignoredUpdates.IsIgnored(app.Id, "*");

        InstallButtonIsEnabled = InstallChevronButtonIsEnabled = UpdateButtonIsEnabled = UninstallButtonIsEnabled = true;

        PackageDetails = null;
        ApplyDetailsSnapshot(_metadata.CreateSnapshot(app), availableVersion);
        IsLoadingProgressBarVisible = PackageManager.LoadsExtendedMetadata;

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
            RequiresElevationCheckBoxIsChecked = false;
            IgnoreSecurityHashCheckBoxIsChecked = false;
            InstallVersions = (string[])["Mais recente"];
            SelectedInstallVersion = "Mais recente";
            IsLoadingVersions = false;
            CanLoadVersions = true;
            IsVersionLoadErrorVisible = false;
        }
        IsVersionSelectorVisible = capabilities.SupportsVersionSelection(SelectedOptionsOperation);
        InstallAllUsersCheckBoxIsEnabled = capabilities.SelectScope;
        CanInstallForUser = CanInstallForMachine = false;
        InteractiveInstallCheckBoxIsEnabled = capabilities.InteractiveInstall;
        var supportedArchitectures = (app.Architectures ?? [])
            .Where(value => value is not null)
            .Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value is "x86" or "x64" or "arm" or "arm64")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string previousArchitecture = SelectedArchitecture;
        string[] managerArchitectures = PackageManager.SupportedArchitectures.ToArray();
        string[] availableArchitectures = managerArchitectures.Length > 0
            ? managerArchitectures : supportedArchitectures;
        Architectures = new[] { "Automática" }.Concat(availableArchitectures).ToArray();
        if (preserveOptions)
            SelectedArchitecture = Architectures.Contains(previousArchitecture, StringComparer.OrdinalIgnoreCase)
                ? previousArchitecture : "Automática";
        if (!preserveOptions)
        {
            SelectedArchitecture = "Automática";
            LoadSavedPackageOptions(app);
        }
        IsInstallArchitecturePanelVisible = PackageManager?.Capabilities?.SelectArchitecture == true && availableArchitectures.Length > 0;

        _app.PropertyChanged += App_PropertyChanged;
        UpdateInstallActionsVisibility();
        UpdateInstallCommandPreview();
    }
    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppEntry.IsInstalled)) _dispatcher.Post(UpdateInstallActionsVisibility);
        if (e.PropertyName == nameof(AppEntry.IconUrl))
            _dispatcher.Post(() =>
            {
                OnPropertyChanged(nameof(IconUrl));
                OnPropertyChanged(nameof(ArePackageIconsEnabled));
                OnPropertyChanged(nameof(IsPackageIconVisible));
            });
    }
    public void Clear()
    {
        if (_app is not null) _app.PropertyChanged -= App_PropertyChanged;
        _app = null;
        OnPropertyChanged(nameof(App));
        OnPropertyChanged(nameof(IconUrl));
        OnPropertyChanged(nameof(ArePackageIconsEnabled));
        OnPropertyChanged(nameof(IsPackageIconVisible));
    }
    private void OnPackageIconsChanged(bool _)
    {
        OnPropertyChanged(nameof(IconUrl));
        OnPropertyChanged(nameof(ArePackageIconsEnabled));
        OnPropertyChanged(nameof(IsPackageIconVisible));
    }
    private void OnScreenshotsChanged(bool _)
    {
        Screenshots = _visuals.GetScreenshots(PackageDetails?.Screenshots ?? []).ToArray();
        OnPropertyChanged(nameof(IsScreenshotPanelVisible));
    }
    public void SetLocation(string location) { _installLocation = location; SelectedLocationText = location; UpdateInstallCommandPreview(); }
    public async Task LoadInstallVersionsAsync(CancellationToken token)
    {
        if (_app is not { } app || !IsVersionSelectorVisible || IsLoadingVersions) return;
        string cacheKey = $"{app.Source.Trim()}|{app.Id.Trim()}";
        if (VersionCache.TryGetValue(cacheKey, out string[]? cachedVersions))
        {
            InstallVersions = new[] { "Mais recente" }.Concat(cachedVersions).ToArray();
            if (!InstallVersions.Contains(SelectedInstallVersion, StringComparer.OrdinalIgnoreCase))
                SelectedInstallVersion = "Mais recente";
            IsVersionLoadErrorVisible = cachedVersions.Length == 0;
            return;
        }
        IsLoadingVersions = true;
        CanLoadVersions = false;
        IsVersionLoadErrorVisible = false;
        try
        {
            string[] versions = await _metadata.LoadVersionsAsync(app, token);
            if (_app != app || token.IsCancellationRequested) return;
            VersionCache[cacheKey] = versions;
            InstallVersions = new[] { "Mais recente" }.Concat(versions).ToArray();
            if (!InstallVersions.Contains(SelectedInstallVersion, StringComparer.OrdinalIgnoreCase))
                SelectedInstallVersion = "Mais recente";
            IsVersionLoadErrorVisible = versions.Length == 0;
            if (versions.Length > 0) StatusText = string.Empty;
            await LoadSelectedVersionScopeAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_app == app)
            {
                InstallVersions = new[] { "Mais recente" };
                SelectedInstallVersion = "Mais recente";
                IsVersionLoadErrorVisible = true;
                StatusText = $"Não foi possível listar versões: {ex.Message}";
            }
        }
        finally { if (_app == app) { IsLoadingVersions = false; CanLoadVersions = true; } }
    }
    public async Task LoadSelectedVersionScopeAsync(CancellationToken token)
    {
        if (_app is not { } app || !IsVersionSelectorVisible) return;
        string selected = SelectedInstallVersion;
        string architecture = SelectedArchitecture;
        int generation = ++_scopeLoadGeneration;
        bool requestedMachine = InstallAllUsersCheckBoxIsChecked;
        bool requestedUser = _forcePerUserInstall;
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
    }
    public void SetUserScope(bool allUsers)
    {
        if (_updatingScope || allUsers && !CanInstallForMachine || !allUsers && !CanInstallForUser) return;
        InstallAllUsersCheckBoxIsChecked = allUsers;
        _forcePerUserInstall = !allUsers;
        UpdateInstallCommandPreview();
    }
    public void CopyInstallCommand()
    {
        string manager = PackageManager.DisplayName;
        StatusText = _clipboard.TrySetText(InstallCommandPreviewTextBox) ? $"Comando {manager} copiado para a área de transferência." : $"Não foi possível copiar o comando {manager}.";
    }
    public void CopyInstallerSha256()
    {
        if (!IsSha256CopyEnabled || string.IsNullOrWhiteSpace(Sha256Text)) return;
        StatusText = _clipboard.TrySetText(Sha256Text)
            ? "SHA-256 do instalador copiado para a área de transferência."
            : "Não foi possível copiar o SHA-256 do instalador.";
    }
    private void UpdateInstallActionsVisibility()
    {
        if (_app is null) return;

        bool isUpdateContext = !string.IsNullOrWhiteSpace(_availableUpdateVersion);
        IsUpdateButtonVisible = !_app.IsSelf && isUpdateContext;
        IsInstallSplitGroupVisible = !_app.IsSelf && !(isUpdateContext || _app.IsInstalled);
        IsUninstallSplitGroupVisible = !_app.IsSelf && !(isUpdateContext || !_app.IsInstalled);

    }
    private void ApplyDetailsSnapshot(PackageDetailsSnapshot snapshot, string? availableVersion = null)
    {
        PackageManager = snapshot.Manager;
        PackageDetails = snapshot.Package;
        PackageManagerText = $"{snapshot.Manager.DisplayName}: {snapshot.Package.Source}";
        AppNameText = snapshot.Package.Name;
        PackageSourceText = snapshot.Manager.DisplayName;
        PackageIdText = snapshot.Package.PackageId;
        PackageVersionText = availableVersion ?? snapshot.Package.Version ?? string.Empty;
        IsPackageVersionVisible = !string.IsNullOrWhiteSpace(PackageVersionText);
        DescriptionText = snapshot.Package.Description ?? string.Empty;
        IsDescriptionVisible = !string.IsNullOrWhiteSpace(DescriptionText);
        Tags = snapshot.Package.Tags;
        IsTagsListVisible = snapshot.Package.Tags.Count > 0;
        Screenshots = _visuals.GetScreenshots(snapshot.Package.Screenshots).ToArray();
        OnPropertyChanged(nameof(IsScreenshotPanelVisible));

        DeveloperText = snapshot.Package.Publisher ?? string.Empty;
        AuthorText = snapshot.Package.Author ?? string.Empty;
        IsDeveloperVisible = !string.IsNullOrWhiteSpace(DeveloperText);
        IsAuthorVisible = !string.IsNullOrWhiteSpace(AuthorText);
        LastUpdatedText = snapshot.Package.LastUpdated?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.CurrentCulture) ?? "Não disponível";
        IsLastUpdatedVisible = true;

        ManifestLinkText = snapshot.Package.ManifestUrl ?? string.Empty;
        ManifestLinkTag = snapshot.Package.ManifestUrl;
        IsManifestLinkVisible = !string.IsNullOrWhiteSpace(snapshot.Package.ManifestUrl)
            && snapshot.Manager.Capabilities.ManifestLink;
        IsManifestRowVisible = IsManifestLinkVisible;

        SetupHomepage(snapshot.Package.Homepage);
        IsHomepageRowVisible = IsHomepageLinkVisible;
        SetupLicense(snapshot.Package.License?.Name, snapshot.Package.License?.Url);
        IsLicenseRowVisible = snapshot.Package.License is not null;

        PackageInstallerDetails? installer = snapshot.Package.Installer;
        InstallerTypeText = FormatInstallerType(installer?.Type);
        IsInstallerTypeRowVisible = !string.IsNullOrWhiteSpace(installer?.Type)
            && snapshot.Manager.Capabilities.InstallerInfo;
        InstallerUrlLinkText = installer?.Url ?? string.Empty;
        InstallerUrlLinkTag = installer?.Url;
        DownloadInstallerLinkTag = installer?.Url;
        IsInstallerUrlLinkVisible = !string.IsNullOrWhiteSpace(installer?.Url) && snapshot.Manager.Capabilities.InstallerInfo;
        IsInstallerUrlRowVisible = IsInstallerUrlLinkVisible;
        Sha256Text = installer?.Sha256?.ToUpperInvariant() ?? string.Empty;
        IsSha256CopyEnabled = !string.IsNullOrWhiteSpace(installer?.Sha256);
        IsSha256RowVisible = IsSha256CopyEnabled && snapshot.Manager.Capabilities.InstallerInfo;
        IsDownloadInstallerLinkVisible = IsInstallerUrlLinkVisible;
        SetupSize(installer?.SizeBytes);
        IsInstallerSizeVisible = installer?.SizeBytes is > 0 && snapshot.Manager.Capabilities.InstallerInfo;
        IsInstallerDetailsVisible = IsInstallerUrlLinkVisible || IsInstallerSizeVisible;

        DependenciesText = string.Join(Environment.NewLine, snapshot.Package.Dependencies);
        IsDependenciesVisible = snapshot.Package.Dependencies.Count > 0;
        ReleaseNotesText = snapshot.Package.ReleaseNotes?.Text ?? string.Empty;
        IsReleaseNotesVisible = !string.IsNullOrWhiteSpace(snapshot.Package.ReleaseNotes?.Text);
        SetupReleaseNotesUrl(snapshot.Package.ReleaseNotes?.Url);
    }

    private static string FormatInstallerType(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "inno" => "Inno Setup",
        "nullsoft" => "Nullsoft (NSIS)",
        "msi" or "wix" => "Windows Installer (MSI)",
        _ => type?.Trim() ?? string.Empty
    };
    private void SetupHomepage(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        IsHomepageLinkVisible = hasUrl;
        HomepageLinkText = hasUrl ? url!.Trim() : string.Empty;
        HomepageLinkTag = hasUrl ? url : null;
    }
    private void SetupLicense(string? name, string? url)
    {
        bool hasName = !string.IsNullOrWhiteSpace(name);
        bool hasUrl = !string.IsNullOrWhiteSpace(url);

        LicenseText = hasName ? name! : string.Empty;
        IsLicenseLinkVisible = hasUrl;
        LicenseLinkText = hasUrl ? url!.Trim() : string.Empty;
        LicenseLinkTag = hasUrl ? url : null;
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
            InstallerSizeText = string.Empty;
        }
    }
    private void SetupReleaseNotesUrl(string? url)
    {
        bool hasUrl = !string.IsNullOrWhiteSpace(url);
        IsReleaseNotesUrlLinkVisible = hasUrl;
        ReleaseNotesUrlLinkText = hasUrl ? url!.Trim() : string.Empty;
        ReleaseNotesUrlLinkTag = hasUrl ? url : null;
    }
    public async Task LoadExtendedDetailsAsync(AppEntry app, CancellationToken cancellationToken)
    {
        if (!PackageManager.LoadsExtendedMetadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsLoadingProgressBarVisible = false;
            return;
        }
        IsLoadingProgressBarVisible = true;

        try
        {
            PackageDetailsSnapshot snapshot = await _metadata.LoadDetailsAsync(app, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _app != app) return;
            await _dispatcher.InvokeAsync(() => { if (_app == app) ApplyDetailsSnapshot(snapshot, _availableUpdateVersion); });
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
                    ApplyDetailsSnapshot(_metadata.CreateSnapshot(app), _availableUpdateVersion);
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
    public void SaveOptions()
    {
        try
        {
            if (_app is not null && PackageManager.Capabilities.PackageOptions)
            {
                if (FollowGlobalOptions)
                {
                    _packageInstallOptionsService.Remove(_app.Id, _app.Source);
                    StatusText = $"Usando as opções padrão de {PackageManager.DisplayName}.";
                }
                else if (PersistPackageOptionsCheckBoxIsChecked == true)
                {
                    _packageInstallOptionsService.Save(_app.Id, _app.Source, CurrentOptions() with
                    {
                        AutoUpdate = AutoUpdatePackageCheckBoxIsChecked,
                        SkipMinorVersionSegments = SkipMinorUpdatesCheckBoxIsChecked ? SkipMinorUpdatesLevel : null,
                        PreInstallScript = PreInstallScript,
                        PostInstallScript = PostInstallScript,
                        PreUpdateScript = PreUpdateScript,
                        PostUpdateScript = PostUpdateScript,
                        PreUninstallScript = PreUninstallScript,
                        PostUninstallScript = PostUninstallScript,
                        AbortInstallOnPreScriptFailure = AbortInstallOnPreScriptFailure,
                        AbortUpdateOnPreScriptFailure = AbortUpdateOnPreScriptFailure,
                        AbortUninstallOnPreScriptFailure = AbortUninstallOnPreScriptFailure,
                        UpdateArguments = UpdateArguments,
                        UninstallArguments = UninstallArguments,
                        RunInstallHooks = RunInstallHooks,
                        RunUpdateHooks = RunUpdateHooks,
                        RunUninstallHooks = RunUninstallHooks,
                        ProcessesToClose = ParseProcessNames(ProcessesToCloseText),
                        ForceCloseProcesses = ForceCloseProcesses,
                        UninstallPrevious = UninstallPrevious,
                        RequiresElevation = RequiresElevationCheckBoxIsChecked });
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
                StatusText = PackageManager.OptionsUnavailableMessage
                    ?? $"{PackageManager.DisplayName} não oferece opções reutilizáveis para este pacote.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Não foi possível salvar as opções deste pacote: {ex.Message}";
        }

    }

    public void SaveGlobalOptions()
    {
        if (!PackageManager.Capabilities.GlobalOptions)
        {
            StatusText = PackageManager.OptionsUnavailableMessage
                ?? $"Opções globais não estão disponíveis para {PackageManager.DisplayName}.";
            return;
        }
        try
        {
            _packageInstallOptionsService.SaveGlobalDefaults(CurrentOptions() with
            {
                AutoUpdate = AutoUpdatePackageCheckBoxIsChecked,
                SkipMinorVersionSegments = SkipMinorUpdatesCheckBoxIsChecked ? SkipMinorUpdatesLevel : null,
                ProcessesToClose = ParseProcessNames(ProcessesToCloseText),
                ForceCloseProcesses = ForceCloseProcesses,
                UninstallPrevious = UninstallPrevious
            });
            StatusText = $"Opções padrão de {PackageManager.DisplayName} salvas.";
        }
        catch (Exception ex)
        {
            StatusText = $"Não foi possível salvar as opções globais: {ex.Message}";
        }
    }
    private void LoadSavedPackageOptions(AppEntry app)
    {
        var saved = _packageInstallOptionsService.Get(app.Id, app.Source);
        var packageOverride = _packageInstallOptionsService.GetPackageOverride(app.Id, app.Source);
        PackageManagerCapabilities capabilities = PackageManager.Capabilities ?? new PackageManagerCapabilities();
        bool supported = capabilities.InteractiveInstall || capabilities.SkipHashCheck || capabilities.CustomVersion
            || capabilities.CustomArgs || capabilities.SelectArchitecture || capabilities.SelectScope
            || capabilities.SelectInstallLocation;
        PersistPackageOptionsCheckBoxIsEnabled = supported;
        FollowGlobalOptions = packageOverride is null;
        PersistPackageOptionsCheckBoxIsChecked = packageOverride is not null;
        PersistPackageOptionsHintText = supported
            ? $"Opções compatíveis com {PackageManager.DisplayName} podem ser reutilizadas nesta operação e em futuras atualizações."
            : $"{PackageManager.DisplayName} não oferece opções reutilizáveis neste pacote.";

        ApplySavedOptions(saved ?? _packageInstallOptionsService.GetGlobalDefaults());
        UpdateInstallCommandPreview();
    }

    private void ApplySavedOptions(PackageInstallOptions saved)
    {
        CustomInstallVersion = string.Empty;
        SelectedInstallVersion = saved.Version ?? "Mais recente";
        InstallAllUsersCheckBoxIsChecked = saved.Scope == "machine";
        InteractiveInstallCheckBoxIsChecked = saved.Interactive;
        RequiresElevationCheckBoxIsChecked = saved.RequiresElevation;
        IgnoreSecurityHashCheckBoxIsChecked = saved.IgnoreSecurityHash;
        if (saved.Scope == "user")
        {
            InstallAllUsersCheckBoxIsChecked = false;
            _forcePerUserInstall = true;
        }
        else
        {
            _forcePerUserInstall = false;
        }

        if (saved.Architecture is { } architecture
            && Architectures.Contains(architecture))
            SelectedArchitecture = architecture;
        else
            SelectedArchitecture = "Automática";

        _installLocation = saved.InstallLocation;
        PreInstallScript = saved.PreInstallScript ?? string.Empty;
        PostInstallScript = saved.PostInstallScript ?? string.Empty;
        PreUpdateScript = saved.PreUpdateScript ?? string.Empty;
        PostUpdateScript = saved.PostUpdateScript ?? string.Empty;
        PreUninstallScript = saved.PreUninstallScript ?? string.Empty;
        PostUninstallScript = saved.PostUninstallScript ?? string.Empty;
        UpdateArguments = saved.UpdateArguments ?? string.Empty;
        UninstallArguments = saved.UninstallArguments ?? string.Empty;
        SkipMinorUpdatesCheckBoxIsChecked = saved.SkipMinorVersionSegments is not null;
        SkipMinorUpdatesLevel = Math.Clamp(saved.SkipMinorVersionSegments ?? 2, 2, 4);
        ProcessesToCloseText = string.Join(Environment.NewLine, saved.ProcessesToClose ?? []);
        ForceCloseProcesses = saved.ForceCloseProcesses;
        UninstallPrevious = saved.UninstallPrevious;
        CustomArguments = saved.CustomArguments ?? string.Empty;
        OverrideArguments = PackageManager.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
            && PackageManager.Capabilities.OverrideArguments ? saved.OverrideArguments ?? string.Empty : string.Empty;
        RunInstallHooks = false;
        SelectedLocationText = string.IsNullOrWhiteSpace(_installLocation)
            ? "Local padrão do sistema"
            : _installLocation;
        UpdateInstallCommandPreview();
    }

    partial void OnSelectedOptionsOperationChanged(string value)
    {
        OnPropertyChanged(nameof(IsInstallOptionsOperation));
        OnPropertyChanged(nameof(IsUpdateOptionsOperation));
        OnPropertyChanged(nameof(IsUninstallOptionsOperation));
        OnPropertyChanged(nameof(IsSkipMinorLevelEnabled));
        OnPropertyChanged(nameof(IsOptionsOperationNotUninstall));
        OnPropertyChanged(nameof(IsOptionsOperationInstall));
        OnPropertyChanged(nameof(IsVersionOperationVisible));
        OnPropertyChanged(nameof(CanEditScope));
        IsVersionSelectorVisible = PackageManager.Capabilities.SupportsVersionSelection(value);
        UpdateInstallCommandPreview();
    }

    partial void OnSkipMinorUpdatesCheckBoxIsCheckedChanged(bool value) => OnPropertyChanged(nameof(IsSkipMinorLevelEnabled));
    partial void OnCustomInstallVersionChanged(string value) => UpdateInstallCommandPreview();
    partial void OnInteractiveInstallCheckBoxIsCheckedChanged(bool value) => UpdateInstallCommandPreview();
    partial void OnRequiresElevationCheckBoxIsCheckedChanged(bool value) => UpdateInstallCommandPreview();
    partial void OnIgnoreSecurityHashCheckBoxIsCheckedChanged(bool value) => UpdateInstallCommandPreview();
    partial void OnInstallAllUsersCheckBoxIsCheckedChanged(bool value) => UpdateInstallCommandPreview();
    partial void OnSelectedArchitectureChanged(string value) => UpdateInstallCommandPreview();
    partial void OnSelectedInstallVersionChanged(string value) => UpdateInstallCommandPreview();
    partial void OnUpdateArgumentsChanged(string value) => UpdateInstallCommandPreview();
    partial void OnUninstallArgumentsChanged(string value) => UpdateInstallCommandPreview();
    partial void OnCustomArgumentsChanged(string value) => UpdateInstallCommandPreview();
    partial void OnOverrideArgumentsChanged(string value) => UpdateInstallCommandPreview();

    partial void OnFollowGlobalOptionsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCustomOptionsMode));
        OnPropertyChanged(nameof(CanPersistPackageOptions));
        if (value && _app is not null)
        {
            PersistPackageOptionsCheckBoxIsChecked = false;
            ApplySavedOptions(_packageInstallOptionsService.GetGlobalDefaults());
        }
        else if (!value)
        {
            PersistPackageOptionsCheckBoxIsChecked = true;
        }
    }

    public void SetAutoUpdatePackage(bool enabled)
    {
        if (_app is null || _app.IsSelf) return;
        bool selected = _applicationPreferences.IsAutoUpdatePackageSelected(_app.Id, _app.Source);
        if (selected != enabled) _applicationPreferences.ToggleAutoUpdatePackage(_app.Id, _app.Source);
        if (!FollowGlobalOptions && PackageManager.Capabilities.PackageOptions)
        {
            PackageInstallOptions saved = _packageInstallOptionsService.Get(_app.Id, _app.Source) ?? new PackageInstallOptions();
            _packageInstallOptionsService.Save(_app.Id, _app.Source, saved with { AutoUpdate = enabled });
        }
        AutoUpdatePackageCheckBoxIsChecked = enabled;
    }

    public void SetIgnoreFutureUpdates(bool ignored)
    {
        if (_app is null || _app.IsSelf) return;
        if (ignored) _ignoredUpdates.Ignore(_app.Id, "*");
        else _ignoredUpdates.Unignore(_app.Id);
        IgnoreFutureUpdatesCheckBoxIsChecked = ignored;
    }
    private static bool IsMicrosoftStoreSource(string? source) =>
        string.Equals(source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "Microsoft Store", StringComparison.OrdinalIgnoreCase);
    private static bool IsSupportedCollectionSource(string? source) =>
        string.Equals(source?.Trim(), "winget", StringComparison.OrdinalIgnoreCase)
        || source?.Trim().StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase) == true
        || string.Equals(source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "Microsoft Store", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "chocolatey", StringComparison.OrdinalIgnoreCase);
    public void AddCurrentAppToPackages()
    {
        if (_app is null) return;
        if (!CanAddCurrentAppToPackages)
        {
            StatusText = "Somente pacotes de gerenciadores ativos podem ser adicionados à coleção.";
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
        if (_app is null || _app.IsSelf || _app.IsInstalling) return;

        var app = _app;
        app.IsInstalling = true;
        InstallButtonIsEnabled = false;
        InstallChevronButtonIsEnabled = false;
        StatusText = $"{app.Name} entrou na fila de instalação.";

        try
        {
            var result = await _installedPackagesViewModel.InstallAsync(app, CurrentOptions());

            if (result.Success)
            {
                app.IsInstalled = true;
                app.IsInstalledUnconfirmed = false;
                if (_app == app)
                    StatusText = $"{app.Name} instalado.";
            }
            else if (result.IsUnconfirmed)
            {
                app.IsInstalled = false;
                app.IsInstalledUnconfirmed = true;
                if (_app == app) StatusText = result.Output;
            }
            else if (_app == app)
            {
                StatusText = result.InstallHookFailed
                    ? result.Output
                    : WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", app.Name);
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
            app.IsInstalling = false;
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
            var result = await _installedPackagesViewModel.UpdateAsync(new UpgradablePackage
            {
                Id = app.Id,
                Name = app.Name,
                IconUrl = app.IconUrl,
                Source = app.Source,
                CurrentVersion = _currentUpdateVersion ?? string.Empty,
                AvailableVersion = _availableUpdateVersion ?? string.Empty
            }, CurrentOptions());

            if (_app == app)
            {
                StatusText = result.Success
                    ? $"{app.Name} atualizado."
                    : result.IsUnconfirmed
                        || result.Output.StartsWith("A atualização foi executada, mas não foi confirmada.", StringComparison.Ordinal)
                        ? result.Output
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
        if (_app is null || string.IsNullOrWhiteSpace(PackageManager.CommandLabel))
        {
            InstallCommandPreviewTextBox = string.Empty;
            CopyInstallCommandButtonIsEnabled = false;
            return;
        }
        try
        {
            InstallCommandPreviewTextBox = _metadata.BuildCommandPreview(_app, CurrentOptions(), SelectedOptionsOperation);
            CopyInstallCommandButtonIsEnabled = true;
        }
        catch (ArgumentException exception)
        {
            InstallCommandPreviewTextBox = exception.Message;
            CopyInstallCommandButtonIsEnabled = false;
        }
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
            bool removed = (await _installedPackagesViewModel.UninstallAsync(app,
                CurrentOptions().Interactive, CurrentOptions().RequiresElevation, CurrentOptions())).Success;
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
    public PackageInstallOptions CurrentOptions()
    {
        PackageManagerCapabilities capabilities = PackageManager.Capabilities ?? new PackageManagerCapabilities();
        string? version = null;
        if (IsOptionsOperationNotUninstall && capabilities.CustomVersion && !string.IsNullOrWhiteSpace(CustomInstallVersion))
            version = CustomInstallVersion.Trim();
        else if (IsOptionsOperationNotUninstall && capabilities.SupportsVersionSelection(SelectedOptionsOperation)
            && !string.IsNullOrWhiteSpace(SelectedInstallVersion) && SelectedInstallVersion != "Mais recente")
            version = SelectedInstallVersion;
        else if (IsUninstallOptionsOperation && !PackageManager.Source.Equals("chocolatey", StringComparison.OrdinalIgnoreCase))
            version = _currentUpdateVersion ?? _app?.Version;

        return new PackageInstallOptions(
            Interactive: capabilities.InteractiveInstall && InteractiveInstallCheckBoxIsChecked,
            RequiresElevation: capabilities.RunAsAdministrator && RequiresElevationCheckBoxIsChecked,
            Scope: capabilities.SelectScope
                ? InstallAllUsersCheckBoxIsChecked ? "machine" : _forcePerUserInstall ? "user" : null
                : null,
            Architecture: capabilities.SelectArchitecture && SelectedArchitecture != "Automática" ? SelectedArchitecture : null,
            IgnoreSecurityHash: capabilities.SkipHashCheck && IgnoreSecurityHashCheckBoxIsChecked,
            InstallLocation: capabilities.SelectInstallLocation ? _installLocation : null,
            Version: version,
            CustomArguments: capabilities.CustomArgs ? CustomArguments : null,
            OverrideArguments: PackageManager.Source.Equals("winget", StringComparison.OrdinalIgnoreCase) ? OverrideArguments : null,
            PreInstallScript: capabilities.PreInstallScripts ? PreInstallScript : null,
            PostInstallScript: capabilities.PostInstallScripts ? PostInstallScript : null,
            RunInstallHooks: capabilities.PreInstallScripts && RunInstallHooks,
            UpdateArguments: capabilities.CustomArgs ? UpdateArguments : null,
            UninstallArguments: capabilities.CustomArgs ? UninstallArguments : null,
            PreUpdateScript: capabilities.PreInstallScripts ? PreUpdateScript : null,
            PostUpdateScript: capabilities.PostInstallScripts ? PostUpdateScript : null,
            RunUpdateHooks: capabilities.PreInstallScripts && RunUpdateHooks,
            AbortInstallOnPreScriptFailure: AbortInstallOnPreScriptFailure,
            AbortUpdateOnPreScriptFailure: AbortUpdateOnPreScriptFailure,
            PreUninstallScript: capabilities.PreInstallScripts ? PreUninstallScript : null,
            PostUninstallScript: capabilities.PostInstallScripts ? PostUninstallScript : null,
            RunUninstallHooks: capabilities.PreInstallScripts && RunUninstallHooks,
            AbortUninstallOnPreScriptFailure: AbortUninstallOnPreScriptFailure,
            SkipMinorVersionSegments: capabilities.VersionSelector && SkipMinorUpdatesCheckBoxIsChecked ? SkipMinorUpdatesLevel : null,
            ProcessesToClose: capabilities.CloseRunningApps ? ParseProcessNames(ProcessesToCloseText) : [],
            ForceCloseProcesses: capabilities.CloseRunningApps && ForceCloseProcesses,
            UninstallPrevious: capabilities.UninstallPrevious && UninstallPrevious);
    }

    private static string[] ParseProcessNames(string text) => text
        .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(name => name.Length <= 128 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(32)
        .ToArray();
    public void Dispose()
    {
        _visuals.PackageIconsChanged -= OnPackageIconsChanged;
        _visuals.ScreenshotsChanged -= OnScreenshotsChanged;
        Clear();
    }
}
