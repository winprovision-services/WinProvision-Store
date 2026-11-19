using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using WinProvision.Core.Models;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store;

internal static partial class SmokeTest
{
    private static async Task VerifyPackageScreenContractAsync(MainWindow window, IServiceProvider services)
    {
        PackageScreenCatalog catalog = PackageScreenCatalog.Default;
        PackageScreenIconSources? officeIconSources = catalog.ResolveIconSources("office");
        Check(officeIconSources is { Light: "ms-appx:///Assets/Icons/microsoft-office.svg", Dark: "ms-appx:///Assets/Icons/microsoft-office.svg" },
            "Telas de pacotes: ícone colorido da origem Office é compartilhado entre temas");
        string[] expectedScreens = ["discover", "updates", "installed", "collections"];
        string? toolbarSignature = null;
        string? footerSignature = null;
        string[] footerMembers = ["SelectionSummary", "StatusSeparator", "StatusText", "SortLabel", "SortSelector",
            "ListModeButton", "DetailsModeButton", "GridModeButton"];
        var navigation = (NavigationView)window.RootElement.FindName("RootNavigation");
        foreach (string id in expectedScreens)
        {
            PackageScreenDefinition definition = catalog.Get(id);
            Check(definition.Toolbar.Primary.Action.Length > 0 && definition.Views.List.Columns.Count > 0
                && definition.Views.Details.Columns.Count > 0 && definition.Views.Grid.Card.Count > 0,
                $"Telas de pacotes: contrato {id} declara conteúdo para os três modos");
        }

        static async Task VerifyTitleBarSearchAsync(MainWindow window)
        {
            static RectInt32 GetExpectedInputRegion(MainWindow window, double scale)
            {
                FrameworkElement input = window.TitleBarSearchInputForSmoke;
                Windows.Foundation.Rect bounds = input.TransformToVisual(window.RootElement)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, input.ActualWidth, input.ActualHeight));
                int x = (int)Math.Floor(bounds.X * scale);
                int y = (int)Math.Floor(bounds.Y * scale);
                return new RectInt32
                {
                    X = x,
                    Y = y,
                    Width = (int)Math.Ceiling(bounds.Right * scale) - x,
                    Height = (int)Math.Ceiling(bounds.Bottom * scale) - y
                };
            }

            static bool MatchesInputBounds(IReadOnlyList<RectInt32> regions, RectInt32 expected) =>
                regions.Count == 1
                && regions[0].X == expected.X
                && regions[0].Y == expected.Y
                && regions[0].Width == expected.Width
                && regions[0].Height == expected.Height;

            using Stream catalogStream = typeof(PackageScreenCatalog).Assembly.GetManifestResourceStream("WinProvision.Core.Models.package-screen-catalog.json")
                ?? throw new FileNotFoundException("Catálogo embedded de telas não encontrado.");
            using var catalogReader = new StreamReader(catalogStream);
            string catalogJson = catalogReader.ReadToEnd();
            using Stream screenStream = typeof(PackageScreenCatalog).Assembly.GetManifestResourceStream("WinProvision.Core.Models.package-screens.updates.json")
                ?? throw new FileNotFoundException("Descritor embedded de Atualizações não encontrado.");
            using var screenReader = new StreamReader(screenStream);
            string baseJson = screenReader.ReadToEnd();

            PackageScreenDefinition LoadSearchDefinition(string id, bool enabled, string trigger, bool keepQuery = true)
            {
                var json = (JsonObject)(JsonNode.Parse(baseJson)
                    ?? throw new InvalidDataException("Descritor base inválido."));
                json["id"] = id;
                json["search"] = new JsonObject
                {
                    ["enabled"] = enabled,
                    ["placeholder"] = "search.placeholder",
                    ["trigger"] = trigger,
                    ["debounceMs"] = 0,
                    ["minLength"] = 0,
                    ["keepQuery"] = keepQuery
                };
                return PackageScreenCatalog.Load(new Dictionary<string, string> { [id] = json.ToJsonString() }, catalogJson).Get(id);
            }

            FakePackageScreenProvider disabledProvider = new(LoadSearchDefinition("search-disabled", false, "live"));
            FakePackageScreenProvider liveProvider = new(LoadSearchDefinition("search-live", true, "live"));
            FakePackageScreenProvider submitProvider = new(LoadSearchDefinition("search-submit", true, "submit"));
            ContentControl pageHost = (ContentControl)window.RootElement.FindName("PageHost");
            object? previousContent = pageHost.Content;
            try
            {
                Check(FindVisualElements<AutoSuggestBox>(window.RootElement).Count() == 1,
                    "Pesquisa: a janela contém exatamente uma caixa de pesquisa");
                Check(window.RootElement.KeyboardAccelerators.Any(accelerator =>
                        accelerator.Key == Windows.System.VirtualKey.F
                        && accelerator.Modifiers == Windows.System.VirtualKeyModifiers.Control),
                    "Pesquisa: atalho Ctrl+F está registrado na janela");

                var disabledScreen = new PackageScreen { Provider = disabledProvider };
                pageHost.Content = disabledScreen;
                pageHost.UpdateLayout();
                await Task.Delay(50);
                window.RefreshActiveSearchTargetForSmoke();
                window.SetTitleBarSearchTextForSmoke("ignored");
                Check(!window.TitleBarSearchIsVisibleForSmoke
                    && disabledProvider.SearchText.Length == 0,
                    "Pesquisa: tela declarada enabled=false oculta a caixa e não recebe texto");

                var liveScreen = new PackageScreen { Provider = liveProvider };
                pageHost.Content = liveScreen;
                pageHost.UpdateLayout();
                await Task.Delay(50);
                window.RefreshActiveSearchTargetForSmoke();
                window.SetTitleBarSearchTextForSmoke("consulta local");
                Check(window.TitleBarSearchBoxForSmoke.Visibility == Visibility.Visible
                    && liveProvider.SearchText == "consulta local"
                    && liveProvider.SubmittedQueries.SequenceEqual(new[] { "consulta local" })
                    && liveScreen.FindName("SearchBox") is null,
                    "Pesquisa: trigger live aplica texto à tela ativa e submete sem busca lateral");

                window.RefreshTitleBarInputRegionsForSmoke(1);
                RectInt32[] at100 = window.TitleBarInputRegionsForSmoke.ToArray();
                window.RefreshTitleBarInputRegionsForSmoke(1.5);
                RectInt32[] at150 = window.TitleBarInputRegionsForSmoke.ToArray();
                Check(at100.Length == 1 && at150.Length == 1
                    && Math.Abs(at150[0].Width - at100[0].Width * 1.5) <= 1
                    && Math.Abs(at150[0].Height - at100[0].Height * 1.5) <= 1,
                    "Pesquisa: região passthrough acompanha os limites da caixa ao recalcular a escala");
                Check(MatchesInputBounds(at100, GetExpectedInputRegion(window, 1))
                    && MatchesInputBounds(at150, GetExpectedInputRegion(window, 1.5)),
                    "Pesquisa: regiões passthrough coincidem com os limites reais do controle nas escalas testadas");
                window.RefreshTitleBarInputRegionsForSmoke(window.RootElement.XamlRoot.RasterizationScale);

                SizeInt32 originalWindowSize = window.AppWindow.Size;
                try
                {
                    window.AppWindow.Resize(new SizeInt32(560, originalWindowSize.Height));
                    await Task.Delay(300);
                    window.RootElement.UpdateLayout();
                    double resizedScale = window.RootElement.XamlRoot.RasterizationScale;
                    window.RefreshTitleBarInputRegionsForSmoke(resizedScale);
                    Check(window.TitleBarSearchIsVisibleForSmoke
                        && MatchesInputBounds(window.TitleBarInputRegionsForSmoke,
                            GetExpectedInputRegion(window, resizedScale)),
                        "Pesquisa: região passthrough acompanha o controle após redimensionar a janela estreita");
                }
                finally
                {
                    window.AppWindow.Resize(originalWindowSize);
                    await Task.Delay(300);
                    window.RootElement.UpdateLayout();
                    window.RefreshTitleBarInputRegionsForSmoke(window.RootElement.XamlRoot.RasterizationScale);
                }

                window.SetTitleBarSearchTextForSmoke(string.Empty);
                Check(liveProvider.SearchText.Length == 0,
                    "Pesquisa: limpar o campo remove imediatamente o filtro da tela live");
                window.SetTitleBarSearchTextForSmoke("consulta local");

                var submitScreen = new PackageScreen { Provider = submitProvider };
                pageHost.Content = submitScreen;
                pageHost.UpdateLayout();
                await Task.Delay(50);
                window.RefreshActiveSearchTargetForSmoke();
                window.SetTitleBarSearchTextForSmoke("consulta remota");
                Check(submitProvider.SearchText == "consulta remota" && submitProvider.SubmittedQueries.Count == 0,
                    "Pesquisa: trigger submit atualiza a consulta sem pesquisar durante a digitação");
                await window.SubmitActiveSearchForSmoke("consulta remota");
                Check(submitProvider.SubmittedQueries.SequenceEqual(new[] { "consulta remota" })
                    && liveProvider.SearchText == "consulta local",
                    "Pesquisa: Enter submete somente a tela ativa e preserva a consulta da tela anterior");
                window.SetTitleBarSearchTextForSmoke("limpar");
                window.SetTitleBarSearchTextForSmoke(string.Empty);
                Check(submitProvider.SubmittedQueries.LastOrDefault() == string.Empty
                    && submitProvider.SearchText.Length == 0,
                    "Pesquisa: botão limpar também reseta resultados de uma tela submit");
                window.SetTitleBarSearchTextForSmoke("esc");
                Check(window.HandleSearchEscapeForSmoke()
                    && submitProvider.SearchText.Length == 0
                    && window.TitleBarSearchBoxForSmoke.Text.Length == 0,
                    "Pesquisa: Esc limpa a consulta ativa");

                pageHost.Content = liveScreen;
                pageHost.UpdateLayout();
                await Task.Delay(50);
                window.RefreshActiveSearchTargetForSmoke();
                Check(window.TitleBarSearchBoxForSmoke.Text == "consulta local",
                    "Pesquisa: voltar à tela restaura a consulta mantida por ela");
            }
            finally
            {
                pageHost.Content = previousContent;
                pageHost.UpdateLayout();
                window.RefreshActiveSearchTargetForSmoke();
                disabledProvider.Dispose();
                liveProvider.Dispose();
                submitProvider.Dispose();
            }

            window.Navigate("settings");
            await WaitForPageAsync(window, "settings", ElementTheme.Light);
            var settingsPage = window.CurrentPage as Views.SettingsPage
                ?? throw new InvalidOperationException("A página de Configurações não foi aberta.");
            settingsPage.NavigateToCategory("Interface");
            settingsPage.UpdateLayout();
            await Task.Delay(50);
            var visualPreferencesList = settingsPage.FindName("PackageVisualPreferencesList") as ItemsControl;
            bool visualPreferenceListExists = visualPreferencesList is not null;
            bool visualPreferenceSourceBound = visualPreferencesList?.ItemsSource is not null;
            int visualPreferenceCount = settingsPage.ViewModel.PackageVisualPreferences.Count;
            Check(visualPreferenceListExists && visualPreferenceSourceBound && visualPreferenceCount == 2,
                $"Configurações: campos visuais renderizados pelo catálogo (lista={visualPreferenceListExists}, fonte={visualPreferenceSourceBound}, campos={visualPreferenceCount})");
            window.SetTitleBarSearchTextForSmoke("tema");
            Check(window.TitleBarSearchBoxForSmoke.PlaceholderText == "Pesquisar configurações"
                && settingsPage.FindName("SettingsSearchBox") is null
                && settingsPage.ViewModel.SearchResults.Any(result => result.Title.Contains("Tema", StringComparison.OrdinalIgnoreCase)),
                "Pesquisa: titlebar filtra as configurações sem caixa duplicada na página");
            window.SetTitleBarSearchTextForSmoke(string.Empty);
            window.SetTitleBarSearchTextForSmoke("mica");
            Check(settingsPage.ViewModel.SearchResults.Any(result => result.Key == "backdrop"),
                "Pesquisa: plano de fundo aparece ao pesquisar por Mica");
            window.SetTitleBarSearchTextForSmoke("capturas");
            Check(settingsPage.ViewModel.SearchResults.Any(result => result.Key == "package-screenshots"),
                "Pesquisa: preferência de capturas é encontrada pelo glossário");
            window.SetTitleBarSearchTextForSmoke("ícones");
            Check(settingsPage.ViewModel.SearchResults.Any(result => result.Key == "package-icons"),
                "Pesquisa: preferência de ícones é encontrada pelo glossário");
            window.SetTitleBarSearchTextForSmoke("instalações");
            Check(settingsPage.ViewModel.SearchResults.Any(result =>
                    result.Key == "parallelOperations" && result.CategoryKey == "Execution"),
                "Pesquisa: preferência de operações é encontrada pelo catálogo e abre seu grupo");
            window.SetTitleBarSearchTextForSmoke("winget");
            Check(settingsPage.ViewModel.SearchResults.Any(result =>
                    result.Key == "package-managers" && result.CategoryKey == "PackageManagers"),
                "Pesquisa: destino de fontes do WinGet vem do catálogo e aponta para Gerenciadores de pacotes");
            window.SetTitleBarSearchTextForSmoke("Office");
            Check(settingsPage.ViewModel.SearchResults.Any(result => result.CategoryKey == "Office"),
                "Pesquisa: opções do Office vêm do catálogo da página e aparecem no índice");
            window.SetTitleBarSearchTextForSmoke(string.Empty);

            window.Navigate("package-managers");
            await WaitForPageAsync(window, "package-managers", ElementTheme.Light);
            var managersPage = window.CurrentPage as Views.PackageManagersPage
                ?? throw new InvalidOperationException("A página de Gerenciadores de pacotes não foi aberta.");
            window.SetTitleBarSearchTextForSmoke("Chocolatey");
            Check(window.TitleBarSearchBoxForSmoke.PlaceholderText == "Pesquisar gerenciadores"
                && managersPage.ViewModel.VisibleManagerRows.Select(row => row.Id).SequenceEqual(new[] { "chocolatey" }),
                "Pesquisa: titlebar filtra gerenciadores por nome");
            window.SetTitleBarSearchTextForSmoke(string.Empty);
        }

        const string testScreenJson = """
            {"schemaVersion":1,"id":"fake","title":"Tela de teste","icon":"installed",
             "subtitle":"{filtered} de {total} {packageLabel}","data":{"provider":"fake"},
             "toolbar":{"primary":{"action":"refresh","menu":["export"]},"overflow":["selectAll"]},
             "sidebar":{"sections":[{"id":"filters","title":"Filtros","items":[{"id":"only","title":"Somente exemplo"}]}]},
             "views":{
               "list":{"columns":["publisher","version"]},
               "details":{"columns":["name","status"],"subtitle":"status"},
               "grid":{"card":["publisher","versionTransition"],"badge":"source"}},
             "row":{"tap":"select","doubleTap":"export","contextActions":["export"]},
             "selection":"multiple",
             "defaults":{"view":"list","sort":{"column":"name","direction":"ascending"}}}
            """;
        using Stream catalogStream = typeof(PackageScreenCatalog).Assembly.GetManifestResourceStream("WinProvision.Core.Models.package-screen-catalog.json")
            ?? throw new FileNotFoundException("Catálogo embedded de telas não encontrado.");
        using var reader = new StreamReader(catalogStream);
        PackageScreenCatalog fakeCatalog = PackageScreenCatalog.Load(
            new Dictionary<string, string> { ["fake"] = testScreenJson }, reader.ReadToEnd());
        string singular = fakeCatalog.FormatSubtitle(fakeCatalog.Get("fake"), 1, 1);
        string plural = fakeCatalog.FormatSubtitle(fakeCatalog.Get("fake"), 2, 2);
        Check(singular.Contains("1 pacote", StringComparison.OrdinalIgnoreCase) && !singular.Contains("pacotes", StringComparison.OrdinalIgnoreCase)
            && plural.Contains("2 pacotes", StringComparison.OrdinalIgnoreCase),
            "Telas de pacotes: pluralização do contrato JSON usa singular e plural");
        FakePackageScreenProvider provider = new(fakeCatalog.Get("fake"));
        var screen = new PackageScreen { Provider = provider };

        ContentControl pageHost = (ContentControl)window.RootElement.FindName("PageHost");
        object? previousContent = pageHost.Content;
        ElementTheme previousTheme = window.RootElement.RequestedTheme;
        var providers = new List<FakePackageScreenProvider> { provider };
        try
        {
            foreach (PackageScreenDefinition definition in catalog.Screens.Append(fakeCatalog.Get("fake")))
            {
                FakePackageScreenProvider currentProvider = definition.Id == "fake" ? provider : new FakePackageScreenProvider(definition);
                if (!ReferenceEquals(currentProvider, provider)) providers.Add(currentProvider);
                var registry = new PackageScreenProviderRegistry(new[]
                {
                    new PackageScreenProviderRegistration(definition.Data.Provider, () => currentProvider)
                });
                var rendered = registry.CreateScreen(definition);
                pageHost.Content = rendered;
                pageHost.UpdateLayout();
                await Task.Delay(80);
                Check(rendered.FindName("FiltersButton") is ToggleButton
                    && rendered.FindName("PrimaryActionButton") is not null
                    && rendered.FindName("ReloadButton") is Button
                    && rendered.FindName("MoreButton") is Button,
                    $"Telas de pacotes: {definition.Id} renderiza as quatro posições da barra de ferramentas");
                var toolbar = (StackPanel)Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent((DependencyObject)rendered.FindName("FiltersButton"));
                string currentToolbarSignature = string.Join("|", toolbar.Children.OfType<FrameworkElement>().Select(element => element.Name));
                Check(currentToolbarSignature == "FiltersButton|PrimaryActionButton|ReloadButton|MoreButton",
                    $"Telas de pacotes: {definition.Id} mantém a mesma estrutura e ordem dos quatro comandos");
                Check(toolbarSignature is null || currentToolbarSignature == toolbarSignature,
                    $"Telas de pacotes: barra de ações de {definition.Id} corresponde às demais telas");
                toolbarSignature ??= currentToolbarSignature;
                Check(rendered.FindName("ListModeButton") is ToggleButton
                    && rendered.FindName("DetailsModeButton") is ToggleButton
                    && rendered.FindName("GridModeButton") is ToggleButton,
                    $"Telas de pacotes: {definition.Id} renderiza os três modos do rodapé");
                string currentFooterSignature = string.Join("|", footerMembers.Select(name =>
                {
                    var member = (FrameworkElement)rendered.FindName(name);
                    return $"{member.Name}:{member.GetType().Name}";
                }));
                Check(footerSignature is null || currentFooterSignature == footerSignature,
                    $"Telas de pacotes: rodapé de {definition.Id} corresponde às demais telas");
                footerSignature ??= currentFooterSignature;
                Check(((MenuFlyout)rendered.FindName("PrimaryFlyout")).Items.Count == definition.Toolbar.Primary.Menu.Count
                    && ((MenuFlyout)rendered.FindName("OverflowFlyout")).Items.Count == definition.Toolbar.Overflow.Count,
                    $"Telas de pacotes: {definition.Id} menus seguem as ações JSON");
                if (definition.Id != "fake")
                {
                    string navigationTag = definition.Id == "discover" ? "home" : definition.Id;
                    NavigationViewItem navigationItem = navigation.MenuItems.OfType<NavigationViewItem>()
                        .Single(item => string.Equals(item.Tag?.ToString(), navigationTag, StringComparison.OrdinalIgnoreCase));
                    IconElement? navigationIcon = navigationItem.Icon;
                    IconElement? headingIcon = ((Border)rendered.FindName("TitleIconContainer")).Child as IconElement;
                    bool matchingIcons = definition.IconUriLight is not null || definition.IconUriDark is not null
                        ? navigationIcon is ImageIcon && headingIcon is ImageIcon
                        : navigationIcon is FluentIcons.WinUI.FluentIcon navigationGlyph
                            && headingIcon is FluentIcons.WinUI.FluentIcon headingGlyph
                            && navigationGlyph.Icon == headingGlyph.Icon;
                    Check(matchingIcons,
                        $"Telas de pacotes: ícone de navegação e cabeçalho de {definition.Id} são iguais");
                }
                var headerColumns = (Grid)rendered.FindName("HeaderColumns");
                var tableHeader = (Grid)rendered.FindName("TableHeader");
                PackageScreenRowView? rowView = FindVisualElements<PackageScreenRowView>(rendered).FirstOrDefault();
                Grid? rowGrid = (rowView?.Content as Border)?.Child as Grid;
                bool columnsAligned = rowGrid is not null
                    && tableHeader.ColumnDefinitions[0].Width == rowGrid.ColumnDefinitions[0].Width
                    && headerColumns.ColumnDefinitions.Count == rowGrid.ColumnDefinitions.Count - 1
                    && headerColumns.ColumnDefinitions.Select(column => column.Width)
                        .SequenceEqual(rowGrid.ColumnDefinitions.Skip(1).Select(column => column.Width));
                Check(columnsAligned && rowView?.Content is Border rowSurface && rowSurface.Padding.Left == 0,
                    $"Telas de pacotes: cabeçalhos e células de {definition.Id} compartilham alinhamento horizontal");
                if (definition.Id == "fake") screen = rendered;
            }

            services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(true);
            PackageScreenSourceRegistry sourceRegistry = services.GetRequiredService<PackageScreenSourceRegistry>();
            IReadOnlyList<PackageScreenSource> runtimeSources = sourceRegistry.GetSources();
            Check(runtimeSources.All(source => PackageScreenCatalog.Default.ResolveIcon(source.Id) == source.IconGlyph)
                && runtimeSources.All(source => sourceRegistry.IsEnabled(source.Id))
                && runtimeSources.All(source => source.Id != "winprovision-store"),
                "Telas de pacotes: ativação e ícones das origens usam o registro de adaptadores em execução");
            if (runtimeSources.FirstOrDefault(source => source.Id == "chocolatey")?.IconUri == "ms-appx:///Assets/Icons/chocolatey.svg")
                Check(true, "Telas de pacotes: origem Chocolatey recebe o SVG do descritor do adaptador");
            else
                RecordKnownPackagedSvgFailure("Assets/Icons/chocolatey.svg (origem Chocolatey)");
            Check(runtimeSources.FirstOrDefault(source => source.Id == "msstore")?.IconUri == "ms-appx:///Assets/Icons/microsoft-store.svg",
                "Telas de pacotes: origem Microsoft Store recebe o SVG do descritor do adaptador");
            Check(runtimeSources.FirstOrDefault(source => source.Id == "winget")?.IconUri == "ms-appx:///Assets/Icons/winget.png",
                "Telas de pacotes: origem WinGet recebe o SVG do descritor do adaptador");
            services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(false);

            StorefrontViewModel inactiveStorefront = ActivatorUtilities.CreateInstance<StorefrontViewModel>(services);
            DiscoverPackageScreenProvider emptyStateProvider = ActivatorUtilities.CreateInstance<DiscoverPackageScreenProvider>(services, inactiveStorefront);
            try
            {
                Check(!inactiveStorefront.HasSearched && emptyStateProvider.EmptyTitle == "Pesquise para encontrar aplicativos",
                    "Descobrir: provider separado mostra o estado de busca ainda não iniciada");
                await inactiveStorefront.SubmitSearchAsync("consulta-sem-resultados-smoke");
                Check(inactiveStorefront.HasSearched && emptyStateProvider.EmptyTitle == "Nenhum aplicativo encontrado"
                    && emptyStateProvider.EmptyMessage.Contains("Tente pesquisar", StringComparison.Ordinal),
                    "Descobrir: busca concluída sem resultados mostra um estado vazio distinto");
            }
            finally
            {
                emptyStateProvider.Dispose();
                inactiveStorefront.Dispose();
            }

            window.RootElement.RequestedTheme = ElementTheme.Dark;
            pageHost.UpdateLayout();
            await CaptureAsync(window.RootElement, "package-screen-fake-dark.png");
            window.RootElement.RequestedTheme = ElementTheme.Light;
            pageHost.UpdateLayout();
            await CaptureAsync(window.RootElement, "package-screen-fake-light.png");
            window.RootElement.RequestedTheme = previousTheme;

            Check(screen.FindName("FiltersButton") is not null,
                "Telas de pacotes: o exemplo JSON falso é montado sem página XAML dedicada");
            var listHeader = (Grid)screen.FindName("HeaderColumns");
            Check(listHeader.Children.OfType<Button>().Select(button => button.Content?.ToString())
                    .SequenceEqual(new[] { "Fabricante", "Versão" })
                && FindVisualElements<TextBlock>(screen).Any(text => text.Text == "Fabricante do teste")
                && FindVisualElements<TextBlock>(screen).Any(text => text.Text == "1.0.0"),
                "Tela JSON falsa: lista usa as colunas próprias declaradas no descritor");

            provider.ViewMode = "details";
            screen.UpdateLayout();
            await Task.Delay(80);
            Check(((Grid)screen.FindName("HeaderColumns")).Children.OfType<Button>()
                    .Select(button => button.Content?.ToString()).SequenceEqual(new[] { "Nome", "Status" })
                && FindVisualElements<TextBlock>(screen).Count(text => text.Text == "Disponível") == 2,
                "Tela JSON falsa: lista detalhada usa suas colunas e subtítulo declarados");

            provider.ViewMode = "grid";
            screen.UpdateLayout();
            await Task.Delay(80);
            PackageScreenRowView fakeCard = FindVisualElements<PackageScreenRowView>(screen)
                .First(view => view.DataContext is PackageScreenRow { Key: "fake-initial" });
            Check(FindVisualElements<TextBlock>(fakeCard).Any(text => text.Text == "Fabricante do teste")
                && FindVisualElements<TextBlock>(fakeCard).Any(text => text.Text == "1.0.0 → 2.0.0")
                && FindVisualElements<TextBlock>(fakeCard).Any(text => text.Text == "Origem simulada"),
                "Tela JSON falsa: cartão usa campos, transição de versão e badge declarados");

            MenuFlyout itemMenu = fakeCard.CreateContextMenu(provider.Rows[0]);
            MenuFlyoutItem exportItem = itemMenu.Items.OfType<MenuFlyoutItem>().Single();
            InvokePackageScreenRowHandler(fakeCard, "ContextAction_Click", exportItem, new RoutedEventArgs());
            Check(exportItem.Tag?.ToString() == "export" && provider.ExecutedItemActions == 1,
                "Tela JSON falsa: ação contextual declarada no descritor aparece e executa");

            provider.Rows[0].IsSelected = false;
            provider.ToggleSelectAllCommand.Execute(null);
            Check(provider.SelectedCount == provider.Rows.Count,
                "Telas de pacotes: seleção em lote sincroniza as linhas do provider falso");
            provider.Rows[0].IsSelected = true;
            InvokePackageScreenHandler(screen, "RefreshPresentation");
            Check(((FrameworkElement)screen.FindName("StatusSeparator")).Visibility == Visibility.Visible,
                "Telas de pacotes: rodapé separa contador de seleção e mensagem");
            provider.Rows[0].IsSelected = false;
            InvokePackageScreenHandler(screen, "RefreshPresentation");
            provider.Rows[0].IsSelected = true;
            var filterButton = new CheckBox { Tag = provider.Filters[0], IsChecked = true };
            InvokePackageScreenHandler(screen, "FilterOption_Click", filterButton, new RoutedEventArgs());
            Check(provider.HasActiveFilters,
                "Telas de pacotes: evento da interface aplica filtro JSON falso");
            InvokePackageScreenHandler(screen, "SortHeader_Click", new Button { Tag = "id" }, new RoutedEventArgs());
            Check(provider.SortBy == "id" && provider.SortAscending,
                "Telas de pacotes: clique no cabeçalho encaminha ordenação ao provider falso");
            InvokePackageScreenHandler(screen, "ViewMode_Click", new ToggleButton { Tag = "grid" }, new RoutedEventArgs());
            Check(provider.ViewMode == "grid", "Telas de pacotes: alternância de visualização encaminha o modo ao provider falso");
            InvokePackageScreenHandler(screen, "MasterSelection_Click", new CheckBox(), new RoutedEventArgs());
            Check(provider.SelectedCount == 0, "Telas de pacotes: comando de seleção principal chega ao provider falso");

            await VerifyPackageScreenVirtualizationAsync(screen, provider, "list", 261);
            await VerifyPackageScreenVirtualizationAsync(screen, provider, "grid", 138);
            PackageScreenRow selectedRow = provider.Rows[0];
            selectedRow.IsSelected = true;
            InvokePackageScreenHandler(screen, "ViewMode_Click", new ToggleButton { Tag = "details" }, new RoutedEventArgs());
            Check(provider.SelectedCount == 1 && ReferenceEquals(provider.Rows[0], selectedRow) && selectedRow.IsSelected,
                "Telas de pacotes: seleção acompanha a linha ao alternar para ícones");
        }
        finally
        {
            pageHost.Content = previousContent;
            foreach (FakePackageScreenProvider item in providers) item.Dispose();
            window.RootElement.RequestedTheme = previousTheme;
        }
        await VerifyTitleBarSearchAsync(window);
    }

    private static void InvokePackageScreenRowHandler(PackageScreenRowView view, string name, params object?[] args) =>
        typeof(PackageScreenRowView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, args);

    private static async Task VerifyPackageScreenVirtualizationAsync(PackageScreen screen,
        FakePackageScreenProvider provider, string mode, int count)
    {
        provider.Rows.Clear();
        for (int index = 0; index < count; index++)
            provider.Rows.Add(new PackageScreenRow { Key = $"fake-{index}", Name = $"Aplicativo {index:D3}", Id = $"Fake.App.{index:D3}" });
        provider.ViewMode = mode;
        screen.UpdateLayout();
        await Task.Delay(250);
        string hostName = mode == "list" ? "ListView" : "GridView";
        var host = (ListViewBase)screen.FindName(hostName);
        object? itemsPanel = host.GetType().GetProperty("ItemsPanelRoot")?.GetValue(host);
        int realizedCount = itemsPanel is Panel panel
            ? panel.Children.OfType<FrameworkElement>().Count(item => item.ActualHeight > 0)
            : 0;
        Check(host.Items.Count == count && realizedCount > 0 && realizedCount < count,
            $"Telas de pacotes: {mode} virtualiza {count} itens ({realizedCount} contêineres medidos; lista {host.ActualWidth:F0}x{host.ActualHeight:F0})");
        host.ScrollIntoView(host.Items[count - 1]);
        await Task.Delay(100);
        host.UpdateLayout();
        Check(host.ContainerFromIndex(count - 1) is not null,
            $"Telas de pacotes: {mode} gera o contêiner do último item após rolagem ({count - 1})");
    }

    private static void InvokePackageScreenHandler(PackageScreen screen, string name, params object?[] args) =>
        typeof(PackageScreen).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(screen, args);

    [WinRT.GeneratedBindableCustomProperty]
    private sealed partial class FakePackageScreenProvider : IPackageScreenProvider, IDisposable
    {
        public FakePackageScreenProvider(PackageScreenDefinition definition)
        {
            Definition = definition;
            _exportCommand = new RelayCommand(() => ExecutedItemActions++);
            _searchCommand = new AsyncRelayCommand<string?>(query =>
            {
                SubmittedQueries.Add(query ?? string.Empty);
                return Task.CompletedTask;
            });
            ToggleSelectAllCommand = new RelayCommand(() =>
            {
                bool select = Rows.Any(row => !row.IsSelected);
                foreach (PackageScreenRow row in Rows) row.IsSelected = select;
            });
            SetSortCommand = new RelayCommand<object?>(column =>
            {
                if (column is string id) SortBy = id;
            });
            SetViewModeCommand = new RelayCommand<object?>(mode =>
            {
                if (mode is string value) ViewMode = value;
            });
            SetFilterCommand = new RelayCommand<object?>(_ => PropertyChanged?.Invoke(this, new(nameof(HasActiveFilters))));
            Rows.Add(new PackageScreenRow
            {
                Key = "fake-initial",
                Name = "Aplicativo simulado",
                Id = "Fake.Initial",
                Version = "1.0.0",
                NewVersion = "2.0.0",
                Source = "Origem simulada",
                SourceGlyph = PackageScreenCatalog.Default.ResolveIcon("local"),
                Status = "Disponível",
                Values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["name"] = "Nome do teste",
                    ["publisher"] = "Fabricante do teste",
                    ["id"] = "ID do teste",
                    ["version"] = "1.0.0",
                    ["newVersion"] = "2.0.0",
                    ["source"] = "Origem simulada",
                    ["status"] = "Disponível"
                }
            });
        }
        private string _search = string.Empty;
        private string _viewMode = "list";
        private string _sortBy = "name";
        private bool _sortAscending = true;
        private bool _filterOpen = true;
        public int ExecutedItemActions { get; private set; }
        public List<string> SubmittedQueries { get; } = [];
        private readonly ICommand _exportCommand;
        private readonly ICommand _searchCommand;
        public PackageScreenDefinition Definition { get; }
        public ObservableCollection<PackageScreenRow> Rows { get; } = [];
        public ObservableCollection<PackageScreenFilterOption> Filters { get; } = [new("only", "Somente exemplo", isChecked: false)];
        public ObservableCollection<PackageScreenAction> Actions { get; } = [];
        public ObservableCollection<PackageScreenAction> ContextActions { get; } = [];
        public string Subtitle => PackageScreenCatalog.Default.FormatSubtitle(Definition, Rows.Count, Rows.Count);
        public string StatusText => "Dados JSON simulados";
        public string SearchText { get => _search; set { _search = value; PropertyChanged?.Invoke(this, new(nameof(SearchText))); } }
        public string SearchPlaceholder => "Pesquisar";
        public string ViewMode { get => _viewMode; set { _viewMode = value; PropertyChanged?.Invoke(this, new(nameof(ViewMode))); } }
        public string SortBy { get => _sortBy; set { _sortBy = value; PropertyChanged?.Invoke(this, new(nameof(SortBy))); } }
        public bool SortAscending { get => _sortAscending; set { _sortAscending = value; PropertyChanged?.Invoke(this, new(nameof(SortAscending))); } }
        public bool IsLoading => false;
        public string? ErrorText => null;
        public bool IsFilterOpen { get => _filterOpen; set { _filterOpen = value; PropertyChanged?.Invoke(this, new(nameof(IsFilterOpen))); } }
        public bool HasActiveFilters => Filters.Any(filter => filter.IsChecked == true);
        public int SelectedCount => Rows.Count(row => row.IsSelected);
        public bool? MasterSelection => SelectedCount == 0 ? false : SelectedCount == Rows.Count ? true : null;
        public string EmptyTitle => "Sem itens";
        public string EmptyMessage => "Adicione dados";
        public IAsyncRelayCommand RefreshCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);
        public ICommand ToggleSelectAllCommand { get; }
        public ICommand SetSortCommand { get; }
        public ICommand SetViewModeCommand { get; }
        public ICommand SetFilterCommand { get; }
        public ICommand ToggleRowSelectionCommand { get; } = new RelayCommand<object?>(_ => { });
        public event PropertyChangedEventHandler? PropertyChanged;
        public ICommand? GetActionCommand(string actionId, PackageScreenRow? row = null) => actionId switch
        {
            "export" => _exportCommand,
            "search" => _searchCommand,
            "refresh" or "selectAll" => new RelayCommand(() => { }),
            _ => null
        };
        public object? GetActionParameter(string actionId, PackageScreenRow? row = null) => null;
        public void Dispose() => RefreshCommand.Cancel();
    }
}
