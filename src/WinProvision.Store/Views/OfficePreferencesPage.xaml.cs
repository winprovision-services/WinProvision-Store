using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class OfficePreferencesPage : Page
{
    private bool _isLoaded;
    public OfficeViewModel ViewModel { get; }
    public event EventHandler? BackRequested;

    public OfficePreferencesPage(OfficeViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => _isLoaded = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async void AutoUpdates_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoaded && ViewModel.HasInstalledProducts && ViewModel.CanEditUpdatePreference)
            await ViewModel.ApplyAutoUpdatesCommand.ExecuteAsync(null);
    }
}
