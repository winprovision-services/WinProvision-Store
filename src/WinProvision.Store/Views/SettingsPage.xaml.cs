using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Views;
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private readonly Dictionary<string, (string Title, StackPanel Panel)> _categories;
    private readonly Dictionary<string, Border> _targets;
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        _categories = new() { ["General"] = ("Preferências gerais", PanelGeneral), ["Interface"] = ("Interface e inicialização", PanelInterface), ["Operations"] = ("Armazenamento e cache", PanelOperations) };
        _targets = new() { ["install-method"] = InstallMethodCard, ["check-updates"] = AppUpdateCard, ["open-logs"] = PrivacyCard, ["reset-preferences"] = PrivacyCard, ["theme"] = ThemeCard, ["startup"] = StartupCard, ["clear-cache"] = CacheCard, ["refresh-catalog"] = CatalogRefreshCard };
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        SizeChanged += (_, _) => SettingsSearchBox.Width = Math.Min(320, Math.Max(160, ActualWidth - 160));
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ThemeChanged;
        RefreshThemeButtons();
        if (!App.IsSmokeTest)
        {
            try { await ViewModel.EnsureUpdateCheckAsync(); }
            catch (Exception exception) { Services.WinProvisionLog.Warning(exception, "Settings.AutomaticUpdateCheck"); }
        }
    }
    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.PropertyChanged -= ThemeChanged;
    private void ThemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(SettingsViewModel.SelectedThemeOption)) RefreshThemeButtons(); }
    private void RefreshThemeButtons()
    {
        LightThemeButton.Style = (Style)Application.Current.Resources[ViewModel.SelectedThemeOption?.Preference == Services.AppThemePreference.Light ? "AccentButtonStyle" : "StoreActionButtonStyle"];
        DarkThemeButton.Style = (Style)Application.Current.Resources[ViewModel.SelectedThemeOption?.Preference == Services.AppThemePreference.Dark ? "AccentButtonStyle" : "StoreActionButtonStyle"];
        SystemThemeButton.Style = (Style)Application.Current.Resources[ViewModel.SelectedThemeOption?.Preference == Services.AppThemePreference.System ? "AccentButtonStyle" : "StoreActionButtonStyle"];
    }
    public void NavigateToCategory(string key)
    {
        if (!_categories.TryGetValue(key, out var category)) return;
        HomepageScrollViewer.Visibility = Visibility.Collapsed; SubCategoryScrollViewer.Visibility = Visibility.Visible; BackButton.Visibility = Visibility.Visible;
        SettingsTitleTextBlock.Text = category.Title;
        foreach (var item in _categories.Values) item.Panel.Visibility = ReferenceEquals(item.Panel, category.Panel) ? Visibility.Visible : Visibility.Collapsed;
        SubCategoryScrollViewer.ChangeView(null, 0, null); BackButton.Focus(FocusState.Programmatic);
    }
    private void CategoryButton_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string key }) NavigateToCategory(key); }
    private void CategoryCard_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key is VirtualKey.Enter or VirtualKey.Space && sender is FrameworkElement { Tag: string key }) { NavigateToCategory(key); e.Handled = true; } }
    public void GoBack()
    {
        SubCategoryScrollViewer.Visibility = Visibility.Collapsed; HomepageScrollViewer.Visibility = Visibility.Visible; BackButton.Visibility = Visibility.Collapsed; SettingsTitleTextBlock.Text = "Configurações";
        foreach (var item in _categories.Values) item.Panel.Visibility = Visibility.Collapsed;
        CardGeneral.Focus(FocusState.Programmatic);
    }
    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();
    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e) { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { ViewModel.SearchText = sender.Text; if (SubCategoryScrollViewer.Visibility == Visibility.Visible) GoBack(); } }
    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) { ViewModel.SearchText = e.QueryText; GoBack(); }
    private void SearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingSearchResult result })
        {
            NavigateToCategory(result.CategoryKey);
            DispatcherQueue.TryEnqueue(() => { if (_targets.TryGetValue(result.Key, out var target)) { target.StartBringIntoView(); } });
        }
    }
    private void LaunchAtStartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && LaunchAtStartupSwitch.IsOn != ViewModel.LaunchAtStartup)
            ViewModel.ChangeStartup(LaunchAtStartupSwitch.IsOn);
    }
}
