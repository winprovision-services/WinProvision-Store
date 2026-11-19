using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

/// <summary>Adapta o estado e as operações existentes de Atualizações para a tela compartilhada de pacotes.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class UpdatesPackageScreenProvider : IPackageScreenProvider, IDisposable
{
    private readonly UpdatesViewModel _viewModel;
    private readonly PackageScreenSourceRegistry _sources;
    private readonly ApplicationPreferencesService _preferences;
    private readonly PackageVisualPreferencesService _visuals;
    private readonly IPresentationService _presentation;
    private readonly IClipboardService _clipboard;
    private readonly Dictionary<string, PackageScreenRow> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UpgradablePackage> _packagesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<UpgradablePackage> _subscribedPackages = [];
    private readonly Dictionary<string, PackageScreenAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageScreenAction> _contextActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _initialViewMode;
    private bool _viewModeInitialized;
    private bool _isFilterOpen = true;
    private bool _syncingSelection;
    private bool _disposed;

    public UpdatesPackageScreenProvider(UpdatesViewModel viewModel, PackageScreenSourceRegistry sources,
        ApplicationPreferencesService preferences, IPresentationService presentation, IClipboardService clipboard,
        PackageVisualPreferencesService? visuals = null)
    {
        _viewModel = viewModel;
        _sources = sources;
        _preferences = preferences;
        _visuals = visuals ?? new PackageVisualPreferencesService(preferences);
        _presentation = presentation;
        _clipboard = clipboard;
        Definition = PackageScreenCatalog.Default.Get("updates");
        _initialViewMode = Definition.Defaults.View;
        SetSort(Definition.Defaults.Sort.Column);
        viewModel.SetSort(viewModel.CurrentSort, Definition.Defaults.Sort.Direction == "ascending");
        RefreshCommand = viewModel.ScanCommand;
        ToggleSelectAllCommand = new RelayCommand(() => viewModel.ToggleAll());
        SetSortCommand = new RelayCommand<object?>(SetSort);
        SetViewModeCommand = new RelayCommand<object?>(SetViewMode);
        SetFilterCommand = new RelayCommand<object?>(SetFilter);
        ToggleRowSelectionCommand = new RelayCommand<object?>(ToggleRowSelection);
        BuildActions();
        BuildFilters();
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.Changed += ViewModel_Changed;
        viewModel.Packages.CollectionChanged += Packages_CollectionChanged;
        preferences.PackageManagerEnabledChanged += PackageManagerEnabledChanged;
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
    public event Action<string>? PresentationActionRequested;

    public string Subtitle => PackageScreenCatalog.Default.FormatSubtitle(Definition, _viewModel.RawPackageCount,
        _viewModel.Packages.Count, SelectedCount);
    public string StatusText => _viewModel.StatusText;
    public string SearchText { get => _viewModel.SearchText; set { if (_viewModel.SearchText == value) return; _viewModel.SearchText = value; _viewModel.ApplyFilters(); } }
    public string SearchPlaceholder => PackageScreenCatalog.Default.GetText("search.placeholder", "Pesquisar nome ou ID…");
    public string ViewMode
    {
        get => _viewModeInitialized ? _viewMode : _initialViewMode;
        set => SetViewMode(value);
    }
    private string _viewMode = "list";
    public string SortBy { get => SortId(_viewModel.CurrentSort); set => SetSort(value); }
    public bool SortAscending { get => _viewModel.SortAscending; set => _viewModel.SetSort(_viewModel.CurrentSort, value); }
    // Once one source has answered, keep its rows visible while a slower source completes.
    public bool IsLoading => _viewModel.IsInitialLoading;
    public string? ErrorText => _viewModel.HasError ? _viewModel.ErrorMessage : null;
    public bool IsFilterOpen { get => _isFilterOpen; set { if (_isFilterOpen == value) return; _isFilterOpen = value; OnPropertyChanged(nameof(IsFilterOpen)); } }
    public bool HasActiveFilters => !_viewModel.FilterWinget || !_viewModel.FilterWinProvision || !_viewModel.FilterMsStore
        || !_viewModel.FilterChocolatey || !_viewModel.HideSameVersion
        || !string.IsNullOrWhiteSpace(_viewModel.SearchText) || _viewModel.ExactSearch || _viewModel.SearchName || _viewModel.SearchId;
    public int SelectedCount => _viewModel.Packages.Count(package => package.IsSelectedForUpdate);
    public bool? MasterSelection => _viewModel.MasterSelection;
    public string EmptyTitle => _viewModel.EmptyStateTitle;
    public string EmptyMessage => _viewModel.EmptyStateText;
    public IAsyncRelayCommand RefreshCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }
    public ICommand SetSortCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ToggleRowSelectionCommand { get; }

    private void BuildActions()
    {
        AddAction("update", new AsyncRelayCommand(UpdateSelectedAsync, () => _viewModel.CanUpdate));
        AddAction("updateAdmin", new AsyncRelayCommand(token => UpdateSelectedVariantAsync(token, elevated: true), () => _viewModel.CanUpdate));
        AddAction("updateInteractive", new AsyncRelayCommand(token => UpdateSelectedVariantAsync(token, interactive: true), () => _viewModel.CanUpdate));
        AddAction("updateAll", new AsyncRelayCommand(token => UpdateAllAsync(token), () => !_viewModel.IsBusy && _viewModel.Packages.Count > 0));
        AddAction("updateIgnoreHash", new AsyncRelayCommand<PackageScreenRow?>(UpdateIgnoreHashAsync,
            row => row is not null && IsWinget(row) && Resolve(row) is not null));
        AddAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync));
        AddAction("ignoreSelection", new RelayCommand(IgnoreSelected, () => SelectedCount > 0));
        AddAction("showIgnored", new RelayCommand(() => PresentationActionRequested?.Invoke("showIgnored")));
        AddAction("manualUpdate", new RelayCommand(() => PresentationActionRequested?.Invoke("manualUpdate")));
        AddAction("updateOptions", new RelayCommand(() => PresentationActionRequested?.Invoke("updateOptions")));
        AddAction("exportCsv", new AsyncRelayCommand(ExportCsvAsync, () => _viewModel.Packages.Count > 0));
        AddAction("selectAll", new RelayCommand(() => _viewModel.SelectAll(true), () => _viewModel.CanSelect));
        AddAction("clearSelection", new RelayCommand(() => _viewModel.SelectAll(false), () => SelectedCount > 0));
        AddAction("select-all", new RelayCommand(() => SetAllSources(true)));
        AddAction("clear", new RelayCommand(() => SetAllSources(false)));
        AddAction("cancel", new RelayCommand(CancelOperations, () => _viewModel.IsBusy));
        AddAction("refresh", RefreshCommand);

        AddContextAction("update", new AsyncRelayCommand<PackageScreenRow?>(UpdateRowAsync, row => Resolve(row) is not null));
        AddContextAction("updateAdmin", new AsyncRelayCommand<PackageScreenRow?>(UpdateRowAdminAsync, row => Resolve(row) is not null));
        AddContextAction("updateInteractive", new AsyncRelayCommand<PackageScreenRow?>(UpdateRowInteractiveAsync, row => Resolve(row) is not null));
        AddContextAction("updateIgnoreHash", new AsyncRelayCommand<PackageScreenRow?>(UpdateIgnoreHashAsync, row => row is not null && IsWinget(row) && Resolve(row) is not null));
        AddContextAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync), "row");
        AddContextAction("ignoreSelection", new RelayCommand<PackageScreenRow?>(IgnoreRow, row => Resolve(row) is not null), "row");
        AddContextAction("ignore", new RelayCommand<PackageScreenRow?>(IgnoreRow, row => Resolve(row) is not null), "row");
        AddContextAction("copyId", new RelayCommand<PackageScreenRow?>(CopyId, row => Resolve(row) is not null), "row");
    }

    private void BuildFilters()
    {
        Filters.Clear();
        foreach (PackageScreenSidebarSectionDefinition section in Definition.Sidebar.Sections)
        {
            if (section.Id.Equals("sources", StringComparison.OrdinalIgnoreCase))
            {
                foreach (PackageScreenSource source in _sources.GetSources())
                    Filters.Add(new($"source:{source.Id}", source.Name, "source", source.IconGlyph,
                        GetSourceState(source.Id), command: SetFilterCommand, iconUri: source.IconUri));
                continue;
            }
            if (section.Type.Equals("search-options", StringComparison.OrdinalIgnoreCase))
            {
                string title = section.Items.FirstOrDefault(item => item.Id.Equals("searchMode", StringComparison.OrdinalIgnoreCase))?.Title ?? section.Title;
                Filters.Add(new("searchMode", PackageScreenCatalog.Default.GetText(title, title), "choice",
                    selectedValue: SearchMode, values: section.Modes.Select(mode => new PackageScreenFilterValue(mode,
                        PackageScreenCatalog.Default.GetText($"search.mode.{mode}", mode))).ToArray(), command: SetFilterCommand));
                continue;
            }
            foreach (PackageScreenSidebarItemDefinition item in section.Items)
            {
                Filters.Add(new(item.Id, PackageScreenCatalog.Default.GetText(item.Title, item.Title), "toggle", item.Icon,
                    item.Id.Equals("hideSameVersion", StringComparison.OrdinalIgnoreCase) ? _viewModel.HideSameVersion : false,
                    command: SetFilterCommand));
            }
        }
    }

    private void SetFilter(object? parameter)
    {
        if (parameter is not PackageScreenFilterOption option) return;
        bool enabled = option.IsChecked == true;
        if (option.Id.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
        {
            switch (option.Id[7..].ToLowerInvariant())
            {
                case "winget": _viewModel.FilterWinget = enabled; break;
                case "msstore": _viewModel.FilterMsStore = enabled; break;
                case "chocolatey": _viewModel.FilterChocolatey = enabled; break;
                case "winprovision": _viewModel.FilterWinProvision = enabled; break;
            }
        }
        else if (option.Id.Equals("searchMode", StringComparison.OrdinalIgnoreCase) && option.SelectedValue is { } mode)
        {
            _viewModel.ExactSearch = mode == "exact";
            _viewModel.SearchName = mode == "name";
            _viewModel.SearchId = mode == "id";
        }
        else if (option.Id.Equals("hideSameVersion", StringComparison.OrdinalIgnoreCase)) _viewModel.HideSameVersion = enabled;
        _viewModel.ApplyFilters();
        RefreshFilters();
    }

    private void SetAllSources(bool enabled)
    {
        foreach (PackageScreenFilterOption option in Filters.Where(option => option.Id.StartsWith("source:", StringComparison.OrdinalIgnoreCase)))
            option.IsChecked = enabled;
        _viewModel.FilterWinget = enabled;
        _viewModel.FilterWinProvision = enabled;
        _viewModel.FilterMsStore = enabled;
        _viewModel.FilterChocolatey = enabled;
        _viewModel.ApplyFilters();
        RefreshFilters();
    }

    private void RefreshFilters()
    {
        foreach (PackageScreenFilterOption filter in Filters)
        {
            if (filter.Id.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
                filter.IsChecked = GetSourceState(filter.Id[7..]);
            else if (filter.Id == "searchMode") filter.SelectedValue = SearchMode;
            else if (filter.Id == "hideSameVersion") filter.IsChecked = _viewModel.HideSameVersion;
        }
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private void SetSort(object? value)
    {
        string id = value?.ToString()?.ToLowerInvariant() ?? "name";
        _viewModel.SetSort(id switch
        {
            "id" => UpdatesViewModel.SortField.Id,
            "version" => UpdatesViewModel.SortField.Version,
            "newversion" => UpdatesViewModel.SortField.NewVersion,
            "source" => UpdatesViewModel.SortField.Source,
            _ => UpdatesViewModel.SortField.Name
        });
    }

    private void SetViewMode(object? value)
    {
        string mode = value?.ToString()?.ToLowerInvariant() ?? _initialViewMode;
        if (mode is not ("list" or "grid" or "details" or "icons")) return;
        if (_viewModeInitialized && _viewMode == mode) return;
        _viewModeInitialized = true;
        _viewMode = mode == "icons" ? "details" : mode;
        OnPropertyChanged(nameof(ViewMode));
    }

    private void ToggleRowSelection(object? value)
    {
        if (value is PackageScreenRow row && Resolve(row) is { } package)
            package.IsSelectedForUpdate = !package.IsSelectedForUpdate;
    }

    private async Task UpdateSelectedAsync() => await _viewModel.UpdateSelectedCommand.ExecuteAsync(null);
    private Task UpdateSelectedVariantAsync(CancellationToken token, bool elevated = false, bool interactive = false) =>
        _viewModel.RunUpdateOnPackagesAsync(_viewModel.Packages.Where(package => package.IsSelectedForUpdate).ToList(), elevated, interactive, cancellationToken: token);
    private Task UpdateAllAsync(CancellationToken token) => _viewModel.RunUpdateOnPackagesAsync(_viewModel.Packages.ToList(), cancellationToken: token);
    private Task UpdateRowAsync(PackageScreenRow? row, CancellationToken token) =>
        Resolve(row) is { } package ? _viewModel.RunUpdateOnPackagesAsync(new List<UpgradablePackage> { package }, cancellationToken: token) : Task.CompletedTask;
    private Task UpdateRowAdminAsync(PackageScreenRow? row, CancellationToken token) =>
        Resolve(row) is { } package ? _viewModel.RunUpdateOnPackagesAsync(new List<UpgradablePackage> { package }, elevated: true, cancellationToken: token) : Task.CompletedTask;
    private Task UpdateRowInteractiveAsync(PackageScreenRow? row, CancellationToken token) =>
        Resolve(row) is { } package ? _viewModel.RunUpdateOnPackagesAsync(new List<UpgradablePackage> { package }, interactive: true, cancellationToken: token) : Task.CompletedTask;

    private async Task UpdateIgnoreHashAsync(PackageScreenRow? row, CancellationToken token)
    {
        if (Resolve(row) is not { } package) return;
        if (!string.Equals(package.Source, "winget", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.StatusText = "Ignorar a verificação de hash está disponível apenas para pacotes WinGet.";
            return;
        }
        await _viewModel.RunUpdateOnPackagesAsync(new List<UpgradablePackage> { package }, ignoreSecurityHash: true, cancellationToken: token);
    }

    private async Task ShowDetailsAsync(PackageScreenRow? row)
    {
        UpgradablePackage? package = Resolve(row) ?? _viewModel.Packages.FirstOrDefault(item => item.IsSelectedForUpdate) ?? _viewModel.Packages.FirstOrDefault();
        if (package is not null) await _presentation.ShowDetailsAsync(_viewModel.ToAppEntry(package), package.CurrentVersion, package.AvailableVersion);
    }

    private void IgnoreSelected() => _viewModel.Ignore(_viewModel.Packages.Where(package => package.IsSelectedForUpdate).ToArray());
    private void IgnoreRow(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.Ignore(new[] { package }); }

    private async Task ExportCsvAsync()
    {
        try
        {
            if (await _presentation.PickSaveAsync($"Atualizacoes_{DateTime.Now:yyyyMMdd_HHmm}.csv") is { } path)
                _viewModel.ExportCsv(path);
        }
        catch (Exception exception) { _viewModel.StatusText = $"Falha ao exportar: {exception.Message}"; }
    }

    private void CopyId(PackageScreenRow? row)
    {
        if (Resolve(row) is not { } package) return;
        _viewModel.StatusText = _clipboard.TrySetText(package.Id)
            ? $"ID '{package.Id}' copiado para a área de transferência."
            : "Não foi possível copiar o ID.";
    }

    private void CancelOperations()
    {
        _viewModel.ScanCommand.Cancel();
        _viewModel.UpdateSelectedCommand.Cancel();
        _viewModel.CancelBatchCommand.Execute(null);
    }

    private void Packages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncRows();
    private void ViewModel_Changed(object? sender, EventArgs e)
    {
        SyncRows();
        RefreshFilters();
        NotifyStateChanged();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdatesViewModel.FilterWinget) or nameof(UpdatesViewModel.FilterWinProvision)
            or nameof(UpdatesViewModel.FilterMsStore) or nameof(UpdatesViewModel.FilterChocolatey)
            or nameof(UpdatesViewModel.HideSameVersion)
            or nameof(UpdatesViewModel.SearchName) or nameof(UpdatesViewModel.SearchId)
            or nameof(UpdatesViewModel.ExactSearch) or nameof(UpdatesViewModel.SearchText)) RefreshFilters();
        NotifyStateChanged();
    }

    private void PackageManagerEnabledChanged(string id, bool enabled) => BuildFilters();
    private void PackageIconsChanged(bool _) => SyncRows();

    private void NotifyStateChanged()
    {
        foreach (PackageScreenAction action in Actions.Concat(ContextActions))
            if (action.Command is IRelayCommand relay) relay.NotifyCanExecuteChanged();
        foreach (string property in new[] { nameof(Subtitle), nameof(StatusText), nameof(IsLoading), nameof(ErrorText), nameof(HasActiveFilters),
            nameof(SelectedCount), nameof(MasterSelection), nameof(EmptyTitle), nameof(EmptyMessage), nameof(SortBy), nameof(SortAscending) })
            OnPropertyChanged(property);
    }

    private void SyncRows()
    {
        UpgradablePackage[] packages = _viewModel.Packages.ToArray();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _packagesByKey.Clear();
        foreach (UpgradablePackage package in packages)
        {
            string key = PackageKey(package);
            keep.Add(key);
            _packagesByKey[key] = package;
            if (_subscribedPackages.Add(package)) package.PropertyChanged += Package_PropertyChanged;
            if (!_rowsByKey.TryGetValue(key, out PackageScreenRow? row))
            {
                row = new PackageScreenRow { Key = key, Payload = package };
                row.PropertyChanged += Row_PropertyChanged;
                _rowsByKey.Add(key, row);
                Rows.Add(row);
            }
            UpdateRow(row, package);
        }
        foreach (string key in _rowsByKey.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            PackageScreenRow row = _rowsByKey[key];
            row.PropertyChanged -= Row_PropertyChanged;
            Rows.Remove(row);
            _rowsByKey.Remove(key);
        }
        foreach (UpgradablePackage package in _subscribedPackages.Where(package => !packages.Contains(package)).ToArray())
        {
            package.PropertyChanged -= Package_PropertyChanged;
            _subscribedPackages.Remove(package);
        }
        for (int index = 0; index < packages.Length; index++)
        {
            PackageScreenRow row = _rowsByKey[PackageKey(packages[index])];
            int current = Rows.IndexOf(row);
            if (current != index) Rows.Move(current, index);
        }
        NotifyStateChanged();
    }

    private void UpdateRow(PackageScreenRow row, UpgradablePackage package)
    {
        _syncingSelection = true;
        row.Name = package.Name;
        row.Id = package.Id;
        row.Version = package.CurrentVersion;
        row.NewVersion = package.AvailableVersion;
        row.Source = package.SourceLabel;
        PackageImagePresentation icon = _visuals.GetPackageIcon(package.IconUrl);
        row.ShowPackageIcon = icon.IsVisible;
        row.IconUri = icon.SourceUrl;
        string sourceId = SourceIconId(package);
        PackageScreenIconSources sourceIcons = _sources.GetIconSources(sourceId);
        row.SourceIconUri = sourceIcons.Light; row.SourceIconUriDark = sourceIcons.Dark;
        row.SourceGlyph = _sources.GetSources().FirstOrDefault(source => source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))?.IconGlyph
            ?? (sourceId == "winprovision" ? "\uE80F" : sourceId == "other" ? "\uE71D" : string.Empty);
        row.IsSelected = package.IsSelectedForUpdate;
        row.IsWorking = package.IsUpdating;
        row.Status = package.IsUpdating ? "Atualizando" : null;
        row.Values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = package.Name, ["id"] = package.Id, ["version"] = package.CurrentVersion,
            ["newVersion"] = package.AvailableVersion, ["source"] = package.SourceLabel, ["status"] = row.Status
        };
        _syncingSelection = false;
    }

    private void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is UpgradablePackage package && _rowsByKey.TryGetValue(PackageKey(package), out PackageScreenRow? row))
            UpdateRow(row, package);
        if (e.PropertyName == nameof(UpgradablePackage.IsSelectedForUpdate))
            _viewModel.RefreshSelection();
        NotifyStateChanged();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingSelection || e.PropertyName != nameof(PackageScreenRow.IsSelected) || sender is not PackageScreenRow row) return;
        if (Resolve(row) is { } package) package.IsSelectedForUpdate = row.IsSelected;
    }

    private UpgradablePackage? Resolve(PackageScreenRow? row) => row is not null && _packagesByKey.TryGetValue(row.Key, out UpgradablePackage? package) ? package : null;
    private static string PackageKey(UpgradablePackage package) => $"{package.UpdateSource}\u001f{package.Id}";
    private static string SourceIconId(UpgradablePackage package) => package.SourceLabel switch
    {
        "WinGet" => "winget", "Microsoft Store" => "msstore", "Chocolatey" or "Chocolatey: community" => "chocolatey",
        "WinProvision API" => "winprovision", "PC Local" => "other", _ => "other"
    };
    private bool IsWinget(PackageScreenRow? row) => Resolve(row) is { } package
        && string.Equals(package.Source, "winget", StringComparison.OrdinalIgnoreCase);
    private bool? GetSourceState(string id) => id.ToLowerInvariant() switch
    {
        "winget" => _viewModel.FilterWinget,
        "msstore" => _viewModel.FilterMsStore,
        "chocolatey" => _viewModel.FilterChocolatey,
        "winprovision" => _viewModel.FilterWinProvision,
        _ => true
    };
    private string SearchMode => _viewModel.ExactSearch ? "exact" : _viewModel.SearchName ? "name" : _viewModel.SearchId ? "id" : "both";
    private static string SortId(UpdatesViewModel.SortField sort) => sort switch
    {
        UpdatesViewModel.SortField.Id => "id", UpdatesViewModel.SortField.Version => "version",
        UpdatesViewModel.SortField.NewVersion => "newVersion", UpdatesViewModel.SortField.Source => "source", _ => "name"
    };

    private void AddAction(string id, ICommand command)
    {
        PackageScreenActionDefinition definition = PackageScreenCatalog.Default.GetAction(id);
        var action = new PackageScreenAction(id, definition.Title, PackageScreenCatalog.Default.ResolveIcon(definition.Icon ?? id), command);
        Actions.Add(action);
        _actions[id] = action;
    }

    private void AddContextAction(string id, ICommand command, string kind = "row")
    {
        PackageScreenActionDefinition definition = PackageScreenCatalog.Default.GetAction(id);
        var action = new PackageScreenAction(id, definition.Title, PackageScreenCatalog.Default.ResolveIcon(definition.Icon ?? id), command, null, kind);
        ContextActions.Add(action);
        _contextActions[id] = action;
    }

    public ICommand? GetActionCommand(string actionId, PackageScreenRow? row = null) =>
        row is not null && _contextActions.TryGetValue(actionId, out PackageScreenAction? context) ? context.Command
            : _actions.TryGetValue(actionId, out PackageScreenAction? action) ? action.Command : null;

    public object? GetActionParameter(string actionId, PackageScreenRow? row = null) => row;
    public int? GetConfirmationItemCount(string actionId, PackageScreenRow? row = null) =>
        actionId.Equals("updateAll", StringComparison.OrdinalIgnoreCase) ? _viewModel.Packages.Count : null;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Changed -= ViewModel_Changed;
        _viewModel.Packages.CollectionChanged -= Packages_CollectionChanged;
        _preferences.PackageManagerEnabledChanged -= PackageManagerEnabledChanged;
        _visuals.PackageIconsChanged -= PackageIconsChanged;
        foreach (UpgradablePackage package in _subscribedPackages) package.PropertyChanged -= Package_PropertyChanged;
        foreach (PackageScreenRow row in Rows) row.PropertyChanged -= Row_PropertyChanged;
    }
}
