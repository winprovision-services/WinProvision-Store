using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Views;
public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }
    private StorefrontViewModel Storefront => ViewModel.Storefront;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _heroTimer;
    private bool _subscribed;
    private bool _active = true;
    private bool _heroRotationPaused;
    private bool _heroPointerOver;
    private bool _isInitializing = true;
    public static readonly DependencyProperty AppsColumnsProperty = DependencyProperty.Register(nameof(AppsColumns), typeof(int), typeof(HomePage), new PropertyMetadata(2));
    public static readonly DependencyProperty ShelfColumnsProperty = DependencyProperty.Register(nameof(ShelfColumns), typeof(int), typeof(HomePage), new PropertyMetadata(2));
    public int AppsColumns { get => (int)GetValue(AppsColumnsProperty); set => SetValue(AppsColumnsProperty, value); }
    public int ShelfColumns { get => (int)GetValue(ShelfColumnsProperty); set => SetValue(ShelfColumnsProperty, value); }
    internal bool IsHeroRotationPaused => _heroRotationPaused;
    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial record CategoryChip(string Tag, string Label, string Icon, bool IsSelected);
    public HomePage(HomeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _isInitializing = false;
        DataContext = Storefront;
        CategoryList.ItemsSource = new System.Collections.ObjectModel.ObservableCollection<CategoryChip>
        {
            new("all", "Todos", "\uE8A9", true), new("productivity", "Produtividade", "\uE7C3", false),
            new("development", "Desenvolvimento", "\uE943", false), new("utilities", "Utilitários", "\uEC7A", false),
            new("multimedia", "Multimídia", "\uE768", false), new("security", "Segurança", "\uE83D", false)
        };
        _heroTimer = DispatcherQueue.CreateTimer(); _heroTimer.Interval = TimeSpan.FromSeconds(8);
        _heroTimer.Tick += (_, _) => Storefront.SetHeroBanner(Storefront.HeroBannerIndex + 1);
        SetAppsViewMode(false);
    }
    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (!_subscribed) { Storefront.PropertyChanged += Storefront_Changed; _subscribed = true; }
        UpdatePresentation();
        App.MemoryLog?.RecordOnce("homepage_loaded");
        if (!App.IsSmokeTest) await Storefront.OnNavigatedToAsync();
    }
    private async void Page_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_subscribed) { Storefront.PropertyChanged -= Storefront_Changed; _subscribed = false; }
        _heroTimer.Stop();
        if (!App.IsSmokeTest) await Storefront.OnNavigatedFromAsync();
    }
    private void Storefront_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(StorefrontViewModel.SelectedBanner) or nameof(StorefrontViewModel.RightPromoApp) or nameof(StorefrontViewModel.CurrentShelf)) UpdatePresentation();
    }
    public void SetWindowActive(bool active) { _active = active; RefreshHeroTimer(); }
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
        PopularMoreIcon.Glyph = shelf == StorefrontViewModel.ShelfView.Popular ? "\uE76B" : "\uE76C";
        StoreMoreIcon.Glyph = shelf == StorefrontViewModel.ShelfView.Store ? "\uE76B" : "\uE76C";
        RefreshHeroTimer(); UpdateStorefrontLayout(ActualWidth);
    }
    private void Page_SizeChanged(object sender, SizeChangedEventArgs args) => UpdateStorefrontLayout(args.NewSize.Width);
    private void UpdateStorefrontLayout(double width)
    {
        bool compact = width < 1050;
        bool promotions = !compact && Storefront.RightPromoApp is not null;
        HeroPromotionsColumn.Width = promotions ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        HeroGapColumn.Width = new GridLength(promotions ? 12 : 0);
        HeroPromotions.Visibility = promotions ? Visibility.Visible : Visibility.Collapsed;
        StorefrontHeroGrid.Height = width < 700 ? 300 : compact ? 360 : 440;
        StorefrontShelves.Columns = compact || Storefront.CurrentShelf != StorefrontViewModel.ShelfView.Overview ? 1 : 2;
        ShelfColumns = width < 760 ? 1 : 2;
        AppsColumns = width < 700 ? 1 : width < 1100 ? 2 : 3;
        SearchCardsLayout.MaximumRowsOrColumns = AppsColumns;
    }
    private void CategoryChip_Checked(object sender, RoutedEventArgs args) { if (sender is RadioButton { Tag: string tag }) Storefront.SelectCategory(tag); }
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { }
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) { Storefront.SearchText = args.QueryText; Storefront.ApplyFilter(); }
    private void CatalogSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args) { if (ViewModel is not null) Storefront.SelectSort(CatalogSortComboBox.SelectedIndex); }
    private void GridViewToggleButton_Click(object sender, RoutedEventArgs args) => SetAppsViewMode(false);
    private void ListViewToggleButton_Click(object sender, RoutedEventArgs args) => SetAppsViewMode(true);
    private void IconsViewToggleButton_Click(object sender, RoutedEventArgs args) => SetAppsViewMode(false, true);
    public void SetAppsViewMode(bool list, bool icons = false)
    {
        GridViewToggleButton.IsChecked = !list && !icons; ListViewToggleButton.IsChecked = list; IconsViewToggleButton.IsChecked = icons;
        AppsGridScrollViewer.Visibility = !list && !icons ? Visibility.Visible : Visibility.Collapsed;
        AppsListScrollViewer.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        AppsIconsScrollViewer.Visibility = icons ? Visibility.Visible : Visibility.Collapsed;
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
        HeroRotationIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
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
