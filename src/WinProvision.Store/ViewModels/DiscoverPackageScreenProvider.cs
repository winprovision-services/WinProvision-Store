using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

/// <summary>Projects Discover search results into the shared package screen while the storefront remains independent.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DiscoverPackageScreenProvider : IPackageScreenProvider, IDisposable
{
    private static readonly PackageScreenFilterValue[] SearchModes =
    [
        new("name", "Nome"), new("id", "Identificador"), new("both", "Nome e identificador"), new("exact", "Correspondência exata")
    ];
    private static readonly PackageScreenFilterValue[] Categories =
    [
        new("all", "Todas"), new("productivity", "Produtividade"), new("development", "Desenvolvimento"),
        new("utilities", "Utilitários"), new("multimedia", "Multimídia"), new("security", "Segurança"),
        new("gaming", "Jogos"), new("browsers", "Navegadores")
    ];

    private readonly StorefrontViewModel _storefront;
    private readonly PackageScreenSourceRegistry _sources;
    private readonly ApplicationPreferencesService _preferences;
    private readonly PackageVisualPreferencesService _visuals;
    private readonly IPresentationService _presentation;
    private readonly IClipboardService _clipboard;
    private readonly CollectionsViewModel _collections;
    private readonly IPackageBackend _backend;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, PackageScreenRow> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppEntry> _appsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<AppEntry> _subscribedApps = [];
    private bool _syncingSelection;
    private bool _syncQueued;
    private bool _disposed;
    private string _viewMode = "list";
    private string _searchMode = "both";
    private string _category = "all";
    private string _sortBy = "name";
    private bool _sortAscending = true;
    private bool _isFilterOpen = true;
    private string _searchText = string.Empty;
    private readonly ICommand _searchCommand;

    public DiscoverPackageScreenProvider(
        StorefrontViewModel storefront,
        PackageScreenSourceRegistry sources,
        ApplicationPreferencesService preferences,
        IPresentationService presentation,
        IClipboardService clipboard,
        CollectionsViewModel collections,
        IUiDispatcher dispatcher, IPackageBackend backend,
        PackageVisualPreferencesService? visuals = null)
    {
        _storefront = storefront;
        _sources = sources;
        _preferences = preferences;
        _visuals = visuals ?? new PackageVisualPreferencesService(preferences);
        _presentation = presentation;
        _clipboard = clipboard;
        _collections = collections;
        _dispatcher = dispatcher;
        _backend = backend;
        Definition = PackageScreenCatalog.Default.Get("discover");
        _viewMode = NormalizeViewMode(Definition.Defaults.View);
        _sortBy = Definition.Defaults.Sort.Column;
        _sortAscending = Definition.Defaults.Sort.Direction == "ascending";
        _storefront.SelectSort((int)ToCatalogSort(_sortBy), _sortAscending);
        _searchText = storefront.SearchText;
        RefreshCommand = new AsyncRelayCommand(SubmitSearchAsync);
        ToggleSelectAllCommand = new RelayCommand(() => SetAllVisibleSelected(MasterSelection != true));
        SetSortCommand = new RelayCommand<object?>(SetSort);
        SetViewModeCommand = new RelayCommand<object?>(SetViewMode);
        SetFilterCommand = new RelayCommand<object?>(SetFilter);
        ToggleRowSelectionCommand = new RelayCommand<object?>(ToggleRowSelection);
        _searchCommand = new AsyncRelayCommand<string?>(query => _storefront.SubmitSearchAsync(query ?? SearchText));
        BuildActions();
        BuildFilters();
        _storefront.Apps.CollectionChanged += Apps_CollectionChanged;
        _storefront.PropertyChanged += Storefront_PropertyChanged;
        _preferences.PackageManagerEnabledChanged += PackageManagerEnabledChanged;
        _visuals.PackageIconsChanged += PackageIconsChanged;
        SyncRows();
    }

    public PackageScreenDefinition Definition { get; }
    public IPresentationService PresentationService => _presentation;
    public ObservableCollection<PackageScreenRow> Rows { get; } = [];
    public ObservableCollection<PackageScreenFilterOption> Filters { get; } = [];
    public ObservableCollection<PackageScreenAction> Actions { get; } = [];
    public ObservableCollection<PackageScreenAction> ContextActions { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string, PackageScreenRow?>? PresentationActionRequested;

    public string Subtitle => PackageScreenCatalog.Default.FormatSubtitle(Definition, Rows.Count, Rows.Count, SelectedCount);
    public string StatusText => _storefront.StatusText;
    public string SearchText
    {
        get => _storefront.SearchText;
        set { if (_storefront.SearchText == value) return; _storefront.SearchText = value; _searchText = value; OnPropertyChanged(nameof(SearchText)); }
    }
    public string SearchPlaceholder => PackageScreenCatalog.Default.GetText("search.placeholder", "Pesquisar aplicativos...");
    public string ViewMode { get => _viewMode; set { if (_viewMode == value) return; _viewMode = NormalizeViewMode(value); OnPropertyChanged(nameof(ViewMode)); } }
    public string SortBy { get => _sortBy; set => SetSort(value); }
    public bool SortAscending { get => _sortAscending; set { if (_sortAscending == value) return; _sortAscending = value; ApplySort(); OnPropertyChanged(nameof(SortAscending)); } }
    public bool IsLoading => _storefront.StatusText.StartsWith("Pesquisando", StringComparison.OrdinalIgnoreCase)
        || _storefront.StatusText.StartsWith("Atualizando resultados", StringComparison.OrdinalIgnoreCase);
    public string? ErrorText => null;
    public bool IsFilterOpen { get => _isFilterOpen; set { if (_isFilterOpen == value) return; _isFilterOpen = value; OnPropertyChanged(nameof(IsFilterOpen)); } }
    public bool HasActiveFilters => !_storefront.IncludeWinGet || !_storefront.IncludeMicrosoftStore || !_storefront.IncludeChocolatey
        || _category != "all" || _searchMode != "both";
    public int SelectedCount => Rows.Count(row => row.IsSelected);
    public bool? MasterSelection
    {
        get
        {
            if (Rows.Count == 0 || !Rows.Any(row => row.IsSelected)) return false;
            return Rows.All(row => row.IsSelected) ? true : null;
        }
    }
    public string EmptyTitle => _storefront.HasSearched ? "Nenhum aplicativo encontrado" : "Pesquise para encontrar aplicativos";
    public string EmptyMessage => _storefront.HasSearched
        ? "Tente pesquisar pelo nome, publicador ou identificador do pacote."
        : "Digite um nome, publicador ou identificador para pesquisar.";
    public IAsyncRelayCommand RefreshCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }
    public ICommand SetSortCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ToggleRowSelectionCommand { get; }

    private void BuildActions()
    {
        Actions.Add(CreateAction("install", new AsyncRelayCommand<PackageScreenRow?>(InstallActionAsync, row => row is null ? _storefront.HasSelectedApps : Resolve(row) is { IsSelf: false, IsInstalling: false })));
        Actions.Add(CreateAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync)));
        Actions.Add(CreateAction("manualInstall", new RelayCommand<PackageScreenRow?>(ManualInstallAction)));
        Actions.Add(CreateAction("manageOptions", new RelayCommand<PackageScreenRow?>(ManageOptionsAction)));
        Actions.Add(CreateAction("download", new AsyncRelayCommand<PackageScreenRow?>(DownloadActionAsync)));


        Actions.Add(CreateAction("addToCollection", new RelayCommand<PackageScreenRow?>(AddToCollectionAction, row => row is null ? _storefront.HasSelectedApps : Resolve(row) is { IsSelf: false })));
        Actions.Add(CreateAction("selectAll", new RelayCommand(() => SetAllVisibleSelected(true))));
        Actions.Add(CreateAction("clearSelection", new RelayCommand(() => SetAllVisibleSelected(false))));

        ContextActions.Add(CreateAction("install", new AsyncRelayCommand<PackageScreenRow?>(InstallActionAsync, row => Resolve(row) is { IsSelf: false, IsInstalling: false }), "row"));
        ContextActions.Add(CreateAction("installAdmin", new AsyncRelayCommand<PackageScreenRow?>(InstallAdminAsync), "row"));
        ContextActions.Add(CreateAction("installInteractive", new AsyncRelayCommand<PackageScreenRow?>(InstallInteractiveAsync), "row"));
        ContextActions.Add(CreateAction("updateIgnoreHash", new AsyncRelayCommand<PackageScreenRow?>(InstallIgnoringHashAsync), "row"));
        ContextActions.Add(CreateAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync), "row"));
        ContextActions.Add(CreateAction("manualInstall", new RelayCommand<PackageScreenRow?>(ManualInstallAction), "row"));
        ContextActions.Add(CreateAction("manageOptions", new RelayCommand<PackageScreenRow?>(ManageOptionsAction), "row"));
        ContextActions.Add(CreateAction("download", new AsyncRelayCommand<PackageScreenRow?>(DownloadActionAsync), "row"));
        ContextActions.Add(CreateAction("addToCollection", new RelayCommand<PackageScreenRow?>(AddToCollectionAction), "row"));
        ContextActions.Add(CreateAction("copyId", new RelayCommand<PackageScreenRow?>(CopyId), "row"));
        ContextActions.Add(CreateAction("copyLink", new RelayCommand<PackageScreenRow?>(CopyLink), "row"));
    }

    private void BuildFilters()
    {
        Filters.Clear();
        IReadOnlyList<PackageScreenSource> activeSources = _sources.GetSources();
        foreach (PackageScreenSource source in activeSources)
        {
            string id = source.Id.ToLowerInvariant();
            bool? selected = id switch
            {
                "winget" => _storefront.IncludeWinGet,
                "msstore" => _storefront.IncludeMicrosoftStore,
                "chocolatey" => _storefront.IncludeChocolatey,
                _ => true
            };
            Filters.Add(new($"source:{id}", source.Name, "source", source.IconGlyph, selected, command: SetFilterCommand, iconUri: source.IconUri));
        }
        foreach (PackageScreenSidebarSectionDefinition section in Definition.Sidebar.Sections)
        {
            if (section.Id.Equals("search", StringComparison.OrdinalIgnoreCase))
            {
                string title = section.Items.FirstOrDefault(item => item.Id.Equals("searchMode", StringComparison.OrdinalIgnoreCase))?.Title ?? section.Title;
                Filters.Add(new("searchMode", PackageScreenCatalog.Default.GetText(title, title), "choice", selectedValue: _searchMode,
                    values: section.Modes.Select(mode => new PackageScreenFilterValue(mode, PackageScreenCatalog.Default.GetText($"search.mode.{mode}", mode))).ToArray(), command: SetFilterCommand));
            }
            else if (section.Id.Equals("categories", StringComparison.OrdinalIgnoreCase))
                Filters.Add(new("category", "Categoria", "choice", selectedValue: _category, values: Categories, command: SetFilterCommand));
        }
    }

    private static PackageScreenAction CreateAction(string id, ICommand command, string kind = "action")
    {
        PackageScreenActionDefinition definition = PackageScreenCatalog.Default.GetAction(id);
        return new(id, definition.Title, PackageScreenCatalog.Default.ResolveIcon(definition.Icon ?? id), command, null, kind);
    }

    private async Task SubmitSearchAsync() => await _storefront.SubmitSearchAsync(SearchText);

    public void CancelPendingSearch() => _storefront.CancelPendingSearch();

    private async Task InstallActionAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is { IsSelf: false } app)
            await _storefront.InstallAsync(app);
        else if (row is null)
        {
            using IDisposable? batch = _backend.BeginBatch();
            await Task.WhenAll(_storefront.GetSelectedApps().Where(app => !app.IsSelf).Select(app => _storefront.InstallAsync(app)));
        }
    }
    private async Task InstallAdminAsync(PackageScreenRow? row) { if (Resolve(row) is { IsSelf: false } app) await _storefront.InstallAsync(app, new PackageInstallOptions(Scope: "machine", RequiresElevation: true)); }
    private async Task InstallInteractiveAsync(PackageScreenRow? row) { if (Resolve(row) is { IsSelf: false } app) await _storefront.InstallAsync(app, new PackageInstallOptions(Interactive: true)); }
    private async Task InstallIgnoringHashAsync(PackageScreenRow? row) { if (Resolve(row) is { IsSelf: false } app) await _storefront.InstallAsync(app, new PackageInstallOptions(IgnoreSecurityHash: true)); }
    private async Task ShowDetailsAsync(PackageScreenRow? row)
    {
        AppEntry? app = Resolve(row) ?? _storefront.GetSelectedApps().FirstOrDefault();
        if (app is not null) await _presentation.ShowDetailsAsync(app);
    }
    private void ManualInstallAction(PackageScreenRow? row) => PresentationActionRequested?.Invoke("manualInstall", row);
    private void ManageOptionsAction(PackageScreenRow? row) => PresentationActionRequested?.Invoke("manageOptions", row);
    private async Task DownloadActionAsync(PackageScreenRow? row)
    {
        AppEntry[] apps = Resolve(row) is { } app ? [app] : _storefront.GetSelectedApps().ToArray();
        if (apps.Length == 0) return;
        string? folder = await _presentation.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder)) return;
        foreach (AppEntry package in apps)
        {
            try
            {
                var result = await _backend.DownloadAsync(package.Id, package.Source, folder, CancellationToken.None, package.Version);
                _storefront.StatusText = result.Success ? $"Instalador de {package.Name} baixado." : $"Não foi possível baixar {package.Name}.";
            }
            catch (Exception exception) { _storefront.StatusText = $"Não foi possível baixar {package.Name}: {exception.Message}"; }
        }
    }
    private void AddToCollectionAction(PackageScreenRow? row)
    {
        IEnumerable<AppEntry> apps = Resolve(row) is { } app ? new[] { app } : _storefront.GetSelectedApps();
        foreach (AppEntry package in apps.Where(package => !package.IsSelf)) _collections.Add(package);
    }
    private void CopyId(PackageScreenRow? row)
    {
        if (Resolve(row) is { } app) _storefront.StatusText = _clipboard.TrySetText(app.Id) ? $"ID '{app.Id}' copiado para a área de transferência." : "Não foi possível copiar o ID do pacote.";
    }
    private void CopyLink(PackageScreenRow? row)
    {
        if (Resolve(row) is not { } app) return;
        string? link = app.PackageUrl ?? app.Homepage;
        _storefront.StatusText = Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http"
            ? _clipboard.TrySetText(uri.AbsoluteUri) ? "Link do pacote copiado para a área de transferência." : "Não foi possível copiar o link do pacote."
            : "Este pacote não possui um link público disponível.";
    }

    private void SetFilter(object? value)
    {
        if (value is not PackageScreenFilterOption option) return;
        switch (option.Id)
        {
            case "source:winget": _storefront.IncludeWinGet = option.IsChecked == true; break;
            case "source:msstore": _storefront.IncludeMicrosoftStore = option.IsChecked == true; break;
            case "source:chocolatey": _storefront.IncludeChocolatey = option.IsChecked == true; break;
            case "searchMode": _searchMode = option.SelectedValue ?? "both"; SyncRows(); break;
            case "category": _category = option.SelectedValue ?? "all"; _storefront.SelectCategory(_category); break;
        }
        RefreshFilters();
    }
    private void SetAllVisibleSelected(bool selected)
    {
        foreach (PackageScreenRow row in Rows)
            if (_appsByKey.TryGetValue(row.Key, out AppEntry? app) && !app.IsSelf)
            {
                _storefront.SetAppSelected(app, selected);
                SetRowSelected(row, selected);
            }
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
    }
    private void ToggleRowSelection(object? value)
    {
        if (value is PackageScreenRow row && _appsByKey.TryGetValue(row.Key, out AppEntry? app) && !app.IsSelf)
        {
            bool selected = !_storefront.IsSelected(app);
            _storefront.SetAppSelected(app, selected);
            SetRowSelected(row, selected);
        }
    }
    private void SetRowSelected(PackageScreenRow row, bool selected)
    {
        _syncingSelection = true; row.IsSelected = selected; _syncingSelection = false;
    }

    private void SetSort(object? value)
    {
        string column = value?.ToString()?.ToLowerInvariant() ?? "name";
        _sortAscending = string.Equals(_sortBy, column, StringComparison.OrdinalIgnoreCase) ? !_sortAscending : true;
        _sortBy = column;
        ApplySort();
        OnPropertyChanged(nameof(SortBy)); OnPropertyChanged(nameof(SortAscending));
    }
    private void ApplySort()
    {
        _storefront.SelectSort((int)ToCatalogSort(_sortBy), _sortAscending);
        SyncRows();
    }
    private static StorefrontViewModel.CatalogSortMode ToCatalogSort(string column) => column switch
    {
        "relevance" => StorefrontViewModel.CatalogSortMode.Relevance,
        "popularity" => StorefrontViewModel.CatalogSortMode.Popularity,
        "id" => StorefrontViewModel.CatalogSortMode.Id,
        "name" => StorefrontViewModel.CatalogSortMode.Name,
        _ => StorefrontViewModel.CatalogSortMode.Popularity
    };
    private void SetViewMode(object? value) => ViewMode = value?.ToString() ?? "list";
    private static string NormalizeViewMode(string value) => value switch { "icons" or "details" or "detailed" => "details", "grid" => "grid", _ => "list" };
    private AppEntry? Resolve(PackageScreenRow? row) => row is not null && _appsByKey.TryGetValue(row.Key, out AppEntry? app) ? app : null;

    private void Apps_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleSyncRows();
    private void ScheduleSyncRows()
    {
        if (_syncQueued || _disposed) return;
        _syncQueued = true;
        _dispatcher.Post(() => { _syncQueued = false; SyncRows(); });
    }
    private void Storefront_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StorefrontViewModel.StatusText): OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsLoading)); break;
            case nameof(StorefrontViewModel.SearchText):
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(EmptyTitle));
                OnPropertyChanged(nameof(EmptyMessage));
                break;
            case nameof(StorefrontViewModel.HasSearched):
                OnPropertyChanged(nameof(EmptyTitle));
                OnPropertyChanged(nameof(EmptyMessage));
                break;
            case nameof(StorefrontViewModel.SelectedAppCount):
            case nameof(StorefrontViewModel.HasSelectedApps):
                SyncRows(); break;
            case nameof(StorefrontViewModel.IncludeWinGet):
            case nameof(StorefrontViewModel.IncludeMicrosoftStore):
            case nameof(StorefrontViewModel.IncludeChocolatey):
                RefreshFilters(); break;
        }
        OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
        OnPropertyChanged(nameof(HasActiveFilters));
        foreach (PackageScreenAction action in Actions.Concat(ContextActions))
            if (action.Command is IRelayCommand command) command.NotifyCanExecuteChanged();
    }
    private void PackageManagerEnabledChanged(string id, bool enabled) => BuildFilters();
    private void PackageIconsChanged(bool _) => SyncRows();

    private void SyncRows()
    {
        AppEntry[] visibleApps = GetFilteredApps().ToArray();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _appsByKey.Clear();
        foreach (AppEntry app in visibleApps)
        {
            string key = Key(app); keep.Add(key); _appsByKey[key] = app;
            if (_subscribedApps.Add(app)) app.PropertyChanged += App_PropertyChanged;
            if (!_rowsByKey.TryGetValue(key, out PackageScreenRow? row))
            {
                row = new PackageScreenRow { Key = key, Payload = app };
                row.PropertyChanged += Row_PropertyChanged;
                _rowsByKey.Add(key, row); Rows.Add(row);
            }
            UpdateRow(row, app);
        }
        foreach (string key in _rowsByKey.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            PackageScreenRow row = _rowsByKey[key]; row.PropertyChanged -= Row_PropertyChanged;
            Rows.Remove(row); _rowsByKey.Remove(key);
        }
        foreach (AppEntry app in _subscribedApps.Where(app => !visibleApps.Contains(app)).ToArray())
        {
            app.PropertyChanged -= App_PropertyChanged; _subscribedApps.Remove(app);
        }
        for (int index = 0; index < visibleApps.Length; index++)
        {
            PackageScreenRow row = _rowsByKey[Key(visibleApps[index])];
            int current = Rows.IndexOf(row);
            if (current != index) Rows.Move(current, index);
        }
        OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
    }
    private IEnumerable<AppEntry> GetFilteredApps()
    {
        IEnumerable<AppEntry> apps = _storefront.Apps;
        string query = SearchText.Trim();
        if (query.Length == 0 || _searchMode == "both") return apps;
        return apps.Where(app => _searchMode switch
        {
            "name" => app.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
            "id" => app.Id.Contains(query, StringComparison.OrdinalIgnoreCase),
            "exact" => app.Name.Equals(query, StringComparison.OrdinalIgnoreCase) || app.Id.Equals(query, StringComparison.OrdinalIgnoreCase),
            _ => true
        });
    }
    private void UpdateRow(PackageScreenRow row, AppEntry app)
    {
        _syncingSelection = true;
        row.Name = app.Name; row.Id = app.Id; row.Version = app.Version; row.Source = app.CatalogSourceLabel;
        PackageImagePresentation icon = _visuals.GetPackageIcon(app.IconUrl);
        row.ShowPackageIcon = icon.IsVisible; row.IconUri = icon.SourceUrl;
        row.IsSelected = _storefront.IsSelected(app); row.IsWorking = app.IsInstalling;
        row.IsInstalled = app.IsInstalled; row.CanSelect = !app.IsSelf;
        row.Status = app.IsInstalling ? "Instalando" : app.IsInstalled ? "Instalado" : app.IsInstalledUnconfirmed ? "Instalação não confirmada" : "Disponível";
        string sourceId = app.Source.ToLowerInvariant();
        PackageScreenIconSources sourceIcons = _sources.GetIconSources(sourceId);
        row.SourceIconUri = sourceIcons.Light; row.SourceIconUriDark = sourceIcons.Dark;
        row.SourceGlyph = _sources.GetSources().FirstOrDefault(source => source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))?.IconGlyph
            ?? PackageScreenCatalog.Default.ResolveIcon(sourceId == "msstore" ? "msstore" : sourceId == "chocolatey" ? "chocolatey" : "winget");
        row.Values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = app.Name, ["publisher"] = app.Publisher, ["id"] = app.Id,
            ["version"] = app.Version, ["source"] = app.CatalogSourceLabel, ["status"] = row.Status
        };
        _syncingSelection = false;
    }
    private static string Key(AppEntry app) => $"{app.Source}|{app.Id}";
    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AppEntry app && _rowsByKey.TryGetValue(Key(app), out PackageScreenRow? row)) UpdateRow(row, app);
    }
    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingSelection || e.PropertyName != nameof(PackageScreenRow.IsSelected) || sender is not PackageScreenRow row) return;
        if (_appsByKey.TryGetValue(row.Key, out AppEntry? app)) _storefront.SetAppSelected(app, row.IsSelected);
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
    }
    private void RefreshFilters()
    {
        foreach (PackageScreenFilterOption option in Filters)
        {
            option.IsChecked = option.Id switch
            {
                "source:winget" => _storefront.IncludeWinGet,
                "source:msstore" => _storefront.IncludeMicrosoftStore,
                "source:chocolatey" => _storefront.IncludeChocolatey,
                _ => option.IsChecked
            };
            if (option.Id == "searchMode") option.SelectedValue = _searchMode;
            if (option.Id == "category") option.SelectedValue = _category;
        }
        OnPropertyChanged(nameof(HasActiveFilters));
    }
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));

    public ICommand? GetActionCommand(string actionId, PackageScreenRow? row = null) => actionId.Equals("search", StringComparison.OrdinalIgnoreCase)
        ? _searchCommand
        : Actions.Concat(ContextActions).FirstOrDefault(action => action.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase))?.Command;
    public object? GetActionParameter(string actionId, PackageScreenRow? row = null)
    {
        PackageScreenAction? action = Actions.Concat(ContextActions).FirstOrDefault(item => item.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
        return row ?? action?.Parameter ?? (actionId.Equals("search", StringComparison.OrdinalIgnoreCase) ? SearchText : null);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _storefront.Apps.CollectionChanged -= Apps_CollectionChanged;
        _storefront.PropertyChanged -= Storefront_PropertyChanged;
        _preferences.PackageManagerEnabledChanged -= PackageManagerEnabledChanged;
        _visuals.PackageIconsChanged -= PackageIconsChanged;
        foreach (AppEntry app in _subscribedApps) app.PropertyChanged -= App_PropertyChanged;
        foreach (PackageScreenRow row in Rows) row.PropertyChanged -= Row_PropertyChanged;
    }
}
