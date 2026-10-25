using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.BadgeNotifications;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;
namespace WinProvision.Store;
public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ApplicationPreferencesService _applicationPreferences;
    private readonly Windows.UI.ViewManagement.UISettings _motionSettings = new();
    private readonly OperationsViewModel _operations;
    private Controls.AppDetailsOverlay? _details;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _queueAutoClose;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _cacheBudgetTimer;
    private bool? _compactNavigation;
    private bool _isNavigating;
    private bool _isClosing;
    private string? _currentNavigationTag;
    public FrameworkElement RootElement => Root;
    internal Visibility ProvisioningMenuVisibility => ProvisioningNavigationItem.Visibility;
    internal Page? CurrentPage => PageHost.Content as Page;
    internal Controls.AppDetailsOverlay? CreatedDetailsOverlay => _details;
    internal AppDetailsViewModel? CreatedDetailsViewModel =>
        _details is null ? null : _services.GetRequiredService<AppDetailsViewModel>();
    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        _applicationPreferences = services.GetRequiredService<ApplicationPreferencesService>();
        ProvisioningNavigationItem.Visibility = _applicationPreferences.ProvisioningMenuEnabled ? Visibility.Visible : Visibility.Collapsed;
        _applicationPreferences.ProvisioningMenuEnabledChanged += ProvisioningMenuEnabledChanged;
        _services.GetRequiredService<ApplicationThemeService>().Attach(Root);
        _operations = services.GetRequiredService<OperationsViewModel>();
        Root.DataContext = _operations;
        QueuePanel.DataContext = _operations;
        Root.ActualThemeChanged += (_, _) => QueuePanel.RequestedTheme = Root.ActualTheme;
        QueuePopup.Closed += (_, _) => QueuePanel.CloseOutput();
        services.GetRequiredService<PresentationService>().DetailsPresenter = ShowDetailsAsync;
        services.GetRequiredService<PresentationService>().AppUpdatesPresenter = () =>
        {
            _details?.Close();
            Navigate("settings");
            if (CurrentPage is SettingsPage settings) settings.NavigateToCategory("General");
        };
        _queueAutoClose = DispatcherQueue.CreateTimer(); _queueAutoClose.Interval = TimeSpan.FromSeconds(4);
        _queueAutoClose.Tick += (_, _) => { _queueAutoClose.Stop(); if (!_operations.HasPending) _operations.IsQueueOpen = false; };
        _cacheBudgetTimer = DispatcherQueue.CreateTimer();
        _cacheBudgetTimer.Interval = TimeSpan.FromSeconds(15);
        _cacheBudgetTimer.Tick += (_, _) => AsyncImage.RefreshMemoryBudget();
        _cacheBudgetTimer.Start();
        _operations.PropertyChanged += Operations_Changed;
        UpdateTaskbarBadge();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(1240, 760));
        Activated += (_, args) =>
        {
            bool active = args.WindowActivationState != WindowActivationState.Deactivated;
            if (PageHost.Content is HomePage home)
                home.SetWindowActive(active);
            if (!active)
            {
                AsyncImage.TrimForBackground();
                _cacheBudgetTimer.Stop();
            }
            else
            {
                AsyncImage.RestoreForegroundBudget();
                _cacheBudgetTimer.Start();
            }
        };
        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _motionSettings.AnimationsEnabledChanged += MotionSettings_AnimationsEnabledChanged;
    }
    private void ProvisioningMenuEnabledChanged(bool enabled)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosing) return;
            if (!enabled && _currentNavigationTag == "provisioning") Navigate("home");
            ProvisioningNavigationItem.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        });
    }
    private void MotionSettings_AnimationsEnabledChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosing)
            {
                Controls.FluentMotion.RefreshTree(Root);
                _details?.RefreshMotionPreference();
            }
        });
    }
    private Task ShowDetailsAsync(WinProvision.Core.Models.AppEntry app, string? currentVersion, string? availableVersion)
    {
        if (_isClosing || App.CurrentApp.IsShuttingDown || App.CurrentApp.ShutdownComplete)
            return Task.CompletedTask;

        Controls.AppDetailsOverlay details = _details ??= _services.GetRequiredService<Controls.AppDetailsOverlay>();
        DetailsOverlayHost.Content = details;
        return details.ShowAsync(app, currentVersion, availableVersion);
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        var presentation = _services.GetRequiredService<PresentationService>();
        presentation.Root = Root.XamlRoot;
        presentation.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(760 * scale)));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + Math.Max(0, (area.Width - AppWindow.Size.Width) / 2), area.Y + Math.Max(0, (area.Height - AppWindow.Size.Height) / 2)));
        UpdateNavigationLayout();
        Navigate(_applicationPreferences.StartupPage switch
        {
            ApplicationStartupPage.Updates => "updates",
            ApplicationStartupPage.Installed => "installed",
            _ => "home"
        });
        if (!App.IsSmokeTest && CurrentPage is HomePage)
        {
            ShowStartupSplash();
            await _services.GetRequiredService<HomeViewModel>().Storefront.InitialContentReady;
            HideStartupSplash();
        }
    }
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_isClosing && !_isNavigating && args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }
    public void Navigate(string tag)
    {
        if (_isClosing || App.CurrentApp.IsShuttingDown || App.CurrentApp.ShutdownComplete)
            return;
        if (tag == "provisioning" && !_applicationPreferences.ProvisioningMenuEnabled)
            return;

        var selected = RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems).OfType<NavigationViewItem>().First(item => (string)item.Tag == tag);
        if (_currentNavigationTag == tag)
            return;
        _isNavigating = true;
        try
        {
            if (!ReferenceEquals(RootNavigation.SelectedItem, selected))
                RootNavigation.SelectedItem = selected;
        }
        finally
        {
            _isNavigating = false;
        }
        PageHost.Content = tag switch
        {
            "updates" => _services.GetRequiredService<UpdatesPage>(),
            "installed" => _services.GetRequiredService<InstalledPackagesPage>(),
            "collections" => _services.GetRequiredService<CollectionsPage>(),
            "office" => _services.GetRequiredService<OfficePage>(),
            "provisioning" => _services.GetRequiredService<ProvisioningPage>(),
            "account" => _services.GetRequiredService<AccountSyncPage>(),
            "settings" => _services.GetRequiredService<SettingsPage>(),
            "more" => _services.GetRequiredService<MorePage>(),
            "home" => _services.GetRequiredService<HomePage>(),
            _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Página desconhecida.")
        };
        _currentNavigationTag = tag;
    }
    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateNavigationLayout();
        UpdateQueuePopupBounds();
    }
    private void QueuePopup_Opened(object sender, object args)
    {
        QueuePanel.DataContext = _operations;
        QueuePanel.RequestedTheme = Root.ActualTheme;
        QueuePanel.UpdateLayout();
        UpdateQueuePopupBounds();
    }
    private void QueuePanel_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateQueuePopupBounds();
    private void UpdateQueuePopupBounds()
    {
        if (QueuePopup is null || QueuePanel is null || Root.ActualWidth <= 0) return;
        QueuePanel.MaxWidth = Math.Max(0, Root.ActualWidth - (Root.ActualWidth < 1100 ? 64 : 240) - 32);
        QueuePanel.MaxHeight = Math.Max(44, Math.Min(340, Root.ActualHeight - 64));
        QueuePopup.HorizontalOffset = Math.Max(0, Root.ActualWidth - QueuePanel.ActualWidth - 16);
        QueuePopup.VerticalOffset = Math.Max(48, Root.ActualHeight - QueuePanel.ActualHeight - 16);
    }
    private void UpdateNavigationLayout()
    {
        if (RootNavigation is null || Root.ActualWidth <= 0) return;
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            double scale = Root.XamlRoot?.RasterizationScale ?? 1;
            int minWidth = (int)(820 * scale), minHeight = (int)(620 * scale);
            if (AppWindow.Size.Width < minWidth || AppWindow.Size.Height < minHeight)
                AppWindow.Resize(new SizeInt32(Math.Max(minWidth, AppWindow.Size.Width), Math.Max(minHeight, AppWindow.Size.Height)));
        }
        bool compact = Root.ActualWidth < 1100;
        if (_compactNavigation == compact) return;
        _compactNavigation = compact; RootNavigation.IsPaneOpen = !compact;
        if (_operations is not null) QueuePanel.MaxWidth = Math.Max(0, Root.ActualWidth - (compact ? 64 : 240) - 32);
    }
    private void QueueClose_Click(object sender, RoutedEventArgs args) => _operations.IsQueueOpen = false;
    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        if (_details?.HandleBack() == true) { args.Handled = true; return; }
        if (!_operations.IsQueueOpen) return;
        if (!QueuePanel.CloseOutput()) _operations.IsQueueOpen = false;
        args.Handled = true;
    }
    private void Operations_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(OperationsViewModel.HasPending) or nameof(OperationsViewModel.HasFailedOperations))
            UpdateTaskbarBadge();
        if (args.PropertyName != nameof(OperationsViewModel.HasPending)) return;
        _queueAutoClose.Stop();
        if (!_operations.HasPending && _operations.HasOperations) _queueAutoClose.Start();
    }
    private void UpdateTaskbarBadge()
    {
        // Badge notifications require package identity; this app is currently deployed unpackaged.
        try { _ = Windows.ApplicationModel.Package.Current.Id; }
        catch (InvalidOperationException) { return; }

        try
        {
            if (_operations.HasFailedOperations)
                BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.Error);
            else if (_operations.HasPending)
                BadgeNotificationManager.Current.SetBadgeAsGlyph(BadgeNotificationGlyph.Activity);
            else
                BadgeNotificationManager.Current.ClearBadge();
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "TaskbarBadge");
        }
    }
    public void ShowStartupSplash() { StartupSplashOverlay.Visibility = Visibility.Visible; MainContentLayer.Opacity = 0; MainContentLayer.IsHitTestVisible = false; }
    public void HideStartupSplash() { StartupSplashOverlay.Visibility = Visibility.Collapsed; MainContentLayer.Opacity = 1; MainContentLayer.IsHitTestVisible = true; }
    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.CurrentApp.ShutdownComplete)
            return;

        args.Cancel = true;
        if (_isClosing)
            return;

        _isClosing = true;
        RootNavigation.IsEnabled = false;
        try
        {
            await App.CurrentApp.ShutdownAsync();
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "WindowShutdown");
        }

        Close();
    }
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _applicationPreferences.ProvisioningMenuEnabledChanged -= ProvisioningMenuEnabledChanged;
        QueuePopup.IsOpen = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _motionSettings.AnimationsEnabledChanged -= MotionSettings_AnimationsEnabledChanged;
        _isClosing = true;
        _queueAutoClose.Stop();
        _cacheBudgetTimer.Stop();
        _operations.PropertyChanged -= Operations_Changed;
    }
}
