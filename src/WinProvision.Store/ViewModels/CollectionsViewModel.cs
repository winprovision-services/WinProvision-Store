using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CollectionProfile : ObservableObject
{
    public ProfileManifest Manifest { get; }
    public ObservableCollection<PackageItemViewModel> Items { get; } = new();
    public bool IsDefault { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    public CollectionProfile(ProfileManifest manifest, bool isDefault = false)
    {
        Manifest = manifest;
        IsDefault = isDefault;
        Title = string.IsNullOrWhiteSpace(manifest.Name) ? "Coleção" : manifest.Name;
    }

    partial void OnTitleChanged(string value) => Manifest.Name = value;

    public ProfileManifest Snapshot()
    {
        Manifest.Apps = Items.Select(item => item.ToReference()).ToList();
        return Manifest;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CollectionsViewModel : PageViewModel
{
    private readonly IPackageBackend _backend;
    private readonly IPresentationService _presentation;
    private readonly IClipboardService? _clipboard;
    private readonly PackageInstallOptionsService? _installOptions;
    private readonly IPackageMetricsService? _metrics;
    private readonly StoreService? _store;
    private readonly IUiDispatcher? _dispatcher;
    private readonly HashSet<PackageItemViewModel> _metricAttempts = [];
    private bool _metricsActive;
    private Task _metricsTask = Task.CompletedTask;
    private readonly WingetExecutor? _winget;
    private readonly WinProvision.PackageManagement.ApplicationEngine? _engine;
    private readonly OfflineCollectionExportService? _offlineExport;
    private readonly PowerShellScriptAnalyzerService? _scriptAnalyzer;
    private readonly IOfficeCollectionInstaller? _officeInstaller;
    private readonly WindowsAppNotificationService? _notifications;
    private readonly string _path;
    private ProfileBackupSet _backup = new();
    private CancellationTokenSource? _metricsCancellation;
    private bool _suppressSave;

    public ObservableCollection<CollectionProfile> Profiles { get; } = new();
    public ObservableCollection<PackageItemViewModel> Items { get; } = new();
    public event Action<ProfileBackupSet>? BackupSnapshotChanged;

    [ObservableProperty]
    public partial CollectionProfile? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool OnlySelected { get; set; }

    [ObservableProperty]
    public partial bool AllSources { get; set; } = true;

    [ObservableProperty]
    public partial bool SourceWinget { get; set; } = true;

    [ObservableProperty]
    public partial bool SourceMsStore { get; set; } = true;

    [ObservableProperty]
    public partial bool SourceOffice { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFiltersVisible { get; set; } = true;

    [ObservableProperty]
    public partial string ViewMode { get; set; } = "grid";

    [ObservableProperty]
    public partial string SortField { get; set; } = "Name";

    [ObservableProperty]
    public partial bool SortAscending { get; set; } = true;

    [ObservableProperty]
    public partial string ErrorTitle { get; set; } = "";

    [ObservableProperty]
    public partial string SelectionSummaryText { get; set; } = "";

    [ObservableProperty]
    public partial string EstimatedSizeText { get; set; } = "—";

    [ObservableProperty]
    public partial string EstimatedTimeText { get; set; } = "—";

    [ObservableProperty]
    public partial string EmptyStateTitle { get; set; } = "Sua coleção está vazia";

    [ObservableProperty]
    public partial string EmptyStateMessage { get; set; } = "Adicione aplicativos pela página Descobrir ou importe uma coleção JSON.";

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; set; }

    [ObservableProperty]
    public partial bool CanInstall { get; set; }

    [ObservableProperty]
    public partial bool CanRemove { get; set; }

    [ObservableProperty]
    public partial bool CanEditProfile { get; set; }

    [ObservableProperty]
    public partial bool? MasterSelection { get; set; }

    [ObservableProperty]
    public partial string SelectAllText { get; set; } = "Selecionar todos";

    [ObservableProperty]
    public partial bool IsExportingOffline { get; private set; }

    [ObservableProperty]
    public partial bool OfflineProgressIndeterminate { get; private set; } = true;

    [ObservableProperty]
    public partial double OfflineProgress { get; private set; }

    [ObservableProperty]
    public partial string OfflineStatusText { get; private set; } = "";

    public IRelayCommand NewCommand { get; }
    public IRelayCommand RemoveSelectedCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearFiltersCommand { get; }
    public IRelayCommand CancelBatchCommand { get; }
    public IRelayCommand CancelOfflineExportCommand { get; }
    public IAsyncRelayCommand InstallSelectedCommand { get; }
    public IAsyncRelayCommand DownloadSelectedCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand ExportScriptCommand { get; }
    public IAsyncRelayCommand ExportOfflineCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand ClearCommand { get; }

    public CollectionsViewModel(
        IPackageBackend backend,
        IPresentationService presentation,
        string? storagePath = null,
        WingetExecutor? winget = null,
        PackageInstallOptionsService? installOptions = null,
        IClipboardService? clipboard = null,
        IPackageMetricsService? metrics = null,
        OfflineCollectionExportService? offlineExport = null,
        PowerShellScriptAnalyzerService? scriptAnalyzer = null,
        IOfficeCollectionInstaller? officeInstaller = null, WindowsAppNotificationService? notifications = null,
        StoreService? store = null, IUiDispatcher? dispatcher = null, WinProvision.PackageManagement.ApplicationEngine? engine = null)
    {
        _backend = backend;
        _engine = engine;
        _presentation = presentation;
        _winget = winget;
        _installOptions = installOptions;
        _clipboard = clipboard;
        _metrics = metrics;
        _store = store;
        _dispatcher = dispatcher;
        _offlineExport = offlineExport;
        _scriptAnalyzer = scriptAnalyzer;
        _officeInstaller = officeInstaller;
        _notifications = notifications;
        _path = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore.WinUI3", "collections.json");

        NewCommand = new RelayCommand(CreateProfile);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => CanRemove && !IsBusy);
        SelectAllCommand = new RelayCommand(ToggleSelectAll, () => HasItems);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, () => CanInstall && !IsBusy);
        DownloadSelectedCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => HasSelectedPackages && !IsBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => SelectedProfile is not null && !IsBusy);
        ExportScriptCommand = new AsyncRelayCommand(ExportScriptAsync, () => HasItems && !IsBusy);
        ExportOfflineCommand = new AsyncRelayCommand(ExportOfflineAsync, () => HasSelectedPackages && !IsBusy);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => CanEditProfile && !IsBusy);
        ClearCommand = new AsyncRelayCommand(ClearAsync, () => HasItems && !IsBusy);
        CancelBatchCommand = new RelayCommand(() => InstallSelectedCommand.Cancel(), () => IsBusy && InstallSelectedCommand.IsRunning);
        CancelOfflineExportCommand = new RelayCommand(() => ExportOfflineCommand.Cancel(), () => IsExportingOffline);

        LoadLocal();
    }

    public bool HasSelectedPackages => SelectedProfile?.Items.Any(item => item.IsSelected) == true;

    partial void OnSelectedProfileChanged(CollectionProfile? value)
    {
        OnPropertyChanged(nameof(HasSelectedPackages));
        UpdateFilteredItems();
        ScheduleMetrics();
    }

    partial void OnSearchTextChanged(string value) => UpdateFilteredItems();
    partial void OnOnlySelectedChanged(bool value) => UpdateFilteredItems();
    partial void OnAllSourcesChanged(bool value) => UpdateFilteredItems();
    partial void OnSourceWingetChanged(bool value) => UpdateFilteredItems();
    partial void OnSourceMsStoreChanged(bool value) => UpdateFilteredItems();
    partial void OnSourceOfficeChanged(bool value) => UpdateFilteredItems();
    partial void OnSortFieldChanged(string value) => UpdateFilteredItems();
    partial void OnSortAscendingChanged(bool value) => UpdateFilteredItems();
    protected override void OnBusyChanged(bool value)
    {
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        DownloadSelectedCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ExportScriptCommand.NotifyCanExecuteChanged();
        ExportOfflineCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
        UpdateStatus();
    }

    partial void OnIsExportingOfflineChanged(bool value) => CancelOfflineExportCommand.NotifyCanExecuteChanged();

    private CollectionProfile AddProfile(ProfileManifest manifest, bool isDefault = false)
    {
        var profile = new CollectionProfile(manifest, isDefault);
        foreach (ProfileAppRef reference in manifest.Apps)
            profile.Items.Add(CreateItem(FromReference(reference), reference));
        profile.PropertyChanged += Profile_PropertyChanged;
        profile.Items.CollectionChanged += ProfileItems_CollectionChanged;
        foreach (PackageItemViewModel item in profile.Items)
            item.PropertyChanged += Item_PropertyChanged;
        Profiles.Add(profile);
        return profile;
    }

    private PackageItemViewModel CreateItem(AppEntry app, ProfileAppRef? reference = null) =>
        new(app, InstallItemAsync, DetailsAsync, item => Add(item.App), DownloadItemAsync, reference, Remove);

    private static AppEntry FromReference(ProfileAppRef reference) => new()
    {
        Id = reference.Id,
        Name = string.IsNullOrWhiteSpace(reference.Name) ? reference.Id : reference.Name,
        Publisher = reference.Publisher ?? "",
        Source = reference.Source ?? (reference.OfficeOptions is null ? "winget" : "office"),
        IconUrl = reference.IconUrl ?? "",
        Description = reference.Description,
        Office = reference.OfficeOptions
    };

    public void Add(AppEntry app, ProfileAppRef? reference = null)
    {
        if (SelectedProfile is null)
            throw new InvalidOperationException("Selecione uma coleção antes de adicionar aplicativos.");
        var existing = SelectedProfile.Items.FirstOrDefault(item =>
                item.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase) &&
                item.App.Source.Equals(app.Source, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (app.Office is not null)
            {
                var saved = existing.ToReference();
                saved.OfficeOptions = app.Office;
                var replacement = CreateItem(app, saved);
                replacement.IsSelected = existing.IsSelected;
                SelectedProfile.Items[SelectedProfile.Items.IndexOf(existing)] = replacement;
                StatusText = $"Configuração de {app.Name} atualizada na coleção {SelectedProfile.Title}.";
                return;
            }
            StatusText = $"{app.Name} já está nesta coleção.";
            return;
        }

        SelectedProfile.Items.Add(CreateItem(app, reference));
        StatusText = $"{app.Name} adicionado à coleção {SelectedProfile.Title}.";
    }

    public void CreateProfile()
    {
        var profile = AddProfile(new ProfileManifest { Name = $"Coleção {Profiles.Count + 1}" });
        SelectedProfile = profile;
        Save();
    }

    public ProfileBackupSet CreateBackupSnapshot()
    {
        return new ProfileBackupSet
        {
            CreatedAt = DateTime.UtcNow,
            Tabs = Profiles
                .Where(profile => profile.Items.Count > 0)
                .Select(profile => profile.Snapshot())
                .ToList(),
            Provisioning = _backup.Provisioning
        };
    }

    public int ImportJsonBackup(string json, string fallbackName)
    {
        ProfileBackupSet importedBackup = ProfileDocumentService.Parse(json, fallbackName);
        IReadOnlyList<ProfileManifest> manifests = importedBackup.Tabs;

        foreach (ProfileManifest manifest in manifests)
            SelectedProfile = AddProfile(manifest);

        if (importedBackup?.Provisioning is not null)
            _backup.Provisioning = importedBackup.Provisioning;

        Save();
        return manifests.Count;
    }

    public void RenameProfile(string title)
    {
        if (SelectedProfile is not { IsDefault: false } profile)
            return;
        if (string.IsNullOrWhiteSpace(title))
        {
            StatusText = "Informe um nome para a coleção.";
            return;
        }

        profile.Title = title.Trim();
        Save();
        StatusText = $"Coleção renomeada para {profile.Title}.";
    }

    public void SetSort(string field, bool? ascending = null)
    {
        bool nextAscending = ascending ?? (SortField == field ? !SortAscending : true);
        SortField = field;
        SortAscending = nextAscending;
    }

    public void SetViewMode(string mode)
    {
        if (mode is not ("list" or "grid" or "icons"))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Modo de visualização inválido.");
        ViewMode = mode;
    }

    public void SetFiltersVisible(bool visible) => IsFiltersVisible = visible;

    public void SetAllSelected(bool selected)
    {
        foreach (PackageItemViewModel item in Items.ToArray())
            item.IsSelected = selected;
    }

    private void ToggleSelectAll()
    {
        if (Items.Count > 0)
            SetAllSelected(Items.Any(item => !item.IsSelected));
    }

    private void ClearFilters()
    {
        SearchText = "";
        OnlySelected = false;
        AllSources = true;
        SourceWinget = SourceMsStore = SourceOffice = true;
    }

    private void UpdateFilteredItems()
    {
        Items.Clear();
        if (SelectedProfile is { } profile)
        {
            IEnumerable<PackageItemViewModel> matches = profile.Items.Where(MatchesFilters);
            IOrderedEnumerable<PackageItemViewModel> ordered = SortField switch
            {
                "Id" => SortAscending ? matches.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase) : matches.OrderByDescending(item => item.Id, StringComparer.OrdinalIgnoreCase),
                "Version" => SortAscending ? matches.OrderBy(item => item.Version, StringComparer.OrdinalIgnoreCase) : matches.OrderByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase),
                "Source" => SortAscending ? matches.OrderBy(item => item.SourceLabel, StringComparer.OrdinalIgnoreCase) : matches.OrderByDescending(item => item.SourceLabel, StringComparer.OrdinalIgnoreCase),
                _ => SortAscending ? matches.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase) : matches.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
            };
            foreach (PackageItemViewModel item in ordered)
                Items.Add(item);
        }

        UpdateStatus();
    }

    private bool MatchesFilters(PackageItemViewModel item)
    {
        if (OnlySelected && !item.IsSelected)
            return false;

        string query = SearchText.Trim();
        if (query.Length > 0 &&
            !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !item.Publisher.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        if (AllSources)
            return true;

        if (item.App.Office is not null || item.App.Source.Equals("office", StringComparison.OrdinalIgnoreCase))
            return SourceOffice;
        if (item.App.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            return SourceMsStore;
        return SourceWinget;
    }

    private void UpdateStatus()
    {
        int total = SelectedProfile?.Items.Count ?? 0;
        int visible = Items.Count;
        int selected = SelectedProfile?.Items.Count(item => item.IsSelected) ?? 0;
        int visibleSelected = Items.Count(item => item.IsSelected);
        int installable = SelectedProfile?.Items.Count(item => item.IsSelected && item.SupportsInstall && !item.IsInstalled) ?? 0;

        HasItems = total > 0;
        IsEmpty = total == 0;
        HasNoResults = total > 0 && visible == 0;
        CanInstall = installable > 0 && !IsBusy;
        CanRemove = selected > 0 && !IsBusy;
        CanEditProfile = SelectedProfile is { IsDefault: false };
        MasterSelection = visibleSelected == 0 ? false : visibleSelected == visible ? true : null;
        SelectAllText = visible > 0 && Items.All(item => item.IsSelected) ? "Desmarcar todos" : "Selecionar todos";
        SelectionSummaryText = $"{selected} selecionado(s) · {total} aplicativo(s)";
        StatusText = SelectedProfile is null ? "Nenhuma coleção selecionada." : $"Coleção “{SelectedProfile.Title}” · {total} aplicativo(s)";
        EmptyStateTitle = SearchText.Length > 0 || OnlySelected || !AllSources
            ? "Nenhum aplicativo encontrado"
            : "Sua coleção está vazia";
        EmptyStateMessage = SearchText.Length > 0 || OnlySelected || !AllSources
            ? "Ajuste os filtros ou limpe a pesquisa para ver os aplicativos desta coleção."
            : "Adicione aplicativos pela página Descobrir, importe um perfil JSON ou crie uma nova coleção.";

        UpdateEstimates();
        OnPropertyChanged(nameof(HasSelectedPackages));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        DownloadSelectedCommand.NotifyCanExecuteChanged();
        ExportScriptCommand.NotifyCanExecuteChanged();
        ExportOfflineCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private void UpdateEstimates()
    {
        PackageItemViewModel[] selected = SelectedProfile?.Items.Where(item => item.IsSelected).ToArray() ?? [];
        long knownBytes = selected.Sum(item => item.App.InstallerSizeBytes ?? 0);
        int unknownCount = selected.Count(item => item.App.InstallerSizeBytes is not > 0);
        bool loading = selected.Any(item => item.App.Office is null && item.App.InstallerSizeBytes is not > 0 && !_metricAttempts.Contains(item));
        if (selected.Length == 0)
        {
            EstimatedSizeText = EstimatedTimeText = "—";
            return;
        }

        if (knownBytes == 0 && unknownCount > 0)
        {
            EstimatedSizeText = EstimatedTimeText = loading ? "Calculando…" : "Indisponível";
            return;
        }

        string suffix = unknownCount > 0 ? " +" : "";
        double megabytes = knownBytes / 1024d / 1024d;
        EstimatedSizeText = knownBytes == 0 ? "—" : (megabytes >= 1024 ? $"~ {megabytes / 1024:0.0} GB" : $"~ {megabytes:0} MB") + suffix;
        if (knownBytes == 0)
            EstimatedTimeText = "—";
        else
        {
            double seconds = Math.Max(10, selected.Length * 12) + megabytes / 8;
            EstimatedTimeText = seconds >= 3600 ? $"~ {seconds / 3600:0.0} h{suffix}" :
                seconds >= 60 ? $"~ {Math.Ceiling(seconds / 60):0} min{suffix}" :
                $"~ {Math.Max(1, Math.Ceiling(seconds)):0} s{suffix}";
        }
    }

    private void Profile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionProfile.Title))
        {
            Save();
            UpdateStatus();
        }
    }

    private void ProfileItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (PackageItemViewModel item in e.NewItems)
                item.PropertyChanged += Item_PropertyChanged;
        if (e.OldItems is not null)
            foreach (PackageItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= Item_PropertyChanged;
                _metricAttempts.Remove(item);
            }

        _metricAttempts.RemoveWhere(item => !Profiles.Any(profile => profile.Items.Contains(item)));
        Save();
        if (ReferenceEquals(sender, SelectedProfile?.Items))
        {
            UpdateFilteredItems();
            ScheduleMetrics();
        }
    }

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is PackageItemViewModel item &&
            SelectedProfile?.Items.Contains(item) == true &&
            (e.PropertyName is nameof(PackageItemViewModel.IsSelected) or nameof(PackageItemViewModel.IsInstalled) or nameof(PackageItemViewModel.IsWorking)))
        {
            UpdateFilteredItems();
        }
    }

    public void StartMetrics()
    {
        _metricsActive = true;
        ScheduleMetrics();
    }

    private void ScheduleMetrics()
    {
        if (!_metricsActive || _metrics is null) return;
        Task previous = _metricsTask;
        Task current = RefreshMetricsAsync();
        _metricsTask = previous.IsCompleted ? current : Task.WhenAll(previous, current);
    }

    public async Task RefreshMetricsAsync(CancellationToken cancellationToken = default)
    {
        if (_metrics is null || SelectedProfile is not { } profile) return;
        _metricsCancellation?.Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _metricsCancellation = linked;
        using var gate = new SemaphoreSlim(3);
        PackageItemViewModel[] pending = profile.Items.Where(item => item.App.Office is null &&
            item.App.InstallerSizeBytes is not > 0 && !_metricAttempts.Contains(item)).ToArray();
        try
        {
            await Task.WhenAll(pending.Select(LoadMetricAsync));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Collections.RefreshMetrics"); }
        finally
        {
            if (ReferenceEquals(_metricsCancellation, linked)) _metricsCancellation = null;
        }

        async Task LoadMetricAsync(PackageItemViewModel item)
        {
            await gate.WaitAsync(linked.Token);
            try
            {
                long? size = null;
                try
                {
                    AppEntry? catalogApp = _store?.GetAll().FirstOrDefault(app =>
                        string.Equals(app.Id, item.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(app.Source, item.App.Source, StringComparison.OrdinalIgnoreCase));
                    using var lookup = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    lookup.CancelAfter(TimeSpan.FromSeconds(45));
                    if (catalogApp is not null && catalogApp.InstallerSizeBytes is not > 0 &&
                        !string.IsNullOrWhiteSpace(catalogApp.CatalogDetailPath))
                        catalogApp = await _store!.LoadDetailsAsync(catalogApp, lookup.Token);
                    size = catalogApp?.InstallerSizeBytes is > 0 ? catalogApp.InstallerSizeBytes :
                        await _metrics.GetInstallerSizeAsync(item.App, lookup.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception exception) { WinProvisionLog.Warning(exception, "Collections.PackageMetric"); }
                linked.Token.ThrowIfCancellationRequested();
                void Apply()
                {
                    if (!profile.Items.Contains(item)) return;
                    _metricAttempts.Add(item);
                    if (size is > 0) item.App.InstallerSizeBytes = size;
                    if (ReferenceEquals(SelectedProfile, profile)) UpdateEstimates();
                }
                if (_dispatcher is not null) await _dispatcher.InvokeAsync(Apply, linked.Token);
                else Apply();
            }
            finally { gate.Release(); }
        }
    }

    public void CancelMetrics()
    {
        _metricsActive = false;
        _metricsCancellation?.Cancel();
    }

    public async Task StopMetricsAsync()
    {
        CancelMetrics();
        await _metricsTask;
    }

    private void RemoveSelected()
    {
        if (SelectedProfile is not { } profile)
            return;
        PackageItemViewModel[] selected = profile.Items.Where(item => item.IsSelected && !item.IsWorking).ToArray();
        foreach (PackageItemViewModel item in selected)
            profile.Items.Remove(item);
        StatusText = $"{selected.Length} aplicativo(s) removido(s) da coleção.";
    }

    private void Remove(PackageItemViewModel item)
    {
        if (SelectedProfile?.Items.Contains(item) == true && !item.IsWorking)
        {
            SelectedProfile.Items.Remove(item);
            StatusText = $"{item.Name} removido da coleção.";
        }
    }

    private async Task InstallSelectedAsync(CancellationToken token) =>
        await InstallSelectedWithOptionsAsync(new PackageInstallOptions(), token);

    public async Task InstallSelectedWithOptionsAsync(PackageInstallOptions options, CancellationToken token = default)
    {
        PackageItemViewModel[] selected = SelectedProfile?.Items.Where(item => item.IsSelected).ToArray() ?? [];
        PackageItemViewModel[] installable = selected.Where(item => item.SupportsInstall && !item.IsInstalled).ToArray();
        if (selected.Length == 0 || installable.Length == 0)
        {
            StatusText = selected.Length == 0
                ? "Selecione aplicativos compatíveis para instalar."
                : selected.All(item => item.IsInstalled)
                    ? "Os itens selecionados já estão instalados."
                    : "Os itens selecionados não têm uma configuração de instalação compatível.";
            return;
        }

        HasError = false;
        IsBusy = true;
        int succeeded = 0;
        var failures = new List<string>();
        try
        {
            failures.AddRange(selected
                .Where(item => !item.IsInstalled && !item.SupportsInstall)
                .Select(item => $"{item.Name}: configuração de instalação ausente ou incompatível."));
            foreach (PackageItemViewModel item in installable)
            {
                token.ThrowIfCancellationRequested();
                item.IsWorking = true;
                try
                {
                    bool success = await InstallPackageOrOfficeAsync(item, options, token);
                    if (success)
                    {
                        item.IsInstalled = true;
                        succeeded++;
                    }
                    else
                        failures.Add($"{item.Name}: {StatusText}");
                }
                finally
                {
                    item.IsWorking = false;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText = "Lote interrompido. Acompanhe a operação em andamento na fila.";
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            IsBusy = false;
        }

        if (failures.Count > 0)
        {
            ErrorTitle = "Alguns aplicativos não puderam ser instalados";
            ErrorMessage = string.Join(Environment.NewLine, failures);
            HasError = true;
        }
        if (!token.IsCancellationRequested)
            StatusText = failures.Count == 0
                ? $"{succeeded} aplicativo(s) enviado(s) à fila de operações."
                : $"{succeeded} instalado(s); {failures.Count} falha(s).";
    }

    private static PackageInstallOptions MergeOptions(
        PackageInstallOptions? saved,
        PackageInstallOptions requested,
        string? pinnedVersion = null) =>
        (saved ?? new PackageInstallOptions()) with
        {
            Interactive = requested.Interactive || saved?.Interactive == true,
            RequiresElevation = requested.RequiresElevation || saved?.RequiresElevation == true,
            IgnoreSecurityHash = false,
            Scope = requested.Scope ?? saved?.Scope,
            Architecture = requested.Architecture ?? saved?.Architecture,
            InstallLocation = requested.InstallLocation ?? saved?.InstallLocation,
            Version = pinnedVersion ?? requested.Version ?? saved?.Version,
            CustomArguments = requested.CustomArguments ?? saved?.CustomArguments,
            AutoUpdate = requested.AutoUpdate || saved?.AutoUpdate == true,
            SkipMinorVersionSegments = requested.SkipMinorVersionSegments ?? saved?.SkipMinorVersionSegments
        };

    private Task InstallItemAsync(PackageItemViewModel item) =>
        InstallItemWithOptionsAsync(item, new PackageInstallOptions());

    public async Task InstallItemWithOptionsAsync(PackageItemViewModel item, PackageInstallOptions requested)
    {
        if (!item.SupportsInstall || item.IsInstalled)
            return;
        item.IsWorking = true;
        HasError = false;
        try
        {
            item.IsInstalled = await InstallPackageOrOfficeAsync(item, requested, CancellationToken.None);
            if (item.IsInstalled)
                StatusText = $"{item.Name} enviado à fila de operações.";
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            item.IsWorking = false;
        }
    }

    private async Task<bool> InstallPackageOrOfficeAsync(
        PackageItemViewModel item,
        PackageInstallOptions requested,
        CancellationToken cancellationToken)
    {
        if (item.App.Office is { } officeOptions)
        {
            if (_officeInstaller is null)
                throw new InvalidOperationException("O instalador do Office não foi configurado.");
            if (!TryBuildOfficeRequest(officeOptions, out OfficeInstallRequest? request, out string? error))
            {
                StatusText = error ?? "A configuração do Office é inválida.";
                return false;
            }

            OfficeInstallRequest validRequest = request!;
            StatusText = $"Instalando {validRequest.Plan.DisplayName}…";
            bool success = await _officeInstaller.InstallAsync(validRequest, cancellationToken);
            if (!success)
                StatusText = $"Falha ao instalar {validRequest.Plan.DisplayName}. Veja a fila de operações.";
            return success;
        }

        if (item.App.Source.Equals("office", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "O item Office não contém opções de instalação.";
            return false;
        }

        PackageInstallOptions effective = MergeOptions(
            _installOptions?.Get(item.Id, item.App.Source),
            requested,
            item.ImportedReference?.PinnedVersion);
        WingetExecutionResult result = await _backend.InstallAsync(item.App, effective);
        StatusText = result.Success
            ? $"{item.Name} enviado à fila de operações."
            : WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", item.Name);
        return result.Success;
    }

    internal static bool TryBuildOfficeRequest(
        OfficeInstallOptions options,
        out OfficeInstallRequest? request,
        out string? error)
    {
        try { request = OfficeProfileConfiguration.Build(options); error = null; return true; }
        catch (InvalidDataException exception) { request = null; error = exception.Message; return false; }
    }

    private async Task DetailsAsync(PackageItemViewModel item)
    {
        try
        {
            await _presentation.ShowDetailsAsync(item.App);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private async Task DownloadItemAsync(PackageItemViewModel item)
    {
        if (!item.SupportsDownload)
        {
            StatusText = "O download está disponível para pacotes WinGet e Microsoft Store sem versão fixa.";
            return;
        }
        string? folder = await _presentation.PickFolderAsync();
        if (folder is null)
            return;
        await DownloadAsync((WinProvision.Store.ViewModels.PackageItemViewModel[])[item], folder, CancellationToken.None);
    }

    private async Task DownloadSelectedAsync(CancellationToken token)
    {
        PackageItemViewModel[] selected = SelectedProfile?.Items
            .Where(item => item.IsSelected && item.SupportsDownload).ToArray() ?? [];
        if (selected.Length == 0)
        {
            StatusText = "Selecione pacotes WinGet ou Microsoft Store sem versão fixa para baixar.";
            return;
        }
        string? folder = await _presentation.PickFolderAsync();
        if (folder is null)
            return;
        await DownloadAsync(selected, folder, token);
    }

    private async Task DownloadAsync(IReadOnlyList<PackageItemViewModel> selected, string folder, CancellationToken token)
    {
        if (_winget is null && _engine is null)
            throw new InvalidOperationException("O serviço de download de instaladores não foi configurado.");
        int downloaded = 0;
        var failures = new List<string>();
        for (int index = 0; index < selected.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            PackageItemViewModel item = selected[index];
            StatusText = $"Baixando instalador {index + 1} de {selected.Count}: {item.Name}…";
            try
            {
                WingetExecutionResult result = _engine is not null
                    ? await _engine.DownloadProfileAsync(item.ToReference(), folder, _ => { }, token)
                    : await _winget!.DownloadInstallerAsync(item.Id, folder, item.App.Source, cancellationToken: token);
                if (result.Success)
                    downloaded++;
                else
                    failures.Add(item.Name);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                WinProvisionLog.Warning(exception, "Collections.DownloadInstaller");
                failures.Add(item.Name);
            }
        }

        StatusText = failures.Count == 0
            ? $"{downloaded} instalador(es) baixado(s) em {folder}."
            : $"{downloaded} baixado(s); falha em {failures.Count}: {string.Join(", ", failures)}.";
    }

    public void CopyId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        bool copied = _clipboard?.TrySetText(id) == true;
        StatusText = copied ? $"ID “{id}” copiado para a área de transferência." : "Não foi possível copiar o ID do aplicativo.";
    }

    private async Task ImportAsync()
    {
        HasError = false;
        try
        {
            string? path = await _presentation.PickOpenAsync();
            if (path is null)
                return;
            string json = await File.ReadAllTextAsync(path);
            IReadOnlyList<ProfileManifest> manifests = ParseProfiles(json, Path.GetFileNameWithoutExtension(path));
            foreach (ProfileManifest manifest in manifests)
                SelectedProfile = AddProfile(manifest);
            Save();
            StatusText = $"{manifests.Count} coleção(ões) importada(s).";
            _notifications?.ShowCollectionResult("Coleção atualizada", StatusText);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    public static IReadOnlyList<ProfileManifest> ParseProfiles(string json, string fallback)
    {
        return ProfileDocumentService.Parse(json, fallback).Tabs;
    }

    private async Task ExportAsync()
    {
        if (SelectedProfile is not { } profile)
            return;
        try
        {
            string? path = await _presentation.PickSaveAsync(profile.Title, ".json");
            if (path is null)
                return;
            await AtomicFile.WriteAllTextAsync(path, ProfileDocumentService.Serialize(profile.Snapshot()));
            StatusText = $"Coleção exportada para {Path.GetFileName(path)}.";
            _notifications?.ShowCollectionResult("Coleção atualizada", StatusText);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private async Task ExportScriptAsync()
    {
        if (SelectedProfile is not { } profile)
            return;
        try
        {
            string script = BuildInstallScript(profile);
            string? path = await _presentation.PickSaveAsync(profile.Title, ".ps1");
            if (path is null)
                return;
            if (_scriptAnalyzer is not null)
            {
                IReadOnlyList<PowerShellScriptDiagnostic> diagnostics = await _scriptAnalyzer.AnalyzeAsync(script);
                if (diagnostics.Count > 0)
                    throw new InvalidDataException("O script não passou na validação:\n" +
                        string.Join("\n", diagnostics.Take(8).Select(item => $"{item.RuleName}: {item.Message}")));
            }
            await AtomicFile.WriteAllTextAsync(path, script, new UTF8Encoding(true));
            StatusText = $"Script PowerShell salvo em {Path.GetFileName(path)}.";
            _notifications?.ShowCollectionResult("Script validado", StatusText);
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private string BuildInstallScript(CollectionProfile profile)
    {
        var unsupported = profile.Items.Where(item => !item.SupportsInstall
            || _installOptions?.Get(item.Id, item.App.Source) is { CustomArguments: not null and not "" }).ToArray();
        if (unsupported.Length > 0)
            throw new InvalidOperationException("Itens incompatíveis com exportação de script: " + string.Join(", ", unsupported.Select(item => item.Name)));
        return new CollectionScriptService(_installOptions).Build(profile.Items.Select(item => item.App),
            app => profile.Items.First(item => ReferenceEquals(item.App, app)).ImportedReference?.PinnedVersion);
    }

    private async Task ExportOfflineAsync(CancellationToken token)
    {
        if (_offlineExport is null || _installOptions is null)
            throw new InvalidOperationException("A exportação de coleções offline não foi configurada.");
        PackageItemViewModel[] selected = SelectedProfile?.Items.Where(item => item.IsSelected).ToArray() ?? [];
        if (selected.Length == 0)
        {
            StatusText = "Selecione aplicativos para criar o ZIP.";
            return;
        }

        string? destination = await _presentation.PickSaveAsync($"{SelectedProfile!.Title}.zip", ".zip");
        if (destination is null)
            return;

        IsBusy = true;
        IsExportingOffline = true;
        HasError = false;
        OfflineProgress = 0;
        OfflineProgressIndeterminate = true;
        OfflineStatusText = "Validando o script de instalação…";
        try
        {
            IReadOnlyList<PowerShellScriptDiagnostic> diagnostics =
                _scriptAnalyzer is null ? [] : await _scriptAnalyzer.AnalyzeAsync(OfflineCollectionExportService.GetInstallationScript(), token);
            if (diagnostics.Count > 0)
                throw new InvalidDataException("O script do ZIP não passou na validação:\n" +
                    string.Join("\n", diagnostics.Take(8).Select(item => $"{item.RuleName}: {item.Message}")));

            OfflineCollectionRequest[] requests = selected.Select(item =>
                new OfflineCollectionRequest(item.Id, item.Name, item.App.Source,
                    MergeOptions(_installOptions.Get(item.Id, item.App.Source), new PackageInstallOptions(),
                        item.ImportedReference?.PinnedVersion),
                    item.App.Office is not null,
                    item.App.Office)).ToArray();
            var progress = new Progress<OfflineCollectionProgress>(value =>
            {
                OfflineStatusText = value.Message;
                OfflineProgressIndeterminate = value.Percentage is null;
                if (value.Percentage is { } percentage)
                    OfflineProgress = percentage;
            });
            OfflineCollectionResult result = await _offlineExport.ExportAsync(SelectedProfile.Title, requests, destination, progress, token);
            int packageCount = result.Packages.Count + (result.Office?.ProductIds.Count ?? 0);
            StatusText = $"ZIP criado com {packageCount} aplicativo(s); {result.Skipped.Count} não incluído(s). Leia Readme.txt dentro do arquivo.";
            if (result.Skipped.Count > 0)
            {
                ErrorTitle = "Alguns aplicativos não foram incluídos no ZIP";
                ErrorMessage = string.Join(Environment.NewLine, result.Skipped.Select(item => $"{item.Name}: {item.Reason}"));
                HasError = true;
            }
        }
        catch (OperationCanceledException exception) when (token.IsCancellationRequested || exception is OfficeElevationCanceledException)
        {
            StatusText = "Exportação cancelada. Nenhum ZIP incompleto foi publicado.";
        }
        catch (Exception exception)
        {
            ReportError(exception);
            StatusText = "Não foi possível criar o ZIP da coleção.";
        }
        finally
        {
            IsExportingOffline = false;
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (SelectedProfile is not { } profile || !CanEditProfile)
            return;
        try
        {
            if (!await _presentation.ConfirmAsync("Excluir coleção", $"Excluir “{profile.Title}”? Os aplicativos instalados permanecerão no Windows.", "Excluir"))
                return;
            int index = Profiles.IndexOf(profile);
            profile.PropertyChanged -= Profile_PropertyChanged;
            profile.Items.CollectionChanged -= ProfileItems_CollectionChanged;
            foreach (PackageItemViewModel item in profile.Items)
                item.PropertyChanged -= Item_PropertyChanged;
            foreach (PackageItemViewModel item in profile.Items) _metricAttempts.Remove(item);
            Profiles.Remove(profile);
            if (Profiles.Count == 0)
                AddProfile(new ProfileManifest { Name = "Coleção padrão" }, isDefault: true);
            SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
            Save();
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private async Task ClearAsync()
    {
        if (SelectedProfile is not { } profile || profile.Items.Count == 0)
            return;
        try
        {
            if (!await _presentation.ConfirmAsync("Limpar coleção?", $"Os {profile.Items.Count} aplicativos de “{profile.Title}” serão removidos.", "Limpar coleção"))
                return;
            profile.Items.Clear();
            StatusText = "Coleção limpa.";
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }

    private void LoadLocal()
    {
        _suppressSave = true;
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                _backup = WinProvision.Core.Services.AotJson.Deserialize<ProfileBackupSet>(json, WinProvisionJsonOptions.Profile) ?? new();
                    foreach (ProfileManifest manifest in ParseProfiles(json, "Coleção"))
                        AddProfile(manifest, isDefault: Profiles.Count == 0 && manifest.Name?.Equals("Coleção padrão", StringComparison.OrdinalIgnoreCase) == true);
            }
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            if (Profiles.Count == 0)
                AddProfile(new ProfileManifest { Name = "Coleção padrão" }, isDefault: true);
            SelectedProfile = Profiles[0];
            _suppressSave = false;
            UpdateFilteredItems();
        }
    }

    private void Save()
    {
        if (_suppressSave)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _backup.Tabs = Profiles.Select(profile => profile.Snapshot()).ToList();
            AtomicFile.WriteAllText(_path, WinProvision.Core.Services.AotJson.Serialize(_backup, WinProvisionJsonOptions.Profile));
            BackupSnapshotChanged?.Invoke(CreateBackupSnapshot());
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
    }
}
