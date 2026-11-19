using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Core.Models.Office;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class OfficePage : Page
{
    public OfficeViewModel ViewModel { get; }
    public OfficePackageScreenProvider ScreenProvider { get; private set; }
    private readonly OfficeCatalogRemoteService _catalogService;
    private bool _catalogLoaded;

    public OfficePage(OfficeViewModel viewModel, OfficeCatalogRemoteService catalogService)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        _catalogService = catalogService;
        ScreenProvider = new OfficePackageScreenProvider(viewModel);
        InitializeComponent();
        OfficeScreen.Provider = ScreenProvider;
        Loaded += Page_Loaded;
        Unloaded += Page_Unloaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_catalogLoaded)
        {
            _catalogLoaded = true;
            OfficeCatalogDocument catalog = await _catalogService.LoadAsync();
            ViewModel.ApplyOfficeCatalog(catalog);
            ScreenProvider.Dispose();
            ScreenProvider = new OfficePackageScreenProvider(ViewModel);
            OfficeScreen.Provider = ScreenProvider;
        }
        ViewModel.RefreshInstalledProducts();
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e) => ScreenProvider.Dispose();

}
