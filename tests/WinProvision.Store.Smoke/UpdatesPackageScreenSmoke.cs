using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Core.Models;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store;
internal static partial class SmokeTest
{
    private static async Task VerifyUpdatesActionsAsync(Views.UpdatesPage page, UpdatesViewModel model,
        SmokePackageBackend backend, IServiceProvider services)
    {
        var provider = page.ScreenProvider;
        var screen = (PackageScreen)page.FindName("PackageScreenControl");
        Check(((TextBlock)screen.FindName("PrimaryActionText")).Text == "Atualizar",
            "Atualizações: ação principal usa o rótulo comum Atualizar");
        Check(provider.Filters.Single(option => option.Id == "searchMode").Title == "Modo de pesquisa",
            "Atualizações: rótulo interno das opções de pesquisa é Modo de pesquisa");
        Check(provider.Filters.All(option => option.Id != "source:other")
            && provider.Filters.Any(option => option.Id == "source:winget")
            && provider.Filters.Any(option => option.Id == "source:msstore")
            && provider.Filters.Any(option => option.Id == "source:chocolatey"),
            "Atualizações: filtros mantêm WinGet, Microsoft Store e Chocolatey sem Outras fontes");
        async Task Run(string id, PackageScreenRow? row = null)
        {
            var command = provider.GetActionCommand(id, row) ?? throw new InvalidOperationException($"Ação ausente: {id}");
            object? parameter = provider.GetActionParameter(id, row);
            Check(command.CanExecute(parameter), $"Atualizações: ação {id} está disponível no estado esperado");
            if (command is IAsyncRelayCommand asyncCommand) await asyncCommand.ExecuteAsync(parameter);
            else command.Execute(parameter);
        }
        void Filter(string id, bool value)
        {
            var option = provider.Filters.Single(option => option.Id == id);
            InvokePackageScreenHandler(screen, "FilterOption_Click", new CheckBox { Tag = option, IsChecked = value }, new RoutedEventArgs());
        }
        InvokePackageScreenHandler(screen, "FiltersButton_Click", screen.FindName("FiltersButton"), new RoutedEventArgs());
        Check(!provider.IsFilterOpen, "Atualizações: botão compartilhado fecha filtros");
        InvokePackageScreenHandler(screen, "FiltersButton_Click", screen.FindName("FiltersButton"), new RoutedEventArgs());
        Check(provider.IsFilterOpen, "Atualizações: botão compartilhado abre filtros");
        InvokePackageScreenHandler(screen, "BulkAction_Click", new Button { Tag = "clear" }, new RoutedEventArgs());
        Check(model.Packages.Count == 0, "Atualizações: Limpar desmarca as fontes e filtra resultados");
        InvokePackageScreenHandler(screen, "BulkAction_Click", new Button { Tag = "select-all" }, new RoutedEventArgs());
        Check(model.Packages.Count == 2, "Atualizações: Selecionar tudo restaura fontes e resultados");
        Filter("source:msstore", false); Check(!model.FilterMsStore, "Atualizações: filtro individual de origem funciona"); Filter("source:msstore", true);
        var search = provider.Filters.Single(option => option.Id == "searchMode");
        search.SelectedValue = "id"; provider.SetFilterCommand.Execute(search); Check(model.SearchId, "Atualizações: pesquisa por ID funciona");
        search.SelectedValue = "exact"; provider.SetFilterCommand.Execute(search); Check(model.ExactSearch, "Atualizações: pesquisa exata funciona");
        search.SelectedValue = "both"; provider.SetFilterCommand.Execute(search);
        Filter("hideSameVersion", false); Check(!model.HideSameVersion, "Atualizações: filtro de versão idêntica funciona"); Filter("hideSameVersion", true);
        foreach (var column in provider.Definition.Columns)
        { provider.SetSortCommand.Execute(column); Check(provider.SortBy.Equals(column, StringComparison.OrdinalIgnoreCase), $"Atualizações: ordena por {column}"); }
        foreach (string mode in new[] { "list", "grid", "details" })
        {
            provider.SetViewModeCommand.Execute(mode);
            screen.UpdateLayout();
            await Task.Delay(60);
            Check(((FrameworkElement)screen.FindName(mode == "list" ? "ListView" : mode == "grid" ? "GridView" : "DetailsView")).Visibility == Visibility.Visible,
                $"Atualizações: modo {mode} apresenta os resultados compartilhados");
            if (mode == "grid")
            {
                screen.UpdateLayout();
                await Task.Delay(150);
                PackageScreenRow updateCard = provider.Rows.First(row => !string.IsNullOrWhiteSpace(row.NewVersion));
                Check(FindVisualElements<PackageScreenRowView>(screen).Any(view => ReferenceEquals(view.DataContext, updateCard))
                    && ContainsText(screen, $"{updateCard.Version} → {updateCard.NewVersion}"),
                    "Atualizações: cartão apresenta versão atual e nova em uma linha com seta");
                await CaptureAsync(page, "updates-cards-grid.png");
            }
        }
        provider.SetViewModeCommand.Execute("list");
        await Run("clearSelection"); Check(provider.SelectedCount == 0, "Atualizações: limpa seleção");
        var primary = (SplitButton)screen.FindName("PrimaryActionButton");
        var primaryMenu = (MenuFlyout)screen.FindName("PrimaryFlyout");
        Check(!primary.IsEnabled && primaryMenu.Items.OfType<MenuFlyoutItem>().All(item => !item.IsEnabled),
            "Atualizações: ação principal e seta ficam desabilitadas sem seleção");
        PackageScreenRow firstRow = provider.Rows[0];
        firstRow.IsSelected = true;
        screen.UpdateLayout();
        Check(provider.SelectedCount == 1 && provider.MasterSelection is null
            && provider.GetActionCommand("update")?.CanExecute(null) == true && primary.IsEnabled
            && primaryMenu.Items.OfType<MenuFlyoutItem>().All(item => item.IsEnabled),
            "Atualizações: uma seleção individual habilita ação principal e opções do menu");
        firstRow.IsSelected = false;
        screen.UpdateLayout();
        Check(provider.SelectedCount == 0 && provider.MasterSelection == false
            && provider.GetActionCommand("update")?.CanExecute(null) == false && !primary.IsEnabled,
            "Atualizações: remover a última seleção desabilita a ação");
        provider.Rows[0].IsSelected = true;
        var secondRow = provider.Rows.First(row => !ReferenceEquals(row, provider.Rows[0]));
        secondRow.IsSelected = true;
        screen.UpdateLayout();
        Check(provider.SelectedCount == 2 && provider.MasterSelection == true
            && provider.GetActionCommand("update")?.CanExecute(null) == true && primary.IsEnabled,
            "Atualizações: selecionar todos os resultados mantém ação habilitada e cabeçalho marcado");
        await Run("clearSelection");
        Check(provider.Definition.Toolbar.Overflow.Contains("updateAll")
            && provider.GetActionCommand("updateAll")?.CanExecute(null) == true,
            "Atualizações: Atualizar tudo preserva disponibilidade em Mais ações sem seleção");
        var presentation = (SmokePresentationService)services.GetRequiredService<IPresentationService>();
        int updateCount = backend.UpdateCount;
        int confirmCount = presentation.ConfirmCallCount;
        MenuFlyoutItem updateAllItem = ((MenuFlyout)screen.FindName("OverflowFlyout")).Items
            .OfType<MenuFlyoutItem>().Single(item => Equals(item.Tag, "updateAll"));
        presentation.ConfirmResult = false;
        InvokePackageScreenHandler(screen, "ActionMenuItem_Click", updateAllItem, new RoutedEventArgs());
        Check(presentation.ConfirmCallCount == confirmCount + 1
            && presentation.LastConfirmTitle == "Confirmar ação"
            && presentation.LastConfirmMessage?.Contains("Atualizar tudo", StringComparison.Ordinal) == true
            && presentation.LastConfirmMessage.Contains("2 itens", StringComparison.Ordinal)
            && backend.UpdateCount == updateCount,
            "Atualizações: cancelar Atualizar tudo mostra confirmação localizada com contagem e não executa");
        presentation.ConfirmResult = true;
        InvokePackageScreenHandler(screen, "ActionMenuItem_Click", updateAllItem, new RoutedEventArgs());
        await WaitUntilAsync(() => backend.UpdateCount > updateCount, "Atualizar tudo confirmado não chamou o backend simulado.");
        Check(presentation.ConfirmCallCount == confirmCount + 2 && backend.UpdateCount > updateCount,
            "Atualizações: confirmar Atualizar tudo executa a operação em lote");
        await Run("selectAll"); Check(provider.SelectedCount == model.Packages.Count, "Atualizações: seleciona todos");
        var chocolatey = provider.Rows.First(row => row.Payload is UpgradablePackage { Source: "chocolatey" });
        await Run("update", chocolatey);
        Check(backend.LastUpdatePackage?.Source == "chocolatey", "Atualizações: contexto Chocolatey mantém a origem na operação");
        Filter("source:chocolatey", false);
        int scans = backend.ScanCount; await provider.RefreshCommand.ExecuteAsync(null);
        Check(backend.ScanCount > scans, "Atualizações: Recarregar consulta o backend simulado");
        foreach (string id in new[] { "update", "updateAdmin", "updateInteractive" })
        {
            model.SelectAll(true); int before = backend.UpdateCount; await Run(id);
            Check(backend.UpdateCount > before
                && (id != "updateAdmin" || backend.LastUpdateOptions?.RequiresElevation == true)
                && (id != "updateInteractive" || backend.LastUpdateOptions?.Interactive == true),
                $"Atualizações: {id} encaminha opções ao backend simulado");
        }
        var winget = provider.Rows.First();
        await Run("updateIgnoreHash", winget);
        Check(backend.LastUpdateOptions?.IgnoreSecurityHash == true, "Atualizações: ignorar hash permanece uma operação individual");
        presentation.RecordDetailsOnly = true;
        int details = presentation.DetailsCallCount; await Run("details");
        Check(presentation.DetailsCallCount == details + 1, "Atualizações: detalhes encaminha o pacote ao serviço de apresentação");
        presentation.RecordDetailsOnly = false;
        await Run("updateOptions"); Check(((FrameworkElement)page.Dialogs.FindName("OptionsOverlay")).Visibility == Visibility.Visible, "Atualizações: opções preservadas"); page.CloseModal();
        await Run("manualUpdate"); Check(((FrameworkElement)page.Dialogs.FindName("ManualUpdateOverlay")).Visibility == Visibility.Visible, "Atualizações: instalação manual preservada");
        ((TextBox)page.Dialogs.FindName("ManualPackageIdBox")).Text = "Google.Chrome";
        var manual = (SmokeManualWinGetCommandService)services.GetRequiredService<IManualWinGetCommandService>();
        int opens = manual.OpenCount, updates = backend.UpdateCount;
        InvokeUpdatesHandler(page, "ExecuteManualUpdate_Click", page.Dialogs.FindName("ManualExecuteButton"), new RoutedEventArgs());
        Check(manual.OpenCount == opens + 1 && backend.UpdateCount == updates && manual.LastCommand == "winget upgrade --id \"Google.Chrome\" --exact",
            "Atualizações: comando manual abre terminal sem executar pelo backend");
        model.SelectAll(true); await Run("ignoreSelection");
        Check(model.Packages.Count == 0 && model.HasIgnored, "Atualizações: ignorar grava regra e remove a oferta");
        await Run("showIgnored"); Check(((FrameworkElement)page.Dialogs.FindName("IgnoredUpdatesOverlay")).Visibility == Visibility.Visible, "Atualizações: gerenciador de ignoradas preservado");
        InvokeUpdatesHandler(page, "RestoreAllIgnored_Click", page.Dialogs.FindName("RestoreAllIgnoredButton"), new RoutedEventArgs());
        await WaitUntilAsync(() => model.Packages.Count > 0 && !model.HasIgnored && !model.IsBusy, "Restauração de ignoradas não terminou.");
        string path = Path.Combine(OutputDirectory, "updates-buttons.csv");
        if (File.Exists(path)) File.Delete(path);
        presentation.SavePath = path; await Run("exportCsv");
        Check(File.ReadAllText(path).Contains("Google.Chrome", StringComparison.Ordinal), "Atualizações: CSV exportado mantém o identificador");
    }
}
