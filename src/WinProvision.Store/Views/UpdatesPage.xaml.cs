using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Core.Models;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store.Views;
public sealed partial class UpdatesPage : Page
{
    public UpdatesViewModel ViewModel { get; }
    public UpdatesPackageScreenProvider ScreenProvider { get; }
    internal UpdatesDialogs Dialogs { get; }
    private Task? _load;
    public Task InitialContentReady => _load ?? Task.CompletedTask;
    public UpdatesPage(UpdatesViewModel model, PackageScreenProviderRegistry registry, IManualWinGetCommandService manual)
    {
        ViewModel = model;
        ScreenProvider = (UpdatesPackageScreenProvider)registry.Resolve(PackageScreenCatalog.Default.Get("updates"));
        InitializeComponent(); DataContext = model; PackageScreenControl.Provider = ScreenProvider;
        Dialogs = new UpdatesDialogs(model, manual); DialogHost.Content = Dialogs;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ScreenProvider.PresentationActionRequested += PresentationAction;
        if (_load is null) _load = App.IsSmokeTest ? Task.CompletedTask : LoadAsync();
        await _load;
    }
    private async Task LoadAsync()
    {
        try
        {
            await ViewModel.CheckUpdatesAsync();
            if (!Environment.GetCommandLineArgs().Contains("--capture-updates")) await ViewModel.LoadAutoUpdateStateAsync();
        }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "UpdatesPage.Load"); ViewModel.StatusText = exception.Message; }
    }
    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ScreenProvider.PresentationActionRequested -= PresentationAction;
        CloseModal();
    }
    private void PresentationAction(string id) => Dialogs.ShowModal(id);
    internal void ShowModal(string id) => Dialogs.ShowModal(id);
    internal bool CloseModal() => Dialogs.CloseModal();
}
