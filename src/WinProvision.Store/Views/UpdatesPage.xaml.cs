using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Views;
public sealed partial class UpdatesPage : Page
{
    public UpdatesViewModel ViewModel { get; }
    private readonly IPresentationService _presentation;
    private readonly IClipboardService _clipboard;
    private bool _loaded, _suppressSort, _suppressToggle;
    private FrameworkElement? _previousFocus;
    private Task? _load;
    public Task InitialContentReady => _load ?? Task.CompletedTask;
    public UpdatesPage(UpdatesViewModel viewModel, IPresentationService presentation, IClipboardService clipboard)
    {
        ViewModel = viewModel; _presentation = presentation; _clipboard = clipboard;
        InitializeComponent(); DataContext = viewModel;
        IgnoredUpdatesModalList.ItemsSource = viewModel.IgnoredPackages;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _loaded = true; ViewModel.Changed += ViewModel_Changed; ViewModel.PropertyChanged += ModelPropertyChanged; RefreshPresentation();
        if (!App.IsSmokeTest) { _load = LoadAsync(); await _load; }
    }
    private async Task LoadAsync()
    {
        try { await ViewModel.CheckUpdatesAsync(); _suppressToggle = true; if(!Environment.GetCommandLineArgs().Contains("--capture-updates")) await ViewModel.LoadAutoUpdateStateAsync(); }
        catch (Exception ex) { WinProvisionLog.Warning(ex,"UpdatesPage.Load"); ViewModel.StatusText = ex.Message; }
        finally { _suppressToggle = false; }
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e) { _loaded = false; ViewModel.Changed -= ViewModel_Changed; ViewModel.PropertyChanged -= ModelPropertyChanged; CloseModal(); }
    private void ModelPropertyChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName is nameof(UpdatesViewModel.IsInitialLoading) or nameof(UpdatesViewModel.IsEmpty)) RefreshPresentation();
        if(e.PropertyName==nameof(UpdatesViewModel.CanUpdate)) DispatcherQueue.TryEnqueue(RefreshUpdateAction);
    }
    private void RefreshUpdateAction()
    {
        UpdateSelectedButton.IsEnabled=ViewModel.CanUpdate;
        UpdateSelectedButton.ApplyTemplate();
        VisualStateManager.GoToState(UpdateSelectedButton,ViewModel.CanUpdate?"Normal":"Disabled",false);
    }
    private void ViewModel_Changed(object? sender, EventArgs e) => RefreshPresentation();
    private void RefreshPresentation()
    {
        RefreshUpdateAction();
        bool show = !ViewModel.IsEmpty && !ViewModel.IsInitialLoading;
        EmptyStatePanel.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        TableHeaderBar.Visibility = UpdatesTableView.Visibility = show && ViewModeListRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdatesGridView.Visibility = show && ViewModeGridRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdatesIconsView.Visibility = show && ViewModeIconsRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var icons = new[] { SortNameIcon, SortIdIcon, SortVersionIcon, SortNewVersionIcon, SortSourceIcon };
        for (int i=0;i<icons.Length;i++) { icons[i].Visibility = (int)ViewModel.CurrentSort == i ? Visibility.Visible : Visibility.Collapsed; icons[i].Glyph = ViewModel.SortAscending ? "\uE74A" : "\uE74B"; }
        _suppressSort = true; SortModeComboBox.SelectedIndex = (int)ViewModel.CurrentSort; _suppressSort = false;
    }
    private void ApplyFilters()
    {
        if (!_loaded) return;
        ViewModel.FilterWinget = SourceWingetCheck.IsChecked == true; ViewModel.FilterMsStore = SourceMsStoreCheck.IsChecked == true; ViewModel.FilterOther = SourceOtherCheck.IsChecked == true;
        ViewModel.HideSameVersion = HideSameVersionCheck.IsChecked == true; ViewModel.SearchName = SearchModeNameRadio.IsChecked == true; ViewModel.SearchId = SearchModeIdRadio.IsChecked == true; ViewModel.ExactSearch = SearchModeExactRadio.IsChecked == true;
        AllSourcesChip.IsChecked = ViewModel.FilterWinget && ViewModel.FilterMsStore && ViewModel.FilterOther;
        Controls.UpdateSelection.SetFiltersActive(ToggleFiltersButton, AllSourcesChip.IsChecked != true || !ViewModel.HideSameVersion);
        ViewModel.ApplyFilters();
    }
    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e) => await ViewModel.CheckUpdatesAsync();
    private void HeaderSortName_Click(object sender,RoutedEventArgs e)=>ViewModel.SetSort(UpdatesViewModel.SortField.Name);
    private void HeaderSortId_Click(object sender,RoutedEventArgs e)=>ViewModel.SetSort(UpdatesViewModel.SortField.Id);
    private void HeaderSortVersion_Click(object sender,RoutedEventArgs e)=>ViewModel.SetSort(UpdatesViewModel.SortField.Version);
    private void HeaderSortNewVersion_Click(object sender,RoutedEventArgs e)=>ViewModel.SetSort(UpdatesViewModel.SortField.NewVersion);
    private void HeaderSortSource_Click(object sender,RoutedEventArgs e)=>ViewModel.SetSort(UpdatesViewModel.SortField.Source);
    private void SortModeComboBox_SelectionChanged(object sender,SelectionChangedEventArgs e) { if (_loaded && !_suppressSort && SortModeComboBox.SelectedIndex >=0) ViewModel.SetSort((UpdatesViewModel.SortField)SortModeComboBox.SelectedIndex,true); }
    private void MasterSelectCheckBox_Click(object sender,RoutedEventArgs e)=>ViewModel.SelectAll(MasterSelectCheckBox.IsChecked==true);
    private void ToggleSelectAllToolbarButton_Click(object sender,RoutedEventArgs e)=>ViewModel.ToggleAll();
    private void PackageRow_Click(object sender,TappedRoutedEventArgs e) { if (sender is FrameworkElement { Tag:UpgradablePackage p }) { ViewModel.ToggleSelection(p); e.Handled=true; } }
    private async void PackageRow_DoubleTapped(object sender,DoubleTappedRoutedEventArgs e) { if (sender is FrameworkElement { Tag:UpgradablePackage p }) { e.Handled=true; await OpenDetailsAsync(p); } }
    private void PackageRow_RightClick(object sender,RightTappedRoutedEventArgs e) { if (sender is FrameworkElement { Tag:UpgradablePackage p } element) { ViewModel.SelectedPackage=p; FlyoutBase.ShowAttachedFlyout(element); e.Handled=true; } }
    private async void PackageRow_KeyDown(object sender,KeyRoutedEventArgs e)
    {
        if(sender is not FrameworkElement { Tag:UpgradablePackage p } element) return;
        if(e.Key==VirtualKey.Space) { ViewModel.ToggleSelection(p); e.Handled=true; }
        else if(e.Key==VirtualKey.Enter) { e.Handled=true; await OpenDetailsAsync(p); }
        else if(e.Key==VirtualKey.Application) { ViewModel.SelectedPackage=p; FlyoutBase.ShowAttachedFlyout(element); e.Handled=true; }
    }
    internal void SetViewMode(string mode) { ViewModeListRadio.IsChecked=mode=="list"; ViewModeGridRadio.IsChecked=mode=="grid"; ViewModeIconsRadio.IsChecked=mode=="icons"; RefreshPresentation(); }
    private void ViewModeList_Click(object sender,RoutedEventArgs e)=>SetViewMode("list");
    private void ViewModeGrid_Click(object sender,RoutedEventArgs e)=>SetViewMode("grid");
    private void ViewModeIcons_Click(object sender,RoutedEventArgs e)=>SetViewMode("icons");
    internal void SetFiltersVisible(bool visible) { ToggleFiltersButton.IsChecked=visible; FilterSidebarColumn.Width=new(visible?260:0); FilterSplitterColumn.Width=new(visible?8:0); FilterSidebarPanel.Visibility=FilterSplitter.Visibility=visible?Visibility.Visible:Visibility.Collapsed; }
    private void ToggleFiltersButton_Click(object sender,RoutedEventArgs e)=>SetFiltersVisible(ToggleFiltersButton.IsChecked==true);
    private void MoreActionsButton_Click(object sender,RoutedEventArgs e)=>FlyoutBase.ShowAttachedFlyout(MoreActionsButton);
    private void SetSources(bool selected) { ViewModel.FilterWinget=ViewModel.FilterMsStore=ViewModel.FilterOther=selected; SourceWingetCheck.IsChecked=SourceMsStoreCheck.IsChecked=SourceOtherCheck.IsChecked=selected; ApplyFilters(); }
    private void AllSourcesChip_Click(object sender,RoutedEventArgs e)=>SetSources(AllSourcesChip.IsChecked==true);
    private void SelectAllSources_Click(object sender,RoutedEventArgs e)=>SetSources(true);
    private void ClearSources_Click(object sender,RoutedEventArgs e)=>SetSources(false);
    private void SourceFilter_Changed(object sender,RoutedEventArgs e)=>ApplyFilters();
    private void FilterOption_Changed(object sender,RoutedEventArgs e)=>ApplyFilters();
    private void SearchMode_Changed(object sender,RoutedEventArgs e)=>ApplyFilters();
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender,AutoSuggestBoxQuerySubmittedEventArgs e) { ViewModel.SearchText=e.QueryText; ApplyFilters(); }
    private async void UpdateSelectedButton_Click(SplitButton sender,SplitButtonClickEventArgs e)=>await ViewModel.UpdateSelectedCommand.ExecuteAsync(null);
    private async void UpdateVariant_Click(object sender,RoutedEventArgs e)
    {
        if(sender is MenuFlyoutItem { Tag:string variant }) await ViewModel.RunUpdateOnPackagesAsync(ViewModel.Packages.Where(p=>p.IsSelectedForUpdate).ToList(),variant=="Admin",variant=="Interactive",false);
    }
    private UpgradablePackage? ContextPackage(object sender) => (sender as FrameworkElement)?.DataContext as UpgradablePackage ?? ViewModel.SelectedPackage;
    private async void ContextMenuUpdate_Click(object sender,RoutedEventArgs e) { if(ContextPackage(sender) is {} p) await ViewModel.RunUpdateOnPackagesAsync([p]); }
    private async void ContextMenuUpdateAdmin_Click(object sender,RoutedEventArgs e) { if(ContextPackage(sender) is {} p) await ViewModel.RunUpdateOnPackagesAsync([p],elevated:true); }
    private async void ContextMenuUpdateInteractive_Click(object sender,RoutedEventArgs e) { if(ContextPackage(sender) is {} p) await ViewModel.RunUpdateOnPackagesAsync([p],interactive:true); }
    private async void ContextMenuDetails_Click(object sender,RoutedEventArgs e) { if(ContextPackage(sender) is {} p) await OpenDetailsAsync(p); }
    private void ContextMenuIgnoreVersion_Click(object sender,RoutedEventArgs e) { if(ContextPackage(sender) is {} p) ViewModel.Ignore((WinProvision.Core.Models.UpgradablePackage[])[p]); }
    private void ContextMenuCopyId_Click(object sender,RoutedEventArgs e) { if(ContextPackage(sender) is {} p) ViewModel.StatusText=_clipboard.TrySetText(p.Id)?$"ID '{p.Id}' copiado para a área de transferência.":"Não foi possível copiar o ID."; }
    private Task OpenDetailsAsync(UpgradablePackage p)=>_presentation.ShowDetailsAsync(ViewModel.ToAppEntry(p),p.CurrentVersion,p.AvailableVersion);
    private async void PackageDetailsToolbarButton_Click(object sender,RoutedEventArgs e) { var p=ViewModel.Packages.Contains(ViewModel.SelectedPackage!)?ViewModel.SelectedPackage:ViewModel.Packages.FirstOrDefault(p=>p.IsSelectedForUpdate)??ViewModel.Packages.FirstOrDefault(); if(p is not null) await OpenDetailsAsync(p); }
    private void IgnoreSelectedButton_Click(object sender,RoutedEventArgs e)=>ViewModel.Ignore(ViewModel.Packages.Where(p=>p.IsSelectedForUpdate).ToArray());
    internal void ShowModal(string modal)
    {
        CloseModal(); _previousFocus=FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
        if(modal=="ignored") { ViewModel.RefreshIgnoredUpdates(); IgnoredUpdatesOverlay.Visibility=Visibility.Visible; }
        if(modal=="manual") { ManualPackageIdBox.Text=ViewModel.SelectedPackage?.Id??""; ManualUpdateOverlay.Visibility=Visibility.Visible; ManualPackageIdBox.Focus(FocusState.Programmatic); }
        if(modal=="options") { LoadOptions(); OptionsOverlay.Visibility=Visibility.Visible; UpdateScheduleComboBox.Focus(FocusState.Programmatic); }
    }
    internal bool CloseModal()
    {
        bool wasOpen=IgnoredUpdatesOverlay.Visibility==Visibility.Visible||ManualUpdateOverlay.Visibility==Visibility.Visible||OptionsOverlay.Visibility==Visibility.Visible;
        IgnoredUpdatesOverlay.Visibility=ManualUpdateOverlay.Visibility=OptionsOverlay.Visibility=Visibility.Collapsed;
        if(wasOpen) _previousFocus?.Focus(FocusState.Programmatic);
        return wasOpen;
    }
    private void ManageIgnoredButton_Click(object sender,RoutedEventArgs e)=>ShowModal("ignored");
    private void ManualUpdateButton_Click(object sender,RoutedEventArgs e)=>ShowModal("manual");
    private void OptionsButton_Click(object sender,RoutedEventArgs e)=>ShowModal("options");
    private void CloseIgnoredOverlay_Click(object sender,RoutedEventArgs e)=>CloseModal();
    private void CloseManualOverlay_Click(object sender,RoutedEventArgs e)=>CloseModal();
    private void CloseOptionsOverlay_Click(object sender,RoutedEventArgs e)=>CloseModal();
    private async void RestoreIgnoredUpdate_Click(object sender,RoutedEventArgs e) { if(sender is FrameworkElement { DataContext:IgnoredUpdateEntry p }) await ViewModel.RestoreAsync((WinProvision.Core.Services.IgnoredUpdateEntry[])[p]); }
    private async void RestoreAllIgnored_Click(object sender,RoutedEventArgs e) { CloseModal(); await ViewModel.RestoreAsync(ViewModel.IgnoredPackages.ToArray()); }
    private async void ExportCsvButton_Click(object sender,RoutedEventArgs e) { try { if(await _presentation.PickSaveAsync($"Atualizacoes_{DateTime.Now:yyyyMMdd_HHmm}.csv") is {} path) ViewModel.ExportCsv(path); } catch(Exception ex) { ViewModel.StatusText=$"Falha ao exportar: {ex.Message}"; } }
    private async void ExecuteManualUpdate_Click(object sender,RoutedEventArgs e) { CloseModal(); await ViewModel.UpdateManualAsync(ManualPackageIdBox.Text); }
    private void LoadOptions()
    {
        _suppressToggle=true; AutoUpdateSwitch.IsOn=ViewModel.AutoUpdateEnabled; SelectAllByDefaultSwitch.IsOn=ViewModel.SelectAllByDefault; _suppressToggle=false;
        UpdateScheduleComboBox.SelectedValue=ViewModel.AutomationSchedule; UpdateAutomationModeComboBox.SelectedValue=ViewModel.AutomationMode; UpdateAutomationScopeComboBox.SelectedValue=ViewModel.AutomationScope;
        UpdateScheduleDayComboBox.SelectedValue=ViewModel.AutomationDay; UpdateScheduleTimeBox.Text=ViewModel.AutomationTime; AutoUpdatePackageSelectionText.Text=ViewModel.AutomationSelectionText;
    }
    private void UpdateScheduleComboBox_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        string? schedule = UpdateScheduleComboBox.SelectedValue as string;
        if (UpdateScheduleDayComboBox is not null) UpdateScheduleDayComboBox.Visibility = schedule == "Weekly" ? Visibility.Visible : Visibility.Collapsed;
        if (UpdateScheduleDetailsPanel is not null) UpdateScheduleDetailsPanel.Visibility = schedule is "Daily" or "Weekly" ? Visibility.Visible : Visibility.Collapsed;
        if (UpdateScheduleIntervalText is not null) UpdateScheduleIntervalText.Visibility = schedule == "Interval" ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ApplyAutomationOptions_Click(object sender,RoutedEventArgs e) { if(await ViewModel.ApplyAutomationOptionsAsync(UpdateScheduleComboBox.SelectedValue as string??"",UpdateAutomationModeComboBox.SelectedValue as string??"",UpdateAutomationScopeComboBox.SelectedValue as string??"",UpdateScheduleDayComboBox.SelectedValue as string??"",UpdateScheduleTimeBox.Text)) CloseModal(); }
    private async void AutoUpdateSwitch_Toggled(object sender,RoutedEventArgs e) { if(!_loaded||_suppressToggle)return; await ViewModel.SetAutoUpdateAsync(AutoUpdateSwitch.IsOn); _suppressToggle=true; AutoUpdateSwitch.IsOn=ViewModel.AutoUpdateEnabled; _suppressToggle=false; }
    private void SelectAllByDefaultSwitch_Toggled(object sender,RoutedEventArgs e) { if(_loaded&&!_suppressToggle) ViewModel.SelectAllByDefault=SelectAllByDefaultSwitch.IsOn; }
    private void DismissModalOverlay_Click(object sender,TappedRoutedEventArgs e)=>CloseModal();
    private void ModalCard_Click(object sender,TappedRoutedEventArgs e)=>e.Handled=true;
    private void Page_KeyDown(object sender,KeyRoutedEventArgs e) { if(e.Key==VirtualKey.Escape&&CloseModal())e.Handled=true; }
}
