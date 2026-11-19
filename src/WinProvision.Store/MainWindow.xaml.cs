using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Microsoft.Windows.BadgeNotifications;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using FluentIcons.Common;
using FluentIcons.WinUI;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;
namespace WinProvision.Store;
public sealed partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ApplicationPreferencesService _applicationPreferences;
    private readonly WindowBackdropController _windowBackdrop;
    private readonly Windows.UI.ViewManagement.UISettings _motionSettings = new();
    private readonly OperationsViewModel _operations;
    private readonly IOperationsPanelCatalog _operationsPanelCatalog;
    private readonly ToastMessageService _toastMessages;
    private readonly MainShellDefinition _shell;
    private readonly MainShellCommandRegistry _shellCommands;
    private readonly Dictionary<NavigationViewItem, (MainShellNavigationItem Definition, Icon Fallback)> _themedNavigationIcons = [];
    private Controls.AppDetailsOverlay? _details;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _cacheBudgetTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _searchDebounceTimer;
    private InputNonClientPointerSource? _nonClientPointerSource;
    private ITitleBarSearchTarget? _activeSearchTarget;
    private TitleBarSearchPageDefinition? _activeSearchDefinition;
    private ITitleBarSearchTarget? _pendingSearchTarget;
    private string? _pendingSearchText;
    private bool? _compactNavigation;
    private bool _compactTitleBarSearch;
    private bool _isNavigating;
    private bool _isClosing;
    private string? _currentNavigationTag;
    private string? _managerLogsReturnId;
    private string _officePreferencesReturnTag = "office";
    public FrameworkElement RootElement => Root;
    public void Minimize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }
    internal Visibility ProvisioningMenuVisibility => Visibility.Collapsed;
    internal Page? CurrentPage => PageHost.Content as Page;
    internal AutoSuggestBox TitleBarSearchBoxForSmoke => TitleBarSearchBox;
    internal bool TitleBarSearchIsVisibleForSmoke =>
        TitleBarSearchHost.Visibility == Visibility.Visible || CompactTitleBarSearchButton.Visibility == Visibility.Visible;
    internal string EffectiveBackdropForSmoke => _windowBackdrop.EffectiveBackdropId;
    internal FrameworkElement TitleBarSearchInputForSmoke =>
        _compactTitleBarSearch ? CompactTitleBarSearchButton : TitleBarSearchVisual;
    internal Controls.AppDetailsOverlay? CreatedDetailsOverlay => _details;
    internal AppDetailsViewModel? CreatedDetailsViewModel =>
        _details is null ? null : _services.GetRequiredService<AppDetailsViewModel>();
    public bool IsFocused { get; private set; }
    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        _shell = MainShellDefinition.LoadEmbedded();
        _shellCommands = new MainShellCommandRegistry();
        RegisterShellCommands();
        ConfigureNavigation();
        RootNavigation.BackRequested += RootNavigation_BackRequested;
        _applicationPreferences = services.GetRequiredService<ApplicationPreferencesService>();
        _operationsPanelCatalog = services.GetRequiredService<IOperationsPanelCatalog>();
        OperationsPanelLayout initialPanelLayout = _operationsPanelCatalog.Current.Layout;
        _operationsPanelHeight = Math.Clamp(_applicationPreferences.OperationsPanelHeight, initialPanelLayout.PanelMinHeight, initialPanelLayout.PanelMaxHeight);
        _operationsPanelResized = _applicationPreferences.HasSavedOperationsPanelHeight;
        _applicationPreferences.OfficeModuleEnabledChanged += OfficeModuleEnabled_Changed;
        UpdateOfficeNavigationVisibility();
        AsyncImage.SetPackageVisualPreferences(services.GetRequiredService<PackageVisualPreferencesService>());
        _toastMessages = services.GetRequiredService<ToastMessageService>();
        _services.GetRequiredService<ApplicationThemeService>().Attach(Root);
        _windowBackdrop = new WindowBackdropController(this, Root, WindowBackdropCatalog.Default, _applicationPreferences.BackdropPreference);
        _applicationPreferences.BackdropPreferenceChanged += OnBackdropPreferenceChanged;
        _operations = services.GetRequiredService<OperationsViewModel>();
        _operationsPanelCatalog.Changed += OperationsPanelCatalog_Changed;
        _operations.IsQueueExpanded = _applicationPreferences.HasSavedOperationsPanelExpanded
            ? _applicationPreferences.OperationsPanelExpanded
            : _operations.PanelBehavior.DefaultExpanded;
        Root.DataContext = _operations;
        QueuePanel.DataContext = _operations;
        QueuePanel.RequestedTheme = Root.ActualTheme;
        _operations.PropertyChanged += OperationsPanel_Changed;
        _operations.Items.CollectionChanged += OperationsItems_CollectionChanged;
        ToastHost.ItemsSource = _toastMessages.Messages;
        Root.ActualThemeChanged += (_, _) =>
        {
            if (_isClosing) return;
            QueuePanel.RequestedTheme = Root.ActualTheme;
            _windowBackdrop.RefreshTheme();
            EnqueueWhileWindowOpen(ApplyNavigationPaneBackground);
            EnqueueWhileWindowOpen(UpdateThemedNavigationIcons);
        };
        RootNavigation.ActualThemeChanged += (_, _) =>
        {
            EnqueueWhileWindowOpen(ApplyNavigationPaneBackground);
            EnqueueWhileWindowOpen(UpdateThemedNavigationIcons);
        };
        services.GetRequiredService<PresentationService>().DetailsPresenter = ShowDetailsAsync;
        services.GetRequiredService<PresentationService>().AppUpdatesPresenter = () =>
        {
            _details?.Close();
            Navigate("settings");
            if (CurrentPage is SettingsPage settings) settings.NavigateToCategory("General");
        };
        _searchDebounceTimer = DispatcherQueue.CreateTimer();
        _searchDebounceTimer.Tick += (_, _) => FlushPendingSearch();
        _cacheBudgetTimer = DispatcherQueue.CreateTimer();
        _cacheBudgetTimer.Interval = TimeSpan.FromSeconds(15);
        _cacheBudgetTimer.Tick += (_, _) => AsyncImage.RefreshMemoryBudget();
        _cacheBudgetTimer.Start();
        _operations.PropertyChanged += Operations_Changed;
        UpdateOperationsPanelLayout();
        UpdateTaskbarBadge();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Changed += (_, _) => EnqueueWhileWindowOpen(() => { ClampOperationsPanelHeight(); UpdateOperationsPanelLayout(); UpdateTitleBarInputRegions(); });
        if (_shell.Search.Shortcut.Equals("Ctrl+F", StringComparison.OrdinalIgnoreCase))
        {
            var searchAccelerator = new KeyboardAccelerator
            {
                Key = Windows.System.VirtualKey.F,
                Modifiers = Windows.System.VirtualKeyModifiers.Control,
                ScopeOwner = Root
            };
            searchAccelerator.Invoked += (_, args) =>
            {
                if (_activeSearchTarget?.SearchContextAvailable == true && GetActiveSearchDefinition()?.Enabled == true)
                {
                    args.Handled = true;
                    FocusTitleBarSearch();
                }
            };
            Root.KeyboardAccelerators.Add(searchAccelerator);
        }
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Resize(new SizeInt32(1240, 760));
        Activated += (_, args) =>
        {
            bool active = args.WindowActivationState != WindowActivationState.Deactivated;
            IsFocused = active;
            if (active)
                _toastMessages.RefreshHistory();
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

    private void ToastDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ToastMessageItem item })
            _toastMessages.Dismiss(item);
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) =>
        ToggleNavigationPane();

    private void ToggleNavigationPane()
    {
        RootNavigation.IsPaneOpen = !RootNavigation.IsPaneOpen;
        UpdateNavigationToolTips();
    }

    private void OnBackdropPreferenceChanged(string preference) => _windowBackdrop.Apply(preference);
    private void Toast_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastMessageItem item })
            _toastMessages.Pause(item);
    }
    private void Toast_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ToastMessageItem item })
            _toastMessages.Resume(item);
    }
    private void MotionSettings_AnimationsEnabledChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        EnqueueWhileWindowOpen(() =>
        {
            Controls.FluentMotion.RefreshTree(Root);
            _details?.RefreshMotionPreference();
        });
    }

    private void EnqueueWhileWindowOpen(Action callback)
    {
        if (_isClosing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isClosing) callback();
        });
    }
    private Task ShowDetailsAsync(WinProvision.Core.Models.AppEntry app, string? currentVersion, string? availableVersion, bool showOptions)
    {
        if (_isClosing || App.CurrentApp.IsShuttingDown || App.CurrentApp.ShutdownComplete)
            return Task.CompletedTask;

        Controls.AppDetailsOverlay details = _details ??= _services.GetRequiredService<Controls.AppDetailsOverlay>();
        DetailsOverlayHost.Content = details;
        Task load = details.ShowAsync(app, currentVersion, availableVersion);
        if (showOptions) details.SetOptionsVisible(true);
        return load;
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        long firstScreenStartedAt = DebugPerformanceTelemetry.Start();
        var presentation = _services.GetRequiredService<PresentationService>();
        AppTitleBar.Title = PackageScreenCatalog.Default.GetText(_shell.Title, _shell.Title);
        presentation.Root = Root.XamlRoot;
        presentation.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        Root.XamlRoot.Changed += (_, _) => EnqueueWhileWindowOpen(UpdateTitleBarSearchLayout);
        double scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(760 * scale)));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + Math.Max(0, (area.Width - AppWindow.Size.Width) / 2), area.Y + Math.Max(0, (area.Height - AppWindow.Size.Height) / 2)));
        RootNavigation.UpdateLayout();
        ApplyNavigationPaneBackground();
        UpdateNavigationLayout();
        Navigate(_applicationPreferences.StartupPage switch
        {
            ApplicationStartupPage.Updates => "updates",
            ApplicationStartupPage.Installed => "installed",
            _ => "home"
        });
        PageHost.UpdateLayout();
        UpdateActiveSearchTarget();
        if (!App.IsSmokeTest && CurrentPage is HomePage)
        {
            ShowStartupSplash();
            await _services.GetRequiredService<HomeViewModel>().Storefront.InitialContentReady;
            HideStartupSplash();
        }
        DebugPerformanceTelemetry.Duration("startup-first-usable-screen", firstScreenStartedAt);
        DebugPerformanceTelemetry.Mark("first-usable-screen");
        DebugPerformanceTelemetry.ImageSnapshot("first-usable-screen");
        DebugPerformanceTelemetry.CatalogSnapshot("first-usable-screen");
        App.MemoryLog?.RecordOnce("first_usable_screen");
    }
    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ReferenceEquals(args.SelectedItem, MoreFlyoutNavigationItem))
        {
            _isNavigating = true;
            try
            {
                sender.SelectedItem = _currentNavigationTag is null ? null : sender.MenuItems.Concat(sender.FooterMenuItems)
                    .OfType<NavigationViewItem>().FirstOrDefault(candidate => candidate.Tag is string tag && tag == _currentNavigationTag);
            }
            finally { _isNavigating = false; }
            return;
        }
        if (!_isClosing && !_isNavigating && args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            Navigate(tag);
    }

    private void ConfigureNavigation()
    {
        RootNavigation.OpenPaneLength = _shell.OpenPaneLength;
        RootNavigation.IsPaneOpen = _shell.StartsOpen;
        RootNavigation.MenuItems.Clear();
        RootNavigation.FooterMenuItems.Clear();
        FlyoutBase.SetAttachedFlyout(MoreFlyoutNavigationItem, (FlyoutBase)RootNavigation.Resources["MoreFlyoutResource"]);
        foreach (MainShellNavigationItem definition in _shell.Items)
        {
            string label = PackageScreenCatalog.Default.GetText(definition.Label, definition.Label);
            string iconName = PackageScreenCatalog.Default.ResolveIcon(definition.Icon);
            if (!Enum.TryParse(iconName, true, out Icon icon))
                continue;

            if (definition.Kind == "flyout")
            {
                if (definition.Placement == "top")
                    RootNavigation.MenuItems.Add(MoreFlyoutNavigationItem);
                else
                    RootNavigation.FooterMenuItems.Add(MoreFlyoutNavigationItem);
                MoreFlyoutNavigationItem.Content = label;
                MoreFlyoutNavigationItem.Tag = null;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MoreFlyoutNavigationItem, label);
                SetNavigationToolTip(MoreFlyoutNavigationItem, label);
                BuildMoreFlyout(definition.Children ?? []);
                continue;
            }
            var item = new NavigationViewItem
            {
                Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
                Tag = GetNavigationRoute(definition),
                Icon = CreateNavigationIcon(definition, icon)
            };
            if (definition.IconUriLight is not null || definition.IconUriDark is not null)
                _themedNavigationIcons[item] = (definition, icon);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, $"Navigate{ToAutomationSuffix(definition)}");
            SetNavigationToolTip(item, label);
            if (definition.Placement == "top")
                RootNavigation.MenuItems.Add(item);
            else
                RootNavigation.FooterMenuItems.Add(item);
        }
    }

    private IconElement CreateNavigationIcon(MainShellNavigationItem definition, Icon fallback)
    {
        string? iconUri = Root.ActualTheme == ElementTheme.Dark
            ? definition.IconUriDark ?? definition.IconUriLight
            : definition.IconUriLight ?? definition.IconUriDark;
        if (iconUri is not null)
            return new ImageIcon
            {
                Width = 20,
                Height = 20,
                Source = new SvgImageSource { UriSource = new Uri(iconUri) }
            };
        return new FluentIcon { Icon = fallback, IconVariant = IconVariant.Regular, IconSize = IconSize.Size20 };
    }

    private void UpdateThemedNavigationIcons()
    {
        if (_isClosing) return;
        foreach ((NavigationViewItem item, (MainShellNavigationItem definition, Icon fallback)) in _themedNavigationIcons)
            item.Icon = CreateNavigationIcon(definition, fallback);
    }

    private void SetNavigationToolTip(NavigationViewItem item, string label)
    {
        if (RootNavigation.IsPaneOpen)
        {
            ToolTipService.SetToolTip(item, null);
            ToolTipService.SetPlacement(item, PlacementMode.Mouse);
        }
        else
        {
            ToolTipService.SetToolTip(item, label);
            ToolTipService.SetPlacement(item, PlacementMode.Right);
        }
    }

    private void UpdateNavigationToolTips()
    {
        foreach (NavigationViewItem item in RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems).OfType<NavigationViewItem>())
        {
            string label = item.Tag is MainShellNavigationItem definition
                ? PackageScreenCatalog.Default.GetText(definition.Label, definition.Label)
                : item.Content?.ToString() ?? string.Empty;
            SetNavigationToolTip(item, label);
        }
    }

    private void BuildMoreFlyout(IReadOnlyList<MainShellMenuItem> definitions)
    {
        MoreFlyoutItems.Children.Clear();
        foreach (MainShellMenuItem definition in definitions)
        {
            FrameworkElement element;
            if (definition.Type == "separator")
            {
                element = new NavigationViewItemSeparator { Margin = new Thickness(8, 4, 8, 4) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, "Separador");
            }
            else if (definition.Type == "info")
            {
                var infoRow = new Grid { MinHeight = 36, Padding = new Thickness(8, 4, 8, 4), ColumnSpacing = 12 };
                infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                infoRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (definition.Icon is not null
                    && Enum.TryParse(PackageScreenCatalog.Default.ResolveIcon(definition.Icon), true, out Icon infoIcon))
                    infoRow.Children.Add(new FluentIcon { Icon = infoIcon, IconVariant = IconVariant.Regular, IconSize = IconSize.Size16, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
                var infoLabel = new TextBlock { Text = definition.Label, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(infoLabel, 1);
                infoRow.Children.Add(infoLabel);
                if (!string.IsNullOrWhiteSpace(definition.Shortcut))
                {
                    var infoShortcut = new TextBlock { Text = definition.Shortcut, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(infoShortcut, 2);
                    infoRow.Children.Add(infoShortcut);
                }
                element = infoRow;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, definition.Label);
            }
            else
            {
                var row = new Grid { MinHeight = 40, Padding = new Thickness(8, 4, 8, 4), ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                if (definition.Icon is not null)
                {
                    string iconKey = PackageScreenCatalog.Default.ResolveIcon(definition.Icon);
                    if (Enum.TryParse(iconKey, true, out Icon childIcon))
                        row.Children.Add(new FluentIcon { Icon = childIcon, IconVariant = IconVariant.Regular, IconSize = IconSize.Size16, VerticalAlignment = VerticalAlignment.Center });
                }
                var label = new TextBlock { Text = definition.Label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1); row.Children.Add(label);
                if (!string.IsNullOrWhiteSpace(definition.Shortcut))
                {
                    var shortcut = new TextBlock { Text = definition.Shortcut, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(shortcut, 2); row.Children.Add(shortcut);
                }
                var button = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), Tag = definition };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, definition.Label);
                if (!IsMenuItemVisible(definition)) { button.Visibility = Visibility.Collapsed; }
                else if (definition.Type == "command" && (definition.CommandId is null || !_shellCommands.TryGet(definition.CommandId, out _)))
                {
                    WinProvisionLog.Warning(new InvalidOperationException($"Comando desconhecido '{definition.CommandId}'."), "MainShellMenu.Command");
                    button.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (definition.Type == "command") button.IsEnabled = _shellCommands.IsEnabled(definition.CommandId!);
                    button.Click += MoreFlyoutItem_Click;
                }
                ToolTipService.SetToolTip(button, definition.Label);
                element = button;
            }
            MoreFlyoutItems.Children.Add(element);
        }
    }

    private void MoreFlyoutNavigationItem_Tapped(object sender, TappedRoutedEventArgs args)
    {
        FlyoutBase.ShowAttachedFlyout(MoreFlyoutNavigationItem);
        args.Handled = true;
    }

    private void MoreFlyoutNavigationItem_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
        {
            FlyoutBase.ShowAttachedFlyout(MoreFlyoutNavigationItem);
            args.Handled = true;
        }
        else if (args.Key == Windows.System.VirtualKey.Escape && MoreFlyout.IsOpen)
        {
            MoreFlyout.Hide();
            MoreFlyoutNavigationItem.Focus(FocusState.Programmatic);
            args.Handled = true;
        }
        else if (MoreFlyout.IsOpen && args.Key is Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up)
        {
            var candidates = MoreFlyoutItems.Children.OfType<Button>().Where(button => button.Visibility == Visibility.Visible && button.IsEnabled).ToArray();
            if (candidates.Length > 0)
            {
                int current = Array.FindIndex(candidates, button => button.FocusState != FocusState.Unfocused);
                int next = args.Key == Windows.System.VirtualKey.Down
                    ? (current + 1 + candidates.Length) % candidates.Length
                    : (current <= 0 ? candidates.Length - 1 : current - 1);
                candidates[next].Focus(FocusState.Keyboard);
                args.Handled = true;
            }
        }
    }

    private void MoreFlyoutItems_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape || !MoreFlyout.IsOpen) return;
        MoreFlyout.Hide();
        MoreFlyoutNavigationItem.Focus(FocusState.Programmatic);
        args.Handled = true;
    }

    private void MoreFlyout_Closed(object sender, object args) => MoreFlyoutNavigationItem.Focus(FocusState.Programmatic);

    private void RegisterShellCommands()
    {
        SettingsViewModel settings = _services.GetRequiredService<SettingsViewModel>();
        _shellCommands.Register("open-logs", () => { settings.OpenLogsCommand.Execute(null); return Task.CompletedTask; });
        _shellCommands.Register("open-release", () => settings.OpenReleaseCommand.ExecuteAsync(null));
        _shellCommands.Register("check-updates", () => settings.CheckUpdateCommand.ExecuteAsync(null), () => settings.CheckUpdateCommand.CanExecute(null));
        _shellCommands.Register("open-about", OpenAboutDialogAsync);
        _shellCommands.Register("exit", RequestExitAsync);
    }

    private Task OpenAboutDialogAsync() => AboutDialogRenderer.ShowAsync(
        Root,
        Root.XamlRoot,
        AboutContentCatalog.LoadEmbedded(message => WinProvisionLog.Warning(new InvalidDataException(message), "AboutContentCatalog.Load")),
        AboutValueProviderRegistry.CreateDefault(),
        uri => _services.GetRequiredService<IDesktopIntegrationService>().OpenUriAsync(uri),
        () => MoreFlyoutNavigationItem.Focus(FocusState.Keyboard));

    private async Task RequestExitAsync()
    {
        if (ShouldConfirmExit(_operations.HasPending))
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Sair do WinProvision Store",
                Content = PendingExitMessage,
                PrimaryButtonText = "Sair",
                CloseButtonText = "Continuar usando",
                DefaultButton = ContentDialogButton.Close
            };
            if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        }
        Close();
    }

    internal static bool ShouldConfirmExit(bool hasPendingOperations) => hasPendingOperations;
    internal const string PendingExitMessage = "Há operações em andamento. Sair interromperá as operações ainda não concluídas.";

    private bool IsMenuItemVisible(MainShellMenuItem item)
    {
        if (item.DependsOn is null) return true;
        var catalog = _services.GetRequiredService<ElevationSettingsCatalogService>().Catalog;
        var preferences = _services.GetRequiredService<WinProvision.Core.Services.ElevationPreferencesService>();
        var values = catalog.Settings.Where(setting => setting.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(setting => setting.Key, setting => preferences.GetBoolean(setting.Key, setting.DefaultBooleanValue ?? false).ToString(), StringComparer.OrdinalIgnoreCase);
        var evaluated = WinProvision.Core.Services.ElevationSettingConditionEvaluator.Evaluate(item.DependsOn, catalog.Settings, values);
        if (!evaluated.IsValid) { WinProvisionLog.Warning(new InvalidDataException($"Condição inválida no item '{item.Id}'."), "MainShellMenu.Condition"); return false; }
        return evaluated.IsSatisfied;
    }

    private async void MoreFlyoutItem_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: MainShellMenuItem item }) return;
        if (item.Type == "navigation" && item.TargetId is { } route)
        {
            Navigate(route);
            MoreFlyout.Hide();
            return;
        }
        if (item.Type != "command" || item.CommandId is null || !_shellCommands.TryGet(item.CommandId, out var action))
        {
            WinProvisionLog.Warning(new InvalidOperationException($"Comando desconhecido '{item.CommandId}'."), "MainShellMenu.Command");
            return;
        }
        MoreFlyout.Hide();
        await action();
    }

    internal void OpenMenuItemForSmoke(MainShellMenuItem item) => MoreFlyoutItem_Click(
        new Button { Tag = item }, new RoutedEventArgs());

    internal void OpenMoreFlyoutForSmoke() => FlyoutBase.ShowAttachedFlyout(MoreFlyoutNavigationItem);
    internal void CloseMoreFlyoutForSmoke() => MoreFlyout.Hide();
    internal void RefreshNavigationToolTipsForSmoke() => UpdateNavigationToolTips();
    internal bool IsMenuFlyoutOpenForSmoke => MoreFlyout.IsOpen;
    internal bool IsNavigationSelectionUnchangedForSmoke(object? selected) => ReferenceEquals(RootNavigation.SelectedItem, selected);

    private static string GetNavigationRoute(MainShellNavigationItem item) =>
        item.TargetType == "screen" && item.TargetId == "discover" ? "home" : item.TargetId;

    private static string ToAutomationSuffix(MainShellNavigationItem item) => item.TargetType == "screen"
        ? item.TargetId == "discover" ? "Home" : char.ToUpperInvariant(item.TargetId[0]) + item.TargetId[1..]
        : item.TargetId == "package-managers" ? "PackageManagers" : char.ToUpperInvariant(item.TargetId[0]) + item.TargetId[1..];
    public void Navigate(string tag)
    {
        if (_isClosing || App.CurrentApp.IsShuttingDown || App.CurrentApp.ShutdownComplete)
            return;
        if ((tag is "office" or "office-preferences") && !_applicationPreferences.OfficeModuleEnabled)
            tag = "home";
        if (tag is not ("home" or "updates" or "installed" or "collections" or "office" or "office-preferences" or "settings" or "package-managers" or "operation-history"))
            return;

        NavigationViewItem? selected = RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>().FirstOrDefault(item => (string)item.Tag == tag);
        if (_currentNavigationTag == tag)
            return;
        _isNavigating = true;
        try
        {
            if (selected is not null && !ReferenceEquals(RootNavigation.SelectedItem, selected))
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
            "office" => CreateOfficePage(),
            "office-preferences" => CreateOfficePreferencesPage(),
            "provisioning" => _services.GetRequiredService<ProvisioningPage>(),
            "account" => _services.GetRequiredService<AccountSyncPage>(),
            "settings" => CreateSettingsPage(),
            "package-managers" => CreatePackageManagersPage(),
            "operation-history" => _services.GetRequiredService<OperationHistoryPage>(),
            "home" => _services.GetRequiredService<HomePage>(),
            _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "Página desconhecida.")
        };
        _currentNavigationTag = tag;
        PageHost.UpdateLayout();
        if (tag == "operation-history")
        {
            _isNavigating = true;
            try { RootNavigation.SelectedItem = null; }
            finally { _isNavigating = false; }
            RootNavigation.IsBackButtonVisible = _managerLogsReturnId is null
                ? NavigationViewBackButtonVisible.Collapsed : NavigationViewBackButtonVisible.Visible;
            if (_managerLogsReturnId is { } managerId && PageHost.Content is OperationHistoryPage historyPage)
                _ = historyPage.ShowManagerLogsAsync(managerId);
        }
        else
        {
            RootNavigation.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
            _managerLogsReturnId = null;
        }
        UpdateActiveSearchTarget();
    }

    private PackageManagersPage CreatePackageManagersPage()
    {
        var page = _services.GetRequiredService<PackageManagersPage>();
        page.ManagerLogsNavigationRequested += OnManagerLogsNavigationRequested;
        page.ViewModel.OfficeSettingsRequested += (_, _) => NavigateToOfficePreferences("package-managers");
        return page;
    }

    private OfficePage CreateOfficePage()
    {
        return _services.GetRequiredService<OfficePage>();
    }

    private OfficePreferencesPage CreateOfficePreferencesPage()
    {
        var page = _services.GetRequiredService<OfficePreferencesPage>();
        page.BackRequested += (_, _) => BackFromOfficePreferences();
        return page;
    }

    private void NavigateToOfficePreferences(string returnTag)
    {
        _officePreferencesReturnTag = returnTag;
        Navigate("office-preferences");
    }

    private void BackFromOfficePreferences()
    {
        if (_officePreferencesReturnTag == "package-managers") Navigate("package-managers");
        else Navigate("office");
    }

    private void OfficeModuleEnabled_Changed(bool enabled)
    {
        UpdateOfficeNavigationVisibility();
        if (!enabled && (_currentNavigationTag is "office" or "office-preferences"))
            Navigate("home");
    }

    private void UpdateOfficeNavigationVisibility()
    {
        NavigationViewItem? officeItem = RootNavigation.MenuItems.Concat(RootNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>().FirstOrDefault(item => item.Tag is string tag && tag == "office");
        if (officeItem is not null)
            officeItem.Visibility = _applicationPreferences.OfficeModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnManagerLogsNavigationRequested(string managerId)
    {
        _managerLogsReturnId = managerId;
        Navigate("operation-history");
    }

    private void RootNavigation_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (_managerLogsReturnId is not { } managerId) return;
        _managerLogsReturnId = null;
        Navigate("package-managers");
        if (PageHost.Content is PackageManagersPage managersPage)
            managersPage.RestoreManagerDetails(managerId);
    }

    private SettingsPage CreateSettingsPage()
    {
        SettingsPage page = _services.GetRequiredService<SettingsPage>();
        page.PackageManagersNavigationRequested += (_, _) => Navigate("package-managers");
        return page;
    }

    private void Root_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateNavigationLayout();
        UpdateOperationsPanelLayout();
        UpdateTitleBarSearchLayout();
        UpdateTitleBarInputRegions();
    }
    private void TitleBarSearchContent_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateTitleBarSearchLayout();
    private void TitleBarSearchBox_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateTitleBarInputRegions();
    private void TitleBarSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            return;
        HandleTitleBarSearchInput(sender.Text);
    }
    private void HandleTitleBarSearchInput(string input)
    {
        if (_activeSearchTarget is not { SearchContextAvailable: true } target
            || GetActiveSearchDefinition() is not { Enabled: true } search)
            return;
        string text = NormalizeSearchText(input, search);
        if (search.Trigger == "submit")
        {
            target.SearchText = text;
            if (text.Length == 0)
                _ = target.SubmitSearchAsync(string.Empty);
            return;
        }

        _pendingSearchTarget = target;
        _pendingSearchText = text;
        if (search.Trigger == "live")
        {
            target.SearchText = text;
            target.CancelPendingSearch();
        }
        if (search.DebounceMs == 0 || text.Length == 0)
        {
            FlushPendingSearch();
            return;
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(search.DebounceMs);
        _searchDebounceTimer.Start();
    }
    private async void TitleBarSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        await SubmitActiveSearchAsync(args.QueryText);
    }
    private void TitleBarSearchBox_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape && HandleSearchEscape())
        {
            args.Handled = true;
        }
    }
    private void CompactTitleBarSearchFlyout_Opened(object sender, object args) => TitleBarSearchBox.Focus(FocusState.Programmatic);
    private void CompactTitleBarSearchFlyout_Closed(object sender, object args) => UpdateTitleBarInputRegions();
    private void UpdateActiveSearchTarget()
    {
        FlushPendingSearch();
        ITitleBarSearchTarget? target = (ITitleBarSearchTarget?)FindVisualChild<Controls.PackageScreen>(PageHost)
            ?? PageHost.Content as ITitleBarSearchTarget;
        if (ReferenceEquals(target, _activeSearchTarget))
        {
            _activeSearchDefinition = GetActiveSearchDefinition();
            RefreshActiveSearchPresentation();
            return;
        }
        if (_activeSearchTarget is not null)
        {
            if (_activeSearchDefinition is { KeepQuery: false })
                _activeSearchTarget.SearchText = string.Empty;
            _activeSearchTarget.SearchContextChanged -= ActiveSearchTarget_ContextChanged;
        }
        _activeSearchTarget = target;
        _activeSearchDefinition = GetActiveSearchDefinition();
        if (_activeSearchTarget is not null)
        {
            _activeSearchTarget.SearchContextChanged += ActiveSearchTarget_ContextChanged;
            if (GetActiveSearchDefinition() is { KeepQuery: false })
                _activeSearchTarget.SearchText = string.Empty;
        }
        RefreshActiveSearchPresentation();
    }
    private void ActiveSearchTarget_ContextChanged(object? sender, EventArgs e) => RefreshActiveSearchPresentation();
    private void RefreshActiveSearchPresentation()
    {
        TitleBarSearchBox.Text = _activeSearchTarget?.SearchText ?? string.Empty;
        string placeholderKey = _activeSearchDefinition?.Placeholder ?? "search.placeholder";
        TitleBarSearchBox.PlaceholderText = PackageScreenCatalog.Default.GetText(placeholderKey, placeholderKey);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TitleBarSearchBox, TitleBarSearchBox.PlaceholderText);
        UpdateTitleBarSearchLayout();
    }
    private TitleBarSearchPageDefinition? GetActiveSearchDefinition()
    {
        if (_activeSearchTarget is Controls.PackageScreen screen
            && screen.Definition?.Search is { } search)
            return new(search.Enabled, search.Placeholder, search.Trigger, search.DebounceMs, search.MinLength, search.KeepQuery);
        return _currentNavigationTag is { } pageId
            && _shell.Search.Pages.TryGetValue(pageId, out TitleBarSearchPageDefinition? pageSearch)
                ? pageSearch
                : null;
    }
    private void UpdateTitleBarSearchLayout()
    {
        if (_isClosing) return;
        if (TitleBarSearchContent is null || TitleBarSearchContent.ActualWidth <= 0) return;
        bool enabled = _activeSearchTarget?.SearchContextAvailable == true
            && GetActiveSearchDefinition()?.Enabled == true;
        double minWidth = _shell.Search.MinWidth;
        double maxWidth = _shell.Search.MaxWidth;
        bool compact = enabled && TitleBarSearchContent.ActualWidth < minWidth + 16;
        if (!enabled) compact = false;
        if (compact != _compactTitleBarSearch)
        {
            CompactTitleBarSearchFlyout.Hide();
            if (compact)
            {
                if (TitleBarSearchHost.Content is Grid searchVisual)
                {
                    TitleBarSearchHost.Content = null;
                    CompactTitleBarSearchHost.Content = searchVisual;
                }
                TitleBarSearchVisual.Width = 360;
            }
            else if (CompactTitleBarSearchHost.Content is Grid searchVisual)
            {
                CompactTitleBarSearchHost.Content = null;
                TitleBarSearchHost.Content = searchVisual;
            }
            _compactTitleBarSearch = compact;
        }

        TitleBarSearchHost.Visibility = enabled && !compact ? Visibility.Visible : Visibility.Collapsed;
        CompactTitleBarSearchButton.Visibility = enabled && compact ? Visibility.Visible : Visibility.Collapsed;
        if (enabled && !compact)
        {
            double width = Math.Min(maxWidth, Math.Max(minWidth, TitleBarSearchContent.ActualWidth - 16));
            TitleBarSearchVisual.Width = width;
            TitleBarSearchBox.Width = width;
        }
        UpdateTitleBarInputRegions();
    }
    private void UpdateTitleBarInputRegions() => UpdateTitleBarInputRegions(Root.XamlRoot?.RasterizationScale ?? 1);
    private void UpdateTitleBarInputRegions(double rasterizationScale)
    {
        if (_isClosing) return;
        if (_nonClientPointerSource is null || Root.XamlRoot is null) return;
        FrameworkElement? input = _activeSearchTarget?.SearchContextAvailable != true
            || GetActiveSearchDefinition()?.Enabled != true
            ? null
            : _compactTitleBarSearch
                ? CompactTitleBarSearchButton
            : TitleBarSearchBox;
        if (input is null || input.Visibility != Visibility.Visible
            || (_compactTitleBarSearch && CompactTitleBarSearchFlyout.IsOpen))
        {
            _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, []);
            return;
        }

        Windows.Foundation.Rect bounds = input.TransformToVisual(Root)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, input.ActualWidth, input.ActualHeight));
        var rect = new RectInt32
        {
            X = (int)Math.Floor(bounds.X * rasterizationScale),
            Y = (int)Math.Floor(bounds.Y * rasterizationScale),
            Width = (int)Math.Ceiling(bounds.Right * rasterizationScale) - (int)Math.Floor(bounds.X * rasterizationScale),
            Height = (int)Math.Ceiling(bounds.Bottom * rasterizationScale) - (int)Math.Floor(bounds.Y * rasterizationScale)
        };
        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough, rect.Width > 0 && rect.Height > 0 ? [rect] : []);
    }
    private static string NormalizeSearchText(string text, TitleBarSearchPageDefinition definition) =>
        text.Length < definition.MinLength ? string.Empty : text;
    private void FlushPendingSearch()
    {
        _searchDebounceTimer.Stop();
        if (_pendingSearchTarget is { SearchContextAvailable: true } target && _pendingSearchText is { } text
            && ReferenceEquals(_pendingSearchTarget, _activeSearchTarget))
        {
            if (GetActiveSearchDefinition() is { Trigger: "live" })
            {
                _ = target.SubmitSearchAsync(text);
            }
            else
                target.SearchText = text;
        }
        _pendingSearchTarget = null;
        _pendingSearchText = null;
    }
    private async Task SubmitActiveSearchAsync(string query)
    {
        if (_activeSearchTarget is not { SearchContextAvailable: true } target
            || GetActiveSearchDefinition() is not { Enabled: true } search
            || search.Trigger is not ("submit" or "live"))
            return;
        string text = NormalizeSearchText(query, search);
        if (search.Trigger == "live")
        {
            _searchDebounceTimer.Stop();
            _pendingSearchTarget = null;
            _pendingSearchText = null;
        }
        TitleBarSearchBox.Text = text;
        target.SearchText = text;
        await target.SubmitSearchAsync(text);
    }
    private async void ClearActiveSearch()
    {
        if (_activeSearchTarget is not { SearchContextAvailable: true } target) return;
        FlushPendingSearch();
        TitleBarSearchBox.Text = string.Empty;
        target.SearchText = string.Empty;
        if (GetActiveSearchDefinition() is { Trigger: "submit" or "live" })
            await target.SubmitSearchAsync(string.Empty);
    }
    private bool HandleSearchEscape()
    {
        if (CompactTitleBarSearchFlyout.IsOpen)
        {
            CompactTitleBarSearchFlyout.Hide();
            return true;
        }
        if (_activeSearchTarget?.SearchContextAvailable != true
            || GetActiveSearchDefinition()?.Enabled != true
            || string.IsNullOrEmpty(TitleBarSearchBox.Text))
            return false;
        ClearActiveSearch();
        return true;
    }
    private void FocusTitleBarSearch()
    {
        if (_activeSearchTarget?.SearchContextAvailable != true
            || GetActiveSearchDefinition()?.Enabled != true) return;
        if (_compactTitleBarSearch)
            CompactTitleBarSearchFlyout.ShowAt(CompactTitleBarSearchButton);
        else
            TitleBarSearchBox.Focus(FocusState.Programmatic);
    }
    internal void RefreshTitleBarInputRegionsForSmoke(double rasterizationScale) =>
        UpdateTitleBarInputRegions(rasterizationScale);
    internal void RefreshActiveSearchTargetForSmoke() => UpdateActiveSearchTarget();
    internal void SetTitleBarSearchTextForSmoke(string text)
    {
        TitleBarSearchBox.Text = text;
        HandleTitleBarSearchInput(text);
    }
    internal Task SubmitActiveSearchForSmoke(string text) => SubmitActiveSearchAsync(text);
    internal bool HandleSearchEscapeForSmoke() => HandleSearchEscape();
    internal IReadOnlyList<RectInt32> TitleBarInputRegionsForSmoke =>
        _nonClientPointerSource?.GetRegionRects(NonClientRegionKind.Passthrough) ?? [];
    private void ApplyNavigationPaneBackground()
    {
        if (_isClosing) return;
        Brush surfaceBrush = Root.Background;

        RootNavigation.Background = surfaceBrush;
        if (FindVisualChild<SplitView>(RootNavigation) is { } splitView)
        {
            splitView.PaneBackground = surfaceBrush;
            splitView.Background = surfaceBrush;
            splitView.BorderThickness = new Thickness(0);
            splitView.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }
    }
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
    private double _operationsPanelHeight = 240;
    private bool _operationsPanelResized;
    private bool _isDraggingOperationsSplitter;
    private void OperationsPanel_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(OperationsViewModel.IsQueueExpanded))
            _applicationPreferences.SetOperationsPanelState(_operationsPanelHeight, _operations.IsQueueExpanded);
        if (args.PropertyName is nameof(OperationsViewModel.HasOperations) or nameof(OperationsViewModel.IsQueueExpanded) or nameof(OperationsViewModel.HasPending))
            UpdateOperationsPanelLayout();
    }
    private void OperationsPanelCatalog_Changed(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => { ClampOperationsPanelHeight(); UpdateOperationsPanelLayout(); });
    private void OperationsItems_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => UpdateOperationsPanelLayout();
    private void UpdateOperationsPanelLayout()
    {
        OperationsPanelLayout metrics = _operationsPanelCatalog.Current.Layout;
        if (ContentRoot is null || QueuePanel is null || OperationsSplitter is null) return;
        bool visible = _operations.HasOperations;
        QueuePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OperationsSplitter.Visibility = visible && _operations.IsQueueExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (ContentRoot.RowDefinitions.Count < 3) return;
        if (!visible)
        {
            QueuePanel.Height = 0;
            ContentRoot.RowDefinitions[1].Height = new GridLength(0);
            ContentRoot.RowDefinitions[2].Height = new GridLength(0);
            return;
        }
        if (_operations.IsQueueExpanded)
        {
            ContentRoot.RowDefinitions[1].Height = new GridLength(12);
            double panelHeight = _operationsPanelResized ? _operationsPanelHeight : metrics.PanelDefaultHeight;
            double maxHeight = Math.Min(metrics.PanelMaxHeight, Math.Max(metrics.PanelMinHeight,
                ContentRoot.ActualHeight * _operations.PanelBehavior.HeightMaxFractionOfContent));
            panelHeight = Math.Clamp(panelHeight, metrics.PanelMinHeight, maxHeight);
            _operationsPanelHeight = panelHeight;
            QueuePanel.Height = panelHeight;
            ContentRoot.RowDefinitions[2].Height = new GridLength(panelHeight);
        }
        else
        {
            QueuePanel.Height = 32;
            ContentRoot.RowDefinitions[1].Height = new GridLength(0);
            ContentRoot.RowDefinitions[2].Height = new GridLength(32);
        }
    }
    private void ClampOperationsPanelHeight()
    {
        OperationsPanelLayout metrics = _operationsPanelCatalog.Current.Layout;
        if (ContentRoot is null || ContentRoot.ActualHeight <= 0) return;
        double maxHeight = Math.Min(metrics.PanelMaxHeight, Math.Max(metrics.PanelMinHeight,
            ContentRoot.ActualHeight * _operations.PanelBehavior.HeightMaxFractionOfContent));
        double clamped = Math.Clamp(double.IsFinite(_operationsPanelHeight) ? _operationsPanelHeight : metrics.PanelDefaultHeight, metrics.PanelMinHeight, maxHeight);
        if (Math.Abs(clamped - _operationsPanelHeight) < .5) return;
        _operationsPanelHeight = clamped;
        _applicationPreferences.SetOperationsPanelState(clamped, _operations.IsQueueExpanded);
    }
    private void OperationsSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        => OperationsSplitter.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 128, 128, 128));
    private void OperationsSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingOperationsSplitter) OperationsSplitter.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }
    private void OperationsSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingOperationsSplitter = OperationsSplitter.CapturePointer(e.Pointer);
        e.Handled = true;
    }
    private void OperationsSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingOperationsSplitter) return;
        double height = ContentRoot.ActualHeight - e.GetCurrentPoint(ContentRoot).Position.Y;
        OperationsPanelLayout metrics = _operationsPanelCatalog.Current.Layout;
        _operationsPanelHeight = Math.Clamp(height, metrics.PanelMinHeight, Math.Min(metrics.PanelMaxHeight,
            Math.Max(metrics.PanelMinHeight, ContentRoot.ActualHeight * _operations.PanelBehavior.HeightMaxFractionOfContent)));
        _operationsPanelResized = true;
        UpdateOperationsPanelLayout();
    }
    private void OperationsSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingOperationsSplitter) return;
        OperationsSplitter.ReleasePointerCapture(e.Pointer);
        _isDraggingOperationsSplitter = false;
        _applicationPreferences.SetOperationsPanelState(_operationsPanelHeight, _operations.IsQueueExpanded);
        e.Handled = true;
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
        UpdateNavigationToolTips();
        UpdateOperationsPanelLayout();
    }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        if (HandleSearchEscape())
        {
            args.Handled = true;
            return;
        }
        if (_details?.HandleBack() == true) { args.Handled = true; return; }
    }
    private void Operations_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(OperationsViewModel.HasPending) or nameof(OperationsViewModel.HasFailedOperations))
            UpdateTaskbarBadge();
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
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _motionSettings.AnimationsEnabledChanged -= MotionSettings_AnimationsEnabledChanged;
        _isClosing = true;
        _applicationPreferences.BackdropPreferenceChanged -= OnBackdropPreferenceChanged;
        _windowBackdrop.Dispose();
        _searchDebounceTimer.Stop();
        _cacheBudgetTimer.Stop();
        _operations.PropertyChanged -= Operations_Changed;
        _operations.PropertyChanged -= OperationsPanel_Changed;
        _operations.Items.CollectionChanged -= OperationsItems_CollectionChanged;
        _operationsPanelCatalog.Changed -= OperationsPanelCatalog_Changed;
    }
}
