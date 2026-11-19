using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class PackageManagersPage : Page, ITitleBarSearchTarget
{
    public PackageManagersViewModel ViewModel { get; }
    private readonly IClipboardService _clipboard;
    bool ITitleBarSearchTarget.SearchContextAvailable => ManagersHome.Visibility == Visibility.Visible;
    string ITitleBarSearchTarget.SearchText
    {
        get => ViewModel.ManagerSearchText;
        set => ViewModel.ManagerSearchText = value;
    }
    Task ITitleBarSearchTarget.SubmitSearchAsync(string query)
    {
        ViewModel.ManagerSearchText = query;
        return Task.CompletedTask;
    }
    public event EventHandler? SearchContextChanged;
    public event Action<string>? ManagerLogsNavigationRequested;

    public PackageManagersPage(PackageManagersViewModel viewModel, IClipboardService clipboard)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _clipboard = clipboard;
        DataContext = viewModel;
        ViewModel.ManagerDetailsRequested += OnManagerDetailsRequested;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ManagersHome.Visibility = Visibility.Visible;
        ManagerDetails.Visibility = Visibility.Collapsed;
        ManagerSettingsScrollViewer.Visibility = Visibility.Visible;
        WinProvisionApiDetailsContent.Visibility = Visibility.Collapsed;
        SearchContextChanged?.Invoke(this, EventArgs.Empty);
        if (!ViewModel.AreManagerChecksComplete)
            _ = ViewModel.CheckAllManagersCommand.ExecuteAsync(null);
    }

    private void OnManagerDetailsRequested(object? sender, string id)
    {
        _ = ViewModel.OpenManagerDetails(id);
        bool isWinProvisionApi = id.Equals("winprovision-store", StringComparison.OrdinalIgnoreCase);
        ManagerSettingsScrollViewer.Visibility = isWinProvisionApi ? Visibility.Collapsed : Visibility.Visible;
        WinProvisionApiDetailsContent.Visibility = isWinProvisionApi ? Visibility.Visible : Visibility.Collapsed;
        ManagersHome.Visibility = Visibility.Collapsed;
        ManagerDetails.Visibility = Visibility.Visible;
        SearchContextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        ManagerDetails.Visibility = Visibility.Collapsed;
        ManagersHome.Visibility = Visibility.Visible;
        SearchContextChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RestoreManagerDetails(string managerId) => OnManagerDetailsRequested(this, managerId);

    private async void SelectedAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PackageManagerSettingsActionViewModel action }) return;
        bool isLogAction = action.Id.Equals("openLogs", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (action.RequiresConfirmation)
            {
                var confirmation = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = action.Label + "?",
                    Content = "Esta ação pode alterar a configuração do gerenciador.",
                    PrimaryButtonText = action.Label,
                    CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await ContentDialogCoordinator.ShowAsync(confirmation) != ContentDialogResult.Primary) return;
            }

            if (isLogAction)
            {
                if (ViewModel.SelectedManager?.Id is { } managerId)
                    ManagerLogsNavigationRequested?.Invoke(managerId);
                return;
            }

            await ViewModel.RunActionAsync(action.Id);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "PackageManagerSettings.Action");
        }
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var name = new TextBox { PlaceholderText = "Nome da fonte" };
        var url = new TextBox { PlaceholderText = "https://endereco-da-fonte", Margin = new Thickness(0, 8, 0, 0) };
        var content = new StackPanel { Spacing = 8, Children = { name, url } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Adicionar fonte",
            Content = content,
            PrimaryButtonText = "Adicionar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        ViewModel.NewSelectedSourceName = name.Text;
        ViewModel.NewSelectedSourceUrl = url.Text;
        await ViewModel.AddSelectedSourceCommand.ExecuteAsync(null);
    }

    private void CopyExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.SelectedManagerExecutable))
            _clipboard.TrySetText(ViewModel.SelectedManagerExecutable);
    }

    private void OfficeDetails_Click(object sender, RoutedEventArgs e) => ViewModel.OpenOfficeSettings();
}
