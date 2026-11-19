using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Services;
using WinProvision.Core.Models;

namespace WinProvision.Store.Views;

public sealed partial class CollectionsPage : Page
{
    public CollectionsViewModel ViewModel { get; }
    public CollectionsPackageScreenProvider ScreenProvider { get; }
    public AccountSyncViewModel Account { get; }

    public CollectionsPage(CollectionsViewModel viewModel, PackageScreenProviderRegistry registry, AccountSyncViewModel account)
    {
        ViewModel = viewModel;
        ScreenProvider = (CollectionsPackageScreenProvider)registry.Resolve(PackageScreenCatalog.Default.Get("collections"));
        Account = account;
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ScreenProvider.PresentationActionRequested += HandlePresentationAction;
        Account.Refresh();
        if (!App.IsSmokeTest) ViewModel.StartMetrics();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ScreenProvider.PresentationActionRequested -= HandlePresentationAction;
        ViewModel.CancelMetrics();
    }

    private async void HandlePresentationAction(string actionId)
    {
        try
        {
            switch (actionId)
            {
                case "openCollection": await ViewModel.ImportCommand.ExecuteAsync(null); break;
                case "importCloud": await Account.RestoreFromCloudCommand.ExecuteAsync(null); break;
                case "renameCollection": await RenameCollectionAsync(); break;
                case "deleteCollection": await DeleteCollectionAsync(); break;
                case "clearCollection": await ClearCollectionAsync(); break;
                case "details":
                    PackageItemViewModel? item = ViewModel.Items.FirstOrDefault(candidate => candidate.IsSelected) ?? ViewModel.Items.FirstOrDefault();
                    if (item is not null) await item.DetailsCommand.ExecuteAsync(null);
                    break;
            }
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = $"Não foi possível concluir a ação: {exception.Message}";
            ViewModel.HasError = true;
            ViewModel.ErrorMessage = exception.Message;
        }
    }

    private async Task RenameCollectionAsync()
    {
        if (ViewModel.SelectedProfile is not { IsDefault: false } profile) return;
        var editor = new TextBox { Text = profile.Title, MinWidth = 300 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Renomear coleção",
            Content = editor,
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary
        };
        editor.Loaded += (_, _) => { editor.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); editor.SelectAll(); };
        if (await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary) ViewModel.RenameProfile(editor.Text);
    }

    private async Task DeleteCollectionAsync()
    {
        if (ViewModel.SelectedProfile is not { IsDefault: false } profile) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Excluir coleção",
            Content = $"Deseja excluir a coleção “{profile.Title}”?",
            PrimaryButtonText = "Excluir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary) await ViewModel.DeleteCommand.ExecuteAsync(null);
    }

    private async Task ClearCollectionAsync()
    {
        if (!ViewModel.HasItems) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Limpar coleção",
            Content = "Remover todos os aplicativos da coleção atual?",
            PrimaryButtonText = "Limpar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary) await ViewModel.ClearCommand.ExecuteAsync(null);
    }
}
