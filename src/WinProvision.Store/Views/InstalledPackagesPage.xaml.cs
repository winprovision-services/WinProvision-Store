using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class InstalledPackagesPage : Page
{
    private void ViewModeMain_Click(SplitButton sender, SplitButtonClickEventArgs e) { SetViewMode(ViewModel.ViewMode == "list" ? "grid" : ViewModel.ViewMode == "grid" ? "icons" : "list"); }

    private bool _loaded;
    private bool _suppressSort;
    private FrameworkElement? _previousFocus;
    private InstalledPackageRow? _contextPackage;
    private Task? _load;
    private double _filterWidth = 260;

    public InstalledPackagesViewModel ViewModel { get; }
    public Task InitialContentReady => _load ?? Task.CompletedTask;

    public InstalledPackagesPage(InstalledPackagesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SyncActiveItemsSource();
        RefreshPresentation();
        if (ViewModel.Packages.Count == 0)
        {
            _load = ViewModel.RefreshCommand.ExecuteAsync(null);
            await _load;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        InstalledListView.ItemsSource = null;
        InstalledGridView.ItemsSource = null;
        InstalledIconsView.ItemsSource = null;
        CloseIgnoredOverlay();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loaded)
            RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        SyncActiveItemsSource();
        bool showPackages = ViewModel.HasPackages && !ViewModel.IsInitialLoading;
        TableHeaderBar.Visibility = ViewModel.ViewMode == "list" && showPackages ? Visibility.Visible : Visibility.Collapsed;
        InstalledListView.Visibility = ViewModel.ViewMode == "list" && showPackages ? Visibility.Visible : Visibility.Collapsed;
        InstalledGridView.Visibility = ViewModel.ViewMode == "grid" && showPackages ? Visibility.Visible : Visibility.Collapsed;
        InstalledIconsView.Visibility = ViewModel.ViewMode == "icons" && showPackages ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = ViewModel.IsInitialLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        FilterSidebarColumn.Width = new GridLength(ViewModel.IsFiltersVisible ? _filterWidth : 0);
        FilterSplitterColumn.Width = new GridLength(ViewModel.IsFiltersVisible ? 8 : 0);
        FilterSidebarPanel.Visibility = ViewModel.IsFiltersVisible ? Visibility.Visible : Visibility.Collapsed;
        FilterSplitter.Visibility = FilterSidebarPanel.Visibility;
        var sortIcons = new[] { SortNameIcon, SortIdIcon, SortVersionIcon, SortSourceIcon };
        int activeSort = ViewModel.SortColumn switch { "Id" => 1, "Version" => 2, "Source" => 3, _ => 0 };
        for (int index = 0; index < sortIcons.Length; index++)
        {
            sortIcons[index].Visibility = index == activeSort ? Visibility.Visible : Visibility.Collapsed;
            sortIcons[index].Icon = ViewModel.SortAscending ? FluentIcons.Common.Icon.ChevronUp : FluentIcons.Common.Icon.ChevronDown;
        }
        bool filtersActive = !ViewModel.FilterWinget || !ViewModel.FilterStore || !ViewModel.FilterLocal
            || ViewModel.OnlyRemovable || !ViewModel.HideSystemComponents || ViewModel.OnlySelected;
        ToggleFiltersButton.BorderBrush = (Brush)Application.Current.Resources[filtersActive ? "InstallActionBrush" : "ControlStrokeColorDefaultBrush"];
        UninstallSelectedButton.ApplyTemplate();
        VisualStateManager.GoToState(UninstallSelectedButton, ViewModel.CanRemove ? "Normal" : "Disabled", false);

        InstalledViewModeButtonIcon.Symbol = ViewModel.ViewMode == "list" ? Microsoft.UI.Xaml.Controls.Symbol.List : ViewModel.ViewMode == "grid" ? Microsoft.UI.Xaml.Controls.Symbol.ViewAll : Microsoft.UI.Xaml.Controls.Symbol.AllApps;
        InstalledViewModeButton.IsChecked = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(InstalledViewModeButton, ViewModel.ViewMode == "list" ? "Visualização em lista" : ViewModel.ViewMode == "grid" ? "Visualização em cartões" : "Visualização em ícones");
        MasterSelectAllCheckBox.IsChecked = ViewModel.MasterSelection;
        _suppressSort = true;
        SortModeComboBox.SelectedIndex = ViewModel.SortColumn switch
        {
            "Id" => 1,
            "Version" => 2,
            "Source" => 3,
            _ => 0
        };
        _suppressSort = false;
        ToggleFiltersButton.IsChecked = ViewModel.IsFiltersVisible;
        IgnoredUpdatesEmptyText.Visibility = ViewModel.IgnoredUpdates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncActiveItemsSource()
    {
        var packages = ViewModel.VisiblePackages;
        // Only the displayed control needs collection notifications and realized rows.
        if (ViewModel.ViewMode != "list" && InstalledListView.ItemsSource is not null) InstalledListView.ItemsSource = null;
        if (ViewModel.ViewMode != "grid" && InstalledGridView.ItemsSource is not null) InstalledGridView.ItemsSource = null;
        if (ViewModel.ViewMode != "icons" && InstalledIconsView.ItemsSource is not null) InstalledIconsView.ItemsSource = null;
        if (!_loaded) return;
        if (ViewModel.ViewMode == "list" && !ReferenceEquals(InstalledListView.ItemsSource, packages)) InstalledListView.ItemsSource = packages;
        if (ViewModel.ViewMode == "grid" && !ReferenceEquals(InstalledGridView.ItemsSource, packages)) InstalledGridView.ItemsSource = packages;
        if (ViewModel.ViewMode == "icons" && !ReferenceEquals(InstalledIconsView.ItemsSource, packages)) InstalledIconsView.ItemsSource = packages;
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _filterWidth = Math.Min(_filterWidth, Math.Max(220, e.NewSize.Width - 320));
        FilterSidebarColumn.Width = new GridLength(ViewModel.IsFiltersVisible ? _filterWidth : 0);
    }

    private void ToggleFiltersButton_Click(object sender, RoutedEventArgs e) =>
        SetFiltersVisible(ToggleFiltersButton.IsChecked == true);

    private void FilterSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _filterWidth = Math.Clamp(_filterWidth + e.HorizontalChange, 220, Math.Max(220, Math.Min(420, ActualWidth - 320)));
        FilterSidebarColumn.Width = new GridLength(_filterWidth);
    }

    private void SetFiltersVisible(bool visible)
    {
        ViewModel.SetFiltersVisible(visible);
        RefreshPresentation();
    }

    private void SourceFilter_Changed(object sender, RoutedEventArgs e)
    {
        ViewModel.FilterWinget = FilterWingetCheck.IsChecked == true;
        ViewModel.FilterStore = FilterStoreCheck.IsChecked == true;
        ViewModel.FilterLocal = FilterLocalCheck.IsChecked == true;
        FilterAllSourcesCheck.IsChecked = ViewModel.FilterWinget && ViewModel.FilterStore && ViewModel.FilterLocal;
    }

    private void AllSources_Click(object sender, RoutedEventArgs e) => SetSources(FilterAllSourcesCheck.IsChecked == true);
    private void SelectAllSources_Click(object sender, RoutedEventArgs e) => SetSources(true);
    private void ClearSources_Click(object sender, RoutedEventArgs e) => SetSources(false);

    private void SetSources(bool selected)
    {
        FilterAllSourcesCheck.IsChecked = selected;
        FilterWingetCheck.IsChecked = selected;
        FilterStoreCheck.IsChecked = selected;
        FilterLocalCheck.IsChecked = selected;
        ViewModel.FilterWinget = selected;
        ViewModel.FilterStore = selected;
        ViewModel.FilterLocal = selected;
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ViewModel.SearchText = sender.Text;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) =>
        ViewModel.SearchText = e.QueryText;

    private void SearchMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode })
        {
            ViewModel.IsExactSearch = mode == "Exato";
            ViewModel.SearchMode = mode is "Nome" or "ID" ? mode : "Ambos";
        }
    }

    private void MasterSelectAll_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ToggleSelectAllCommand.Execute(null);

    private void SortByName_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Name");
    private void SortById_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Id");
    private void SortByVersion_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Version");
    private void SortBySource_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Source");

    private void SortModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppressSort || SortModeComboBox.SelectedIndex < 0)
            return;
        ViewModel.SetSort(SortModeComboBox.SelectedIndex switch
        {
            1 => "Id",
            2 => "Version",
            3 => "Source",
            _ => "Name"
        }, ascending: true);
    }

    internal void SetViewMode(string mode)
    {
        ViewModel.SetViewMode(mode);
        RefreshPresentation();
    }

    private void InstalledViewMode_Click(object sender, RoutedEventArgs e) => SetViewMode((sender as FrameworkElement)?.Tag as string ?? "list");

    private void PackageRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledPackageRow package } || IsControlSource(e.OriginalSource as DependencyObject))
            return;
        if (package.CanSelect)
            package.IsSelected = !package.IsSelected;
        e.Handled = true;
    }

    private async void PackageRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InstalledPackageRow package })
        {
            e.Handled = true;
            await ViewModel.ShowDetailsAsync(package);
        }
    }

    private async void PackageName_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledPackageRow package)
        {
            e.Handled = true;
            await ViewModel.ShowDetailsAsync(package);
        }
    }

    private async void PackagesView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
        if (focused?.DataContext is not InstalledPackageRow package) return;
        if (e.Key == VirtualKey.Space && package.CanSelect)
        {
            package.IsSelected = !package.IsSelected;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.ShowDetailsAsync(package);
        }
        else if (e.Key == VirtualKey.Application && focused is not null)
        {
            _contextPackage = package;
            var container = (sender as ListViewBase)?.ContainerFromItem(package) as DependencyObject;
            if (container is not null && FindPackageSurface(container) is { } surface)
                FlyoutBase.ShowAttachedFlyout(surface);
            e.Handled = true;
        }
    }

    private static FrameworkElement? FindPackageSurface(DependencyObject root)
    {
        if (root is FrameworkElement { Tag: InstalledPackageRow } element) return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindPackageSurface(VisualTreeHelper.GetChild(root, index)) is { } surface) return surface;
        return null;
    }

    private void PackageRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InstalledPackageRow package } element)
        {
            _contextPackage = package;
            FlyoutBase.ShowAttachedFlyout(element);
            e.Handled = true;
        }
    }

    private async void PackageRow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstalledPackageRow package } element)
            return;
        if (e.Key == VirtualKey.Space && package.CanSelect)
        {
            package.IsSelected = !package.IsSelected;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            await ViewModel.ShowDetailsAsync(package);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Application)
        {
            _contextPackage = package;
            FlyoutBase.ShowAttachedFlyout(element);
            e.Handled = true;
        }
    }

    private static bool IsControlSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { Tag: InstalledPackageRow })
                return false;
            if (source is Control)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private InstalledPackageRow? ContextPackage(object sender) =>
        (sender as FrameworkElement)?.DataContext as InstalledPackageRow
        ?? (sender as MenuFlyoutItem)?.DataContext as InstalledPackageRow
        ?? _contextPackage;

    private async void ContextUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            await ViewModel.RemoveSingleAsync(package);
    }

    private async void ContextInteractiveUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            await ViewModel.RemoveSingleAsync(package, interactive: true);
    }

    private async void ContextUninstallAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            await ViewModel.RemoveSingleAsync(package, requiresElevation: true);
    }

    private void ContextOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            ViewModel.OpenLocation(package);
    }

    private async void ContextDownload_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
        {
            if (!package.CanAddToCollection)
            {
                ViewModel.Status = "O download do instalador está disponível para pacotes WinGet e Microsoft Store.";
                return;
            }
            package.IsSelected = true;
            await ViewModel.DownloadSelectedCommand.ExecuteAsync(null);
        }
    }

    private void ContextIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            ViewModel.Ignore(package);
    }

    private void ContextAutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            ViewModel.ToggleAutoUpdate(package);
    }

    private void ContextMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || _contextPackage is not { } package)
            return;

        UpdateAutoUpdateMenuState(menu, package);
    }

    internal void UpdateAutoUpdateMenuState(MenuFlyout menu, InstalledPackageRow package)
    {
        bool self = WinProvision.Core.Services.ApplicationPackagePolicy.IsSelf(package.Id, package.Name);
        foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
            if (item.Text.StartsWith("Desinst", StringComparison.OrdinalIgnoreCase))
                item.Visibility = self ? Visibility.Collapsed : Visibility.Visible;
        var autoUpdateItem = menu.Items.OfType<ToggleMenuFlyoutItem>().FirstOrDefault();
        if (autoUpdateItem is not null)
        {
            autoUpdateItem.IsChecked = ViewModel.IsAutoUpdateSelected(package);
            autoUpdateItem.Visibility = self ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void ContextAddToCollection_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            ViewModel.AddToCollection(package);
    }

    private void ContextCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            ViewModel.CopyId(package);
    }

    private void ContextCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package) ViewModel.CopyName(package);
    }

    private void ContextCopyUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            ViewModel.CopyManualUninstallCommand(package);
    }

    private async void ContextDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ContextPackage(sender) is { } package)
            await ViewModel.ShowDetailsAsync(package);
    }

    private async void RemoveSelectedInteractive_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RemoveSelectedAsync(interactive: true);

    private async void RemoveSelectedAdmin_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.RemoveSelectedAsync(requiresElevation: true);

    private void AddSelectedToCollection_Click(object sender, RoutedEventArgs e) => ViewModel.AddSelectedToCollection();
    private void IgnoreSelected_Click(object sender, RoutedEventArgs e) => ViewModel.IgnoreSelectedUpdates();

    private async void PackageDetails_Click(object sender, RoutedEventArgs e)
    {
        var package = ViewModel.Packages.FirstOrDefault(item => item.IsSearchMatch && item.IsSelected)
            ?? ViewModel.Packages.FirstOrDefault(item => item.IsSearchMatch);
        if (package is not null)
            await ViewModel.ShowDetailsAsync(package);
        else
            ViewModel.Status = "Selecione um pacote para exibir os detalhes.";
    }

    private void ManageIgnored_Click(object sender, RoutedEventArgs e)
        => ShowIgnoredUpdates();

    internal void ShowIgnoredUpdates()
    {
        ViewModel.RefreshIgnoredUpdates();
        _previousFocus = FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
        IgnoredUpdatesOverlay.Visibility = Visibility.Visible;
        RefreshPresentation();
    }

    private void CloseIgnoredOverlay_Click(object sender, RoutedEventArgs e) => CloseIgnoredOverlay();

    internal void CloseIgnoredOverlay()
    {
        IgnoredUpdatesOverlay.Visibility = Visibility.Collapsed;
        _previousFocus?.Focus(FocusState.Programmatic);
        _previousFocus = null;
    }

    private void DismissIgnoredOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, IgnoredUpdatesOverlay))
            CloseIgnoredOverlay();
    }

    private void IgnoredModalCard_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void RestoreIgnoredUpdate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is IgnoredUpdateEntry entry)
            ViewModel.RestoreIgnored(entry.AppId);
    }

    private void RestoreAllIgnored_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RestoreAllIgnored();
        CloseIgnoredOverlay();
    }

    private async void UninstallOptions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Opções de desinstalação",
            Content = new TextBlock
            {
                Text = "O WinProvision usa o WinGet, a API própria e os comandos de desinstalação registrados no Windows como alternativas.",
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private void ManualUninstall_Click(object sender, RoutedEventArgs e)
    {
        var package = ViewModel.Packages.FirstOrDefault(item => item.IsSearchMatch && item.IsSelected)
            ?? ViewModel.Packages.FirstOrDefault(item => item.IsSearchMatch);
        if (package is null)
        {
            ViewModel.Status = "Selecione um pacote para copiar o comando de desinstalação.";
            return;
        }
        ViewModel.CopyManualUninstallCommand(package);
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && IgnoredUpdatesOverlay.Visibility == Visibility.Visible)
        {
            CloseIgnoredOverlay();
            e.Handled = true;
        }
    }
}
