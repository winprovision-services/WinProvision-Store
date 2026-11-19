using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store;
internal static partial class SmokeTest
{
    private static async Task VerifyCollectionSharedScreenAsync(Views.CollectionsPage page, IServiceProvider services)
    {
        var provider = page.ScreenProvider;
        var screen = (PackageScreen)page.FindName("Screen")!;
        Check(provider.Definition.Id == "collections" && provider.Rows.Count == page.ViewModel.Items.Count,
            "Coleção: wrapper apresenta todas as linhas no componente compartilhado");
        Check(provider.Filters.Single(option => option.Id == "searchMode").Title == "Modo de pesquisa",
            "Coleção: rótulo interno das opções de pesquisa é Modo de pesquisa");
        PackageScreenFilterOption officeSource = provider.Filters.Single(option => option.Id == "source:office");
        Check(provider.Filters.All(option => option.Id != "source:all")
            && officeSource.IconUri == "ms-appx:///Assets/Icons/microsoft-office.svg"
            && officeSource.IconUriDark == "ms-appx:///Assets/Icons/microsoft-office.svg",
            "Coleção: filtro de Office usa o ícone compartilhado e não exibe Todas as fontes");
        InvokePackageScreenHandler(screen, "BulkAction_Click", new Button { Tag = "clear" }, new RoutedEventArgs());
        Check(provider.Rows.Count == 0 && provider.Filters.Where(option => option.Kind == "source").All(option => option.IsChecked == false),
            "Coleção: Limpar desmarca as fontes individuais");
        InvokePackageScreenHandler(screen, "BulkAction_Click", new Button { Tag = "select-all" }, new RoutedEventArgs());
        Check(provider.Rows.Count == page.ViewModel.Items.Count && provider.Rows.Count > 0,
            "Coleção: Selecionar tudo restaura os pacotes das fontes habilitadas");
        services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(true);
        Check(provider.Filters.Any(option => option.Id == "source:chocolatey") && provider.Filters.Any(option => option.Id == "source:office"),
            "Coleção: Chocolatey vem do registro e Office é uma fonte adicional da definição");
        services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(false);
        var officeApp = new AppEntry { Id = "Smoke.Office", Name = "Microsoft 365", Source = "office", IconUrl = "https://cdn.winprovisionstore.com.br/office/media/ms365.png" };
        page.ViewModel.Add(officeApp);
        PackageScreenRow officeRow = provider.Rows.Single(row => row.Id == officeApp.Id);
        try
        {
            Check(officeRow.Source == "Microsoft 365"
                && officeRow.IconUri == OfficeAppCatalog.Microsoft365IconUrl
                && officeRow.IconUriDark is null
                && officeRow.IconFallbackUri == OfficeCatalogRuntime.Current.SourceIconFallback
                && officeRow.SourceIconUri == "ms-appx:///Assets/Icons/microsoft-office.svg"
                && officeRow.SourceIconUriDark == "ms-appx:///Assets/Icons/microsoft-office.svg",
                "Coleção: ícone do produto Office é colorido e o badge da origem usa a logo monocromática por tema");
        }
        finally
        {
            page.ViewModel.SelectedProfile!.Items.Remove(officeRow.Payload as PackageItemViewModel
                ?? throw new InvalidOperationException("Pacote Office sintético ausente."));
        }
        foreach (string mode in new[] { "list", "details", "grid" })
        {
            provider.SetViewModeCommand.Execute(mode);
            await Task.Delay(60);
            Check(((FrameworkElement)screen.FindName(mode == "list" ? "ListView" : mode == "details" ? "DetailsView" : "GridView")).Visibility == Visibility.Visible,
                $"Coleção: visualização {mode} usa o mesmo componente");
        }
        provider.SetViewModeCommand.Execute("grid");
        Check(provider.Definition.Toolbar.Overflow.All(id => provider.GetActionCommand(id) is not null),
            "Coleção: todas as ações declaradas têm comando, incluindo cancelamento");
        page.ViewModel.SetAllSelected(false);
        var primary = (SplitButton)screen.FindName("PrimaryActionButton");
        var primaryMenu = (MenuFlyout)screen.FindName("PrimaryFlyout");
        Check(!primary.IsEnabled && primaryMenu.Items.OfType<MenuFlyoutItem>().All(item => !item.IsEnabled),
            "Coleção: ação principal e seta exigem pacotes selecionados");
        Check(provider.Definition.Toolbar.Overflow.Contains("installAll")
            && provider.GetActionCommand("installAll")?.CanExecute(null) == true,
            "Coleção: Instalar todos permanece disponível em Mais ações sem seleção");
        var presentation = (SmokePresentationService)services.GetRequiredService<Services.IPresentationService>();
        var backend = (SmokePackageBackend)services.GetRequiredService<IPackageBackend>();
        int installCount = backend.InstallCount;
        int confirmCount = presentation.ConfirmCallCount;
        int eligibleCount = page.ViewModel.SelectedProfile!.Items.Count(item => item.SupportsInstall && !item.IsInstalled);
        MenuFlyoutItem installAllItem = ((MenuFlyout)screen.FindName("OverflowFlyout")).Items
            .OfType<MenuFlyoutItem>().Single(item => Equals(item.Tag, "installAll"));
        presentation.ConfirmResult = false;
        InvokePackageScreenHandler(screen, "ActionMenuItem_Click", installAllItem, new RoutedEventArgs());
        string expectedEligibleCount = eligibleCount == 1 ? "1 item" : $"{eligibleCount} itens";
        Check(presentation.ConfirmCallCount == confirmCount + 1
            && presentation.LastConfirmMessage?.Contains(expectedEligibleCount, StringComparison.Ordinal) == true
            && backend.InstallCount == installCount,
            "Coleção: cancelar Instalar todos informa a quantidade elegível e não instala");
        presentation.ConfirmResult = true;
        InvokePackageScreenHandler(screen, "ActionMenuItem_Click", installAllItem, new RoutedEventArgs());
        await WaitUntilAsync(() => backend.InstallCount > installCount, "Instalar todos confirmado não chamou o backend simulado.");
        Check(presentation.ConfirmCallCount == confirmCount + 2 && backend.InstallCount > installCount,
            "Coleção: confirmar Instalar todos executa a operação em lote");
        // Export uses a temporary smoke artifact and leaves the stored collection untouched.
        string path = Path.Combine(OutputDirectory, "collection-shared.json");
        presentation.SavePath = path;
        if (provider.GetActionCommand("saveCollection") is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand save)
            await save.ExecuteAsync(null);
        Check(File.Exists(path), "Coleção: Salvar como preserva a exportação JSON pelo comando existente");
        CollectionProfile profile = page.ViewModel.SelectedProfile ?? throw new InvalidOperationException("Coleção padrão ausente no smoke.");
        var app = new AppEntry { Id = "Smoke.TransientStatus", Name = "Smoke Transitório", Source = "winget" };
        page.ViewModel.Add(app);
        PackageItemViewModel transientItem = profile.Items.Single(item => item.Id == app.Id);
        string transientStatus = $"{app.Name} adicionado à {profile.Title}.";
        try
        {
            Check(page.ViewModel.StatusText == transientStatus
                && ((TextBlock)screen.FindName("StatusText")).Text == transientStatus,
                "Coleção: mensagem de inclusão aparece sem repetir a palavra coleção");
            await Task.Delay(TimeSpan.FromSeconds(4.3));
            Check(page.ViewModel.StatusText != transientStatus
                && page.ViewModel.StatusText.Contains($"{profile.Items.Count} aplicativo(s)", StringComparison.Ordinal),
                "Coleção: mensagem temporária expira e restaura o estado estável");
        }
        finally
        {
            profile.Items.Remove(transientItem);
        }
    }
}
