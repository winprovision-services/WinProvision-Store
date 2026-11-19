using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

/// <summary>Projection of the existing Office state and commands into the shared package-list screen.</summary>
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OfficePackageScreenProvider : IPackageScreenProvider, IDisposable
{
    private readonly OfficeViewModel _office;
    private readonly List<PackageScreenRow> _allRows = [];
    private readonly Dictionary<string, OfficeAppToggleViewModel> _apps = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = string.Empty;
    private string _viewMode;
    private string _sortBy;
    private bool _sortAscending;
    private bool _isFilterOpen = true;
    private readonly IAsyncRelayCommand _installCommand;
    private readonly IAsyncRelayCommand _uninstallCommand;
    private readonly RelayCommand _addToCollectionCommand;

    public OfficePackageScreenProvider(OfficeViewModel office)
    {
        _office = office;
        if (_office.ExcludedComponents.Count == 0)
            foreach (var item in OfficeAppCatalog.AdvancedApps)
                _office.ExcludedComponents.Add(new OfficeAppToggleViewModel(item.Id, item.DisplayName, string.Empty, false));
        Definition = PackageScreenCatalog.Default.Get("office");
        _viewMode = Definition.Defaults.View;
        _sortBy = Definition.Defaults.Sort.Column;
        _sortAscending = Definition.Defaults.Sort.Direction == "ascending";
        _installCommand = new AsyncRelayCommand(InstallAsync, () => _office.CanInstallSelectedProducts);
        _uninstallCommand = new AsyncRelayCommand(UninstallAsync, () => _office.CanRemoveOffice);
        _addToCollectionCommand = new RelayCommand(() => _office.AddToCatalogCommand.Execute(null),
            () => _office.CanInstallSelectedProducts && _office.SelectedAppCount > 0);
        _office.PropertyChanged += OfficeChanged;
        RefreshCommand = new AsyncRelayCommand(() => Task.Run(() => _office.RefreshInstalledProducts()));
        ToggleSelectAllCommand = new RelayCommand(ToggleAll);
        SetSortCommand = new RelayCommand<object?>(SetSort);
        SetViewModeCommand = new RelayCommand<object?>(SetViewMode);
        SetFilterCommand = new RelayCommand<object?>(SetFilter);
        ToggleRowSelectionCommand = new RelayCommand<object?>(ToggleRow);
        BuildRows();
        BuildFilters();
        foreach (OfficeAppToggleViewModel app in _apps.Values) app.PropertyChanged += AppChanged;
        foreach (OfficeAppToggleViewModel app in _office.ExcludedComponents) app.PropertyChanged += SettingItemChanged;
    }

    public PackageScreenDefinition Definition { get; }
    public ObservableCollection<PackageScreenRow> Rows { get; } = [];
    public ObservableCollection<PackageScreenFilterOption> Filters { get; } = [];
    public ObservableCollection<PackageScreenAction> Actions { get; } = [];
    public ObservableCollection<PackageScreenAction> ContextActions { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
    private static string CatalogState(string key) => OfficeCatalogRuntime.Current.States.TryGetValue(key, out OfficeCatalogState? state) ? state.Label : key;
    public string Subtitle => $"{SelectedCount} de {ApplicableCount} aplicativos · {(_office.HasInstalledProducts ? CatalogState("installed") : CatalogState("notInstalled"))}";
    public string StatusText => _office.StatusText;
    public string? StatusCardText => null;
    public string? GetStatusValue(string fieldId)
    {
        string[] parts = _office.InstalledSummary.Split(" · ", StringSplitOptions.TrimEntries);
        return fieldId.ToLowerInvariant() switch
        {
            "state" => parts.FirstOrDefault() ?? CatalogState("notInstalled"),
            "version" => parts.FirstOrDefault(part => part.StartsWith("Versão ", StringComparison.OrdinalIgnoreCase)),
            "channel" => parts.FirstOrDefault(part => part.StartsWith("Canal ", StringComparison.OrdinalIgnoreCase))?.Replace("Canal ", string.Empty, StringComparison.OrdinalIgnoreCase),
            "architecture" => parts.FirstOrDefault(part => part.EndsWith(" bits", StringComparison.OrdinalIgnoreCase)),
            "language" => parts.Skip(1).LastOrDefault(part => !part.StartsWith("Versão ", StringComparison.OrdinalIgnoreCase)
                && !part.StartsWith("Canal ", StringComparison.OrdinalIgnoreCase) && !part.EndsWith(" bits", StringComparison.OrdinalIgnoreCase)),
            "verified" => _office.LastCheckedText,
            _ => null
        };
    }
    public string SearchText { get => _searchText; set { _searchText = value ?? string.Empty; ApplyFilter(); Changed(nameof(SearchText)); } }
    public string SearchPlaceholder => Definition.Search.Placeholder;
    public string ViewMode { get => _viewMode; set => SetViewMode(value); }
    public string SortBy { get => _sortBy; set => SetSort(value); }
    public bool SortAscending { get => _sortAscending; set { _sortAscending = value; ApplyFilter(); } }
    public bool IsLoading => false;
    public string? ErrorText => null;
    public bool IsFilterOpen { get => _isFilterOpen; set { _isFilterOpen = value; Changed(nameof(IsFilterOpen)); } }
    public bool HasActiveFilters => Filters.Any(filter => filter.Kind == "selectedOnly" && filter.IsChecked == true) || !string.IsNullOrWhiteSpace(SearchText);
    public int SelectedCount => _allRows.Count(row => row.CanSelect && row.IsSelected);
    public int ApplicableCount => _allRows.Count(row => row.CanSelect);
    public bool? MasterSelection
    {
        get
        {
            PackageScreenRow[] scope = Rows.Where(row => row.CanSelect).ToArray();
            if (scope.Length == 0) return false;
            return scope.All(row => row.IsSelected) ? true : scope.Any(row => row.IsSelected) ? null : false;
        }
    }
    public string EmptyTitle => "Nenhum aplicativo encontrado";
    public string EmptyMessage => "Altere a pesquisa para ver outros aplicativos do Office";
    public IAsyncRelayCommand RefreshCommand { get; }
    public ICommand ToggleSelectAllCommand { get; }
    public ICommand SetSortCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ToggleRowSelectionCommand { get; }

    public ICommand? GetActionCommand(string actionId, PackageScreenRow? row = null) => actionId switch
    {
        "officeApply" => _installCommand,
        "officeRefresh" => RefreshCommand,
        "officeUninstall" => _uninstallCommand,
        "officeOpenFolder" => _office.OpenWorkFolderCommand,
        "officeAddCollection" => _addToCollectionCommand,
        _ => null
    };
    public bool IsActionEnabled(string actionId)
    {
        ICommand? command = GetActionCommand(actionId);
        if (command?.CanExecute(GetActionParameter(actionId)) != true) return false;
        PackageScreenActionDefinition definition = PackageScreenCatalog.Default.GetAction(actionId);
        if (definition.RequiresSelection && SelectedCount == 0) return false;
        if (definition.RequiresValidConfiguration && _office.CreateInstallRequest() is null) return false;
        return true;
    }
    public string? GetActionTitle(string actionId) => actionId == "officeApply" ? _office.PrimaryActionText : null;
    public object? GetActionParameter(string actionId, PackageScreenRow? row = null) => null;

    public void Dispose()
    {
        _office.PropertyChanged -= OfficeChanged;
        foreach (OfficeAppToggleViewModel app in _apps.Values) app.PropertyChanged -= AppChanged;
        foreach (OfficeAppToggleViewModel app in _office.ExcludedComponents) app.PropertyChanged -= SettingItemChanged;
    }

    private void BuildRows()
    {
        _allRows.Clear();
        _apps.Clear();
        foreach (OfficeAppToggleViewModel app in _office.CoreAppItems.Concat(_office.AppToggleItems))
        {
            _apps[app.Id] = app;
            OfficeAppStatusRow? detected = _office.InstalledApps.FirstOrDefault(item => item.DisplayName.Equals(app.DisplayName, StringComparison.OrdinalIgnoreCase));
            bool isMainPlanApp = app.IsAdditionalProduct && _office.SelectedPlan?.IncludedApplicationIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase) == true;
            bool isAppIncluded = _office.SelectedPlan?.IncludedApplicationIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase) == true;
            bool isAppConditional = _office.SelectedPlan?.ConditionalApplicationIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase) == true;
            bool canSelect = app.IsEnabled && !isMainPlanApp;
            string? selectionReason = isMainPlanApp
                ? "Este aplicativo já é o produto principal"
                : canSelect ? null : app.IsAdditionalProduct
                    ? "Não há uma edição compatível com o produto e o canal selecionados"
                    : $"Este aplicativo não faz parte de {_office.SelectedPlan?.DisplayName ?? "este produto"}";
            if (isAppConditional && canSelect) selectionReason = OfficeCatalogRuntime.Current.ConditionalAppsDescription;
            if (app.IsAdditionalProduct && canSelect)
                selectionReason = _office.SelectedPlan?.AddOns.FirstOrDefault(addOn => addOn.AppId.Equals(app.Id, StringComparison.OrdinalIgnoreCase))?.LicenseWarning;
            string status = detected?.StatusText == CatalogState("installed") ? CatalogState("installed")
                : isAppIncluded || isAppConditional || app.IsAdditionalProduct && canSelect ? CatalogState("notInstalled")
                : CatalogState("notIncluded");
            var row = new PackageScreenRow
            {
                Key = app.Id, Name = app.DisplayName, Id = app.Id, Source = OfficeCatalogRuntime.Current.SourceLabel,
                SourceIconUri = Definition.IconUriLight, SourceIconUriDark = Definition.IconUriDark,
                IconUri = app.IconUrl, IconFallbackUri = app.FallbackIconUrl, IsSelected = canSelect && app.IsOn, CanSelect = canSelect, SelectionHelpText = selectionReason,
                IsInstalled = detected?.StatusText == CatalogState("installed"), Status = status,
                SelectionGroup = isMainPlanApp ? "mainProduct" : app.IsAdditionalProduct ? "optionalProducts" : "standardApps",
                Values = new Dictionary<string, string?> { ["name"] = app.DisplayName, ["id"] = app.Id, ["state"] = status, ["source"] = OfficeCatalogRuntime.Current.SourceLabel },
                Payload = app
            };
            _allRows.Add(row);
        }
        ApplyFilter();
    }

    private void BuildFilters()
    {
        Filters.Clear();
        foreach (PackageScreenSidebarSectionDefinition section in Definition.Sidebar.Sections)
        foreach (PackageScreenSidebarItemDefinition item in section.Items)
        {
            if (item.DependsOn is { } dependency)
            {
                var values = Definition.Sidebar.Sections.SelectMany(candidate => candidate.Items).ToDictionary(candidate => candidate.Id,
                    candidate => GetSelectedId(candidate.StorageKey) ?? GetCollection(candidate.StorageKey)
                        .FirstOrDefault(toggle => toggle.Id.Equals(candidate.Value, StringComparison.OrdinalIgnoreCase))?.IsOn.ToString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
                foreach (OfficeAppToggleViewModel app in _apps.Values) values[app.Id] = app.IsOn.ToString();
                WinProvision.Core.Services.ElevationSettingCondition effectiveDependency = dependency.Key?.StartsWith("$row:", StringComparison.OrdinalIgnoreCase) == true
                    ? dependency with { Key = dependency.Key[5..] } : dependency;
                var result = WinProvision.Core.Services.ElevationSettingConditionEvaluator.Evaluate(effectiveDependency, _office.CatalogDefinitions, values);
                if (!result.IsValid || !result.IsSatisfied) continue;
            }
            if (item.Kind.Equals("selectedOnly", StringComparison.OrdinalIgnoreCase))
                Filters.Add(new(item.Id, item.Title, "selectedOnly", isChecked: false, command: SetFilterCommand));
            else if (item.Kind.Equals("toggleSetting", StringComparison.OrdinalIgnoreCase))
                Filters.Add(new(item.Id, item.Title, "toggle", isChecked: ReadBoolean(item.StorageKey), command: SetFilterCommand));
            else if (item.Kind.Equals("collectionToggle", StringComparison.OrdinalIgnoreCase))
            {
                OfficeAppToggleViewModel? settingItem = GetCollection(item.StorageKey).FirstOrDefault(candidate => candidate.Id.Equals(item.Value, StringComparison.OrdinalIgnoreCase));
                if (settingItem is not null)
                    Filters.Add(new(item.Id, item.Title, "toggle", isChecked: settingItem.IsOn, command: SetFilterCommand));
            }
            else
            {
                IReadOnlyList<object> source = ResolveOptions(item.OptionsSource);
                if (source.Count == 0) continue;
                IReadOnlyList<PackageScreenFilterValue> values = source.Select(option => new PackageScreenFilterValue(GetOptionId(option), GetOptionTitle(option))).ToArray();
                string? current = GetSelectedId(item.StorageKey);
                Filters.Add(new(item.Id, item.Title, "choice", selectedValue: current, values: values, command: SetFilterCommand));
            }
        }
    }

    private void SetFilter(object? parameter)
    {
        if (parameter is not PackageScreenFilterOption option) return;
        if (option.Kind.Equals("selectedOnly", StringComparison.OrdinalIgnoreCase)) { ApplyFilter(); return; }
        PackageScreenSidebarItemDefinition? item = Definition.Sidebar.Sections.SelectMany(section => section.Items)
            .FirstOrDefault(candidate => candidate.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase));
        if (item?.Kind.Equals("toggleSetting", StringComparison.OrdinalIgnoreCase) == true)
        {
            SetBoolean(item.StorageKey, option.IsChecked == true);
            if (item.ApplyCommand is { } commandName && typeof(OfficeViewModel).GetProperty(commandName)?.GetValue(_office) is IAsyncRelayCommand command)
                _ = command.ExecuteAsync(null);
            return;
        }
        if (item?.Kind.Equals("collectionToggle", StringComparison.OrdinalIgnoreCase) == true)
        {
            OfficeAppToggleViewModel? settingItem = GetCollection(item.StorageKey).FirstOrDefault(candidate => candidate.Id.Equals(item.Value, StringComparison.OrdinalIgnoreCase));
            if (settingItem is not null) settingItem.IsOn = option.IsChecked == true;
            return;
        }
        if (item is not null && option.SelectedValue is { } value)
        {
            object? selected = ResolveOptions(item.OptionsSource).FirstOrDefault(candidate => GetOptionId(candidate).Equals(value, StringComparison.OrdinalIgnoreCase));
            SetSelection(item.StorageKey, selected, value);
        }
    }

    private void ApplyFilter()
    {
        string query = SearchText.Trim();
        bool onlySelected = Filters.FirstOrDefault(item => item.Kind == "selectedOnly")?.IsChecked == true;
        IEnumerable<PackageScreenRow> result = _allRows.Where(row => (!onlySelected || row.IsSelected)
            && (query.Length == 0 || row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase)));
        result = result.OrderBy(row => row.GetValue(_sortBy) ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase);
        if (!_sortAscending) result = result.Reverse();
        Rows.Clear(); foreach (PackageScreenRow row in result) Rows.Add(row);
        Changed(nameof(Subtitle)); Changed(nameof(SelectedCount)); Changed(nameof(MasterSelection)); Changed(nameof(HasActiveFilters));
    }

    private void ToggleAll()
    {
        PackageScreenRow[] scope = Rows.Where(row => row.CanSelect).ToArray();
        bool select = scope.Any(row => !row.IsSelected);
        foreach (PackageScreenRow row in scope)
            if (row.Payload is OfficeAppToggleViewModel app) app.IsOn = select;
    }
    private void ToggleRow(object? value) { if (value is PackageScreenRow { CanSelect: true } row && row.Payload is OfficeAppToggleViewModel app) app.IsOn = !app.IsOn; }
    private void SetSort(object? value) { _sortBy = value?.ToString() ?? "name"; ApplyFilter(); Changed(nameof(SortBy)); }
    private void SetViewMode(object? value) { string mode = value?.ToString()?.ToLowerInvariant() ?? "list"; if (mode is "list" or "details" or "grid") { _viewMode = mode; Changed(nameof(ViewMode)); } }
    private void AppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(OfficeAppToggleViewModel.IsOn) or nameof(OfficeAppToggleViewModel.IsEnabled))) return;
        foreach (PackageScreenRow row in _allRows.Where(row => ReferenceEquals(row.Payload, sender)))
        {
            row.CanSelect = ((OfficeAppToggleViewModel)sender!).IsEnabled && row.SelectionGroup != "mainProduct";
            row.IsSelected = row.CanSelect && ((OfficeAppToggleViewModel)sender!).IsOn;
            if (row.SelectionGroup == "optionalProducts" && row.CanSelect)
                row.Status = _office.InstalledApps.FirstOrDefault(item => item.DisplayName.Equals(row.Name, StringComparison.OrdinalIgnoreCase))?.StatusText ?? CatalogState("notInstalled");
        }
        if (sender is OfficeAppToggleViewModel { IsAdditionalProduct: true }) BuildFilters();
        ApplyFilter();
    }
    private void SettingItemChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(OfficeAppToggleViewModel.IsOn)) BuildFilters(); }
    private void OfficeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OfficeViewModel.CanInstallSelectedProducts)
            or nameof(OfficeViewModel.SelectedAppCount))
            _addToCollectionCommand.NotifyCanExecuteChanged();
        _installCommand.NotifyCanExecuteChanged();
        _uninstallCommand.NotifyCanExecuteChanged();
        Changed(nameof(StatusText));
        if (e.PropertyName is nameof(OfficeViewModel.HasInstalledProducts) or nameof(OfficeViewModel.InstalledSummary) or nameof(OfficeViewModel.LastCheckedText))
        {
            RefreshRowStatuses(); BuildFilters(); Changed(nameof(Subtitle)); Changed(nameof(StatusText)); Changed(nameof(StatusCardText));
        }
        else if (e.PropertyName is nameof(OfficeViewModel.SelectedPlan) or nameof(OfficeViewModel.SelectedChannel))
        {
            RefreshRowStatuses(); BuildFilters(); Changed(nameof(Subtitle));
        }
    }
    private void RefreshRowStatuses()
    {
        foreach (PackageScreenRow row in _allRows)
        {
            OfficeAppStatusRow? state = _office.InstalledApps.FirstOrDefault(item => item.DisplayName.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
            row.IsInstalled = state?.StatusText == CatalogState("installed");
            bool isMainPlanApp = row.Payload is OfficeAppToggleViewModel { IsAdditionalProduct: true }
                && _office.SelectedPlan?.IncludedApplicationIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase) == true;
            bool isIncluded = _office.SelectedPlan?.IncludedApplicationIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase) == true;
            bool isConditional = _office.SelectedPlan?.ConditionalApplicationIds.Contains(row.Id, StringComparer.OrdinalIgnoreCase) == true;
            row.SelectionGroup = isMainPlanApp ? "mainProduct" : row.Payload is OfficeAppToggleViewModel { IsAdditionalProduct: true } ? "optionalProducts" : "standardApps";
            row.CanSelect = row.Payload is OfficeAppToggleViewModel app && app.IsEnabled && !isMainPlanApp;
            row.SelectionHelpText = isMainPlanApp ? "Este aplicativo já é o produto principal"
                : row.CanSelect ? null : row.Payload is OfficeAppToggleViewModel { IsAdditionalProduct: true }
                    ? "Não há uma edição compatível com o produto e o canal selecionados"
                    : $"Este aplicativo não faz parte de {_office.SelectedPlan?.DisplayName ?? "este produto"}";
            if (isConditional && row.CanSelect) row.SelectionHelpText = OfficeCatalogRuntime.Current.ConditionalAppsDescription;
            if (row.Payload is OfficeAppToggleViewModel { IsAdditionalProduct: true } addOn && row.CanSelect)
                row.SelectionHelpText = _office.SelectedPlan?.AddOns.FirstOrDefault(item => item.AppId.Equals(addOn.Id, StringComparison.OrdinalIgnoreCase))?.LicenseWarning;
            row.IsSelected = row.CanSelect && row.Payload is OfficeAppToggleViewModel toggle && toggle.IsOn;
            row.Status = state?.StatusText == CatalogState("installed") ? CatalogState("installed")
                : isIncluded || isConditional || row.SelectionGroup == "optionalProducts" && row.CanSelect ? CatalogState("notInstalled")
                : CatalogState("notIncluded");
            row.Values = new Dictionary<string, string?> { ["name"] = row.Name, ["id"] = row.Id, ["state"] = row.Status, ["source"] = row.Source };
        }
        ApplyFilter();
    }
    private IReadOnlyList<object> ResolveOptions(string? source) => source?.ToLowerInvariant() switch
    {
        "plans" => OfficePlanCatalog.All.Cast<object>().ToArray(),
        "channels" => _office.Channels.Cast<object>().ToArray(),
        "architecture" => _office.ArchitectureOptions.Cast<object>().ToArray(),
        "language" => _office.LanguageOptions.Cast<object>().ToArray(),
        "display" => _office.DisplayOptions.Cast<object>().ToArray(),
        "visioplans" => _office.VisioProductOptions.Cast<object>().ToArray(),
        "projectplans" => _office.ProjectProductOptions.Cast<object>().ToArray(),
        _ => []
    };
    private static string GetOptionId(object option) => ReadMember(option, "ProductId") ?? ReadMember(option, "Id") ?? string.Empty;
    private static string GetOptionTitle(object option) => ReadMember(option, "DisplayName") ?? ReadMember(option, "Label") ?? GetOptionId(option);
    private static string? ReadMember(object value, string member) => value.GetType().GetProperty(member)?.GetValue(value)?.ToString();
    private string? GetSelectedId(string? storageKey)
    {
        if (storageKey is null) return null;
        object? value = typeof(OfficeViewModel).GetProperty(storageKey)?.GetValue(_office);
        return value is null ? null : value is string text ? text : GetOptionId(value);
    }
    private void SetSelection(string? storageKey, object? selection, string value)
    {
        if (storageKey is null) return;
        var property = typeof(OfficeViewModel).GetProperty(storageKey);
        if (property is null || !property.CanWrite) return;
        property.SetValue(_office, property.PropertyType == typeof(string) ? value : selection);
    }
    private IEnumerable<OfficeAppToggleViewModel> GetCollection(string? storageKey) => storageKey is null
        ? [] : typeof(OfficeViewModel).GetProperty(storageKey)?.GetValue(_office) as IEnumerable<OfficeAppToggleViewModel> ?? [];
    private bool ReadBoolean(string? storageKey) => storageKey is not null
        && typeof(OfficeViewModel).GetProperty(storageKey)?.GetValue(_office) is true;
    private void SetBoolean(string? storageKey, bool value)
    {
        if (storageKey is not null && typeof(OfficeViewModel).GetProperty(storageKey) is { CanWrite: true } property && property.PropertyType == typeof(bool))
            property.SetValue(_office, value);
    }
    private Task InstallAsync() => _office.InstallCommand.ExecuteAsync(null);
    private Task UninstallAsync() => _office.RemoveCommand.ExecuteAsync(null);
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
