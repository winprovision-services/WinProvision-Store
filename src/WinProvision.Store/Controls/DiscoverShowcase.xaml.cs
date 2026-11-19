using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using FluentIcons.Common;

using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Controls;
public sealed partial class DiscoverShowcase : UserControl
{
    public HomeViewModel ViewModel { get; }
    private StorefrontViewModel Storefront => ViewModel.Storefront;
    private readonly IPresentationService _presentation;
    private readonly IPackageBackend _backend;
    private readonly IClipboardService _clipboard;
    private readonly PackageInstallOptionsService _installOptions;
    private readonly ManualInstallPresentationService _manual;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _heroTimer;
    private bool _active = true, _heroRotationPaused, _heroPointerOver, _isInitializing = true;
    private AppEntry? _activeSelection;
    public static readonly DependencyProperty ShelfColumnsProperty = DependencyProperty.Register(nameof(ShelfColumns), typeof(int), typeof(DiscoverShowcase), new PropertyMetadata(2));
    public int ShelfColumns { get => (int)GetValue(ShelfColumnsProperty); set => SetValue(ShelfColumnsProperty, value); }
    internal bool IsHeroRotationPaused => _heroRotationPaused;
    internal void SetWindowActive(bool active) { _active = active; RefreshHeroTimer(); }
    public DiscoverShowcase(HomeViewModel model, IPresentationService presentation, IPackageBackend backend, IClipboardService clipboard, PackageInstallOptionsService options)
    {
        ViewModel = model; _presentation = presentation; _backend = backend; _clipboard = clipboard; _installOptions = options;
        _manual = new ManualInstallPresentationService(clipboard, options);
        InitializeComponent(); DataContext = Storefront; _isInitializing = false;
        _heroTimer = DispatcherQueue.CreateTimer(); _heroTimer.Interval = TimeSpan.FromSeconds(8);
        _heroTimer.Tick += (_, _) => Storefront.SetHeroBanner(Storefront.HeroBannerIndex + 1);
        Loaded += (_, _) => { Storefront.PropertyChanged += StorefrontChanged; UpdatePresentation(); };
        Unloaded += (_, _) => { Storefront.PropertyChanged -= StorefrontChanged; _heroTimer.Stop(); };
        UpdatePresentation();
    }
    private void StorefrontChanged(object? sender, PropertyChangedEventArgs e) => UpdatePresentation();
    private void RefreshHeroTimer()
    {
        if (_active && IsLoaded && !_heroRotationPaused && !_heroPointerOver
            && Storefront.StoreBannerApps.Count > 1 && !App.IsSmokeTest)
            _heroTimer.Start();
        else
            _heroTimer.Stop();
    }
    private void UpdatePresentation()
    {
        HeroBanner.DataContext = Storefront.SelectedBanner;
        HeroBanner.Visibility = Storefront.SelectedBanner is null ? Visibility.Collapsed : Visibility.Visible;
        RightStorePromo.DataContext = Storefront.RightPromoApp;
        RightStorePromo.Visibility = Storefront.RightPromoApp is null ? Visibility.Collapsed : Visibility.Visible;
        HeroBannerDot0.Visibility = Storefront.StoreBannerApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeroBannerDot1.Visibility = Storefront.StoreBannerApps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        HeroBannerDot2.Visibility = Storefront.StoreBannerApps.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
        HeroBannerPreviousButton.IsEnabled = HeroBannerNextButton.IsEnabled = Storefront.StoreBannerApps.Count > 1;
        Brush blue = (Brush)Application.Current.Resources["InstallActionBrush"];
        Brush inactive = HeroRotationIcon.Foreground;
        HeroBannerIndicator0.Fill = Storefront.HeroBannerIndex == 0 ? blue : inactive;
        HeroBannerIndicator1.Fill = Storefront.HeroBannerIndex == 1 ? blue : inactive;
        HeroBannerIndicator2.Fill = Storefront.HeroBannerIndex == 2 ? blue : inactive;
        var shelf = Storefront.CurrentShelf;
        PopularShelfCard.Visibility = shelf == StorefrontViewModel.ShelfView.Store ? Visibility.Collapsed : Visibility.Visible;
        StoreShelfCard.Visibility = shelf == StorefrontViewModel.ShelfView.Popular ? Visibility.Collapsed : Visibility.Visible;
        PopularMoreIcon.Icon = shelf == StorefrontViewModel.ShelfView.Popular ? Icon.ChevronLeft : Icon.ChevronRight;
        StoreMoreIcon.Icon = shelf == StorefrontViewModel.ShelfView.Store ? Icon.ChevronLeft : Icon.ChevronRight;
        RefreshHeroTimer(); UpdateStorefrontLayout(ActualWidth);
    }
    private void Control_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateStorefrontLayout(args.NewSize.Width);
    private void UpdateStorefrontLayout(double width)
    {
        bool compact = width < 1050;
        bool promotions = !compact && Storefront.RightPromoApp is not null;
        HeroPromotionsColumn.Width = promotions ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        HeroGapColumn.Width = new GridLength(promotions ? 12 : 0);
        HeroPromotions.Visibility = promotions ? Visibility.Visible : Visibility.Collapsed;
        StorefrontHeroGrid.Height = width < 700 ? 280 : compact ? 320 : 400;
        StorefrontShelves.Columns = compact || Storefront.CurrentShelf != StorefrontViewModel.ShelfView.Overview ? 1 : 2;
        ShelfColumns = width < 760 ? 1 : width < 1200 ? 2 : 3;
    }
    private void HeroBannerPrevious_Click(object sender, RoutedEventArgs args) => Storefront.SetHeroBanner(Storefront.HeroBannerIndex - 1);
    private void HeroBannerNext_Click(object sender, RoutedEventArgs args) => Storefront.SetHeroBanner(Storefront.HeroBannerIndex + 1);
    private void HeroBannerDot_Click(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int index)) Storefront.SetHeroBanner(index); }
    private void HeroRotationToggleButton_Toggled(object sender, RoutedEventArgs args)
    {
        if (_isInitializing)
            return;

        if (sender is not ToggleButton { IsChecked: bool isPlaying })
            return;

        _heroRotationPaused = !isPlaying;
        string action = isPlaying ? "Pausar" : "Retomar";
        string label = $"{action} rotação automática";
        HeroRotationIcon.Icon = isPlaying ? Icon.Pause : Icon.Play;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HeroRotationToggleButton, label);
        ToolTipService.SetToolTip(HeroRotationToggleButton, label);
        RefreshHeroTimer();
    }
    private void HeroBanner_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _heroPointerOver = true;
        RefreshHeroTimer();
    }
    private void HeroBanner_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _heroPointerOver = false;
        RefreshHeroTimer();
    }
    private async void HeroBanner_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsInteractiveChild(args.OriginalSource as DependencyObject, HeroBanner))
            return;
        if (args.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space))
            return;

        args.Handled = true;
        if (Storefront.SelectedBanner is { } app)
            await ViewModel.ShowDetailsAsync(app);
    }
    private async void HeroBannerDetails_Click(object sender, RoutedEventArgs args) { if (Storefront.SelectedBanner is { } app) await ViewModel.ShowDetailsAsync(app); }
    private void PopularMoreButton_Click(object sender, RoutedEventArgs args) => Storefront.SetShelfView(Storefront.CurrentShelf == StorefrontViewModel.ShelfView.Popular ? StorefrontViewModel.ShelfView.Overview : StorefrontViewModel.ShelfView.Popular);
    private void StoreMoreButton_Click(object sender, RoutedEventArgs args) => Storefront.SetShelfView(Storefront.CurrentShelf == StorefrontViewModel.ShelfView.Store ? StorefrontViewModel.ShelfView.Overview : StorefrontViewModel.ShelfView.Store);
    private async void InstallButton_Click(object sender, RoutedEventArgs args) { if (sender is Button { Tag: AppEntry app }) await Storefront.InstallAsync(app); }
    private async void StorefrontApp_Click(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { DataContext: AppEntry app }) await ViewModel.ShowDetailsAsync(app); }
    private void StorefrontApp_RightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AppEntry app } element || app.IsSelf)
            return;

        _activeSelection = app;
        CreatePackageContextMenu(app).ShowAt(element, args.GetPosition(element));
        args.Handled = true;
    }

    private void PackageOverflowButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: AppEntry app } button || app.IsSelf)
            return;

        _activeSelection = app;
        CreatePackageContextMenu(app).ShowAt(button);
    }

    private MenuFlyout CreatePackageContextMenu(AppEntry app)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(ContextItem("Instalar", app, "install"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(ContextItem("Opções de instalação", app, "options"));
        menu.Items.Add(ContextItem("Copiar comando para instalação manual", app, "manual"));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (IsWinGet(app))
        {
            menu.Items.Add(ContextItem("Instalar como administrador", app, "admin"));
            menu.Items.Add(ContextItem("Instalação interativa", app, "interactive"));
            menu.Items.Add(ContextItem("Instalar ignorando a verificação de hash", app, "skip-hash"));
        }
        if (IsDownloadSupported(app))
            menu.Items.Add(ContextItem("Baixar instalador", app, "download"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(ContextItem("Detalhes do pacote", app, "details"));
        menu.Items.Add(ContextItem("Copiar ID do pacote", app, "copy-id"));
        if (TryGetPackageLink(app) is not null)
            menu.Items.Add(ContextItem("Copiar link do pacote", app, "copy-link"));
        return menu;
    }

    private MenuFlyoutItem ContextItem(string text, AppEntry app, string action)
    {
        FluentIcons.Common.Icon icon = action switch
        {
            "install" => FluentIcons.Common.Icon.ArrowDownload,
            "options" => FluentIcons.Common.Icon.Settings,
            "manual" => FluentIcons.Common.Icon.Code,
            "admin" => FluentIcons.Common.Icon.Shield,
            "interactive" => FluentIcons.Common.Icon.Code,
            "skip-hash" => FluentIcons.Common.Icon.Shield,
            "download" => FluentIcons.Common.Icon.ArrowDownload,
            "details" => FluentIcons.Common.Icon.Info,
            "copy-id" => FluentIcons.Common.Icon.Tag,
            "copy-link" => FluentIcons.Common.Icon.Link,
            _ => FluentIcons.Common.Icon.Apps
        };
        var item = new MenuFlyoutItem { Text = text, Tag = (app, action), Icon = new FluentIcons.WinUI.FluentIcon { Icon = icon, IconVariant = IconVariant.Regular, IconSize = IconSize.Size16, FontSize = 16 } };
        item.Click += ContextMenuItem_Click;
        return item;
    }

    private static bool IsWinGet(AppEntry app) =>
        string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase)
        || app.Source.StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase);

    private static bool IsDownloadSupported(AppEntry app) =>
        IsWinGet(app) || string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetPackageLink(AppEntry app)
    {
        string? link = app.PackageUrl ?? app.Homepage;
        return Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && uri.Scheme is "https" or "http" ? uri.AbsoluteUri : null;
    }

    internal void OpenManualInstall(AppEntry app) => Storefront.StatusText = _manual.Open(app);

    private async void ContextMenuItem_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem { Tag: ValueTuple<AppEntry, string> context }) return;
        AppEntry app = context.Item1;
        switch (context.Item2)
        {
            case "install":
                await Storefront.InstallAsync(app);
                break;
            case "options":
                await _presentation.ShowDetailsAsync(app, showOptions: true);
                break;
            case "manual":
                OpenManualInstall(app);
                break;
            case "admin":
                await Storefront.InstallAsync(app, new PackageInstallOptions(Scope: "machine", RequiresElevation: true));
                break;
            case "interactive":
                await Storefront.InstallAsync(app, new PackageInstallOptions(Interactive: true));
                break;
            case "skip-hash":
                await Storefront.InstallAsync(app, new PackageInstallOptions(IgnoreSecurityHash: true));
                break;
            case "download":
                await DownloadInstallerAsync(app);
                break;
            case "details":
                await ViewModel.ShowDetailsAsync(app);
                break;
            case "copy-id":
                Storefront.StatusText = _clipboard.TrySetText(app.Id)
                    ? $"ID '{app.Id}' copiado para a área de transferência."
                    : "Não foi possível copiar o ID do pacote.";
                break;
            case "copy-link" when TryGetPackageLink(app) is { } link:
                Storefront.StatusText = _clipboard.TrySetText(link)
                    ? "Link do pacote copiado para a área de transferência."
                    : "Não foi possível copiar o link do pacote.";
                break;
        }
    }

    private async Task DownloadInstallerAsync(AppEntry app)
    {
        string? folder = await _presentation.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder)) return;
        Storefront.StatusText = $"Baixando o instalador de {app.Name}…";
        try
        {
            var result = await _backend.DownloadAsync(app.Id, app.Source, folder, CancellationToken.None, app.Version);
            Storefront.StatusText = result.Success
                ? $"Instalador de {app.Name} baixado."
                : $"Não foi possível baixar o instalador de {app.Name}.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "HomePage.DownloadInstallerAsync");
            Storefront.StatusText = $"Não foi possível baixar o instalador de {app.Name}.";
        }
    }
    private async void AppCard_Click(object sender, TappedRoutedEventArgs args)
    {
        if (IsInteractiveChild(args.OriginalSource as DependencyObject, sender)) return;
        if (sender is FrameworkElement { DataContext: AppEntry app }) { args.Handled = true; await ViewModel.ShowDetailsAsync(app); }
    }
    internal static bool IsInteractiveChild(DependencyObject? source, object container)
    {
        for (var current = source; current is not null && !ReferenceEquals(current, container); current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }
}
