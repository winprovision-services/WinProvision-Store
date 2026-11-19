using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store;

internal static partial class SmokeTest
{
    private static async Task VerifyInstalledSharedScreenActionsAsync(
        Views.InstalledPackagesPage page,
        SmokePackageBackend backend,
        IServiceProvider services)
    {
        InstalledPackagesViewModel model = page.ViewModel;
        InstalledPackageScreenProvider provider = page.ScreenProvider;
        var screen = (PackageScreen)page.Content;
        InstalledPackageRow package = model.Packages.Single();
        PackageScreenRow row = provider.Rows.Single();
        var presentation = (SmokePresentationService)services.GetRequiredService<IPresentationService>();
        var installedService = (SmokePackageBackend.SmokeInstalledPackagesService)services.GetRequiredService<IInstalledPackagesService>();

        Check(screen.FindName("PrimaryActionButton") is SplitButton
            && screen.FindName("ReloadButton") is Button
            && screen.FindName("MoreButton") is Button
            && screen.FindName("ListModeButton") is not null
            && screen.FindName("DetailsModeButton") is not null
            && screen.FindName("GridModeButton") is not null,
            "Instalados: wrapper apresenta o shell compartilhado de tela de pacotes");
        var reload = (Button)screen.FindName("ReloadButton");
        int refreshCount = installedService.ListCount;
        Check(ReferenceEquals(reload.Command, provider.RefreshCommand), "Instalados: Recarregar encaminha ao provider");
        await provider.RefreshCommand.ExecuteAsync(null);
        Check(installedService.ListCount == refreshCount + 1 && model.Packages.Count == 1,
            "Instalados: Recarregar consulta serviço e atualiza pacotes");
        row = provider.Rows.Single();
        package = model.Packages.Single();

        Check(provider.Filters.Single(filter => filter.Id == "searchMode").Title == "Modo de pesquisa",
            "Instalados: rótulo interno das opções de pesquisa é Modo de pesquisa");
        var localSource = provider.Filters.Single(filter => filter.Id == "source:local");
        Check(localSource.Icon == PackageScreenCatalog.Default.ResolveIcon("local")
            && localSource.Icon != PackageScreenCatalog.Default.ResolveIcon("installed"),
            "Instalados: PC Local usa um ícone próprio do catálogo");
        var primary = (SplitButton)screen.FindName("PrimaryActionButton");
        var primaryMenu = (MenuFlyout)screen.FindName("PrimaryFlyout");
        Check(!primary.IsEnabled && primaryMenu.Items.OfType<MenuFlyoutItem>().All(item => !item.IsEnabled),
            "Instalados: ação principal e seta ficam desabilitadas sem seleção");

        bool filterOpen = provider.IsFilterOpen;
        InvokePackageScreenHandler(screen, "FiltersButton_Click", screen.FindName("FiltersButton"), new RoutedEventArgs());
        Check(provider.IsFilterOpen != filterOpen && ((FrameworkElement)screen.FindName("Sidebar")).Visibility == Visibility.Collapsed,
            "Instalados: botão Filtros oculta o painel compartilhado");
        InvokePackageScreenHandler(screen, "FiltersButton_Click", screen.FindName("FiltersButton"), new RoutedEventArgs());
        Check(provider.IsFilterOpen && ((FrameworkElement)screen.FindName("Sidebar")).Visibility == Visibility.Visible,
            "Instalados: botão Filtros reabre o painel compartilhado");

        var list = (ListView)screen.FindName("ListView");
        var grid = (GridView)screen.FindName("GridView");
        var icons = (ListView)screen.FindName("DetailsView");
        foreach (string mode in new[] { "grid", "details", "list" })
        {
            InvokePackageScreenHandler(screen, "ViewMode_Click", new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag = mode }, new RoutedEventArgs());
            ItemsControl active = mode == "list" ? list : mode == "grid" ? grid : icons;
            Check(model.ViewMode == mode && ReferenceEquals(active.ItemsSource, provider.Rows)
                && (mode == "list" || list.ItemsSource is null)
                && (mode == "grid" || grid.ItemsSource is null)
                && (mode == "details" || icons.ItemsSource is null),
                $"Instalados: shell ativa somente o host {mode}");
        }

        PackageScreenFilterOption Filter(string id) => provider.Filters.Single(option => option.Id == id);
        void SetFilter(string id, bool enabled)
        {
            PackageScreenFilterOption option = Filter(id);
            option.IsChecked = enabled;
            provider.SetFilterCommand.Execute(option);
        }
        SetFilter("source:winget", false);
        Check(model.VisiblePackages.Count == 0, "Instalados: filtro WinGet remove resultados WinGet");
        SetFilter("source:winget", true);
        Check(model.VisiblePackages.Count == 1, "Instalados: reativar WinGet restaura resultados");
        SetFilter("source:msstore", false);
        Check(model.VisiblePackages.Count == 1, "Instalados: filtro Store mantém pacotes de outras origens");
        SetFilter("source:msstore", true);
        SetFilter("source:local", true);
        Check(model.FilterLocal && model.VisiblePackages.Count == 1, "Instalados: origem PC Local pode ser ativada sem ocultar WinGet");
        SetFilter("source:local", false);

        PackageScreenFilterOption search = Filter("searchMode");
        search.SelectedValue = "id";
        provider.SetFilterCommand.Execute(search);
        model.SearchText = "ausente";
        Check(model.SearchMode == "ID" && model.VisiblePackages.Count == 0, "Instalados: modo por ID filtra resultados");
        model.SearchText = "Test.Package";
        Check(model.VisiblePackages.Count == 1, "Instalados: pesquisa por ID encontra pacote");
        model.SearchText = "Test";
        search.SelectedValue = "exact";
        provider.SetFilterCommand.Execute(search);
        Check(model.VisiblePackages.Count == 0, "Instalados: correspondência exata rejeita trecho parcial");
        model.SearchText = string.Empty;
        search.SelectedValue = "both";
        provider.SetFilterCommand.Execute(search);
        SetFilter("removable", true);
        Check(model.VisiblePackages.Count == 1, "Instalados: filtro removível mantém pacote removível");
        SetFilter("system", true);
        Check(model.VisiblePackages.Count == 1, "Instalados: ocultar sistema preserva aplicativo comum");
        SetFilter("selected", true);
        Check(model.VisiblePackages.Count == 0, "Instalados: somente selecionados respeita seleção");
        package.IsSelected = true;
        Check(model.VisiblePackages.Count == 1, "Instalados: seleção libera filtro somente selecionados");
        package.IsSelected = false;
        SetFilter("selected", false);
        SetFilter("removable", false);
        SetFilter("system", false);
        row = provider.Rows.Single();

        foreach ((string column, string expected) in new[] { ("id", "Id"), ("name", "Name"), ("version", "Version"), ("source", "Source") })
        {
            provider.SetSortCommand.Execute(column);
            Check(model.SortColumn == expected, $"Instalados: ordenação compartilhada aceita {column}");
        }
        var sortSelector = (ComboBox)screen.FindName("SortSelector");
        sortSelector.SelectedValue = "version";
        provider.SetSortCommand.Execute("version");
        Check(model.SortColumn == "Version", "Instalados: seletor Classificar por altera o campo");

        provider.ToggleSelectAllCommand.Execute(null);
        Check(package.IsSelected, "Instalados: seleção principal marca pacotes visíveis");
        provider.ToggleSelectAllCommand.Execute(null);
        Check(!package.IsSelected, "Instalados: seleção principal limpa seleção");
        row.IsSelected = true;

        var clipboard = (SmokeClipboardService)services.GetRequiredService<IClipboardService>();
        Execute(provider, "manualUninstall", row);
        Check(clipboard.LastText == "winget uninstall --id \"Test.Package\"", "Instalados: ação manual copia comando executável");
        Execute(provider, "copyId", row);
        Check(clipboard.LastText == "Test.Package", "Instalados: ação de contexto copia ID");
        Execute(provider, "copyName", row);
        Check(clipboard.LastText == "Aplicativo de teste", "Instalados: ação de contexto copia nome");
        Execute(provider, "openLocation", row);
        Check(model.Status.Contains("não registrado", StringComparison.OrdinalIgnoreCase), "Instalados: local ausente informa o usuário");
        bool autoUpdateBefore = model.IsAutoUpdateSelected(package);
        Execute(provider, "autoUpdate", row);
        Check(model.IsAutoUpdateSelected(package) != autoUpdateBefore, "Instalados: ação de contexto alterna atualização automática");
        Execute(provider, "autoUpdate", row);
        Check(model.IsAutoUpdateSelected(package) == autoUpdateBefore, "Instalados: ação de contexto restaura a configuração automática");

        Check(provider.ContextActions.Any(action => action.Id == "details" && action.Kind == "row")
            && provider.ContextActions.Any(action => action.Id == "uninstallAdmin")
            && provider.GetActionParameter("details", row) == row,
            "Instalados: provider projeta comandos de contexto e parâmetro da linha");
        list.UpdateLayout();
        Check(FindVisualElements<PackageScreenRowView>(list).Any(view => view.DataContext is PackageScreenRow item && item.Id == "Test.Package"),
            "Instalados: linha virtualizada usa o template compartilhado com ações contextuais");
        presentation.RecordDetailsOnly = true;
        int detailsBefore = presentation.DetailsCallCount;
        Execute(provider, "details", row);
        await WaitUntilAsync(() => presentation.DetailsCallCount == detailsBefore + 1, "Detalhes do pacote não foram apresentados.");
        presentation.RecordDetailsOnly = false;
        Check(presentation.LastDetailsApp?.Id == "Test.Package", "Instalados: ação Detalhes apresenta o pacote correto");

        Execute(provider, "ignore", row);
        Check(model.IgnoredUpdates.Any(entry => entry.AppId == "Test.Package"), "Instalados: ação de contexto ignora atualizações");
        model.RestoreIgnored("Test.Package");
        Check(model.IgnoredUpdates.All(entry => entry.AppId != "Test.Package"), "Instalados: regra ignorada pode ser restaurada");
        var collections = services.GetRequiredService<CollectionsViewModel>();
        Execute(provider, "addToCollection", row);
        Check(collections.Items.Any(item => item.Id == "Test.Package") && model.Status.Contains("adicionado", StringComparison.OrdinalIgnoreCase),
            "Instalados: ação de contexto inclui pacote na coleção");

        backend.DownloadHandler = (_, _, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new WingetExecutionResult { Success = true });
        };
        int downloadsBefore = backend.DownloadCount;
        Execute(provider, "download", row);
        await WaitUntilAsync(() => backend.DownloadCount == downloadsBefore + 1 && !model.IsBusy, "Download de contexto não chamou backend.");
        Check(backend.LastDownloadId == "Test.Package" && backend.LastDownloadSource == "winget"
            && backend.LastDownloadDirectory == "C:\\Apps\\Exemplo", "Instalados: download por linha envia ID, fonte e pasta");
        downloadsBefore = backend.DownloadCount;
        await model.DownloadSelectedCommand.ExecuteAsync(null);
        Check(backend.DownloadCount == downloadsBefore + 1, "Instalados: download por seleção continua funcional");

        Execute(provider, "ignoreSelection");
        Check(model.IgnoredUpdates.Any(entry => entry.AppId == "Test.Package"), "Instalados: ação em lote ignora atualizações selecionadas");
        model.RestoreAllIgnored();
        Check(model.IgnoredUpdates.Count == 0, "Instalados: restaurar todas as regras ignoradas funciona");
        page.ShowIgnoredUpdates();
        var ignoredDialogField = typeof(Views.InstalledPackagesPage).GetField("_ignoredUpdatesDialog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await WaitUntilAsync(() => ignoredDialogField.GetValue(page) is ContentDialog, "Tela de ignorados não abriu.");
        Check(Equals(((ContentDialog)ignoredDialogField.GetValue(page)!).Title, "Atualizações ignoradas"),
            "Instalados: gerenciar ignorados abre diálogo compartilhado");
        page.CloseIgnoredOverlay();

        string csvPath = Path.Combine(OutputDirectory, "installed-buttons.csv");
        if (File.Exists(csvPath)) File.Delete(csvPath);
        presentation.SavePath = csvPath;
        Execute(provider, "exportCsv");
        await WaitUntilAsync(() => File.Exists(csvPath), "Exportar CSV não criou o arquivo de teste.");
        Check(File.ReadAllText(csvPath).Contains("Test.Package", StringComparison.Ordinal), "Instalados: Exportar CSV grava o pacote");

        Execute(provider, "uninstallOptions");
        await WaitUntilAsync(() => page.UninstallOptionsDialog is not null, "Opções de desinstalação não abriram o diálogo.");
        Check(Equals(page.UninstallOptionsDialog?.Title, "Opções de desinstalação")
            && page.UninstallOptionsDialog.CloseButtonText == "Fechar", "Instalados: opções abrem diálogo de ajuda");
        page.UninstallOptionsDialog?.Hide();

        backend.UninstallHandler = (_, _, _, _) => Task.FromResult(new WingetExecutionResult { Success = true });
        package.IsSelected = true;
        int canceledUninstallCount = backend.UninstallCount;
        int confirmationCount = presentation.ConfirmCallCount;
        presentation.ConfirmResult = false;
        await PackageScreenActionExecutor.ExecuteAsync(provider, "uninstall");
        Check(presentation.ConfirmCallCount == confirmationCount + 1
            && presentation.LastConfirmMessage?.Contains("1 item", StringComparison.Ordinal) == true
            && backend.UninstallCount == canceledUninstallCount,
            "Instalados: cancelar Desinstalar impede a operação e confirma a quantidade selecionada");
        presentation.ConfirmResult = true;
        foreach (string variant in new[] { "uninstall", "uninstallAdmin", "uninstallInteractive" })
        {
            package.IsSelected = true;
            int before = backend.UninstallCount;
            await PackageScreenActionExecutor.ExecuteAsync(provider, variant);
            await WaitUntilAsync(() => backend.UninstallCount == before + 1 && !model.IsBusy, $"Desinstalação {variant} não chamou backend.");
            Check(backend.UninstallCount == before + 1
                && (variant != "uninstallAdmin" || backend.LastUninstallRequiresElevation)
                && (variant != "uninstallInteractive" || backend.LastUninstallInteractive),
                $"Instalados: ação {variant} envia as opções corretas ao backend simulado");
        }
        foreach (string variant in new[] { "uninstall", "uninstallAdmin", "uninstallInteractive" })
        {
            package.IsSelected = false;
            int before = backend.UninstallCount;
            await PackageScreenActionExecutor.ExecuteAsync(provider, variant, row);
            await WaitUntilAsync(() => backend.UninstallCount == before + 1 && !model.IsBusy, $"Desinstalação contextual {variant} não chamou backend.");
            Check(backend.UninstallCount == before + 1
                && (variant != "uninstallAdmin" || backend.LastUninstallRequiresElevation)
                && (variant != "uninstallInteractive" || backend.LastUninstallInteractive),
                $"Instalados: ação contextual {variant} envia opção correta");
        }
        package.IsSelected = false;
        model.Status = string.Empty;
    }

    private static void Execute(InstalledPackageScreenProvider provider, string actionId, PackageScreenRow? row = null)
    {
        ICommand command = provider.GetActionCommand(actionId, row)
            ?? throw new InvalidOperationException($"Ação de tela '{actionId}' não registrada.");
        object? parameter = provider.GetActionParameter(actionId, row);
        if (command.CanExecute(parameter)) command.Execute(parameter);
    }
}
