using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Views;
public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; }
    public DiscoverPackageScreenProvider ScreenProvider { get; }
    internal DiscoverShowcase Showcase { get; }
    private readonly IPresentationService _presentation;
    private readonly ApplicationPreferencesService _preferences;
    private bool _subscribed;
    internal bool IsHeroRotationPaused => Showcase.IsHeroRotationPaused;
    public void SetWindowActive(bool active) => Showcase.SetWindowActive(active);
    public HomePage(HomeViewModel model, PackageScreenProviderRegistry registry, IPresentationService presentation,
        IPackageBackend backend, IClipboardService clipboard, PackageInstallOptionsService options, ApplicationPreferencesService preferences)
    {
#if DEBUG
        long screenConstructionStartedAt = DebugPerformanceTelemetry.Start();
#endif
        ViewModel = model; _presentation = presentation; _preferences = preferences;
        ScreenProvider = (DiscoverPackageScreenProvider)registry.Resolve(PackageScreenCatalog.Default.Get("discover"));
        InitializeComponent(); DataContext = model.Storefront; Screen.Provider = ScreenProvider;
        Showcase = new DiscoverShowcase(model, presentation, backend, clipboard, options);
        Screen.BrowseContent = Showcase;
        RefreshPresentation();
        App.MemoryLog?.RecordOnce("catalog-opened");
        DebugPerformanceTelemetry.Mark("catalog-opened");
#if DEBUG
        DebugPerformanceTelemetry.Duration("startup-discover-screen-construction", screenConstructionStartedAt);
#endif
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            ViewModel.Storefront.PropertyChanged += StorefrontChanged;
            ScreenProvider.PresentationActionRequested += PresentationAction;
            _preferences.SearchOnDemandChanged += SearchOnDemandChanged;
        }
        RefreshPresentation();
        if (!App.IsSmokeTest) await ViewModel.Storefront.OnNavigatedToAsync();
    }
    private async void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            _subscribed = false;
            ViewModel.Storefront.PropertyChanged -= StorefrontChanged;
            ScreenProvider.PresentationActionRequested -= PresentationAction;
            _preferences.SearchOnDemandChanged -= SearchOnDemandChanged;
        }
        if (!App.IsSmokeTest) await ViewModel.Storefront.OnNavigatedFromAsync();
    }
    private void StorefrontChanged(object? sender, PropertyChangedEventArgs e) => RefreshPresentation();
    private void SearchOnDemandChanged(bool enabled) => RefreshPresentation();
    private void RefreshPresentation() => Screen.ShowBrowseContent = ViewModel.Storefront.IsBrowsing && !_preferences.SearchOnDemand;
    internal void SetAppsViewMode(string mode) => ScreenProvider.SetViewModeCommand.Execute(mode);
    private async void PresentationAction(string action, PackageScreenRow? row)
    {
        AppEntry? app = row?.Payload as AppEntry ?? ViewModel.Storefront.GetSelectedApps().FirstOrDefault();
        if (app is null) return;
        try
        {
            if (action == "manualInstall") Showcase.OpenManualInstall(app);
            else if (action == "manageOptions") await _presentation.ShowDetailsAsync(app, showOptions: true);
        }
        catch (Exception exception) { ViewModel.Storefront.StatusText = exception.Message; }
    }
}
