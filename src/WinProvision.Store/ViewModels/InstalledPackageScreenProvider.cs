using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

/// <summary>Projects the installed-package view model into the shared package-screen contract.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class InstalledPackageScreenProvider : IPackageScreenProvider, IDisposable
{
    private readonly InstalledPackagesViewModel _viewModel;
    private readonly PackageScreenSourceRegistry _sources;
    private readonly ApplicationPreferencesService _preferences;
    private readonly PackageVisualPreferencesService _visuals;
    private readonly IPresentationService _presentation;
    private readonly Dictionary<string, PackageScreenRow> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstalledPackageRow> _packagesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<InstalledPackageRow> _subscribedPackages = [];
    private bool _syncingSelection;
    private bool _disposed;
    private bool _isFilterOpen = true;
    private readonly ICommand _searchCommand;

    public InstalledPackageScreenProvider(InstalledPackagesViewModel viewModel, PackageScreenSourceRegistry sources,
        ApplicationPreferencesService preferences, IPresentationService presentation,
        PackageVisualPreferencesService? visuals = null)
    {
        _viewModel = viewModel;
        _sources = sources;
        _preferences = preferences;
        _visuals = visuals ?? new PackageVisualPreferencesService(preferences);
        _presentation = presentation;
        Definition = PackageScreenCatalog.Default.Get("installed");
        viewModel.SetViewMode(Definition.Defaults.View);
        viewModel.SetSort(ToSortColumn(Definition.Defaults.Sort.Column), Definition.Defaults.Sort.Direction == "ascending");
        _searchCommand = new RelayCommand(viewModel.ApplyFilters);
        RefreshCommand = new AsyncRelayCommand(viewModel.LoadAsync);
        ToggleSelectAllCommand = new RelayCommand(() => viewModel.SetAllSelected(viewModel.MasterSelection != true));
        SetSortCommand = new RelayCommand<object?>(SetSort);
        SetViewModeCommand = new RelayCommand<object?>(SetViewMode);
        SetFilterCommand = new RelayCommand<object?>(SetFilter);
        ToggleRowSelectionCommand = new RelayCommand<object?>(ToggleRowSelection);
        BuildActions();
        BuildFilters();
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.VisiblePackages.CollectionChanged += Packages_CollectionChanged;
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

    public string Subtitle => PackageScreenCatalog.Default.FormatSubtitle(Definition, _viewModel.TotalCount, _viewModel.VisibleCount, _viewModel.SelectedCount);
    public string StatusText => _viewModel.Status;
    public string SearchText { get => _viewModel.SearchText; set => _viewModel.SearchText = value; }
    public string SearchPlaceholder => PackageScreenCatalog.Default.GetText("search.placeholder", "Pesquisar nome ou ID…");
    public string ViewMode { get => _viewModel.ViewMode; set => _viewModel.SetViewMode(value); }
    public string SortBy { get => _viewModel.SortColumn.ToLowerInvariant(); set => _viewModel.SetSort(ToSortColumn(value)); }
    public bool SortAscending { get => _viewModel.SortAscending; set => _viewModel.SetSort(_viewModel.SortColumn, value); }
    public bool IsLoading => _viewModel.IsInitialLoading || _viewModel.IsBusy;
    public string? ErrorText => _viewModel.HasError ? _viewModel.ErrorMessage : null;
    public bool IsFilterOpen { get => _isFilterOpen; set { if (_isFilterOpen == value) return; _isFilterOpen = value; OnPropertyChanged(nameof(IsFilterOpen)); } }
    public bool HasActiveFilters => !_viewModel.FilterWinget || !_viewModel.FilterStore || !_viewModel.FilterChocolatey
        || _viewModel.FilterLocal || _viewModel.OnlyRemovable || !_viewModel.HideSystemComponents || _viewModel.OnlySelected;
    public int SelectedCount => _viewModel.SelectedCount;
    public bool? MasterSelection => _viewModel.MasterSelection;
    public string EmptyTitle => _viewModel.HasError ? "Não foi possível carregar os aplicativos" : "Nenhum aplicativo encontrado";
    public string EmptyMessage => _viewModel.HasError ? _viewModel.ErrorMessage : "Ajuste os filtros ou atualize a lista.";
    public IAsyncRelayCommand RefreshCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }
    public ICommand SetSortCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ToggleRowSelectionCommand { get; }

    private void BuildActions()
    {
        Actions.Add(CreateAction("uninstall", new AsyncRelayCommand<PackageScreenRow?>(UninstallActionAsync, row => row is null ? _viewModel.CanRemove : Resolve(row)?.CanRemove == true)));
        Actions.Add(CreateAction("uninstallInteractive", new AsyncRelayCommand<PackageScreenRow?>(UninstallInteractiveActionAsync, row => row is null ? _viewModel.CanRemove : Resolve(row)?.CanRemove == true)));
        Actions.Add(CreateAction("uninstallAdmin", new AsyncRelayCommand<PackageScreenRow?>(UninstallAdminActionAsync, row => row is null ? _viewModel.CanRemove : Resolve(row)?.CanRemove == true)));
        Actions.Add(CreateAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync)));
        Actions.Add(CreateAction("download", new AsyncRelayCommand<PackageScreenRow?>(DownloadActionAsync)));
        Actions.Add(CreateAction("manualUninstall", new RelayCommand<PackageScreenRow?>(CopyUninstallAction)));
        Actions.Add(CreateAction("ignoreSelection", new RelayCommand(_viewModel.IgnoreSelectedUpdates)));
        Actions.Add(CreateAction("showIgnored", new RelayCommand(() => PresentationActionRequested?.Invoke("showIgnored"))));
        Actions.Add(CreateAction("refresh", RefreshCommand));
        Actions.Add(CreateAction("addToCollection", new RelayCommand<PackageScreenRow?>(AddToCollectionAction, row => row is null ? _viewModel.CanAddToCollection : Resolve(row)?.CanAddToCollection == true)));
        Actions.Add(CreateAction("exportCsv", _viewModel.ExportCsvCommand));
        Actions.Add(CreateAction("selectAll", new RelayCommand(() => _viewModel.SetAllSelected(true))));
        Actions.Add(CreateAction("clearSelection", new RelayCommand(() => _viewModel.SetAllSelected(false))));
        Actions.Add(CreateAction("uninstallOptions", new RelayCommand(() => PresentationActionRequested?.Invoke("uninstallOptions"))));

        ContextActions.Add(CreateAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync), "row"));
        ContextActions.Add(CreateAction("uninstall", new AsyncRelayCommand<PackageScreenRow?>(UninstallActionAsync, row => Resolve(row)?.CanRemove == true), "row"));
        ContextActions.Add(CreateAction("uninstallAdmin", new AsyncRelayCommand<PackageScreenRow?>(UninstallAdminAsync, row => Resolve(row)?.CanRemove == true), "row"));
        ContextActions.Add(CreateAction("uninstallInteractive", new AsyncRelayCommand<PackageScreenRow?>(UninstallInteractiveAsync, row => Resolve(row)?.CanRemove == true), "row"));
        ContextActions.Add(CreateAction("manualUninstall", new RelayCommand<PackageScreenRow?>(CopyUninstallAction), "row"));
        ContextActions.Add(CreateAction("openLocation", new RelayCommand<PackageScreenRow?>(OpenLocation), "row"));
        ContextActions.Add(CreateAction("download", new AsyncRelayCommand<PackageScreenRow?>(DownloadActionAsync), "row"));
        ContextActions.Add(CreateAction("ignore", new RelayCommand<PackageScreenRow?>(Ignore), "row"));
        ContextActions.Add(CreateAction("autoUpdate", new RelayCommand<PackageScreenRow?>(ToggleAutoUpdate), "row"));
        ContextActions.Add(CreateAction("addToCollection", new RelayCommand<PackageScreenRow?>(AddToCollection), "row"));
        ContextActions.Add(CreateAction("copyId", new RelayCommand<PackageScreenRow?>(CopyId), "row"));
        ContextActions.Add(CreateAction("copyName", new RelayCommand<PackageScreenRow?>(CopyName), "row"));
    }

    private void BuildFilters()
    {
        Filters.Clear();
        IReadOnlyList<PackageScreenSource> enabledSources = _sources.GetSources();
        foreach (PackageScreenSidebarSectionDefinition section in Definition.Sidebar.Sections)
        {
            if (section.Id.Equals("sources", StringComparison.OrdinalIgnoreCase))
            {
                foreach (PackageScreenSource source in enabledSources)
                {
                    string sourceId = source.Id.ToLowerInvariant();
                    Filters.Add(new($"source:{sourceId}", source.Name, "source", source.IconGlyph, GetSourceFilterState(sourceId), command: SetFilterCommand, iconUri: source.IconUri));
                }
                foreach (PackageScreenSidebarItemDefinition item in section.Items)
                {
                    string id = item.Id.ToLowerInvariant();
                    if (enabledSources.Any(source => source.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    Filters.Add(new($"source:{id}", PackageScreenCatalog.Default.GetText(item.Title, item.Title), "source",
                        PackageScreenCatalog.Default.ResolveIcon(item.Icon ?? Definition.Icon), GetSourceFilterState(id), command: SetFilterCommand));
                }
                continue;
            }
            if (section.Id.Equals("search", StringComparison.OrdinalIgnoreCase))
            {
                string title = section.Items.FirstOrDefault(item => item.Id.Equals("searchMode", StringComparison.OrdinalIgnoreCase))?.Title ?? section.Title;
                Filters.Add(new("searchMode", PackageScreenCatalog.Default.GetText(title, title), "choice", selectedValue: SearchModeValue,
                    values: section.Modes.Select(mode => new PackageScreenFilterValue(mode, PackageScreenCatalog.Default.GetText($"search.mode.{mode}", mode))).ToArray(), command: SetFilterCommand));
                continue;
            }
            foreach (PackageScreenSidebarItemDefinition item in section.Items)
            {
                string id = item.Id.ToLowerInvariant();
                Filters.Add(new(id, PackageScreenCatalog.Default.GetText(item.Title, item.Title), "toggle", PackageScreenCatalog.Default.ResolveIcon(item.Icon ?? string.Empty), id switch
                {
                    "removable" => _viewModel.OnlyRemovable,
                    "system" => _viewModel.HideSystemComponents,
                    "selected" => _viewModel.OnlySelected,
                    _ => false
                }, command: SetFilterCommand));
            }
        }
    }

    private async Task RemoveSelectedAsync()
    {
        await _viewModel.RemoveSelectedAsync();
    }
    private async Task UninstallActionAsync(PackageScreenRow? row)
    {
        if (row is null) await RemoveSelectedAsync();
        else await UninstallAsync(row);
    }
    private async Task UninstallAdminActionAsync(PackageScreenRow? row)
    {
        if (row is null) await _viewModel.RemoveSelectedAsync(requiresElevation: true);
        else await UninstallAdminAsync(row);
    }
    private async Task UninstallInteractiveActionAsync(PackageScreenRow? row)
    {
        if (row is null) await _viewModel.RemoveSelectedAsync(interactive: true);
        else await UninstallInteractiveAsync(row);
    }
    private async Task ShowSelectedDetailsAsync()
    {
        InstalledPackageRow? package = _viewModel.Packages.FirstOrDefault(item => item.IsSearchMatch && item.IsSelected)
            ?? _viewModel.Packages.FirstOrDefault(item => item.IsSearchMatch);
        if (package is not null) await _viewModel.ShowDetailsAsync(package);
    }

    private void SetSort(object? value) { if (value is string column) _viewModel.SetSort(ToSortColumn(column)); }
    private void SetViewMode(object? value) { if (value is string mode) _viewModel.SetViewMode(mode); }
    private void SetFilter(object? value)
    {
        if (value is not PackageScreenFilterOption option) return;
        bool enabled = option.IsChecked == true;
        switch (option.Id)
        {
            case "source:winget": _viewModel.FilterWinget = enabled; break;
            case "source:msstore": _viewModel.FilterStore = enabled; break;
            case "source:chocolatey": _viewModel.FilterChocolatey = enabled; break;
            case "source:local": _viewModel.FilterLocal = enabled; break;
            case "removable": _viewModel.OnlyRemovable = enabled; break;
            case "system": _viewModel.HideSystemComponents = enabled; break;
            case "selected": _viewModel.OnlySelected = enabled; break;
            case "searchMode": if (option.SelectedValue is { } mode) { _viewModel.IsExactSearch = mode == "exact"; _viewModel.SearchMode = mode switch { "name" => "Nome", "id" => "ID", _ => "Ambos" }; } break;
        }
        RefreshFilters();
    }

    private void ToggleRowSelection(object? value)
    {
        if (value is PackageScreenRow row && _packagesByKey.TryGetValue(row.Key, out InstalledPackageRow? package) && package.CanSelect)
            package.IsSelected = !package.IsSelected;
    }

    private InstalledPackageRow? Resolve(PackageScreenRow? row) => row is not null && _packagesByKey.TryGetValue(row.Key, out var package) ? package : null;
    private async Task ShowDetailsAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is { } package) await _viewModel.ShowDetailsAsync(package);
        else await ShowSelectedDetailsAsync();
    }
    private async Task UninstallAsync(PackageScreenRow? row) { if (Resolve(row) is { } package) await _viewModel.RemoveSingleAsync(package); }
    private async Task UninstallAdminAsync(PackageScreenRow? row) { if (Resolve(row) is { } package) await _viewModel.RemoveSingleAsync(package, requiresElevation: true); }
    private async Task UninstallInteractiveAsync(PackageScreenRow? row) { if (Resolve(row) is { } package) await _viewModel.RemoveSingleAsync(package, interactive: true); }
    private void CopyUninstallCommand(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.CopyManualUninstallCommand(package); }
    private void OpenLocation(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.OpenLocation(package); }
    private async Task DownloadAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is not { } package) return;
        var selection = _viewModel.Packages.ToDictionary(item => item, item => item.IsSelected);
        try
        {
            foreach (InstalledPackageRow item in _viewModel.Packages) item.IsSelected = false;
            package.IsSelected = true;
            await _viewModel.DownloadSelectedAsync();
        }
        finally
        {
            foreach ((InstalledPackageRow item, bool selected) in selection) item.IsSelected = selected;
        }
    }
    private void Ignore(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.Ignore(package); }
    private void ToggleAutoUpdate(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.ToggleAutoUpdate(package); }
    private void AddToCollection(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.AddToCollection(package); }
    private void CopyId(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.CopyId(package); }
    private void CopyName(PackageScreenRow? row) { if (Resolve(row) is { } package) _viewModel.CopyName(package); }
    private async Task DownloadActionAsync(PackageScreenRow? row)
    {
        if (row is null) await _viewModel.DownloadSelectedAsync();
        else await DownloadAsync(row);
    }
    private void CopyUninstallAction(PackageScreenRow? row)
    {
        if (Resolve(row) is { } package) _viewModel.CopyManualUninstallCommand(package);
        else CopySelectedUninstall();
    }
    private void AddToCollectionAction(PackageScreenRow? row)
    {
        if (Resolve(row) is { } package) _viewModel.AddToCollection(package);
        else _viewModel.AddSelectedToCollection();
    }
    private void CopySelectedUninstall()
    {
        InstalledPackageRow? package = _viewModel.Packages.FirstOrDefault(item => item.IsSearchMatch && item.IsSelected)
            ?? _viewModel.Packages.FirstOrDefault(item => item.IsSearchMatch);
        if (package is not null) _viewModel.CopyManualUninstallCommand(package);
    }

    private void Packages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncRows();
    private void PackageManagerEnabledChanged(string id, bool enabled) { BuildFilters(); }
    private void PackageIconsChanged(bool enabled)
    {
        if (enabled && !_viewModel.IsBusy)
            _viewModel.RefreshCommand.Execute(null);
        SyncRows();
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstalledPackagesViewModel.Packages)) SyncRows();
        else if (e.PropertyName is nameof(InstalledPackagesViewModel.FilterWinget) or nameof(InstalledPackagesViewModel.FilterStore)
            or nameof(InstalledPackagesViewModel.FilterChocolatey) or nameof(InstalledPackagesViewModel.FilterLocal)
            or nameof(InstalledPackagesViewModel.OnlyRemovable) or nameof(InstalledPackagesViewModel.HideSystemComponents)
            or nameof(InstalledPackagesViewModel.OnlySelected) or nameof(InstalledPackagesViewModel.SearchMode)
            or nameof(InstalledPackagesViewModel.IsExactSearch)) RefreshFilters();
        OnPropertyChanged(e.PropertyName switch
        {
            nameof(InstalledPackagesViewModel.ViewMode) => nameof(ViewMode),
            nameof(InstalledPackagesViewModel.SortColumn) => nameof(SortBy),
            nameof(InstalledPackagesViewModel.SortAscending) => nameof(SortAscending),
            nameof(InstalledPackagesViewModel.SearchText) => nameof(SearchText),
            nameof(InstalledPackagesViewModel.IsInitialLoading) or nameof(InstalledPackagesViewModel.IsBusy) => nameof(IsLoading),
            nameof(InstalledPackagesViewModel.HasError) or nameof(InstalledPackagesViewModel.ErrorMessage) => nameof(ErrorText),
            nameof(InstalledPackagesViewModel.Status) => nameof(StatusText),
            _ => e.PropertyName ?? string.Empty
        });
        OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
        OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyMessage)); OnPropertyChanged(nameof(HasActiveFilters));
    }

    private void SyncRows()
    {
        var visible = _viewModel.VisiblePackages.ToArray();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _packagesByKey.Clear();
        foreach (InstalledPackageRow package in visible)
        {
            string key = package.SelectionKey;
            keep.Add(key); _packagesByKey[key] = package;
            if (_subscribedPackages.Add(package)) package.PropertyChanged += Package_PropertyChanged;
            if (!_rowsByKey.TryGetValue(key, out PackageScreenRow? row))
            {
                row = new PackageScreenRow { Key = key };
                row.PropertyChanged += Row_PropertyChanged;
                _rowsByKey[key] = row;
                Rows.Add(row);
            }
            UpdateRow(row, package);
        }
        foreach (string key in _rowsByKey.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            PackageScreenRow row = _rowsByKey[key]; row.PropertyChanged -= Row_PropertyChanged;
            Rows.Remove(row); _rowsByKey.Remove(key);
        }
        foreach (InstalledPackageRow package in _subscribedPackages.Where(package => !visible.Contains(package)).ToArray())
        {
            package.PropertyChanged -= Package_PropertyChanged;
            _subscribedPackages.Remove(package);
        }
        for (int index = 0; index < visible.Length; index++)
        {
            PackageScreenRow row = _rowsByKey[visible[index].SelectionKey];
            int currentIndex = Rows.IndexOf(row);
            if (currentIndex != index) Rows.Move(currentIndex, index);
        }
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
    }

    private void UpdateRow(PackageScreenRow row, InstalledPackageRow package)
    {
        _syncingSelection = true;
        row.Name = package.Name; row.Id = package.Id; row.Version = package.VersionLabel;
        row.Source = package.SourceLabel;
        PackageImagePresentation icon = _visuals.GetPackageIcon(package.IconUrl);
        row.ShowPackageIcon = icon.IsVisible; row.IconUri = icon.SourceUrl;
        string sourceId = package.SourceCategory switch
        {
            "WinGet" => "winget", "Microsoft Store" => "msstore", "Chocolatey" => "chocolatey", _ => "local"
        };
        PackageScreenIconSources sourceIcons = _sources.GetIconSources(sourceId);
        row.SourceIconUri = sourceIcons.Light; row.SourceIconUriDark = sourceIcons.Dark;
        row.SourceGlyph = _sources.GetSources().FirstOrDefault(source => source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))?.IconGlyph
            ?? PackageScreenCatalog.Default.ResolveIcon(sourceId == "local" ? Definition.Icon : sourceId);
        row.IsSelected = package.IsSelected; row.IsInstalled = true;
        row.CanSelect = package.CanSelect;
        row.Status = package.CanRemove ? "Removível" : "Gerenciado pelo Windows";
        row.Values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = package.Name, ["id"] = package.Id, ["version"] = package.VersionLabel,
            ["source"] = package.SourceLabel, ["status"] = row.Status
        };
        _syncingSelection = false;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingSelection || e.PropertyName != nameof(PackageScreenRow.IsSelected) || sender is not PackageScreenRow row) return;
        if (_packagesByKey.TryGetValue(row.Key, out InstalledPackageRow? package) && package.CanSelect)
            package.IsSelected = row.IsSelected;
    }

    private void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is InstalledPackageRow package && _rowsByKey.TryGetValue(package.SelectionKey, out PackageScreenRow? row))
            UpdateRow(row, package);
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
    }

    private void RefreshFilters()
    {
        foreach (PackageScreenFilterOption option in Filters)
        {
            option.IsChecked = option.Id switch
            {
                "source:winget" => _viewModel.FilterWinget,
                "source:msstore" => _viewModel.FilterStore,
                "source:chocolatey" => _viewModel.FilterChocolatey,
                "source:local" => _viewModel.FilterLocal,
                "removable" => _viewModel.OnlyRemovable,
                "system" => _viewModel.HideSystemComponents,
                "selected" => _viewModel.OnlySelected,
                _ => option.IsChecked
            };
            if (option.Id == "searchMode") option.SelectedValue = SearchModeValue;
        }
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private static string ToSortColumn(string value) => value.ToLowerInvariant() switch
    {
        "id" => "Id", "version" => "Version", "source" => "Source", _ => "Name"
    };
    private string SearchModeValue => _viewModel.IsExactSearch ? "exact" : _viewModel.SearchMode switch
    {
        "Nome" => "name", "ID" => "id", _ => "both"
    };

    private bool? GetSourceFilterState(string id) => id switch
    {
        "winget" => _viewModel.FilterWinget,
        "msstore" => _viewModel.FilterStore,
        "chocolatey" => _viewModel.FilterChocolatey,
        "local" => _viewModel.FilterLocal,
        _ => true
    };

    private static PackageScreenAction CreateAction(string id, ICommand command, string kind = "action")
    {
        PackageScreenActionDefinition definition = PackageScreenCatalog.Default.GetAction(id);
        return new(id, definition.Title, PackageScreenCatalog.Default.ResolveIcon(definition.Icon ?? id), command, null, kind);
    }

    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));
    public ICommand? GetActionCommand(string actionId, PackageScreenRow? row = null) => actionId.Equals("search", StringComparison.OrdinalIgnoreCase)
        ? _searchCommand
        : Actions.Concat(ContextActions).FirstOrDefault(action => action.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase))?.Command;

    public object? GetActionParameter(string actionId, PackageScreenRow? row = null)
    {
        PackageScreenAction? action = Actions.Concat(ContextActions)
            .FirstOrDefault(item => item.Id.Equals(actionId, StringComparison.OrdinalIgnoreCase));
        return row ?? action?.Parameter;
    }

    public int? GetConfirmationItemCount(string actionId, PackageScreenRow? row = null)
    {
        if (actionId is not ("uninstall" or "uninstallAdmin" or "uninstallInteractive")) return null;
        if (row is not null) return Resolve(row)?.CanRemove == true ? 1 : null;
        return _viewModel.Packages.Count(package => package.IsSearchMatch && package.IsSelected && package.CanRemove);
    }

    public async Task ExecuteConfirmedActionAsync(string actionId, PackageScreenRow? row = null)
    {
        if (actionId is "uninstall" or "uninstallAdmin" or "uninstallInteractive")
        {
            bool interactive = actionId.Equals("uninstallInteractive", StringComparison.OrdinalIgnoreCase);
            bool elevated = actionId.Equals("uninstallAdmin", StringComparison.OrdinalIgnoreCase);
            if (row is null)
                await _viewModel.RemoveSelectedAsync(interactive: interactive, requiresElevation: elevated, alreadyConfirmed: true);
            else if (Resolve(row) is { } package)
                await _viewModel.RemoveSingleAsync(package, interactive, elevated, alreadyConfirmed: true);
            return;
        }

        await PackageScreenActionExecutor.ExecuteCommandAsync(this, actionId, row);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.VisiblePackages.CollectionChanged -= Packages_CollectionChanged;
        _preferences.PackageManagerEnabledChanged -= PackageManagerEnabledChanged;
        _visuals.PackageIconsChanged -= PackageIconsChanged;
        foreach (InstalledPackageRow package in _subscribedPackages) package.PropertyChanged -= Package_PropertyChanged;
        foreach (PackageScreenRow row in Rows) row.PropertyChanged -= Row_PropertyChanged;
    }
}
