using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FluentIcons.Common;
using Windows.System;
using WinProvision.Core.Models;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class CollectionsPage : Page
{
    private void ViewModeMain_Click(SplitButton sender, SplitButtonClickEventArgs e) { ViewModel.SetViewMode(ViewModel.ViewMode == "list" ? "grid" : ViewModel.ViewMode == "grid" ? "icons" : "list"); }

    private bool _loaded;
    private bool _suppressSort;
    private double _filterWidth = 260;
    private FrameworkElement? _contextElement;

    public CollectionsViewModel ViewModel { get; }
    public AccountSyncViewModel Account { get; }

    public CollectionsPage(CollectionsViewModel viewModel, AccountSyncViewModel account)
    {
        Account = account;
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Account.Refresh();
        RefreshPresentation();
        if (!App.IsSmokeTest)
            ViewModel.StartMetrics();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.CancelMetrics();
        CloseProfileEditor();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loaded)
            RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        bool showItems = ViewModel.HasItems && !ViewModel.HasNoResults && !ViewModel.IsExportingOffline;
        TableHeaderBar.Visibility = ViewModel.ViewMode == "list" && showItems ? Visibility.Visible : Visibility.Collapsed;
        CollectionListView.Visibility = ViewModel.ViewMode == "list" && showItems ? Visibility.Visible : Visibility.Collapsed;
        CollectionGridView.Visibility = ViewModel.ViewMode == "grid" && showItems ? Visibility.Visible : Visibility.Collapsed;
        CollectionIconsView.Visibility = ViewModel.ViewMode == "icons" && showItems ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = ViewModel.IsEmpty && !ViewModel.IsExportingOffline ? Visibility.Visible : Visibility.Collapsed;
        NoResultsState.Visibility = ViewModel.HasNoResults && !ViewModel.IsExportingOffline ? Visibility.Visible : Visibility.Collapsed;
        LoadingState.Visibility = ViewModel.IsExportingOffline ? Visibility.Visible : Visibility.Collapsed;
        FilterSidebarColumn.Width = new GridLength(ViewModel.IsFiltersVisible ? _filterWidth : 0);
        FilterSplitterColumn.Width = new GridLength(ViewModel.IsFiltersVisible ? 8 : 0);
        FilterSidebarPanel.Visibility = ViewModel.IsFiltersVisible ? Visibility.Visible : Visibility.Collapsed;
        FilterSplitter.Visibility = FilterSidebarPanel.Visibility;
        ToggleFiltersButton.IsChecked = ViewModel.IsFiltersVisible;
        CollectionViewModeButtonIcon.Icon = ViewModel.ViewMode == "list" ? Icon.TextBulletList : ViewModel.ViewMode == "grid" ? Icon.Grid : Icon.Apps;
        CollectionViewModeButton.IsChecked = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CollectionViewModeButton, ViewModel.ViewMode == "list" ? "Visualização em lista" : ViewModel.ViewMode == "grid" ? "Visualização em cartões" : "Visualização em ícones");
        MasterSelectionCheckBox.IsChecked = ViewModel.MasterSelection;
        InterruptionButton.Visibility = ViewModel.IsBusy && !ViewModel.IsExportingOffline ? Visibility.Visible : Visibility.Collapsed;
        OfflineProgressBar.Value = ViewModel.OfflineProgress;
        OfflineProgressBar.IsIndeterminate = ViewModel.OfflineProgressIndeterminate;
        _suppressSort = true;
        SortModeComboBox.SelectedIndex = ViewModel.SortField switch
        {
            "Id" => 1,
            "Version" => 2,
            "Source" => 3,
            _ => 0
        };
        _suppressSort = false;
        var sortIcons = new[] { SortNameIcon, SortIdIcon, SortVersionIcon, SortSourceIcon };
        int activeSort = SortModeComboBox.SelectedIndex;
        for (int index = 0; index < sortIcons.Length; index++)
        {
            sortIcons[index].Visibility = index == activeSort ? Visibility.Visible : Visibility.Collapsed;
            sortIcons[index].Icon = ViewModel.SortAscending ? Icon.ChevronUp : Icon.ChevronDown;
        }
        InstallSelectedButton.ApplyTemplate();
        VisualStateManager.GoToState(InstallSelectedButton, ViewModel.CanInstall ? "Normal" : "Disabled", false);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _filterWidth = Math.Min(_filterWidth, Math.Max(220, e.NewSize.Width - 320));
        FilterSidebarColumn.Width = new GridLength(ViewModel.IsFiltersVisible ? _filterWidth : 0);
    }

    private void ToggleFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SetFiltersVisible(ToggleFiltersButton.IsChecked == true);
    }

    private void FilterSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _filterWidth = Math.Clamp(_filterWidth + e.HorizontalChange, 220, Math.Max(220, Math.Min(420, ActualWidth - 320)));
        FilterSidebarColumn.Width = new GridLength(_filterWidth);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ViewModel.SearchText = sender.Text;
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) => ViewModel.SearchText = e.QueryText;

    private void SourceAll_Click(object sender, RoutedEventArgs e) =>
        SetSources(SourceAllCheckBox.IsChecked == true);

    private void SourceFilter_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SourceWinget = SourceWingetCheckBox.IsChecked == true;
        ViewModel.SourceMsStore = SourceStoreCheckBox.IsChecked == true;
        ViewModel.SourceOffice = SourceOfficeCheckBox.IsChecked == true;
        ViewModel.AllSources = ViewModel.SourceWinget && ViewModel.SourceMsStore && ViewModel.SourceOffice;
        SourceAllCheckBox.IsChecked = ViewModel.AllSources;
    }

    private void SelectAllSources_Click(object sender, RoutedEventArgs e) => SetSources(true);
    private void ClearSources_Click(object sender, RoutedEventArgs e) => SetSources(false);

    private void SetSources(bool selected)
    {
        SourceAllCheckBox.IsChecked = selected;
        SourceWingetCheckBox.IsChecked = selected;
        SourceStoreCheckBox.IsChecked = selected;
        SourceOfficeCheckBox.IsChecked = selected;
        ViewModel.AllSources = selected;
        ViewModel.SourceWinget = selected;
        ViewModel.SourceMsStore = selected;
        ViewModel.SourceOffice = selected;
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        SetSources(true);
        ViewModel.ClearFiltersCommand.Execute(null);
    }

    private void MasterSelection_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SelectAllCommand.Execute(null);

    private void SortName_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Name");
    private void SortId_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Id");
    private void SortVersion_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Version");
    private void SortSource_Click(object sender, RoutedEventArgs e) => ViewModel.SetSort("Source");

    private void SortMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    private void CollectionViewMode_Click(object sender, RoutedEventArgs e) => ViewModel.SetViewMode((sender as FrameworkElement)?.Tag as string ?? "list");

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProfile is not { IsDefault: false } profile)
            return;
        RenameTextBox.Text = profile.Title;
        AdditionalFiltersExpander.IsExpanded = true;
        ViewProfilePanel.Visibility = Visibility.Collapsed;
        RenamePanel.Visibility = Visibility.Visible;
        RenameTextBox.Focus(FocusState.Programmatic);
        RenameTextBox.SelectAll();
    }

    private void SaveProfileName_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.RenameProfile(RenameTextBox.Text);
        if (!ViewModel.HasError && ViewModel.StatusText.StartsWith("Coleção renomeada", StringComparison.Ordinal))
            CloseProfileEditor();
    }

    private void CancelProfileName_Click(object sender, RoutedEventArgs e) => CloseProfileEditor();

    private void CloseProfileEditor()
    {
        RenamePanel.Visibility = Visibility.Collapsed;
        ViewProfilePanel.Visibility = Visibility.Visible;
    }

    private void RenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            SaveProfileName_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CloseProfileEditor();
            e.Handled = true;
        }
    }

    private async void InstallOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string option })
            return;
        await ViewModel.InstallSelectedWithOptionsAsync(GetInstallOptions(option));
    }

    private async void ContextInstallOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string option } || GetItem(sender) is not { } item)
            return;
        await ViewModel.InstallItemWithOptionsAsync(item, GetInstallOptions(option));
    }

    private static PackageInstallOptions GetInstallOptions(string option) => option switch
    {
        "Admin" => new PackageInstallOptions(RequiresElevation: true, Scope: "machine"),
        "Interactive" => new PackageInstallOptions(Interactive: true),
        _ => new PackageInstallOptions()
    };

    private async void ReloadMetrics_Click(object sender, RoutedEventArgs e) => ViewModel.StartMetrics();

    private async void PackagesView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is not SelectorItem { DataContext: PackageItemViewModel item } container) return;
        if (e.Key == VirtualKey.Space) { item.IsSelected = !item.IsSelected; e.Handled = true; }
        else if (e.Key == VirtualKey.Enter) { e.Handled = true; await item.DetailsCommand.ExecuteAsync(null); }
        else if (e.Key == VirtualKey.Application && container.ContentTemplateRoot is FrameworkElement surface)
        {
            _contextElement = surface;
            FlyoutBase.ShowAttachedFlyout(surface);
            e.Handled = true;
        }
    }

    private PackageItemViewModel? GetItem(object sender) =>
        (sender as FrameworkElement)?.DataContext as PackageItemViewModel
        ?? (sender as FrameworkElement)?.Tag as PackageItemViewModel
        ?? _contextElement?.DataContext as PackageItemViewModel;

    private void PackageRow_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PackageItemViewModel item } row || IsInteractive(e.OriginalSource as DependencyObject, row))
            return;
        item.IsSelected = !item.IsSelected;
        e.Handled = true;
    }

    private async void PackageRow_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (GetItem(sender) is { } item)
        {
            e.Handled = true;
            await item.DetailsCommand.ExecuteAsync(null);
        }
    }

    private void PackageRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;
        _contextElement = element;
        FlyoutBase.ShowAttachedFlyout(element);
        e.Handled = true;
    }

    private async void PackageRow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (GetItem(sender) is not { } item || sender is not FrameworkElement element || IsInteractive(e.OriginalSource as DependencyObject, element))
            return;
        if (e.Key == VirtualKey.Space)
        {
            item.IsSelected = !item.IsSelected;
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            await item.DetailsCommand.ExecuteAsync(null);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Application)
        {
            _contextElement = element;
            FlyoutBase.ShowAttachedFlyout(element);
            e.Handled = true;
        }
    }

    private void ContextCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (GetItem(sender) is { } item)
            ViewModel.CopyId(item.Id);
    }

    private static bool IsInteractive(DependencyObject? source, DependencyObject boundary)
    {
        for (DependencyObject? current = source; current is not null && !ReferenceEquals(current, boundary);)
        {
            if (current is ButtonBase or TextBox or ComboBox or CheckBox)
                return true;
            current = current is UIElement element ? VisualTreeHelper.GetParent(element) : null;
        }
        return false;
    }

    private async void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        PackageItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate => candidate.IsSelected) ?? ViewModel.Items.FirstOrDefault();
        if (item is not null)
            await item.DetailsCommand.ExecuteAsync(null);
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && RenamePanel.Visibility == Visibility.Visible)
        {
            CloseProfileEditor();
            e.Handled = true;
        }
    }
}
