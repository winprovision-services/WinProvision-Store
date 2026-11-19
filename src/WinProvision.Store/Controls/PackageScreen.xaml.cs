using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using FluentIcons.Common;
using FluentIcons.WinUI;
using CommunityToolkit.Mvvm.Input;

namespace WinProvision.Store.Controls;

public sealed partial class PackageScreen : UserControl, ITitleBarSearchTarget
{
    public static readonly DependencyProperty BrowseContentProperty = DependencyProperty.Register(
        nameof(BrowseContent), typeof(UIElement), typeof(PackageScreen), new PropertyMetadata(null, BrowseChanged));
    public static readonly DependencyProperty ShowBrowseContentProperty = DependencyProperty.Register(
        nameof(ShowBrowseContent), typeof(bool), typeof(PackageScreen), new PropertyMetadata(false, BrowseChanged));
    public UIElement? BrowseContent { get => (UIElement?)GetValue(BrowseContentProperty); set => SetValue(BrowseContentProperty, value); }
    public bool ShowBrowseContent { get => (bool)GetValue(ShowBrowseContentProperty); set => SetValue(ShowBrowseContentProperty, value); }
    private static void BrowseChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var screen = (PackageScreen)sender;
        screen.LandingHost.Content = screen.BrowseContent;
        screen.RefreshPresentation();
    }
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(IPackageScreenProvider), typeof(PackageScreen), new PropertyMetadata(null, OnProviderChanged));

    public IPackageScreenProvider? Provider
    {
        get => (IPackageScreenProvider?)GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    public PackageScreenDefinition? Definition => Provider?.Definition;
    bool ITitleBarSearchTarget.SearchContextAvailable => Definition?.Search.Enabled == true;
    string ITitleBarSearchTarget.SearchText
    {
        get => Provider?.SearchText ?? string.Empty;
        set
        {
            if (Provider is { } provider)
                provider.SearchText = value;
        }
    }
    Task ITitleBarSearchTarget.SubmitSearchAsync(string query) =>
        Provider?.SubmitSearchAsync(query) ?? Task.CompletedTask;
    void ITitleBarSearchTarget.CancelPendingSearch() => Provider?.CancelPendingSearch();
    public event EventHandler? SearchContextChanged;
    private PackageScreenCatalog Catalog => PackageScreenCatalog.Default;
    private bool _loaded;
    private bool _updating;
    private IPackageScreenProvider? _filtersOwner;
    private IPackageScreenProvider? _rowsOwner;
    private string? _hostSourceMode;
    private IPackageScreenProvider? _hostSourceProvider;
    private string? _columnsMode;
    private readonly List<ScrollViewer> _diagnosticScrollViewers = [];

    public PackageScreen()
    {
        InitializeComponent();
        MasterSelection.Resources["ControlCornerRadius"] = new CornerRadius(4);
        MasterSelection.IsThreeState = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) =>
        {
            if (Definition is { } definition) ApplyTitleIcon(definition);
            RefreshPresentation();
        };
    }

    private static void OnProviderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (PackageScreen)sender;
        if (control._loaded && args.OldValue is IPackageScreenProvider oldProvider)
            oldProvider.PropertyChanged -= control.Provider_PropertyChanged;
        if (args.OldValue is IPackageScreenProvider oldFiltersOwner && oldFiltersOwner.Filters is INotifyCollectionChanged oldFilters)
            oldFilters.CollectionChanged -= control.Filters_CollectionChanged;
        if (args.OldValue is IPackageScreenProvider oldRowsOwner && oldRowsOwner.Rows is INotifyCollectionChanged oldRows)
            oldRows.CollectionChanged -= control.Rows_CollectionChanged;
        if (control._loaded && args.NewValue is IPackageScreenProvider newProvider)
            newProvider.PropertyChanged += control.Provider_PropertyChanged;
        if (control._loaded && args.NewValue is IPackageScreenProvider newFiltersOwner && newFiltersOwner.Filters is INotifyCollectionChanged newFilters)
        {
            newFilters.CollectionChanged += control.Filters_CollectionChanged;
            control._filtersOwner = newFiltersOwner;
        }
        if (control._loaded && args.NewValue is IPackageScreenProvider newRowsOwner && newRowsOwner.Rows is INotifyCollectionChanged newRows)
        {
            newRows.CollectionChanged += control.Rows_CollectionChanged;
            control._rowsOwner = newRowsOwner;
        }
        control.BuildDefinitionUi();
        control.RefreshPresentation();
        control.UpdateHostSource(force: true);
        control.SearchContextChanged?.Invoke(control, EventArgs.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        if (Provider is { } provider)
        {
            provider.PropertyChanged += Provider_PropertyChanged;
            if (provider.Filters is INotifyCollectionChanged filters) filters.CollectionChanged += Filters_CollectionChanged;
            if (provider.Rows is INotifyCollectionChanged rows) rows.CollectionChanged += Rows_CollectionChanged;
            _filtersOwner = provider;
            _rowsOwner = provider;
        }
        BuildDefinitionUi();
        RefreshPresentation();
        UpdateHostSource(force: true);
#if DEBUG
        AttachDiagnosticScrollViewers();
        DispatcherQueue.TryEnqueue(AttachDiagnosticScrollViewers);
#endif
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
#if DEBUG
        foreach (ScrollViewer viewer in _diagnosticScrollViewers)
            viewer.ViewChanged -= DiagnosticScrollViewer_ViewChanged;
        _diagnosticScrollViewers.Clear();
#endif
        _loaded = false;
        if (Provider is { } provider) provider.PropertyChanged -= Provider_PropertyChanged;
        if (_filtersOwner?.Filters is INotifyCollectionChanged filters) filters.CollectionChanged -= Filters_CollectionChanged;
        if (_rowsOwner?.Rows is INotifyCollectionChanged rows) rows.CollectionChanged -= Rows_CollectionChanged;
        _filtersOwner = null;
        _rowsOwner = null;
    }

#if DEBUG
    private void AttachDiagnosticScrollViewers()
    {
        foreach (FrameworkElement list in new FrameworkElement[] { ListView, GridView, DetailsView })
        {
            ScrollViewer? viewer = FindScrollViewer(list);
            if (viewer is null || _diagnosticScrollViewers.Contains(viewer)) continue;
            viewer.ViewChanged += DiagnosticScrollViewer_ViewChanged;
            _diagnosticScrollViewers.Add(viewer);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer) return viewer;
            ScrollViewer? nested = FindScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void DiagnosticScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.ScrollableHeight <= 0
            || viewer.VerticalOffset < viewer.ScrollableHeight - 8) return;
        App.MemoryLog?.RecordOnce("catalog-list-scrolled-to-end");
        DebugPerformanceTelemetry.Mark("catalog-list-scrolled-to-end");
        DebugPerformanceTelemetry.ImageSnapshot("catalog-list-scrolled-to-end");
        DebugPerformanceTelemetry.CatalogSnapshot("catalog-list-scrolled-to-end");
    }
#endif

    private void Provider_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        void Update()
        {
            RefreshPresentation();
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void Filters_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) BuildSidebar();
        else DispatcherQueue.TryEnqueue(BuildSidebar);
    }

    private void Rows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
#if DEBUG
        DispatcherQueue.TryEnqueue(AttachDiagnosticScrollViewers);
#endif
        if (DispatcherQueue.HasThreadAccess) RefreshPresentation();
        else DispatcherQueue.TryEnqueue(RefreshPresentation);
    }

    private void BuildDefinitionUi()
    {
        if (Provider is not { } provider || Definition is not { } definition) return;
        _updating = true;
        TitleText.Text = Catalog.GetText(definition.Title, definition.Title);
        ApplyTitleIcon(definition);
        SubtitleText.Text = provider.Subtitle;
        string statusSummary = definition.Status is { Fields.Count: > 0 } statusDefinition
            ? string.Join(" · ", statusDefinition.Fields.Select(provider.GetStatusValue).Where(value => !string.IsNullOrWhiteSpace(value)))
            : provider.StatusCardText ?? string.Empty;
        StatusCard.Visibility = string.IsNullOrWhiteSpace(statusSummary) ? Visibility.Collapsed : Visibility.Visible;
        StatusCardText.Text = statusSummary;
        StatusRefreshButton.Visibility = string.IsNullOrWhiteSpace(definition.Status?.RefreshAction) ? Visibility.Collapsed : Visibility.Visible;
        StatusRefreshButton.Tag = definition.Status?.RefreshAction;
        StatusRefreshButton.IsEnabled = definition.Status?.RefreshAction is { } refreshAction
            && GetActionCommand(refreshAction)?.CanExecute(GetActionParameter(refreshAction)) == true;
        PrimaryActionText.Text = Provider.GetActionTitle(definition.Toolbar.Primary.Action) ?? GetActionTitle(definition.Toolbar.Primary.Action);
        PrimaryActionIcon.Visibility = Enum.TryParse(Catalog.ResolveIcon(Catalog.GetAction(definition.Toolbar.Primary.Action).Icon ?? string.Empty), true, out Icon primaryIcon)
            ? Visibility.Visible : Visibility.Collapsed;
        if (PrimaryActionIcon.Visibility == Visibility.Visible) PrimaryActionIcon.Icon = primaryIcon;
        PrimaryActionButton.Command = null;
        PrimaryActionButton.CommandParameter = null;
        PrimaryActionButton.IsEnabled = GetActionCommand(definition.Toolbar.Primary.Action)?.CanExecute(
            GetActionParameter(definition.Toolbar.Primary.Action)) == true;
        PrimaryActionButton.Visibility = Visibility.Visible;
        PrimaryFlyout.Items.Clear();
        foreach (string actionId in definition.Toolbar.Primary.Menu)
            PrimaryFlyout.Items.Add(CreateActionMenuItem(actionId));
        OverflowFlyout.Items.Clear();
        foreach (string actionId in definition.Toolbar.Overflow)
            OverflowFlyout.Items.Add(CreateActionMenuItem(actionId));
        BuildSidebar();
        _columnsMode = provider.ViewMode;
        BuildColumns();
        RebuildSortSelector();
        FiltersText.Text = Catalog.GetText("toolbar.filters", "Filtros");
        ReloadText.Text = Catalog.GetText("toolbar.reload", "Recarregar");
        MoreText.Text = Catalog.GetText("toolbar.more", "Mais");
        SortLabel.Text = Catalog.GetText("footer.sort", "Classificar por:");
        AutomationProperties.SetName(ListModeButton, Catalog.GetText("view.list", "Lista"));
        AutomationProperties.SetName(DetailsModeButton, Catalog.GetText("view.detailed", "Lista detalhada"));
        AutomationProperties.SetName(GridModeButton, Catalog.GetText("view.grid", "Grade"));
        _updating = false;
    }

    private void ApplyTitleIcon(PackageScreenDefinition definition)
    {
        string? iconUri = ActualTheme == ElementTheme.Dark
            ? definition.IconUriDark ?? definition.IconUriLight
            : definition.IconUriLight ?? definition.IconUriDark;
        if (iconUri is not null)
        {
            TitleIconContainer.Child = new ImageIcon
            {
                Width = 24,
                Height = 24,
                Source = new SvgImageSource { UriSource = new Uri(iconUri) }
            };
            return;
        }
        if (Enum.TryParse(Catalog.ResolveIcon(definition.Icon), true, out Icon titleIcon))
        {
            TitleIcon.Icon = titleIcon;
            TitleIconContainer.Child = TitleIcon;
        }
    }

    private void RefreshPresentation()
    {
        if (Provider is not { } provider || Definition is not { } definition) return;
        _updating = true;
        SubtitleText.Text = provider.Subtitle;
        string statusSummary = definition.Status is { Fields.Count: > 0 } statusDefinition
            ? string.Join(" · ", statusDefinition.Fields.Select(provider.GetStatusValue).Where(value => !string.IsNullOrWhiteSpace(value)))
            : provider.StatusCardText ?? string.Empty;
        StatusCard.Visibility = string.IsNullOrWhiteSpace(statusSummary) ? Visibility.Collapsed : Visibility.Visible;
        StatusCardText.Text = statusSummary;
        StatusRefreshButton.Visibility = string.IsNullOrWhiteSpace(definition.Status?.RefreshAction) ? Visibility.Collapsed : Visibility.Visible;
        StatusRefreshButton.Tag = definition.Status?.RefreshAction;
        StatusRefreshButton.IsEnabled = definition.Status?.RefreshAction is { } refreshAction
            && GetActionCommand(refreshAction)?.CanExecute(GetActionParameter(refreshAction)) == true;
        PrimaryActionText.Text = provider.GetActionTitle(definition.Toolbar.Primary.Action) ?? GetActionTitle(definition.Toolbar.Primary.Action);
        if (!string.Equals(_columnsMode, provider.ViewMode, StringComparison.Ordinal))
        {
            _columnsMode = provider.ViewMode;
            BuildColumns();
        }

        PrimaryActionButton.IsEnabled = GetActionCommand(definition.Toolbar.Primary.Action)?.CanExecute(GetActionParameter(definition.Toolbar.Primary.Action)) == true;
        PrimaryActionButton.ApplyTemplate();
        VisualStateManager.GoToState(PrimaryActionButton, PrimaryActionButton.IsEnabled ? "Normal" : "Disabled", false);
        foreach (var item in PrimaryFlyout.Items.Concat(OverflowFlyout.Items).OfType<MenuFlyoutItem>())
            item.IsEnabled = item.Tag is string actionId && IsActionEnabled(actionId);
        SortSelector.SelectedValue = provider.SortBy;
        bool isOffice = provider is OfficePackageScreenProvider;
        bool sidebarOpen = provider.IsFilterOpen && !isOffice;
        FiltersButton.IsChecked = isOffice
            ? provider.Filters.FirstOrDefault(filter => filter.Kind == "selectedOnly")?.IsChecked == true
            : sidebarOpen;
        SidebarColumn.Width = new GridLength(sidebarOpen ? definition.Sidebar.Width ?? 260 : 0);
        SplitterColumn.Width = new GridLength(sidebarOpen ? 8 : 0);
        Sidebar.Visibility = sidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        FiltersButton.BorderBrush = provider.HasActiveFilters
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        TableHeader.Visibility = provider.ViewMode is "list" or "details" && !provider.IsLoading && provider.ErrorText is null ? Visibility.Visible : Visibility.Collapsed;
        ListView.Visibility = provider.ViewMode == "list" && !provider.IsLoading && provider.ErrorText is null && provider.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GridView.Visibility = provider.ViewMode == "grid" && !provider.IsLoading && provider.ErrorText is null && provider.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsView.Visibility = provider.ViewMode == "details" && !provider.IsLoading && provider.ErrorText is null && provider.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = provider.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingProgress.IsIndeterminate = provider.Progress is null;
        LoadingProgress.Value = provider.Progress ?? 0;
        LoadingMessage.Text = string.IsNullOrEmpty(provider.StatusText) ? Catalog.GetText("loading") : provider.StatusText;
        ErrorState.Visibility = provider.ErrorText is not null ? Visibility.Visible : Visibility.Collapsed;
        ErrorTitleText.Text = provider.ErrorTitle ?? string.Empty;
        ErrorText.Text = provider.ErrorText ?? string.Empty;
        EmptyState.Visibility = !provider.IsLoading && provider.ErrorText is null && provider.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = provider.EmptyTitle;
        EmptyMessage.Text = provider.EmptyMessage;
        MasterSelection.IsChecked = provider.MasterSelection;
        MasterSelection.Visibility = definition.Selection == "none" ? Visibility.Collapsed : Visibility.Visible;
        SelectionSummary.Text = provider.SelectedCount == 0 ? string.Empty : Catalog.GetText(provider.SelectedCount == 1 ? "footer.selected.one" : "footer.selected.many", provider.SelectedCount == 1 ? "{selected} selecionado" : "{selected} selecionados").Replace("{selected}", provider.SelectedCount.ToString());
        StatusText.Text = provider.StatusText;
        StatusSeparator.Visibility = !string.IsNullOrEmpty(SelectionSummary.Text) && !string.IsNullOrWhiteSpace(StatusText.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ListModeButton.IsChecked = provider.ViewMode == "list";
        DetailsModeButton.IsChecked = provider.ViewMode == "details";
        GridModeButton.IsChecked = provider.ViewMode == "grid";
        UpdateHostSource();
        LandingHost.Visibility = ShowBrowseContent ? Visibility.Visible : Visibility.Collapsed;
        if (ShowBrowseContent)
            TableHeader.Visibility = ListView.Visibility = GridView.Visibility = DetailsView.Visibility
                = LoadingState.Visibility = ErrorState.Visibility = EmptyState.Visibility = Visibility.Collapsed;
        _updating = false;
#if DEBUG
        DispatcherQueue.TryEnqueue(AttachDiagnosticScrollViewers);
#endif
    }

    private void UpdateHostSource(bool force = false)
    {
        if (Provider is not { } provider || (!force && _hostSourceMode == provider.ViewMode
            && ReferenceEquals(_hostSourceProvider, provider))) return;
        _hostSourceMode = provider.ViewMode;
        _hostSourceProvider = provider;
        ListView.ItemsSource = provider.ViewMode == "list" ? provider.Rows : null;
        GridView.ItemsSource = provider.ViewMode == "grid" ? provider.Rows : null;
        DetailsView.ItemsSource = provider.ViewMode == "details" ? provider.Rows : null;
    }

    private void BuildColumns()
    {
        HeaderColumns.Children.Clear();
        HeaderColumns.ColumnDefinitions.Clear();
        HeaderColumns.Margin = new Thickness(0, 0, 8, 0);
        if (Definition is not { } definition) return;
        foreach (string id in GetViewColumns(definition, Provider?.ViewMode ?? "list"))
        {
            PackageScreenColumnDefinition column = Catalog.GetColumn(id);
            HeaderColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, column.Width ?? 1), GridUnitType.Star) });
            var button = new Button
            {
                Content = Catalog.GetText(column.Title, column.Title),
                Tag = id,
                Style = (Style)Application.Current.Resources["PageToolbarButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = column.CellTemplate == "status" ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                Padding = new Thickness(6, 0, 6, 0),
                MinWidth = 0,
                MinHeight = 0,
                Height = 32,
                FontSize = 12,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                IsEnabled = column.Sortable
            };
            button.Click += SortHeader_Click;
            Grid.SetColumn(button, HeaderColumns.ColumnDefinitions.Count - 1);
            HeaderColumns.Children.Add(button);
        }
    }

    private void RebuildSortSelector()
    {
        if (Definition is not { } definition || Provider is not { } provider) return;
        SortSelector.ItemsSource = null;
        SortSelector.Items.Clear();
        foreach (string id in GetSortColumns(definition).Where(id => Catalog.GetColumn(id).Sortable))
            SortSelector.Items.Add(new ComboBoxItem { Content = Catalog.GetText(Catalog.GetColumn(id).Title, Catalog.GetColumn(id).Title), Tag = id });
        SortSelector.SelectedValuePath = "Tag";
        SortSelector.SelectedValue = provider.SortBy;
    }

    private static IReadOnlyList<string> GetViewColumns(PackageScreenDefinition definition, string mode)
    {
        IReadOnlyList<string> columns = mode == "details" ? definition.Views.Details.Columns : definition.Views.List.Columns;
        return columns.Count > 0 ? columns : definition.Columns;
    }

    private static IReadOnlyList<string> GetSortColumns(PackageScreenDefinition definition)
    {
        if (definition.SortColumns.Count > 0) return definition.SortColumns;
        string[] columns = definition.Views.List.Columns.Concat(definition.Views.Details.Columns)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return columns.Length > 0 ? columns : definition.Columns;
    }

    private void BuildSidebar()
    {
        SidebarSections.Children.Clear();
        if (Definition is not { } definition || Provider is not { } provider) return;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PackageScreenSidebarSectionDefinition section in definition.Sidebar.Sections)
        {
            var content = new StackPanel { Spacing = 4 };
            if (section.Type.Equals("search-options", StringComparison.OrdinalIgnoreCase)
                && provider.Filters.FirstOrDefault(option => option.Id.Equals("searchMode", StringComparison.OrdinalIgnoreCase)) is { } searchMode)
            {
                used.Add(searchMode.Id);
                content.Children.Add(CreateFilterControl(searchMode));
            }
            foreach (PackageScreenSidebarItemDefinition item in section.Items)
            {
                if (item.Id.Equals("search", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                PackageScreenFilterOption? filter = provider.Filters.FirstOrDefault(option => option.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
                    || option.Id.Equals($"source:{item.Id}", StringComparison.OrdinalIgnoreCase));
                if (filter is null) continue;
                if (used.Contains(filter.Id)) continue;
                used.Add(filter.Id);
                content.Children.Add(CreateFilterControl(filter));
            }
            if (section.Id.Equals("sources", StringComparison.OrdinalIgnoreCase))
                foreach (PackageScreenFilterOption dynamicFilter in provider.Filters.Where(option => (option.Kind == "source" || option.Id.StartsWith("source:", StringComparison.OrdinalIgnoreCase)) && !used.Contains(option.Id)))
                {
                    content.Children.Add(CreateFilterControl(dynamicFilter));
                    used.Add(dynamicFilter.Id);
                }
            if (section.BulkActions.Count > 0)
            {
                var bulkActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (string actionId in section.BulkActions) bulkActions.Children.Add(CreateBulkActionButton(actionId));
                content.Children.Insert(0, bulkActions);
            }
            if (content.Children.Count == 0) continue;
            SidebarSections.Children.Add(new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Header = Catalog.GetText(section.Title, section.Title),
                Content = content,
                IsExpanded = section.Default?.Equals("expanded", StringComparison.OrdinalIgnoreCase) == true
                    || section.Default?.Equals("all", StringComparison.OrdinalIgnoreCase) == true
                    || section.Id.Equals("sources", StringComparison.OrdinalIgnoreCase)
                    || section.Type.Equals("search-options", StringComparison.OrdinalIgnoreCase),
                IsEnabled = true
            });
        }
        foreach (PackageScreenFilterOption option in provider.Filters.Where(option => !used.Contains(option.Id)))
            SidebarSections.Children.Add(CreateFilterControl(option));
    }

    private UIElement CreateFilterControl(PackageScreenFilterOption option)
    {
        if (option.Kind.Equals("choice", StringComparison.OrdinalIgnoreCase))
        {
            var combo = new ComboBox { Header = option.Title, SelectedValuePath = "Tag", Tag = option, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var choice in option.Values) combo.Items.Add(new ComboBoxItem { Content = choice.Title, Tag = choice.Id });
            ToolTipService.SetToolTip(combo, option.Values.FirstOrDefault(value => value.Id == option.SelectedValue)?.Title);
            combo.SetBinding(ComboBox.SelectedValueProperty, new Binding { Source = option, Path = new PropertyPath(nameof(PackageScreenFilterOption.SelectedValue)), Mode = BindingMode.TwoWay });
            combo.SelectionChanged += ChoiceFilter_Changed;
            return combo;
        }
        if (option.Kind.Equals("separator", StringComparison.OrdinalIgnoreCase))
            return new TextBlock { Text = option.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) };
        if (option.Kind.Equals("action", StringComparison.OrdinalIgnoreCase)) return CreateBulkActionButton(option.Id);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        PackageScreenIconSources? catalogIconSources = Catalog.ResolveIconSources(option.Icon ?? string.Empty);
        string? lightIconUri = option.IconUri ?? catalogIconSources?.Light;
        string? darkIconUri = option.IconUriDark ?? catalogIconSources?.Dark;
        string? selectedIconUri = ActualTheme == ElementTheme.Dark
            ? darkIconUri ?? lightIconUri
            : lightIconUri ?? darkIconUri;
        if (!string.IsNullOrWhiteSpace(selectedIconUri))
        {
            var image = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
            void ApplyIconTheme()
            {
                string? uriText = image.ActualTheme == ElementTheme.Dark ? darkIconUri ?? lightIconUri : lightIconUri ?? darkIconUri;
                if (Uri.TryCreate(uriText, UriKind.Absolute, out Uri? iconUri))
                    image.Source = iconUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                        ? new SvgImageSource { UriSource = iconUri }
                        : new BitmapImage { UriSource = iconUri };
            }
            image.ActualThemeChanged += (_, _) => ApplyIconTheme();
            ApplyIconTheme();
            content.Children.Add(image);
        }
        else if (!string.IsNullOrEmpty(option.Icon))
        {
            if (Enum.TryParse<Icon>(option.Icon, out var icon)) content.Children.Add(new FluentIcon { Icon = icon, IconSize = IconSize.Size16 });
            else content.Children.Add(new FontIcon { Glyph = option.Icon, FontSize = 16, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center });
        }
        content.Children.Add(new TextBlock { Text = option.Title, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 180 });
        var checkBox = new CheckBox { Content = content, IsChecked = option.IsChecked, Tag = option, MinHeight = 28 };
        checkBox.Resources["ControlCornerRadius"] = new CornerRadius(4);
        checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding { Source = option, Path = new PropertyPath(nameof(PackageScreenFilterOption.IsChecked)), Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(checkBox, option.Title);
        checkBox.Click += FilterOption_Click;
        return checkBox;
    }

    private Button CreateBulkActionButton(string actionId)
    {
        var button = new Button { Content = GetActionTitle(actionId), Tag = actionId, Height = 32, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += BulkAction_Click;
        return button;
    }

    private MenuFlyoutItem CreateActionMenuItem(string actionId)
    {
        PackageScreenActionDefinition actionDefinition = Catalog.GetAction(actionId);
        var item = new MenuFlyoutItem { Text = GetActionTitle(actionId), Tag = actionId };
        item.Click += ActionMenuItem_Click;
        if (actionDefinition.Icon is { } iconKey && Enum.TryParse(Catalog.ResolveIcon(iconKey), true, out Icon actionIcon))
            item.Icon = new FluentIcon { Icon = actionIcon, IconVariant = IconVariant.Regular, IconSize = IconSize.Size16 };
        item.IsEnabled = IsActionEnabled(actionId);
        return item;
    }

    private bool IsActionEnabled(string actionId) => Provider is OfficePackageScreenProvider officeProvider
        ? officeProvider.IsActionEnabled(actionId)
        : GetActionCommand(actionId)?.CanExecute(GetActionParameter(actionId)) == true;

    private string GetActionTitle(string id) => Catalog.GetText(Catalog.GetAction(id).Title, Catalog.GetAction(id).Title);
    private ICommand? GetActionCommand(string id, PackageScreenRow? row = null) => Provider?.GetActionCommand(id, row);
    private object? GetActionParameter(string id, PackageScreenRow? row = null) => Provider?.GetActionParameter(id, row);
    private Task ExecuteActionAsync(string actionId, PackageScreenRow? row = null) =>
        Provider is { } provider ? PackageScreenActionExecutor.ExecuteAsync(provider, actionId, row) : Task.CompletedTask;

    private void FiltersButton_Click(object sender, RoutedEventArgs e)
    {
        if (Provider is OfficePackageScreenProvider officeProvider)
        {
            if (officeProvider.Filters.FirstOrDefault(filter => filter.Kind == "selectedOnly") is { } selectedOnly)
            {
                selectedOnly.IsChecked = selectedOnly.IsChecked != true;
                officeProvider.SetFilterCommand.Execute(selectedOnly);
            }
            return;
        }
        if (Provider is not null) Provider.IsFilterOpen = !Provider.IsFilterOpen;
    }

    private async void PrimaryAction_Click(SplitButton sender, SplitButtonClickEventArgs e)
    {
        if (Definition is { } definition) await ExecuteActionAsync(definition.Toolbar.Primary.Action);
    }

    private async void ActionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string actionId }) await ExecuteActionAsync(actionId);
    }

    private async void BulkAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (id is "select-all" or "clear" && Provider is { } provider)
        {
            foreach (var option in provider.Filters.Where(option => option.Kind == "source" || option.Id.StartsWith("source:", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                option.IsChecked = id == "select-all";
                if (provider.SetFilterCommand.CanExecute(option)) provider.SetFilterCommand.Execute(option);
            }
        }
        else await ExecuteActionAsync(id);
    }

    private void MasterSelection_Click(object sender, RoutedEventArgs e) => Provider?.ToggleSelectAllCommand.Execute(null);
    private async void StatusRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string actionId }) await ExecuteActionAsync(actionId);
    }
    private void SortHeader_Click(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string id }) Provider?.SetSortCommand.Execute(id); }
    private void SortSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_updating && SortSelector.SelectedValue is string id) Provider?.SetSortCommand.Execute(id); }
    private void ViewMode_Click(object sender, RoutedEventArgs e) { if (sender is ToggleButton { Tag: string mode }) Provider?.SetViewModeCommand.Execute(mode); }
    private void FilterOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: PackageScreenFilterOption option })
        {
            option.IsChecked = ((CheckBox)sender).IsChecked;
            (option.Command ?? Provider?.SetFilterCommand)?.Execute(option);
        }
    }
    private void ChoiceFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: PackageScreenFilterOption option } combo && !_updating)
        {
            option.SelectedValue = combo.SelectedValue as string;
            ToolTipService.SetToolTip(combo, option.Values.FirstOrDefault(value => value.Id == option.SelectedValue)?.Title);
            (option.Command ?? Provider?.SetFilterCommand)?.Execute(option);
        }
    }
}
