using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Views;
public sealed partial class SettingsPage : Page, ITitleBarSearchTarget
{
    public SettingsViewModel ViewModel { get; }
    private CancellationTokenSource _pageLifetimeCancellation = new();
    private DispatcherQueueTimer? _maintenanceApplyTimer;
    private readonly Dictionary<string, (string Title, string Description, StackPanel Panel)> _categories;
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        _categories = new()
        {
            ["Notifications"] = ("Notificações", "Escolha quais avisos o aplicativo pode enviar.", PanelNotifications),
            ["Execution"] = ("Operações e downloads", "Ajuste os limites simultâneos e os downloads de instaladores.", PanelExecution),
            ["Elevator"] = ("WinProvision Elevator", "Gerencie pedidos de administrador e a autorização desta sessão.", PanelElevator),
            ["Developer"] = ("Configurações de Desenvolvedor", "Compartilhe instaladores pela rede local ou sincronize uma pasta de outro computador.", PanelDeveloper),
            ["Maintenance"] = ("Manutenção agendada", "Configure verificações e atualizações periódicas.", PanelMaintenance),
            ["General"] = ("Preferências gerais", "Gerencie o atualizador do aplicativo e as configurações gerais.", PanelGeneral),
            ["Provisioning"] = ("Provisionamento", "Exiba ou oculte o menu para preparar perfis de configuração do Windows.", PanelProvisioning),
            ["Interface"] = ("Interface e inicialização", "Personalize o tema e a inicialização do aplicativo.", PanelInterface),
            ["Operations"] = ("Armazenamento e cache", "Atualize o catálogo e gerencie o espaço usado pelos caches.", PanelOperations)
        };
        Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    bool ITitleBarSearchTarget.SearchContextAvailable => true;
    string ITitleBarSearchTarget.SearchText
    {
        get => ViewModel.SearchText;
        set => ApplyTitleBarSearchText(value);
    }
    Task ITitleBarSearchTarget.SubmitSearchAsync(string query)
    {
        ApplyTitleBarSearchText(query);
        return Task.CompletedTask;
    }
    public event EventHandler? SearchContextChanged;
    public event EventHandler? PackageManagersNavigationRequested;
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pageLifetimeCancellation.IsCancellationRequested)
            _pageLifetimeCancellation = new();
        ViewModel.PropertyChanged += ThemeChanged;
        ViewModel.PropertyChanged += LanClientTokenChanged;
        ViewModel.PropertyChanged += LanServerTokenChanged;
        ViewModel.PropertyChanged += MaintenanceBusyChanged;
        ViewModel.ScheduledMaintenanceOptionsChanged += ScheduledMaintenanceOptionsChanged;
        if (_maintenanceApplyTimer is null)
        {
            _maintenanceApplyTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _maintenanceApplyTimer.Tick += MaintenanceApplyTimer_Tick;
        }
        _maintenanceApplyTimer.Interval = TimeSpan.FromMilliseconds(450);
        _maintenanceApplyTimer.IsRepeating = false;
        RefreshThemeButtons();
        ViewModel.LoadCacheOptions();
        ViewModel.InitializeLanSettings();
        ViewModel.RefreshNoticeOptions();
        LanClientTokenPasswordBox.Password = ViewModel.LanClientToken;
        LanServerTokenPasswordBox.Password = ViewModel.LanServerToken;
        ViewModel.ReloadScheduledMaintenance();
        await ViewModel.RefreshElevatorCommand.ExecuteAsync(null);
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pageLifetimeCancellation.Cancel();
        if (_maintenanceApplyTimer is not null)
            _maintenanceApplyTimer.Stop();
        ViewModel.PropertyChanged -= ThemeChanged;
        ViewModel.PropertyChanged -= LanClientTokenChanged;
        ViewModel.PropertyChanged -= LanServerTokenChanged;
        ViewModel.PropertyChanged -= MaintenanceBusyChanged;
        ViewModel.ScheduledMaintenanceOptionsChanged -= ScheduledMaintenanceOptionsChanged;
    }

    private void LanClientTokenChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LanClientToken)
            && LanClientTokenPasswordBox.Password != ViewModel.LanClientToken)
            LanClientTokenPasswordBox.Password = ViewModel.LanClientToken;
    }
    private void LanServerTokenChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.LanServerToken)
            && LanServerTokenPasswordBox.Password != ViewModel.LanServerToken)
            LanServerTokenPasswordBox.Password = ViewModel.LanServerToken;
    }
    private void ScheduledMaintenanceOptionsChanged(object? sender, EventArgs e)
    {
        if (_maintenanceApplyTimer is null || ViewModel.IsMaintenanceBusy)
            return;
        _maintenanceApplyTimer.Stop();
        _maintenanceApplyTimer.Start();
    }
    private void MaintenanceBusyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsMaintenanceBusy)
            && ViewModel.IsMaintenanceBusy)
            _maintenanceApplyTimer?.Stop();
    }
    private async void MaintenanceApplyTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!ViewModel.IsMaintenanceBusy)
            await ViewModel.ApplyScheduledMaintenanceCommand.ExecuteAsync(null);
    }
    private void MaintenanceSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: SettingsCatalogSettingOption option } toggle
            && option.ToggleValue != toggle.IsOn)
            option.Value = toggle.IsOn.ToString();
    }
    private void LanClientTokenPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
            viewModel.LanClientToken = LanClientTokenPasswordBox.Password;
    }
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
        ViewModel.ClearStatus();
        if (key is "Notifications" or "Execution") ViewModel.LoadExecutionOptions();
        if (key == "General") ViewModel.LoadGeneralSettings();
        if (key == "Operations") ViewModel.LoadCacheOptions();
        if (key == "Elevator") ViewModel.LoadElevatorOptions();
        if (key == "Developer") { ViewModel.InitializeLanSettings(); ViewModel.RefreshNoticeOptions(); }
        if (key == "Maintenance") ViewModel.ReloadScheduledMaintenance();
        HomepageScrollViewer.Visibility = Visibility.Visible;
        SettingsHomeContentPanel.Visibility = Visibility.Collapsed;
        SettingsSubCategoryContentPanel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        SettingsTitleTextBlock.Text = category.Title;
        SettingsSubtitleTextBlock.Text = category.Description;
        foreach (var item in _categories.Values) item.Panel.Visibility = ReferenceEquals(item.Panel, category.Panel) ? Visibility.Visible : Visibility.Collapsed;
        HomepageScrollViewer.ChangeView(null, 0, null); BackButton.Focus(FocusState.Programmatic);
        SearchContextChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task NavigateToCategoryAsync(string key)
    {
        NavigateToCategory(key);
        if (key == "Operations")
            await ViewModel.MeasureCacheCommand.ExecuteAsync(null);
    }
    private async void CategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
            await NavigateToCategoryAsync(key);
    }
    public void GoBack(bool focusCategories = true)
    {
        ViewModel.ClearStatus();
        HomepageScrollViewer.Visibility = Visibility.Visible;
        SettingsHomeContentPanel.Visibility = Visibility.Visible;
        SettingsSubCategoryContentPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        SettingsTitleTextBlock.Text = "Configurações do WinProvision Store";
        SettingsSubtitleTextBlock.Text = "Controle as preferências do aplicativo, da interface e das operações.";
        foreach (var item in _categories.Values) item.Panel.Visibility = Visibility.Collapsed;
        if (focusCategories) CardGeneral.Focus(FocusState.Programmatic);
        SearchContextChanged?.Invoke(this, EventArgs.Empty);
    }
    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();
    private void ApplyTitleBarSearchText(string text)
    {
        ViewModel.SearchText = text;
        if (SettingsSubCategoryContentPanel.Visibility == Visibility.Visible)
            GoBack(focusCategories: false);
    }
    private async void SearchResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingSearchResult result })
        {
            if (result.CategoryKey == "PackageManagers")
            {
                PackageManagersNavigationRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
            await NavigateToCategoryAsync(result.CategoryKey);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_categories.TryGetValue(result.CategoryKey, out var category))
                    category.Panel.StartBringIntoView();
            });
        }
    }

    private async void CatalogCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: SettingsCatalogSettingOption option, SelectedValue: string value } combo)
        {
            await Task.Yield();
            if (combo.SelectedValue is not string currentValue || !currentValue.Equals(value, StringComparison.Ordinal))
                return;
            await ViewModel.ApplyCatalogSettingAsync(option, value);
        }
    }

    private async void CatalogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: SettingsCatalogSettingOption option } toggle)
            await ViewModel.ApplyCatalogSettingAsync(option, toggle.IsOn.ToString());
    }

    private async void GeneralCatalogToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: SettingsCatalogSettingOption option } toggle)
            await ViewModel.ApplyGeneralCatalogSettingAsync(option, toggle.IsOn.ToString());
    }

    private async void CatalogNavigationCard_NavigationRequested(object sender, EventArgs e)
    {
        if (sender is not Controls.SettingsNavigationCard { TargetPage.Length: > 0 } navigation)
            return;

        await NavigateToCategoryAsync(navigation.TargetPage);
    }

    private async void ChooseCatalogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsCatalogSettingOption option })
            await ViewModel.PickCatalogFolderAsync(option, _pageLifetimeCancellation.Token);
    }

    private async void ResetCatalogFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SettingsCatalogSettingOption option })
            await ViewModel.UseCatalogFolderDefaultAsync(option);
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

    private async void ElevationSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsElevationBusy
            || sender is not ToggleSwitch toggle
            || toggle.DataContext is not ElevationSettingOption option)
            return;

        await Task.Yield();
        if (!ViewModel.IsElevationBusy
            && toggle.Tag is bool previousValue
            && toggle.IsOn != previousValue
            && toggle.IsOn != option.IsEnabled)
            await ViewModel.ChangeElevationPreferenceAsync(option, toggle.IsOn);
    }

    private async void InspectElevationPolicy_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Política ativa",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock
                {
                    Text = ViewModel.GetActiveElevationPolicyText(),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = "Fechar",
            XamlRoot = Content.XamlRoot
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }

    private async void OpenElevatedLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Registros de operações elevadas",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock
                {
                    Text = await ViewModel.ReadElevatedLogTextAsync(),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = "Fechar",
            XamlRoot = Content.XamlRoot
        };
        await ContentDialogCoordinator.ShowAsync(dialog);
    }
}
