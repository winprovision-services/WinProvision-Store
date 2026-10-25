using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Views;
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }
    private readonly Dictionary<string, (string Title, string Description, StackPanel Panel)> _categories;
    private readonly Dictionary<string, FrameworkElement> _targets;
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        _categories = new()
        {
            ["Notifications"] = ("Notificações", "Escolha quais avisos o aplicativo pode enviar.", PanelNotifications),
            ["Execution"] = ("Operações e downloads", "Ajuste downloads, limites simultâneos e fontes do WinGet.", PanelExecution),
            ["Elevator"] = ("WinProvision Elevator", "Gerencie pedidos de administrador e a autorização desta sessão.", PanelElevator),
            ["Maintenance"] = ("Manutenção agendada", "Configure verificações e atualizações periódicas.", PanelMaintenance),
            ["General"] = ("Preferências gerais", "Escolha como instalar aplicativos e gerenciar atualizações.", PanelGeneral),
            ["Provisioning"] = ("Provisionamento", "Exiba ou oculte o menu para preparar perfis de configuração do Windows.", PanelProvisioning),
            ["Interface"] = ("Interface e inicialização", "Personalize o tema e a inicialização do aplicativo.", PanelInterface),
            ["Operations"] = ("Armazenamento e cache", "Atualize o catálogo e gerencie o espaço usado pelos caches.", PanelOperations)
        };
        _targets = new() { ["notification-categories"] = NotificationCategoriesCard, ["concurrency"] = ConcurrencyCard, ["installer-downloads"] = InstallerDownloadsCard, ["scheduled-maintenance"] = ScheduledMaintenanceCard, ["elevation-cache"] = ElevationCacheCard, ["install-method"] = InstallMethodCard, ["update-method"] = UpdateMethodCard, ["check-updates"] = AppUpdateCard, ["open-logs"] = PrivacyCard, ["reset-preferences"] = PrivacyCard, ["theme"] = ThemeCard, ["startup"] = StartupCard, ["startup-page"] = StartupPageCard, ["provisioning-menu"] = ProvisioningMenuCard, ["clear-cache"] = CacheCard, ["refresh-catalog"] = CatalogRefreshCard };
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        SizeChanged += (_, _) => SettingsSearchBox.Width = Math.Min(320, Math.Max(160, ActualWidth - 160));
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ThemeChanged;
        RefreshThemeButtons();
        ViewModel.LoadCacheOptions();
        await ViewModel.MeasureCacheCommand.ExecuteAsync(null);
        ViewModel.ReloadScheduledMaintenance();
        await ViewModel.RefreshElevatorCommand.ExecuteAsync(null);
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
        if (key is "Notifications" or "Execution") ViewModel.LoadExecutionOptions();
        if (key == "Operations") ViewModel.LoadCacheOptions();
        if (key == "Elevator") ViewModel.LoadElevatorOptions();
        if (key == "Maintenance") ViewModel.ReloadScheduledMaintenance();
        HomepageScrollViewer.Visibility = Visibility.Collapsed; SubCategoryScrollViewer.Visibility = Visibility.Visible; BackButton.Visibility = Visibility.Visible;
        SettingsTitleTextBlock.Text = category.Title;
        SettingsSubtitleTextBlock.Text = category.Description;
        foreach (var item in _categories.Values) item.Panel.Visibility = ReferenceEquals(item.Panel, category.Panel) ? Visibility.Visible : Visibility.Collapsed;
        SubCategoryScrollViewer.ChangeView(null, 0, null); BackButton.Focus(FocusState.Programmatic);
    }
    private void CategoryButton_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string key }) NavigateToCategory(key); }
    public void GoBack(bool focusCategories = true)
    {
        SubCategoryScrollViewer.Visibility = Visibility.Collapsed; HomepageScrollViewer.Visibility = Visibility.Visible; BackButton.Visibility = Visibility.Collapsed; SettingsTitleTextBlock.Text = "Configurações";
        SettingsSubtitleTextBlock.Text = "Controle as preferências do aplicativo, da interface e das operações.";
        foreach (var item in _categories.Values) item.Panel.Visibility = Visibility.Collapsed;
        if (focusCategories) CardGeneral.Focus(FocusState.Programmatic);
    }
    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();
    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e) { if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { ViewModel.SearchText = sender.Text; if (SubCategoryScrollViewer.Visibility == Visibility.Visible) GoBack(focusCategories: false); } }
    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs e) { ViewModel.SearchText = e.QueryText; GoBack(focusCategories: false); }
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
    private void SearchOnDemandSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && SearchOnDemandSwitch.IsOn != ViewModel.SearchOnDemand)
            ViewModel.SearchOnDemand = SearchOnDemandSwitch.IsOn;
    }
    private void ProvisioningMenuSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && ProvisioningMenuSwitch.IsOn != ViewModel.ProvisioningMenuEnabled)
            ViewModel.ProvisioningMenuEnabled = ProvisioningMenuSwitch.IsOn;
    }
}
