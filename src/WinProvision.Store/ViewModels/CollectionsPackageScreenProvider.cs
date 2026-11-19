using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

/// <summary>Projects the existing collections workflow into the shared package screen.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CollectionsPackageScreenProvider : IPackageScreenProvider, IDisposable
{
    private readonly CollectionsViewModel _viewModel;
    private readonly PackageScreenSourceRegistry _sources;
    private readonly ApplicationPreferencesService _preferences;
    private readonly PackageVisualPreferencesService _visuals;
    private readonly AccountSyncViewModel _account;
    private readonly IPresentationService _presentation;
    private readonly Dictionary<string, PackageScreenRow> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageItemViewModel> _packagesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PackageItemViewModel> _subscribedPackages = [];
    private readonly Dictionary<string, PackageScreenAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageScreenAction> _contextActions = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingSelection;
    private bool _disposed;
    private bool _isFilterOpen = true;

    public CollectionsPackageScreenProvider(CollectionsViewModel viewModel, PackageScreenSourceRegistry sources,
        ApplicationPreferencesService preferences, AccountSyncViewModel account, IPresentationService presentation,
        PackageVisualPreferencesService? visuals = null)
    {
        _viewModel = viewModel;
        _sources = sources;
        _preferences = preferences;
        _visuals = visuals ?? new PackageVisualPreferencesService(preferences);
        _account = account;
        _presentation = presentation;
        Definition = PackageScreenCatalog.Default.Get("collections");
        viewModel.SetViewMode(Definition.Defaults.View);
        viewModel.SetSort(ToSortField(Definition.Defaults.Sort.Column), Definition.Defaults.Sort.Direction == "ascending");
        ToggleSelectAllCommand = new RelayCommand(() => _viewModel.SetAllSelected(_viewModel.MasterSelection != true));
        SetSortCommand = new RelayCommand<object?>(SetSort);
        SetViewModeCommand = new RelayCommand<object?>(SetViewMode);
        SetFilterCommand = new RelayCommand<object?>(SetFilter);
        ToggleRowSelectionCommand = new RelayCommand<object?>(ToggleRowSelection);
        RefreshCommand = new AsyncRelayCommand(token => _viewModel.RefreshMetricsAsync(token));
        BuildActions();
        BuildFilters();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.Items.CollectionChanged += Items_CollectionChanged;
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
    /// <summary>Requests page-owned dialogs, navigation, and cloud account actions.</summary>
    public event Action<string>? PresentationActionRequested;

    public string Subtitle => PackageScreenCatalog.Default.FormatSubtitle(Definition,
        _viewModel.SelectedProfile?.Items.Count ?? 0, _viewModel.Items.Count, _viewModel.SelectedProfile?.Items.Count(item => item.IsSelected) ?? 0);
    public string StatusText => _viewModel.IsExportingOffline ? _viewModel.OfflineStatusText
        : _viewModel.HasOfflineReport ? string.Empty : _viewModel.StatusText;
    public string SearchText { get => _viewModel.SearchText; set => _viewModel.SearchText = value; }
    public string SearchPlaceholder => PackageScreenCatalog.Default.GetText("search.placeholder", "Pesquisar nome ou ID…");
    public string ViewMode { get => _viewModel.ViewMode; set => _viewModel.SetViewMode(value); }
    public string SortBy { get => _viewModel.SortField.ToLowerInvariant(); set => _viewModel.SetSort(ToSortField(value)); }
    public bool SortAscending { get => _viewModel.SortAscending; set => _viewModel.SetSort(_viewModel.SortField, value); }
    public bool IsLoading => _viewModel.IsBusy;
    public double? Progress => _viewModel.IsExportingOffline && !_viewModel.OfflineProgressIndeterminate ? _viewModel.OfflineProgress : null;
    public string? ErrorText => _viewModel.HasError ? _viewModel.ErrorMessage : null;
    public string? ErrorTitle => _viewModel.ErrorTitle;
    public bool IsFilterOpen { get => _isFilterOpen; set { if (_isFilterOpen == value) return; _isFilterOpen = value; OnPropertyChanged(nameof(IsFilterOpen)); } }
    public bool HasActiveFilters => _viewModel.OnlySelected || !_viewModel.SourceWinget || !_viewModel.SourceMsStore
        || !_viewModel.SourceOffice || !_viewModel.SourceChocolatey || _viewModel.SearchMode != "both" || !string.IsNullOrWhiteSpace(_viewModel.SearchText);
    public int SelectedCount => Rows.Count(item => item.IsSelected);
    public bool? MasterSelection => SelectedCount == 0 ? false : SelectedCount == Rows.Count ? true : null;
    public string EmptyTitle => _viewModel.HasNoResults ? "Nenhum aplicativo encontrado" : _viewModel.EmptyStateTitle;
    public string EmptyMessage => _viewModel.EmptyStateMessage;
    public IAsyncRelayCommand RefreshCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }
    public ICommand SetSortCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ToggleRowSelectionCommand { get; }

    private void BuildActions()
    {
        AddAction("install", new AsyncRelayCommand(InstallSelectedAsync, () => _viewModel.CanInstall));
        AddAction("installSelected", new AsyncRelayCommand(InstallSelectedAsync, () => _viewModel.CanInstall));
        AddAction("installAdmin", new AsyncRelayCommand(InstallAdminSelectedAsync, () => _viewModel.CanInstall));
        AddAction("installInteractive", new AsyncRelayCommand(InstallInteractiveSelectedAsync, () => _viewModel.CanInstall));
        AddAction("installAll", new AsyncRelayCommand(InstallAllAsync, () => _viewModel.SelectedProfile?.Items.Any(item => item.SupportsInstall && !item.IsInstalled) == true && !_viewModel.IsBusy));
        AddAction("newCollection", new RelayCommand(_viewModel.CreateProfile));
        AddAction("openCollection", new RelayCommand(() => PresentationActionRequested?.Invoke("openCollection")));
        AddAction("saveCollection", _viewModel.ExportCommand);
        AddAction("importCloud", _account.RestoreFromCloudCommand);
        AddAction("renameCollection", new RelayCommand(() => PresentationActionRequested?.Invoke("renameCollection"), () => _viewModel.CanEditProfile));
        AddAction("deleteCollection", new RelayCommand(() => PresentationActionRequested?.Invoke("deleteCollection"), () => _viewModel.CanEditProfile));
        AddAction("clearCollection", new RelayCommand(() => PresentationActionRequested?.Invoke("clearCollection"), () => _viewModel.HasItems));
        AddAction("removeFromCollection", _viewModel.RemoveSelectedCommand);
        AddAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync));
        AddAction("download", _viewModel.DownloadSelectedCommand);
        AddAction("exportScript", _viewModel.ExportScriptCommand);
        AddAction("offlineExport", _viewModel.ExportOfflineCommand);
        AddAction("selectAll", new RelayCommand(() => _viewModel.SetAllSelected(true), () => _viewModel.HasItems));
        AddAction("clearSelection", new RelayCommand(() => _viewModel.SetAllSelected(false), () => _viewModel.HasItems));
        AddAction("cancel", _viewModel.CancelBatchCommand);
        AddAction("offlineCancel", _viewModel.CancelOfflineExportCommand);

        AddContextAction("details", new AsyncRelayCommand<PackageScreenRow?>(ShowDetailsAsync));
        AddContextAction("install", new AsyncRelayCommand<PackageScreenRow?>(InstallRowAsync, row => Resolve(row) is { SupportsInstall: true, IsInstalled: false } && !_viewModel.IsBusy));
        AddContextAction("installAdmin", new AsyncRelayCommand<PackageScreenRow?>(InstallAdminRowAsync, row => Resolve(row) is { SupportsInstall: true, IsInstalled: false } && !_viewModel.IsBusy));
        AddContextAction("installInteractive", new AsyncRelayCommand<PackageScreenRow?>(InstallInteractiveRowAsync, row => Resolve(row) is { SupportsInstall: true, IsInstalled: false } && !_viewModel.IsBusy));
        AddContextAction("download", new AsyncRelayCommand<PackageScreenRow?>(DownloadRowAsync, row => Resolve(row)?.SupportsDownload == true && !_viewModel.IsBusy));
        AddContextAction("removeFromCollection", new RelayCommand<PackageScreenRow?>(RemoveRow, row => row is not null && !_viewModel.IsBusy));
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
                    string id = source.Id.ToLowerInvariant();
                    Filters.Add(new($"source:{id}", source.Name, "source", source.IconGlyph, SourceState(id), command: SetFilterCommand, iconUri: source.IconUri));
                }
                foreach (PackageScreenSidebarItemDefinition item in section.Items)
                {
                    string id = item.Id.ToLowerInvariant();
                    if (enabledSources.Any(source => source.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    PackageScreenIconSources? iconSources = PackageScreenCatalog.Default.ResolveIconSources(item.Icon ?? Definition.Icon);
                    Filters.Add(new($"source:{id}", PackageScreenCatalog.Default.GetText(item.Title, item.Title), "source",
                        PackageScreenCatalog.Default.ResolveIcon(item.Icon ?? Definition.Icon), SourceState(id), command: SetFilterCommand,
                        iconUri: iconSources?.Light, iconUriDark: iconSources?.Dark));
                }
            }
            else if (section.Id.Equals("profiles", StringComparison.OrdinalIgnoreCase))
            {
                Filters.Add(new("profile", "Coleção", "choice", selectedValue: _viewModel.SelectedProfile?.Title,
                    values: _viewModel.Profiles.Select(profile => new PackageScreenFilterValue(profile.Title, profile.Title)).ToArray(), command: SetFilterCommand));
            }
            else if (section.Id.Equals("search", StringComparison.OrdinalIgnoreCase))
            {
                string title = section.Items.FirstOrDefault(item => item.Id.Equals("searchMode", StringComparison.OrdinalIgnoreCase))?.Title ?? section.Title;
                Filters.Add(new("searchMode", PackageScreenCatalog.Default.GetText(title, title), "choice", selectedValue: _viewModel.SearchMode,
                    values: section.Modes.Select(mode => new PackageScreenFilterValue(mode, PackageScreenCatalog.Default.GetText($"search.mode.{mode}", mode))).ToArray(), command: SetFilterCommand));
            }
            else
            {
                foreach (PackageScreenSidebarItemDefinition item in section.Items)
                    Filters.Add(new(item.Id.ToLowerInvariant(), PackageScreenCatalog.Default.GetText(item.Title, item.Title), "toggle",
                        PackageScreenCatalog.Default.ResolveIcon(item.Icon ?? string.Empty), item.Id.Equals("selected", StringComparison.OrdinalIgnoreCase) && _viewModel.OnlySelected,
                        command: SetFilterCommand));
            }
        }
    }

    private async Task InstallSelectedAsync() => await _viewModel.InstallSelectedCommand.ExecuteAsync(null);
    private async Task InstallAdminSelectedAsync() => await _viewModel.InstallSelectedWithOptionsAsync(new PackageInstallOptions(RequiresElevation: true, Scope: "machine"));
    private async Task InstallInteractiveSelectedAsync() => await _viewModel.InstallSelectedWithOptionsAsync(new PackageInstallOptions(Interactive: true));
    private async Task InstallAllAsync()
    {
        if (_viewModel.SelectedProfile is not { } profile) return;
        PackageItemViewModel[] previous = profile.Items.Where(item => item.IsSelected).ToArray();
        try
        {
            foreach (PackageItemViewModel item in profile.Items) item.IsSelected = item.SupportsInstall && !item.IsInstalled;
            await _viewModel.InstallSelectedCommand.ExecuteAsync(null);
        }
        finally
        {
            foreach (PackageItemViewModel item in profile.Items) item.IsSelected = previous.Contains(item);
        }
    }

    private async Task ShowDetailsAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is { } item) await item.DetailsCommand.ExecuteAsync(null);
        else PresentationActionRequested?.Invoke("details");
    }
    private async Task InstallRowAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is { } item) await item.InstallCommand.ExecuteAsync(null);
    }
    private async Task InstallAdminRowAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is { } item) await _viewModel.InstallItemWithOptionsAsync(item, new PackageInstallOptions(RequiresElevation: true, Scope: "machine"));
    }
    private async Task InstallInteractiveRowAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is { } item) await _viewModel.InstallItemWithOptionsAsync(item, new PackageInstallOptions(Interactive: true));
    }
    private async Task DownloadRowAsync(PackageScreenRow? row)
    {
        if (Resolve(row) is { } item) await item.DownloadCommand.ExecuteAsync(null);
    }
    private void RemoveRow(PackageScreenRow? row)
    {
        if (Resolve(row) is { } item) item.RemoveCommand.Execute(null);
    }

    private void SetSort(object? value) { if (value is string column) _viewModel.SetSort(ToSortField(column)); }
    private void SetViewMode(object? value) { if (value is string mode) _viewModel.SetViewMode(mode); }
    private void SetFilter(object? value)
    {
        if (value is not PackageScreenFilterOption option) return;
        bool enabled = option.IsChecked == true;
        if (option.Id.StartsWith("source:", StringComparison.OrdinalIgnoreCase))
        {
            string sourceId = option.Id[7..].ToLowerInvariant();
            switch (sourceId)
            {
                case "winget": _viewModel.SourceWinget = enabled; break;
                case "msstore": _viewModel.SourceMsStore = enabled; break;
                case "office": _viewModel.SourceOffice = enabled; break;
                case "chocolatey": _viewModel.SourceChocolatey = enabled; break;
            }
            _viewModel.AllSources = _viewModel.SourceWinget && _viewModel.SourceMsStore && _viewModel.SourceOffice && _viewModel.SourceChocolatey;
        }
        else if (option.Id == "selected") _viewModel.OnlySelected = enabled;
        else if (option.Id == "searchMode" && option.SelectedValue is { } mode) _viewModel.SearchMode = mode;
        else if (option.Id == "profile" && option.SelectedValue is { } title)
            _viewModel.SelectedProfile = _viewModel.Profiles.FirstOrDefault(profile => profile.Title == title);
        RefreshFilters();
    }
    private void ToggleRowSelection(object? value)
    {
        if (value is PackageScreenRow row && Resolve(row) is { } item) item.IsSelected = !item.IsSelected;
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncRows();
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CollectionsViewModel.Items)) SyncRows();
        if (e.PropertyName is nameof(CollectionsViewModel.SelectedProfile) or nameof(CollectionsViewModel.Profiles)) BuildFilters();
        else if (e.PropertyName is nameof(CollectionsViewModel.SearchText) or nameof(CollectionsViewModel.OnlySelected)
            or nameof(CollectionsViewModel.SourceWinget) or nameof(CollectionsViewModel.SourceMsStore)
            or nameof(CollectionsViewModel.SourceOffice) or nameof(CollectionsViewModel.SourceChocolatey)
            or nameof(CollectionsViewModel.SearchMode)) RefreshFilters();
        foreach (PackageScreenAction action in Actions.Concat(ContextActions))
            if (action.Command is IRelayCommand relay) relay.NotifyCanExecuteChanged();
        OnPropertyChanged(e.PropertyName switch
        {
            nameof(CollectionsViewModel.ViewMode) => nameof(ViewMode),
            nameof(CollectionsViewModel.SortField) => nameof(SortBy),
            nameof(CollectionsViewModel.SortAscending) => nameof(SortAscending),
            nameof(CollectionsViewModel.SearchText) => nameof(SearchText),
            nameof(CollectionsViewModel.IsBusy) or nameof(CollectionsViewModel.IsExportingOffline) => nameof(IsLoading),
            nameof(CollectionsViewModel.HasError) or nameof(CollectionsViewModel.ErrorMessage) => nameof(ErrorText),
            nameof(CollectionsViewModel.StatusText) or nameof(CollectionsViewModel.OfflineStatusText)
                or nameof(CollectionsViewModel.HasOfflineReport) => nameof(StatusText),
            _ => e.PropertyName ?? string.Empty
        });
        foreach (string property in new[] { nameof(Subtitle), nameof(SelectedCount), nameof(MasterSelection), nameof(EmptyTitle), nameof(EmptyMessage), nameof(HasActiveFilters) })
            OnPropertyChanged(property);
    }

    private void Package_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is PackageItemViewModel item && _rowsByKey.TryGetValue(PackageKey(item), out PackageScreenRow? row)) UpdateRow(row, item);
        OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection)); OnPropertyChanged(nameof(Subtitle));
    }
    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncingSelection || e.PropertyName != nameof(PackageScreenRow.IsSelected) || sender is not PackageScreenRow row) return;
        if (Resolve(row) is { } item) item.IsSelected = row.IsSelected;
    }
    private void SyncRows()
    {
        PackageItemViewModel[] items = _viewModel.Items.Where(item => item.App.Office is not null
            || item.App.Source.Equals("office", StringComparison.OrdinalIgnoreCase)
            || _sources.IsEnabled(SourceId(item))).ToArray();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _packagesByKey.Clear();
        foreach (PackageItemViewModel item in items)
        {
            string key = PackageKey(item); keep.Add(key); _packagesByKey[key] = item;
            if (_subscribedPackages.Add(item)) item.PropertyChanged += Package_PropertyChanged;
            if (!_rowsByKey.TryGetValue(key, out PackageScreenRow? row))
            {
                row = new PackageScreenRow { Key = key, Payload = item };
                row.PropertyChanged += Row_PropertyChanged; _rowsByKey.Add(key, row); Rows.Add(row);
            }
            UpdateRow(row, item);
        }
        foreach (string key in _rowsByKey.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            PackageScreenRow row = _rowsByKey[key]; row.PropertyChanged -= Row_PropertyChanged; Rows.Remove(row); _rowsByKey.Remove(key);
        }
        foreach (PackageItemViewModel item in _subscribedPackages.Where(item => !items.Contains(item)).ToArray())
        { item.PropertyChanged -= Package_PropertyChanged; _subscribedPackages.Remove(item); }
        for (int index = 0; index < items.Length; index++)
        {
            PackageScreenRow row = _rowsByKey[PackageKey(items[index])]; int current = Rows.IndexOf(row);
            if (current != index) Rows.Move(current, index);
        }
        OnPropertyChanged(nameof(Subtitle)); OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(MasterSelection));
    }
    private void UpdateRow(PackageScreenRow row, PackageItemViewModel item)
    {
        _syncingSelection = true;
        row.Name = item.Name; row.Id = item.Id; row.Version = item.Version; row.Source = item.SourceLabel;
        PackageImagePresentation icon = _visuals.GetPackageIcon(item.IconUrl);
        row.ShowPackageIcon = icon.IsVisible;
        if (item.App.Office is not null || item.App.Source.Equals("office", StringComparison.OrdinalIgnoreCase))
        {
            row.IconUri = OfficeAppCatalog.Microsoft365IconUrl;
            row.IconUriDark = null;
            row.IconFallbackUri = OfficeCatalogRuntime.Current.SourceIconFallback;
        }
        else
        {
            row.IconUri = icon.SourceUrl;
            row.IconUriDark = null;
        }
        row.IconGlyph = PackageScreenCatalog.Default.ResolveIcon(item.SourceIconName);
        string sourceId = SourceId(item);
        PackageScreenIconSources sourceIcons = _sources.GetIconSources(sourceId);
        row.SourceIconUri = sourceIcons.Light; row.SourceIconUriDark = sourceIcons.Dark;
        row.SourceGlyph = PackageScreenCatalog.Default.ResolveIcon(sourceId); row.IsSelected = item.IsSelected;
        row.IsWorking = item.IsWorking; row.IsInstalled = item.IsInstalled; row.CanSelect = true;
        row.Status = item.IsWorking ? "Em andamento" : item.CompatibilityNote;
        row.Values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        { ["name"] = item.Name, ["id"] = item.Id, ["version"] = item.Version, ["source"] = item.SourceLabel, ["status"] = row.Status };
        _syncingSelection = false;
    }
    private void RefreshFilters()
    {
        foreach (PackageScreenFilterOption option in Filters)
        {
            if (option.Id.StartsWith("source:", StringComparison.OrdinalIgnoreCase)) option.IsChecked = SourceState(option.Id[7..]);
            else if (option.Id == "selected") option.IsChecked = _viewModel.OnlySelected;
            else if (option.Id == "searchMode") option.SelectedValue = _viewModel.SearchMode;
            else if (option.Id == "profile") option.SelectedValue = _viewModel.SelectedProfile?.Title;
        }
        OnPropertyChanged(nameof(HasActiveFilters));
    }
    private bool? SourceState(string id) => id.ToLowerInvariant() switch
    {
        "winget" => _viewModel.SourceWinget, "msstore" => _viewModel.SourceMsStore,
        "office" => _viewModel.SourceOffice, "chocolatey" => _viewModel.SourceChocolatey,
        _ => true
    };
    private static string SourceId(PackageItemViewModel item) => item.App.Office is not null || item.App.Source.Equals("office", StringComparison.OrdinalIgnoreCase)
        ? "office" : item.App.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? "msstore"
        : item.App.Source.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) ? "chocolatey" : "winget";
    private static string PackageKey(PackageItemViewModel item) => $"{item.App.Source}\u001f{item.Id}";
    private PackageItemViewModel? Resolve(PackageScreenRow? row) => row is not null && _packagesByKey.TryGetValue(row.Key, out PackageItemViewModel? item) ? item : null;
    private static string ToSortField(string column) => column.ToLowerInvariant() switch { "id" => "Id", "version" => "Version", "source" => "Source", _ => "Name" };
    private void PackageManagerEnabledChanged(string id, bool enabled) { BuildFilters(); SyncRows(); }
    private void PackageIconsChanged(bool _) => SyncRows();
    private void AddAction(string id, ICommand command)
    {
        PackageScreenActionDefinition definition = PackageScreenCatalog.Default.GetAction(id);
        var action = new PackageScreenAction(id, definition.Title, PackageScreenCatalog.Default.ResolveIcon(definition.Icon ?? id), command);
        Actions.Add(action); _actions[id] = action;
    }
    private void AddContextAction(string id, ICommand command)
    {
        PackageScreenActionDefinition definition = PackageScreenCatalog.Default.GetAction(id);
        var action = new PackageScreenAction(id, definition.Title, PackageScreenCatalog.Default.ResolveIcon(definition.Icon ?? id), command, null, "row");
        ContextActions.Add(action); _contextActions[id] = action;
    }
    public ICommand? GetActionCommand(string actionId, PackageScreenRow? row = null) =>
        row is not null && _contextActions.TryGetValue(actionId, out PackageScreenAction? context) ? context.Command
        : _actions.TryGetValue(actionId, out PackageScreenAction? action) ? action.Command : null;
    public object? GetActionParameter(string actionId, PackageScreenRow? row = null) => row;
    public int? GetConfirmationItemCount(string actionId, PackageScreenRow? row = null) =>
        actionId.Equals("installAll", StringComparison.OrdinalIgnoreCase)
            ? _viewModel.SelectedProfile?.Items.Count(item => item.SupportsInstall && !item.IsInstalled) ?? 0
            : null;

    public Task ExecuteConfirmedActionAsync(string actionId, PackageScreenRow? row = null) =>
        actionId.Equals("deleteCollection", StringComparison.OrdinalIgnoreCase)
            ? _viewModel.DeleteProfileConfirmedAsync()
            : PackageScreenActionExecutor.ExecuteCommandAsync(this, actionId, row);
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged; _viewModel.Items.CollectionChanged -= Items_CollectionChanged;
        _preferences.PackageManagerEnabledChanged -= PackageManagerEnabledChanged;
        _visuals.PackageIconsChanged -= PackageIconsChanged;
        foreach (PackageItemViewModel item in _subscribedPackages) item.PropertyChanged -= Package_PropertyChanged;
        foreach (PackageScreenRow row in Rows) row.PropertyChanged -= Row_PropertyChanged;
    }
}
