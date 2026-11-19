using System.Text.Json;
using System.Reflection;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;
namespace WinProvision.Store;

// Valida saúde da interface, navegação, bindings e serialização.
internal static partial class SmokeTest
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    internal sealed record RenderedCapture(int Width, int Height, byte[] Pixels);
    internal static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "smoke-results");
    internal static readonly List<string> BindingErrors = [];
    private static readonly List<string> Checks = [];
    private static void Check(bool condition, string message)
    {
        Directory.CreateDirectory(OutputDirectory);
        File.AppendAllText(Path.Combine(OutputDirectory, "smoke-progress.log"),
            $"{DateTimeOffset.Now:O} {(condition ? "PASS" : "FAIL")} {message}{Environment.NewLine}");
        if (!condition) throw new InvalidOperationException(message);
        Checks.Add(message);
    }
    private static string? BuildOfficeConfigurationXml(OfficeViewModel viewModel)
    {
        OfficeInstallRequest? request = viewModel.CreateInstallRequest();
        return request is null ? null : OfficeConfigXmlBuilder.Build(request).ToString(System.Xml.Linq.SaveOptions.None);
    }
    internal static void WriteFailure(Exception exception) => WriteResult(false, exception.ToString());
    private static void WriteResult(bool success, string? error = null)
    {
        Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(Path.Combine(OutputDirectory, "result.json"), AotJson.Serialize(
            new Dictionary<string, object?> { ["success"] = success, ["checks"] = Checks,
                ["bindingErrors"] = BindingErrors, ["error"] = error }));
    }
    internal static async Task RunAsync(MainWindow window, IServiceProvider services)
    {
        Directory.CreateDirectory(OutputDirectory);
        File.WriteAllText(Path.Combine(OutputDirectory, "smoke-progress.log"), string.Empty);
        VerifyAppPreferencesContractFixture();
        VerifyBatchObservableCollection();
        window.HideStartupSplash();
        await VerifyInitialNavigationSelectionAsync(window);
        VerifyOperationSettingsRenderer();
        await VerifySharedSettingsLayoutsAsync(window);
        await VerifyElevationSettingsPageAsync(window, services);
        await VerifyWindowBackdropAsync(window, services);
        await VerifyNavigationSelectionGeometryAsync(window);
        VerifyMainShellConfiguration(window);
        await VerifyPackageVisualPreferencesAsync();
        await VerifyImageCacheRenewalAsync();
        await VerifySvgImageDecodeAsync();
        await VerifyPackageScreenContractAsync(window, services);
        await VerifyPackageManagerAdapterExtensibilityAsync();
        await VerifyChocolateyOutdatedCommandAsync();
        await VerifyChocolateyProcessTimeoutKillsTreeAsync();
        await VerifyChocolateyTransientRequestRetryAsync();
        await VerifyChocolateyTlsFailureIsReportedOnceAsync();
        Check(WinGetManifestIconService.IsOfficialIconUrl(
            "https://cdn.winget.microsoft.com/icons/" + new string('a', 64) + ".png", new string('a', 64)),
            "Ícones: aceita URL oficial da CDN com hash válido");
        Check(!WinGetManifestIconService.IsOfficialIconUrl("https://example.com/icons/" + new string('a', 64) + ".png", new string('a', 64)),
            "Ícones: rejeita origem externa à CDN oficial");
        using (var iconTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        using (var catalog = new CatalogV2Client())
        {
            await catalog.RefreshAsync(iconTimeout.Token);
            AppEntry? chromeSummary = (await catalog.SearchAsync(null, 50000, iconTimeout.Token))
                .FirstOrDefault(entry => entry.Id.Equals("Google.Chrome.exe", StringComparison.OrdinalIgnoreCase)
                    || entry.Id.Equals("Google.Chrome", StringComparison.OrdinalIgnoreCase));
            Check(chromeSummary is not null, "Ícones do catálogo v2: encontra Google.Chrome.exe no índice assinado");
            if (chromeSummary is null) throw new InvalidOperationException("Google.Chrome.exe não está no catálogo v2.");
            AppEntry chrome = (await catalog.GetPackageAsync(chromeSummary, iconTimeout.Token)).Entry;
            string? iconHash = chrome.Media?.IconSha256;
            string? chromeIcon = IconService.ResolveCatalogMediaUrl(chrome, chrome.Media?.Icon, iconHash);
            Check(chrome.Media?.Icon?.StartsWith("@media/", StringComparison.Ordinal) == true
                && iconHash is { Length: 64 }
                && Uri.TryCreate(chromeIcon, UriKind.Absolute, out Uri? iconUri)
                && iconUri.Query.Contains("sha256=" + iconHash.ToLowerInvariant(), StringComparison.Ordinal),
                "Ícones do catálogo v2: resolve o caminho de mídia e o hash do app.json assinado");
            using var iconClient = new HttpClient();
            byte[] iconBytes = await iconClient.GetByteArrayAsync(chromeIcon!, iconTimeout.Token);
            DecodedWebpImage decodedIcon = WebpImageDecoder.Decode(iconBytes, 64);
            Check(iconHash is not null
                && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(iconBytes)) == iconHash
                && decodedIcon.Width <= 64 && decodedIcon.Height > 0,
                "Ícones do catálogo v2: baixa e decodifica a imagem WebP publicada");
            var iconProbe = new AppEntry { Id = "7zip.7zip", Name = "7-Zip", Source = "winget" };
            IReadOnlyList<string> cachedManifests = WinGetManifestIconService.FindCachedManifestPaths(iconProbe);
            Check(cachedManifests.All(path => path.StartsWith("manifests/", StringComparison.OrdinalIgnoreCase)),
                "Ícones: caminhos de manifestos encontrados no cache do WinGet são relativos a manifests");
            var iconService = services.GetRequiredService<WinGetManifestIconService>();
            string? officialIcon = await iconService.ResolveAsync(iconProbe, iconTimeout.Token);
            Check(officialIcon is not null, "Ícones: resolve manifesto WinGet e encontra ícone oficial do 7-Zip");
            Check(await AsyncImage.LoadBitmapAsync(officialIcon!, 64, iconTimeout.Token) is not null,
                "Ícones: decodifica imagem real da CDN oficial");
        }

        var app = new AppEntry { Id = "Test.Package", Name = "Aplicativo de teste", Source = "winget", InstallerSizeBytes = 100 * 1024L * 1024 };
        services.GetRequiredService<HomeViewModel>().LoadPreview((AppEntry[])[app]);
        var packageOptions = services.GetRequiredService<PackageInstallOptionsService>();
        packageOptions.Save(app.Id, app.Source, new PackageInstallOptions(IgnoreSecurityHash: true));
        Check(packageOptions.Get(app.Id, app.Source)?.IgnoreSecurityHash == false, "Integridade: preferências legadas não desativam hashes");
        var collections = services.GetRequiredService<CollectionsViewModel>();
        collections.Add(app);
        collections.Add(app);
        Check(collections.Items.Count == 1, "Coleções: inclusão não duplica o pacote");
        Check(collections.EstimatedSizeText.Contains("100") && collections.EstimatedTimeText != "—", "Coleções: estimativas disponíveis para tamanho conhecido");
        string backup = AotJson.Serialize(new ProfileBackupSet { Tabs = [collections.SelectedProfile!.Snapshot()] }, WinProvisionJsonOptions.Profile);
        Check(CollectionsViewModel.ParseProfiles(backup, "Teste").Count == 1, "Coleções: backup aceita restauração");
        var historyItem = new OperationItem("7-Zip", OperationKind.Install)
        {
            PackageId = "7zip.7zip",
            ManagerOverride = "winget",
            SourceNameOverride = "winget",
            VersionTo = "24.09"
        };
        historyItem.State = OperationState.Running;
        historyItem.Method = WingetMethod.ComApi;
        historyItem.AppendLog("Pacote localizado no catálogo WinGet.");
        historyItem.AppendLog("Instalação concluída com sucesso.");
        historyItem.State = OperationState.Completed;
        services.GetRequiredService<OperationHistoryService>().Record(historyItem);
        var updatesViewModel = services.GetRequiredService<UpdatesViewModel>();
        services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(true);
        DateTime initialUpdateDeadline = DateTime.UtcNow.AddSeconds(10);
        while (updatesViewModel.IsBusy && DateTime.UtcNow < initialUpdateDeadline)
            await Task.Delay(50);
        updatesViewModel.FilterChocolatey = true;
        await updatesViewModel.ScanCommand.ExecuteAsync(null);
        updatesViewModel.FilterChocolatey = true;
        updatesViewModel.ApplyFilters();
        Check(updatesViewModel.Packages.Any(package => package.Id == "Google.Chrome"
            && Uri.TryCreate(package.IconUrl, UriKind.Absolute, out Uri? updateIcon)
            && updateIcon.Scheme == Uri.UriSchemeHttps),
            "Atualizações: preserva uma imagem HTTPS válida para Google.Chrome");
        Check(updatesViewModel.Packages.Any(package => package.Id == "demo.package"
            && package.AvailableVersion == "2.0.0-rc.1" && package.Source == "chocolatey"),
            "Atualizações Chocolatey: mantém candidatos SemVer retornados por choco outdated");
        services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(false);
        updatesViewModel.FilterChocolatey = false;
        var updateBackend = (SmokePackageBackend)services.GetRequiredService<IPackageBackend>();
        await VerifyUpdateSourceIsolationAsync(updatesViewModel, updateBackend,
            services.GetRequiredService<ApplicationPreferencesService>());
        updateBackend.UpdateHandler = (_, _) => Task.FromResult(new WingetExecutionResult { Success = true });
        await updatesViewModel.RunUpdateOnPackagesAsync(
            [new UpgradablePackage { Id = "Test.Package", Name = "Aplicativo de teste", Source = "winget", CurrentVersion = "1", AvailableVersion = "2" }],
            ignoreSecurityHash: true);
        Check(updateBackend.LastUpdateOptions?.IgnoreSecurityHash == true,
            "Atualizações: ignorar hash é uma escolha explícita e válida só para a operação atual");
        Check(ManualWinGetCommandService.BuildUpgradeCommand("Google.Chrome") == "winget upgrade --id \"Google.Chrome\" --exact",
            "Atualizações: atualização manual gera comando WinGet upgrade exato");
        bool invalidManualIdRejected = false;
        try { ManualWinGetCommandService.BuildUpgradeCommand("Google.Chrome; Start-Process calc"); }
        catch (ArgumentException) { invalidManualIdRejected = true; }
        Check(invalidManualIdRejected, "Atualizações: ID manual inválido é rejeitado sem ser executado");
        UpgradablePackage? detailsUpdatePackage = null;
        updateBackend.UpdateHandler = (package, _) =>
        {
            detailsUpdatePackage = package;
            return Task.FromResult(new WingetExecutionResult
            {
                Success = false,
                FailureReason = WingetFailureReason.InstallError,
                Output = "A atualização foi executada, mas não foi confirmada. A versão instalada não corresponde ao alvo 2 (encontrada: 1)."
            });
        };
        var detailsViewModel = services.GetRequiredService<AppDetailsViewModel>();
        detailsViewModel.SetApp(app, "1", "2");
        await detailsViewModel.UpdateAsync();
        Check(detailsUpdatePackage is { CurrentVersion: "1", AvailableVersion: "2" },
            "Detalhes: atualização encaminha versões instalada e alvo ao backend");
        Check(detailsViewModel.StatusText.Contains("alvo 2") && detailsViewModel.StatusText.Contains("encontrada: 1"),
            "Detalhes: falha de confirmação informa a versão observada sem saída bruta do WinGet");
        updateBackend.UpdateHandler = null;
        await services.GetRequiredService<InstalledPackagesViewModel>().RefreshCommand.ExecuteAsync(null);
        var applicationPreferences = services.GetRequiredService<ApplicationPreferencesService>();
        Check(!applicationPreferences.ProvisioningMenuEnabled
            && window.ProvisioningMenuVisibility == Visibility.Collapsed,
            "Provisionamento: menu começa oculto por padrão");
        applicationPreferences.SetProvisioningMenuEnabled(true);
        await Task.Delay(100);
        Check(window.ProvisioningMenuVisibility == Visibility.Collapsed,
            "Modo WinGet: áreas fora do escopo permanecem ocultas");
        applicationPreferences.SetProvisioningMenuEnabled(false);
        await Task.Delay(100);
        Check(window.ProvisioningMenuVisibility == Visibility.Collapsed,
            "Provisionamento: desativar a opção oculta o menu");
        applicationPreferences.SetProvisioningMenuEnabled(true);
        foreach (ElementTheme theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            window.RootElement.RequestedTheme = theme;
            foreach (string tag in new[] { "home", "updates", "installed", "collections", "office", "settings" })
            {
                window.Navigate(tag);
                await WaitForPageAsync(window, tag, theme);
                if (window.CurrentPage is Views.HomePage home)
                {
                    if (theme == ElementTheme.Dark)
                        await VerifyDiscoverSortAsync(home, app);
                    var rotation = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)home.Showcase.FindName("HeroRotationToggleButton");
                    var banner = (Border)home.Showcase.FindName("HeroBanner");
                    Check(banner.Visibility == Visibility.Visible && banner.DataContext is AppEntry,
                        $"Descobrir: banner publicado permanece visível em {theme}");
                    Check(home.Showcase.FindName("HeroRotationIcon") is FluentIcons.WinUI.FluentIcon,
                        $"Descobrir: controles do banner carregam em {theme}");
                    rotation.IsChecked = false;
                    Check(home.IsHeroRotationPaused, $"Descobrir: pausa interrompe a rotação em {theme}");
                    rotation.IsChecked = true;
                    Check(!home.IsHeroRotationPaused, $"Descobrir: retomada restaura a rotação em {theme}");
                    if (theme == ElementTheme.Dark)
                    {
                        var preferences = services.GetRequiredService<ApplicationPreferencesService>();
                        preferences.SetSearchOnDemand(true);
                        await Task.Delay(250);
                        Check(((FrameworkElement)((PackageScreen)home.Content).FindName("EmptyState")).Visibility == Visibility.Visible
                            && !((PackageScreen)home.Content).ShowBrowseContent,
                            "Descobrir: pesquisa sob demanda oculta vitrine e apresenta a busca centralizada");
                        var discoverScreen = (PackageScreen)home.Content;
                        var discoverProvider = discoverScreen.Provider
                            ?? throw new InvalidOperationException("Provider de Descobrir ausente no smoke.");
                        Check(((TextBlock)discoverScreen.FindName("EmptyTitle")).Text == "Pesquise para encontrar aplicativos",
                            "Descobrir: estado inicial convida a pesquisar em vez de afirmar ausência de resultados");
                        Check(discoverProvider.Filters.Single(option => option.Id == "searchMode").Title == "Modo de pesquisa",
                            "Descobrir: rótulo interno das opções de pesquisa é Modo de pesquisa");
                        Check(!((SplitButton)discoverScreen.FindName("PrimaryActionButton")).IsEnabled,
                            "Descobrir: instalar e a seta do botão dividido ficam desabilitados sem seleção");
                        Check(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(window.TitleBarSearchBoxForSmoke) == "Pesquisar nome ou ID"
                            && ((PackageScreen)home.Content).FindName("SearchBox") is null,
                            "Descobrir: usa a única busca acessível na barra de título, sem duplicata lateral");
                        await CaptureAsync(window.RootElement, "discover-search-on-demand-dark.png");
                        preferences.SetSearchOnDemand(false);
                        services.GetRequiredService<HomeViewModel>().LoadPreview((AppEntry[])[app]);
                        home.SetAppsViewMode("list");
                        var discover = (StorefrontViewModel)home.DataContext;
                        discover.SearchText = "Aplicativo de teste";
                        discover.ApplyFilter();
                        await Task.Delay(250);
                        Check(discover.Apps.Count == 1 && discover.Apps[0].Id == app.Id,
                            "Descobrir: lista apresenta todos os resultados filtrados para seleção");
                        discover.SetAppSelected(app, true);
                        Check(discover.HasSelectedApps && discover.SelectedAppCount == 1,
                            "Descobrir: seleção de pacotes atualiza o estado das ações em lote");
                        var resultsSurface = (DependencyObject)((PackageScreen)home.Content).FindName("ListView");
                        Check(FindPackageCheckBox(resultsSurface, app) is not null,
                            "Descobrir: resultado renderiza a caixa de seleção do pacote");
                        if (FindPackageCheckBox(resultsSurface, app) is { } selectedCheckBox)
                            selectedCheckBox.IsChecked = true;
                        await CaptureAsync(window.RootElement, "discover-results-selection-dark.png");
                        AppEntry[] cardSamples = Enumerable.Range(0, 18).Reverse().Select(index => index == 0
                            ? app
                            : new AppEntry
                            {
                                Id = $"Test.Package.{index:D2}",
                                Name = $"Aplicativo de teste {index:D2}",
                                Version = "1.0.0",
                                Source = "winget"
                            }).ToArray();
                        services.GetRequiredService<HomeViewModel>().LoadPreview(cardSamples);
                        discover.SearchText = "Aplicativo de teste";
                        discover.ApplyFilter();
                        await Task.Delay(250);
                        discover.SetAppSelected(app, true);
                        home.SetAppsViewMode("grid");
                        await Task.Delay(100);
                        Check(((FrameworkElement)((PackageScreen)home.Content).FindName("GridView")).Visibility == Visibility.Visible
                            && ((FrameworkElement)((PackageScreen)home.Content).FindName("ListView")).Visibility == Visibility.Collapsed,
                            "Descobrir: alternância para cartões exibe somente o layout de cartões");
                        Check(FindPackageCheckBox((DependencyObject)((PackageScreen)home.Content).FindName("GridView"), app) is { IsChecked: true },
                            "Descobrir: seleção permanece sincronizada no cartão");
                        await CaptureAsync(window.RootElement, "discover-results-grid-dark.png");
                        home.SetAppsViewMode("details");
                        await Task.Delay(100);
                        Check(((FrameworkElement)((PackageScreen)home.Content).FindName("DetailsView")).Visibility == Visibility.Visible
                            && ((FrameworkElement)((PackageScreen)home.Content).FindName("GridView")).Visibility == Visibility.Collapsed,
                            "Descobrir: alternância para lista detalhada exibe somente a lista detalhada");
                        Check(FindPackageCheckBox((DependencyObject)((PackageScreen)home.Content).FindName("DetailsView"), app) is { IsChecked: true },
                            "Descobrir: seleção permanece sincronizada na lista detalhada");
                        await CaptureAsync(window.RootElement, "discover-results-icons-dark.png");
                        discover.SetAppSelected(app, false);
                        discover.SearchText = string.Empty;
                        discover.ApplyFilter();
                    }
                }
                await CaptureAsync(window.RootElement, $"{tag}-{theme}.png");
                if (window.CurrentPage is Views.UpdatesPage updates)
                {
                    services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(false);
                    var apiSource = updates.ScreenProvider.Filters.FirstOrDefault(option => option.Id == "source:winprovision");
                    var wingetSource = updates.ScreenProvider.Filters.Single(option => option.Id == "source:winget");
                    Check(updates.ScreenProvider.Filters.All(option => option.Id != "source:chocolatey"),
                        $"Atualizações: filtro Chocolatey oculto quando o gerenciador está desativado em {theme}");
                    Check(apiSource is null, $"Atualizações: filtro da fonte própria oculto em {theme}");
                    updatesViewModel.FilterWinget = false;
                    updatesViewModel.FilterChocolatey = false;
                    updatesViewModel.ApplyFilters();
                    Check(updatesViewModel.Packages.Count == 0,
                        $"Atualizações: filtros WinGet e Chocolatey removem os pacotes visíveis em {theme}");
                    updatesViewModel.FilterWinProvision = false;
                    updatesViewModel.ApplyFilters();
                    Check(updatesViewModel.Packages.Count == 0, $"Atualizações: sem resultados ao desativar as fontes WinGet em {theme}");
                    updatesViewModel.FilterWinget = true;
                    updatesViewModel.FilterWinProvision = true;
                    updatesViewModel.ApplyFilters();
                    Check(wingetSource.IsChecked == true
                        && updatesViewModel.Packages.All(package => package.UpdateSource == "winget"),
                        $"Atualizações: resultados disponíveis somente pela fonte WinGet em {theme}");
                    services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(true);
                    var chocolateySource = updates.ScreenProvider.Filters.Single(option => option.Id == "source:chocolatey");
                    updatesViewModel.FilterChocolatey = true;
                    await updatesViewModel.CheckUpdatesAsync();
                    updatesViewModel.ApplyFilters();
                    if (theme == ElementTheme.Dark)
                        await VerifyUpdatesActionsAsync(updates, updatesViewModel, updateBackend, services);
                    services.GetRequiredService<ApplicationPreferencesService>().SetChocolateyEnabled(false);
                }
                if (window.CurrentPage is Views.InstalledPackagesPage installed)
                {
                    var screen = (PackageScreen)installed.Content;
                    var list = (ListView)screen.FindName("ListView");
                    var grid = (GridView)screen.FindName("GridView");
                    var icons = (ListView)screen.FindName("DetailsView");
                    foreach (string mode in new[] { "list", "grid", "details" })
                    {
                        installed.ScreenProvider.SetViewModeCommand.Execute(mode);
                        ItemsControl activeView = mode == "list" ? (ItemsControl)list : mode == "grid" ? grid : icons;
                        Check(ReferenceEquals(activeView.ItemsSource, installed.ScreenProvider.Rows)
                            && (mode == "list" || list.ItemsSource is null)
                            && (mode == "grid" || grid.ItemsSource is null)
                            && (mode == "details" || icons.ItemsSource is null),
                            $"Instalados: apenas a vista {mode} observa os pacotes em {theme}");
                        if (mode != "list")
                            await CaptureAsync(window.RootElement, $"installed-{mode}-{theme}.png");
                    }
                    installed.ScreenProvider.SetViewModeCommand.Execute("list");
                    if (theme == ElementTheme.Dark)
                        await VerifyInstalledSharedScreenActionsAsync(installed, updateBackend, services);
                }
                if (window.CurrentPage is Views.OfficePage office)
                {
                    var provider = office.ScreenProvider;
                    Check(provider.Rows.Count == 10, $"Office: componentes projetados em linhas compartilhadas em {theme}");
                    Check(provider.StatusCardText is null,
                        $"Office: barra redundante de status removida em {theme}");
                    Check(provider.Rows.All(row => row.Source == "Microsoft 365")
                        && provider.Rows.All(row => row.SourceIconUri == "ms-appx:///Assets/Icons/microsoft-office.svg")
                        && provider.Rows.All(row => row.SourceIconUriDark == "ms-appx:///Assets/Icons/microsoft-office.svg"),
                        $"Office: origem Microsoft 365 usa o ícone colorido declarado por tema em {theme}");
                    Check(provider.Definition.SortColumns.SequenceEqual(new[] { "name", "id", "state" })
                        && provider.Definition.Toolbar.Primary.Menu.SequenceEqual(new[] { "officeAddCollection" })
                        && provider.Definition.Toolbar.Overflow.SequenceEqual(new[] { "officeRefresh", "officeOpenFolder", "officeUninstall" })
                        && provider.Definition.Toolbar.Overflow.Select(id => PackageScreenCatalog.Default.GetAction(id).Icon)
                            .SequenceEqual(new[] { "refresh", "openFolder", "uninstall" })
                        && provider.IsActionEnabled("officeAddCollection") == (office.ViewModel.SelectedAppCount > 0),
                        $"Office: Mais ações contém somente verificar, pasta e desinstalar na ordem com ícones preservados em {theme}");
                    Check(provider.Filters.Where(item => item.Kind == "choice").SelectMany(item => item.Values)
                        .All(value => !value.Title.Contains("(padrão)", StringComparison.OrdinalIgnoreCase)),
                        $"Office: dropdowns mostram rótulos limpos em {theme}");
                    Check(provider.Rows.Single(row => row.Id == "Access").CanSelect
                        && !provider.Rows.Single(row => row.Id == "Publisher").CanSelect
                        && !provider.Rows.Single(row => row.Id == "Teams").CanSelect
                        && provider.Rows.Single(row => row.Id == "Publisher").SelectionHelpText is not null
                        && provider.ApplicableCount == 8
                        && provider.Subtitle.Contains(" de 8 aplicativos", StringComparison.Ordinal),
                        $"Office: produto selecionado limita os checkboxes e o contador em {theme}");
                    var savedOfficeSelection = office.ViewModel.CoreAppItems.Concat(office.ViewModel.AppToggleItems)
                        .ToDictionary(item => item.Id, item => item.IsOn, StringComparer.OrdinalIgnoreCase);
                    office.ScreenProvider.ToggleSelectAllCommand.Execute(null);
                    Check(provider.SelectedCount == 8 && provider.MasterSelection == true,
                        $"Office: selecionar tudo inclui todas as 8 linhas habilitadas, inclusive Project e Visio, em {theme}");
                    provider.SearchText = "Access";
                    Check(provider.MasterSelection == true && provider.Rows.Count == 1,
                        $"Office: o estado mestre acompanha a busca e seleção visíveis em {theme}");
                    office.ScreenProvider.ToggleSelectAllCommand.Execute(null);
                    Check(provider.SelectedCount == 7 && !provider.Rows.Single().IsSelected,
                        $"Office: ação mestre indeterminada limpa apenas a linha visível em {theme}");
                    provider.SearchText = string.Empty;
                    office.ScreenProvider.ToggleSelectAllCommand.Execute(null);
                    Check(provider.SelectedCount == 8 && provider.MasterSelection == true,
                        $"Office: clicar no mestre indeterminado seleciona todas as linhas habilitadas em {theme}");
                    office.ScreenProvider.ToggleSelectAllCommand.Execute(null);
                    Check(provider.SelectedCount == 0 && provider.MasterSelection == false,
                        $"Office: clicar novamente limpa as linhas habilitadas em {theme}");
                    foreach (OfficeAppToggleViewModel item in office.ViewModel.CoreAppItems.Concat(office.ViewModel.AppToggleItems))
                        item.IsOn = savedOfficeSelection[item.Id];
                    OfficeAppToggleViewModel? selectableOfficeApp = office.ViewModel.CoreAppItems.FirstOrDefault(item => item.IsEnabled);
                    if (selectableOfficeApp is not null)
                    {
                        bool previousSelection = selectableOfficeApp.IsOn;
                        foreach (OfficeAppToggleViewModel coreApp in office.ViewModel.CoreAppItems.Where(item => item.IsEnabled)) coreApp.IsOn = false;
                        Check(!provider.IsActionEnabled("officeAddCollection"),
                            $"Office: adicionar à coleção desabilita quando não há aplicativos selecionados em {theme}");
                        selectableOfficeApp.IsOn = true;
                        Check(provider.IsActionEnabled("officeAddCollection"),
                            $"Office: adicionar à coleção habilita quando há aplicativo selecionado em {theme}");
                        selectableOfficeApp.IsOn = previousSelection;
                    }
                    OfficePlan? originalPlan = office.ViewModel.SelectedPlan;
                    OfficeAppToggleViewModel accessSetting = office.ViewModel.CoreAppItems.Single(item => item.Id == "Access");
                    bool originalAccess = accessSetting.IsOn;
                    office.ViewModel.SelectedPlan = OfficePlan.Family;
                    accessSetting.IsOn = true;
                    office.ViewModel.SelectedPlan = OfficePlan.HomeStudent2021;
                    Check(!provider.Rows.Single(row => row.Id == "Access").CanSelect
                        && office.ViewModel.CreateInstallRequest()?.ExcludedApps.Contains("Access", StringComparer.OrdinalIgnoreCase) == false,
                        $"Office: aplicativo incompatível é desabilitado e não entra no XML em {theme}");
                    office.ViewModel.SelectedPlan = originalPlan;
                    accessSetting.IsOn = originalAccess;
                    OfficeAppToggleViewModel wordItem = office.ViewModel.CoreAppItems.First(item => item.Id == "Word");
                    wordItem.IsOn = true;
                    string? originalXml = BuildOfficeConfigurationXml(office.ViewModel);
                    wordItem.IsOn = false;
                    OfficeInstallRequest? wordExcludedRequest = office.ViewModel.CreateInstallRequest();
                    Check(!wordItem.IsOn
                        && wordExcludedRequest?.ExcludedApps.Contains("Word", StringComparer.OrdinalIgnoreCase) == true,
                        $"Office: seleção da lista compartilhada preserva ExcludeApp em {theme}");
                    wordItem.IsOn = true;
                    Check(wordItem.IsOn
                        && BuildOfficeConfigurationXml(office.ViewModel) == originalXml,
                        $"Office: seleção restaurada gera XML original em {theme}");
                    var initialApps = office.ViewModel.CoreAppItems.ToDictionary(item => item.Id, item => item.IsOn, StringComparer.OrdinalIgnoreCase);
                    provider.ToggleSelectAllCommand.Execute(null);
                    Check(office.ViewModel.CoreAppItems.Where(item => item.IsEnabled).All(item => item.IsOn)
                        && office.ViewModel.AppToggleItems.Where(item => item.IsEnabled).All(item => item.IsOn)
                        && office.ViewModel.AppToggleItems.Where(item => !item.IsEnabled).All(item => !item.IsOn),
                        $"Office: selecionar tudo abrange todas as linhas habilitadas e preserva as desabilitadas em {theme}");
                    provider.ToggleSelectAllCommand.Execute(null);
                    foreach (var item in office.ViewModel.CoreAppItems) item.IsOn = initialApps[item.Id];
                    office.ViewModel.DownloadBeforeInstall = true;
                    Check(office.ViewModel.CreateInstallRequest() is { DownloadBeforeInstall: true, SourcePath: not null },
                        $"Office: modo offline segue disponível nas configurações separadas em {theme}");
                    office.ViewModel.DownloadBeforeInstall = false;
                    OfficeAppToggleViewModel extraLanguage = office.ViewModel.AdditionalLanguages.First(item => item.Id == "en-us");
                    extraLanguage.IsOn = true;
                    Check(office.ViewModel.CreateInstallRequest()?.AdditionalLanguageIds?.Contains("en-us") == true,
                        $"Office: idiomas adicionais seguem disponíveis nas configurações separadas em {theme}");
                    extraLanguage.IsOn = false;
                    OfficeAppToggleViewModel excludedComponent = office.ViewModel.ExcludedComponents.First(item => item.Id == "Groove");
                    excludedComponent.IsOn = true;
                    Check(office.ViewModel.CreateInstallRequest()?.ExcludedApps.Contains("Groove") == true,
                        $"Office: exclusões avançadas seguem disponíveis nas configurações separadas em {theme}");
                    excludedComponent.IsOn = false;
                    provider.SearchText = "Access";
                    Check(provider.Rows.Count == 1 && provider.Rows[0].Id == "Access",
                        $"Office: pesquisa compartilhada filtra nome ou identificador em {theme}");
                    provider.SearchText = string.Empty;
                    provider.SortBy = "state";
                    Check(provider.Rows.Select(row => row.GetValue("state")).SequenceEqual(
                        provider.Rows.Select(row => row.GetValue("state")).OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)),
                        $"Office: classificação por estado ordena os itens em {theme}");
                    provider.SortBy = "name";
                    foreach (string mode in new[] { "list", "details", "grid" })
                    {
                        provider.ViewMode = mode;
                        await Task.Delay(100);
                        Check(provider.ViewMode == mode, $"Office: visualização {mode} declarada no catálogo em {theme}");
                    }
                    await CaptureAsync(window.RootElement, $"office-package-screen-{theme}.png");
                }
                if (window.CurrentPage is Views.SettingsPage settings)
                {
                    settings.NavigateToCategory("Interface");
                    var searchOnDemandSwitch = (ToggleSwitch)settings.FindName("SearchOnDemandSwitch");
                    searchOnDemandSwitch.IsOn = true;
                    await Task.Delay(100);
                    Check(settings.ViewModel.SearchOnDemand
                        && services.GetRequiredService<ApplicationPreferencesService>().SearchOnDemand,
                        $"Configurações: pesquisa sob demanda salva pelo controle em {theme}");
                    await CaptureAsync(window.RootElement, $"settings-search-on-demand-{theme}.png");
                    searchOnDemandSwitch.IsOn = false;
                    Check(!settings.ViewModel.SearchOnDemand
                        && !services.GetRequiredService<ApplicationPreferencesService>().SearchOnDemand,
                        $"Configurações: pesquisa sob demanda pode ser desativada em {theme}");
                    settings.NavigateToCategory("Provisioning");
                    await Task.Delay(100);
                    await CaptureAsync(window.RootElement, $"settings-provisioning-{theme}.png");
                    Check(window.ProvisioningMenuVisibility == Visibility.Collapsed,
                        $"Configurações: áreas fora do escopo continuam ocultas em {theme}");
                    services.GetRequiredService<HomeViewModel>().LoadPreview((AppEntry[])[app]);
                    settings.ViewModel.SearchText = "backup";
                    Check(settings.FindName("CardBackup") is null && settings.FindName("PanelBackup") is null && settings.ViewModel.SearchResults.Count == 0,
                        $"Configurações: backup ausente da página e da pesquisa em {theme}");
                    settings.ViewModel.SearchText = "";
                    settings.NavigateToCategory("Maintenance");
                    SettingsCatalogSettingOption MaintenanceOption(string key) =>
                        settings.ViewModel.ScheduledMaintenanceGroups
                            .SelectMany(group => group.Settings.Concat(group.PrimarySetting is { } primary
                                ? new[] { primary }
                                : Array.Empty<SettingsCatalogSettingOption>()))
                            .Single(option => option.Key == key);
                    MaintenanceOption("maintenance-frequency").Value = "Interval";
                    MaintenanceOption("maintenance-enabled").Value = "true";
                    MaintenanceOption("maintenance-mode").Value = "NotifyOnly";
                    MaintenanceOption("maintenance-interval").Value = "60";
                    await settings.ViewModel.ApplyScheduledMaintenanceCommand.ExecuteAsync(null);
                    var scheduledPreferences = services.GetRequiredService<ApplicationPreferencesService>();
                    Check(scheduledPreferences.UpdateSchedule == BackgroundUpdateSchedule.Interval
                        && scheduledPreferences.UpdateIntervalMinutes == 60 && scheduledPreferences.AutoUpdateAtStartup,
                        $"Manutenção: opções declaradas no catálogo são aplicadas às preferências em {theme}");
                    var scheduledService = (SmokeUpdateStartup)services.GetRequiredService<IBackgroundUpdateStartupService>();
                    scheduledService.FailNextEnable = true;
                    MaintenanceOption("maintenance-interval").Value = "120";
                    await settings.ViewModel.ApplyScheduledMaintenanceCommand.ExecuteAsync(null);
                    Check(scheduledPreferences.UpdateIntervalMinutes == 60 && scheduledPreferences.AutoUpdateAtStartup,
                        $"Manutenção: falha no agendamento restaura a preferência anterior em {theme}");
                    ToastMessageService toastMessages = services.GetRequiredService<ToastMessageService>();
                    Check(toastMessages.History.Any(message => message.Title == "Não foi possível aplicar a agenda"),
                        $"Manutenção: falha no agendamento notifica o usuário em {theme}");
                    while (toastMessages.Messages.Count > 0)
                        toastMessages.Dismiss(toastMessages.Messages[0]);
                    await settings.ViewModel.RunScheduledMaintenanceCommand.ExecuteAsync(null);
                    Check(scheduledService.RunCalls > 0, $"Manutenção: execução imediata delegada ao serviço em {theme}");
                    SettingsCatalogSettingOption frequencyOption = MaintenanceOption("maintenance-frequency");
                    SettingsCatalogSettingOption intervalOption = MaintenanceOption("maintenance-interval");
                    SettingsCatalogSettingOption timeOption = MaintenanceOption("maintenance-time");
                    SettingsCatalogSettingOption dayOption = MaintenanceOption("maintenance-day");
                    SettingsCatalogSettingOption enabledOption = MaintenanceOption("maintenance-enabled");
                    SettingsCatalogSettingOption runOption = MaintenanceOption("maintenance-run");
                    Check(frequencyOption.IsCombo && intervalOption.IsCombo && intervalOption.IsEnabled,
                        $"Manutenção: controles compactos derivados do catálogo em {theme}");
                    Check(intervalOption.IsVisible && !timeOption.IsVisible && !dayOption.IsVisible,
                        $"Manutenção: visibilidade das opções segue a repetição declarada no catálogo em {theme}");
                    Check(enabledOption.StateText.StartsWith("Ativada · Em um intervalo fixo", StringComparison.Ordinal),
                        $"Manutenção: estado do cartão principal usa rótulos declarados no catálogo em {theme}");
                    Check(runOption.IsAction && runOption.ActionCommand is not null
                        && runOption.Description.Contains("Última execução", StringComparison.Ordinal),
                        $"Manutenção: linha de execução exibe histórico e ação no catálogo em {theme}");
                    await CaptureAsync(window.RootElement, $"settings-maintenance-{theme}.png");
                    var maintenanceScroll = (ScrollViewer)settings.FindName("HomepageScrollViewer");
                    maintenanceScroll.ChangeView(null, maintenanceScroll.ScrollableHeight, null, true);
                    await Task.Delay(150);
                    await CaptureAsync(window.RootElement, $"settings-maintenance-actions-{theme}.png");
                    settings.GoBack();
                    settings.NavigateToCategory("Elevator");
                    await settings.ViewModel.RefreshElevatorCommand.ExecuteAsync(null);
                    Check(((Button)settings.FindName("RefreshElevatorButton")).Command is not null,
                        $"Elevator: verificação disponível por binding WinRT em {theme}");
                    await settings.ViewModel.AuthorizeElevationCommand.ExecuteAsync(null);
                    var elevator = (SmokeElevatorSettings)services.GetRequiredService<IElevatorSettingsService>();
                    Check(elevator.Authorized, $"Elevator: autorização delegada ao serviço simulado em {theme}");
                    var elevationSettings = settings.ViewModel.ElevationSettingGroups.SelectMany(group => group.Settings).ToArray();
                    Check(elevationSettings.Any(option => option.ShowToggle)
                        && elevationSettings.All(option => option.Definition.Kind == "preference")
                        && settings.ViewModel.GetActiveElevationPolicyText().Contains("Proteções fixas", StringComparison.Ordinal),
                        $"Elevator: catálogo mostra preferências funcionais e mantém proteções na inspeção em {theme}");
                    await settings.ViewModel.AuthorizeElevationCommand.ExecuteAsync(null);
                    await settings.ViewModel.RevokeElevationCommand.ExecuteAsync(null);
                    Check(!elevator.Authorized && elevator.IsAllowed, $"Elevator: encerrar sessão preserva permissão futura em {theme}");
                    await CaptureAsync(window.RootElement, $"settings-elevator-{theme}.png");
                    settings.GoBack();
                }
                if (window.CurrentPage is Views.CollectionsPage packages)
                {
                    if (theme == ElementTheme.Dark)
                        await VerifyCollectionSharedScreenAsync(packages, services);
                    var cloudImport = packages.ScreenProvider.GetActionCommand("importCloud");
                    Check(cloudImport is not null && !cloudImport.CanExecute(null),
                        $"Pacotes: importação da nuvem exige conta conectada em {theme}");
                    packages.ViewModel.ErrorTitle = "Alguns aplicativos não foram incluídos no ZIP";
                    packages.ViewModel.ErrorMessage = "Steam (Valve.Steam): instalador compatível indisponível.\nOutro aplicativo: tempo limite ao baixar.";
                    packages.ViewModel.HasError = true;
                    await Task.Delay(150);
                    var packageScreen = (PackageScreen)packages.FindName("Screen");
                    var exportError = (FrameworkElement)packageScreen.FindName("ErrorState");
                    Check(exportError.Visibility == Visibility.Visible && ContainsText(exportError, "Valve.Steam"), $"Pacotes: relatório de exportação visível em {theme}");
                    await CaptureAsync(window.RootElement, $"collection-export-error-{theme}.png");
                    packages.ViewModel.HasError = false;
                }
                if (window.CurrentPage is Views.ProvisioningPage provisioning)
                {
                    if (((ProvisioningViewModel)provisioning.DataContext).HasWallpaper)
                        ((ProvisioningViewModel)provisioning.DataContext).ClearWallpaperCommand.Execute(null);
                    ((FrameworkElement)provisioning.FindName("DesktopPreviewFrame")!).StartBringIntoView();
                    await Task.Delay(150);
                    await CaptureAsync(window.RootElement, $"provisioning-preview-{theme}.png");
                    ((FrameworkElement)provisioning.FindName("DesktopPreviewTaskbar")!).StartBringIntoView();
                    await Task.Delay(150);
                    await CaptureAsync(window.RootElement, $"provisioning-taskbar-{theme}.png");
                    Check(provisioning.DataContext is ProvisioningViewModel,
                        $"Provisionamento: página e ViewModel carregados em {theme}");
                    var provisioningModel = (ProvisioningViewModel)provisioning.DataContext;
                    ((Expander)provisioning.FindName("AppearanceSettingsExpander")!).IsExpanded = true;
                    await Task.Delay(100);
                    provisioningModel.SelectedAccentMode = provisioningModel.AccentOptions[2];
                    var accentPicker = (ColorPicker)provisioning.FindName("ProvisioningAccentColorPicker")!;
                    accentPicker.StartBringIntoView();
                    await Task.Delay(150);
                    await CaptureAsync(window.RootElement, $"provisioning-color-picker-{theme}.png");
                    accentPicker.Color = Windows.UI.Color.FromArgb(255, 18, 52, 86);
                    await Task.Delay(100);
                    Check(provisioningModel.IsCustomAccentSelected && provisioningModel.AccentColor == "#123456",
                        $"Provisionamento: ColorPicker atualiza a cor personalizada do perfil em {theme}");
                    provisioningModel.SelectedAccentMode = provisioningModel.AccentOptions[0];
                    provisioningModel.IsWallpaperPickerExpanded = true;
                    await provisioningModel.LoadWallpaperOptionsCommand.ExecutionTask!;
                    Check(provisioningModel.WallpaperOptions.Count > 0,
                        $"Provisionamento: fixture local de wallpaper carregada em {theme}");
                    Check(provisioningModel.WallpaperOptions[0].IsLocalChoice,
                        $"Provisionamento: card de imagem local aparece primeiro na galeria em {theme}");
                    var firstWallpaperOption = provisioningModel.WallpaperOptions.First(option => !option.IsLocalChoice);
                    Check(Uri.TryCreate(firstWallpaperOption.Url, UriKind.Absolute, out var wallpaperFixtureUri)
                        && wallpaperFixtureUri.IsFile,
                        $"Provisionamento: smoke usa URL local da fixture, sem depender da CDN em {theme}");
                    var wallpaperThumbnail = await AsyncImage.LoadBitmapAsync(firstWallpaperOption.Url, 380, CancellationToken.None);
                    Check(wallpaperThumbnail is not null,
                        $"Provisionamento: miniatura da fixture local decodificada em {theme}");
                    if (theme == ElementTheme.Dark)
                    {
                        var wallpaperOption = firstWallpaperOption;
                        await provisioningModel.SelectCatalogWallpaperCommand.ExecuteAsync(wallpaperOption);
                        Check(provisioningModel.HasWallpaper && provisioningModel.WallpaperFileName == wallpaperOption.FileName
                            && provisioningModel.StatusText.Contains("incorporada ao perfil", StringComparison.OrdinalIgnoreCase),
                            "Provisionamento: carregar e selecionar fixture local incorpora imagem ao perfil");
                        provisioningModel.ClearWallpaperCommand.Execute(null);
                    }
                    ((FrameworkElement)provisioning.FindName("WallpaperGalleryItems")!).StartBringIntoView();
                    var provisioningScroll = (ScrollViewer)provisioning.FindName("ProvisioningScrollViewer")!;
                    provisioningScroll.ChangeView(null, provisioningScroll.ScrollableHeight, null);
                    await Task.Delay(300);
                    await CaptureAsync(window.RootElement, $"provisioning-wallpaper-gallery-{theme}.png");
                    provisioningModel.IsWallpaperPickerExpanded = false;
                    provisioningModel.SelectedTaskbarAutoHide = provisioningModel.BooleanOptions.First(option => option.Value == true);
                    Check(((Border)provisioning.FindName("DesktopPreviewTaskbar")!).Height == 24
                        && ((FrameworkElement)provisioning.FindName("NormalTaskbarContent")!).Visibility == Visibility.Collapsed
                        && ((FrameworkElement)provisioning.FindName("HiddenTaskbarContent")!).Visibility == Visibility.Visible,
                        $"Provisionamento: ocultação da barra mostra o estado compacto do Figma em {theme}");
                    await CaptureAsync(window.RootElement, $"provisioning-taskbar-hidden-{theme}.png");
                    provisioningModel.SelectedTaskbarAutoHide = provisioningModel.BooleanOptions[0];
                    provisioningModel.PreviewStartCommand.Execute(null);
                    Check(provisioningModel.IsPreviewStartOpen,
                        $"Provisionamento: menu Iniciar abre na prévia em {theme}");
                    await CaptureAsync(window.RootElement, $"provisioning-start-{theme}.png");
                    provisioningModel.PreviewQuickSettingsCommand.Execute(null);
                    Check(provisioningModel.IsPreviewQuickSettingsOpen && !provisioningModel.IsPreviewStartOpen,
                        $"Provisionamento: painel da prévia alterna em {theme}");
                    provisioningModel.ClosePreviewSurfaceCommand.Execute(null);
                    Check(provisioningModel.SelectedSystemTheme?.Label == "Não alterar",
                        $"Provisionamento: opções padrão visíveis em {theme}");
                    Check(((Button)provisioning.FindName("SaveProvisioningButton")!).Command is not null,
                        $"Provisionamento: salvar disponível em {theme}");
                    if (theme == ElementTheme.Dark)
                    {
                        string sampleWallpaperPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Windows11Preview", "wallpaper-dark.png");
                        await provisioningModel.LoadWallpaperFromFileAsync(sampleWallpaperPath, CancellationToken.None);
                        Check(provisioningModel.HasWallpaper && provisioningModel.WallpaperFileName == "wallpaper-dark.png"
                            && ReferenceEquals(((Image)provisioning.FindName("DesktopPreviewWallpaper")!).Source, provisioningModel.WallpaperPreview),
                            "Provisionamento: carregar arquivo real atualiza o wallpaper do Canvas");
                        byte[] samplePng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jUioAAAAASUVORK5CYII=");
                        var image = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                        var writer = new Windows.Storage.Streams.DataWriter(image);
                        writer.WriteBytes(samplePng);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        image.Seek(0);
                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        await bitmap.SetSourceAsync(image);
                        provisioningModel.SetWallpaperForValidation("fixture.png", Convert.ToBase64String(samplePng), bitmap);
                        Check(ReferenceEquals(((Image)provisioning.FindName("DesktopPreviewWallpaper")!).Source, bitmap),
                            "Provisionamento: imagem escolhida aparece imediatamente no Canvas");
                        provisioningModel.SaveCommand.Execute(null);
                        var saved = ProfileDocumentService.Parse(AotJson.Serialize(collections.CreateBackupSnapshot(), WinProvisionJsonOptions.Profile));
                        var savedWallpaper = saved.Provisioning ?? saved.Tabs.First().Provisioning;
                        Check(savedWallpaper?.WallpaperFileName == "fixture.png"
                            && savedWallpaper.WallpaperImageBase64 == Convert.ToBase64String(samplePng),
                            "Provisionamento: wallpaper incorporado preservado no perfil JSON");
                        provisioningModel.ClearWallpaperCommand.Execute(null);
                        Check(((Image)provisioning.FindName("DesktopPreviewWallpaper")!).Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage restored
                            && restored.UriSource?.ToString().Contains("wallpaper-dark.png") == true,
                            "Provisionamento: remover imagem restaura wallpaper do tema");
                        provisioningModel.SelectedSystemTheme = provisioningModel.ThemeOptions[1];
                        Check(((Image)provisioning.FindName("DesktopPreviewWallpaper")!).Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage lightWallpaper
                            && lightWallpaper.UriSource?.ToString().Contains("wallpaper-light.png") == true,
                            "Provisionamento: trocar tema atualiza wallpaper do Canvas");
                        provisioningModel.SelectedSystemTheme = provisioningModel.ThemeOptions[0];
                    }
                }
                if (window.CurrentPage is Views.AccountSyncPage accountPage)
                    Check(accountPage.FindName("ImportJsonButton") is null && accountPage.FindName("ExportJsonButton") is null,
                        $"Conta: ações JSON concentradas em Pacotes em {theme}");
            }
        }
        const string smokePolicyJson = """
            {
              "schemaVersion": 1,
              "categories": {
                "PassiveInformation": {
                  "defaultSeverity": "Information",
                  "channels": ["InApp"],
                  "autoDismissSeconds": 6,
                  "dedupeWindowSeconds": 30,
                  "cooldownSeconds": 0,
                  "coalesce": true,
                  "showWhenAppFocused": true,
                  "showToastWhenUnfocused": false,
                  "requiresAction": false,
                  "persistInHistory": false
                },
                "BlockingError": {
                  "defaultSeverity": "Error",
                  "channels": ["InApp"],
                  "autoDismissSeconds": null,
                  "dedupeWindowSeconds": 30,
                  "cooldownSeconds": 0,
                  "coalesce": true,
                  "showWhenAppFocused": true,
                  "showToastWhenUnfocused": false,
                  "requiresAction": false,
                  "persistInHistory": false
                }
              }
            }
            """;
        var smokeToasts = new ToastMessageService(window.RootElement.DispatcherQueue,
            NotificationPolicyCatalog.Load(smokePolicyJson),
            operationPreferences: new OperationPreferencesService(Path.Combine(Path.GetTempPath(), $"notification-smoke-{Guid.NewGuid():N}.json")),
            historyStore: new NotificationHistoryStore(Path.Combine(Path.GetTempPath(), $"notification-history-smoke-{Guid.NewGuid():N}.json")),
            isAppFocused: static () => true);
        foreach (ToastMessageItem toast in smokeToasts.Messages.ToArray()) smokeToasts.Dismiss(toast);
        string managerPreferencesFile = Path.Combine(Path.GetTempPath(), $"winprovision-manager-preferences-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(managerPreferencesFile, "{}");
            var legacyManagerPreferences = new ApplicationPreferencesService(managerPreferencesFile, persistStartup: false);
            Check(legacyManagerPreferences.WingetEnabled, "Gerenciadores: preferência WinGet ausente mantém comportamento ativado em arquivos antigos");
            legacyManagerPreferences.SetWingetEnabled(false);
            Check(!new ApplicationPreferencesService(managerPreferencesFile, persistStartup: false).WingetEnabled,
                "Gerenciadores: preferência WinGet persiste e é recarregada");
            legacyManagerPreferences.SetPackageManagerEnabled("future-manager", false);
            Check(!new ApplicationPreferencesService(managerPreferencesFile, persistStartup: false)
                    .GetPackageManagerEnabled("future-manager"),
                "Gerenciadores: preferências extensíveis persistem sem criar uma propriedade por gerenciador");
        }
        finally { if (File.Exists(managerPreferencesFile)) File.Delete(managerPreferencesFile); }
        await smokeToasts.PublishAsync(new Notification("search-1", NotificationCategories.PassiveInformation,
            NotificationSeverity.Information, "Resultado", "Busca concluída.", DeduplicationKey: "search-result"));
        await smokeToasts.PublishAsync(new Notification("search-2", NotificationCategories.PassiveInformation,
            NotificationSeverity.Information, "Resultado", "Busca concluída.", DeduplicationKey: "search-result"));
        await Task.Delay(50);
        Check(smokeToasts.Messages.Count == 1 && smokeToasts.Messages[0].Count == 2,
            "Notificações: eventos repetidos com a mesma chave são coalescidos com contagem");
        foreach (ToastMessageItem toast in smokeToasts.Messages.ToArray()) smokeToasts.Dismiss(toast);
        await smokeToasts.PublishAsync(new Notification("info", NotificationCategories.PassiveInformation,
            NotificationSeverity.Information, "Informação", "Aviso informativo."));
        await smokeToasts.PublishAsync(new Notification("error", NotificationCategories.BlockingError,
            NotificationSeverity.Error, "Erro", "Falha na operação."));
        await Task.Delay(50);
        Check(smokeToasts.Messages.Count == 1 && smokeToasts.Messages[0].Id == "error",
            "Notificações: erro passa à frente de aviso informativo");
        foreach (ToastMessageItem toast in smokeToasts.Messages.ToArray()) smokeToasts.Dismiss(toast);
        Check(smokeToasts.Messages.Single().Id == "info",
            "Notificações: aviso informativo retorna à fila após dispensar o erro");
        string englishSourceOutput = "Name    Argument                                              Type\r\n------------------------------------------------------------\r\nwinget  https://cdn.winget.microsoft.com/cache                 Microsoft.PreIndexed.Package\r\nmsstore https://storeedgefd.dsx.mp.microsoft.com/v9.0           Microsoft.Rest\r\ncommunity https://community.chocolatey.org/api/v2/              Microsoft.Rest\r\n";
        string[] expectedEnglishSources =
        [
            "winget  https://cdn.winget.microsoft.com/cache                 Microsoft.PreIndexed.Package",
            "msstore https://storeedgefd.dsx.mp.microsoft.com/v9.0           Microsoft.Rest",
            "community https://community.chocolatey.org/api/v2/              Microsoft.Rest"
        ];
        string portugueseSourceOutput = "Nome    Argumento                                             Especificado explicitamente\r\n-------------------------------------------------------------------------------------\r\nwinget  https://cdn.winget.microsoft.com/cache                 Não\r\nmsstore https://storeedgefd.dsx.mp.microsoft.com/v9.0           Não\r\ncommunity https://community.chocolatey.org/api/v2/              Sim\r\n";
        string[] expectedPortugueseSources =
        [
            "winget  https://cdn.winget.microsoft.com/cache                 Não",
            "msstore https://storeedgefd.dsx.mp.microsoft.com/v9.0           Não",
            "community https://community.chocolatey.org/api/v2/              Sim"
        ];
        IReadOnlyList<string> parsedEnglishSources = WingetSourcesService.ParseListOutput(englishSourceOutput);
        IReadOnlyList<string> parsedPortugueseSources = WingetSourcesService.ParseListOutput(portugueseSourceOutput);
        Check(parsedEnglishSources.Count == expectedEnglishSources.Length
            && parsedEnglishSources.SequenceEqual(expectedEnglishSources)
            && parsedEnglishSources.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0])
                .SequenceEqual(new[] { "winget", "msstore", "community" }),
            "Gerenciadores: cabeçalho e separador do WinGet em inglês são omitidos e três fontes permanecem");
        Check(parsedPortugueseSources.Count == expectedPortugueseSources.Length
            && parsedPortugueseSources.SequenceEqual(expectedPortugueseSources)
            && parsedPortugueseSources.Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0])
                .SequenceEqual(new[] { "winget", "msstore", "community" }),
            "Gerenciadores: cabeçalho Nome/Argumento/Especificado explicitamente e separador em português são omitidos");
        IReadOnlyList<string> structurallySkippedHeader = WingetSourcesService.ParseListOutput(
            "Origem Endereço Tipo\r\n--------------------------\r\nwinget https://example.test/winget\r\n");
        Check(structurallySkippedHeader.SequenceEqual(new[] { "winget https://example.test/winget" }),
            "Gerenciadores: o cabeçalho é descartado pela linha separadora, sem depender de palavras localizadas");
        window.Navigate("package-managers");
        await WaitForPageAsync(window, "package-managers", ElementTheme.Light);
        if (window.CurrentPage is Views.PackageManagersPage packageManagers)
        {
            Check(packageManagers.ViewModel.ManagerRows.Select(row => row.Id).SequenceEqual(new[] { "winget", "chocolatey", "winprovision-store" }),
                "Gerenciadores: inclui a futura entrada integrada sem adaptador ativo");
            var storeManagerRow = packageManagers.ViewModel.ManagerRows.Single(row => row.Id == "winprovision-store");
            PackageManagerDescriptor storeDescriptor = storeManagerRow.Definition;
            PackageManagerCatalog managerCatalog = PackageManagerCatalog.Default;
            Check(storeManagerRow.DisplayName == managerCatalog.GetText(storeDescriptor.DisplayNameKey, storeDescriptor.DisplayName)
                && storeManagerRow.CanToggle == (storeDescriptor.CanToggle && !storeManagerRow.IsBusy)
                && storeManagerRow.ToggleHelpText == managerCatalog.GetText(storeDescriptor.DisabledReasonKey, string.Empty),
                "Gerenciadores: a linha reflete nome, estado e aviso do descritor ativo");
            if (storeManagerRow.IconUri == "ms-appx:///Assets/Icons/winprovision-store.svg"
                && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "winprovision-store.svg")))
                Check(true, "Gerenciadores: o descritor aponta para o SVG empacotado");
            else
                RecordKnownPackagedSvgFailure("Assets/Icons/winprovision-store.svg (linha de gerenciador)");
            using (var svgStream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "winprovision-store.svg")))
            using (var svgRandom = svgStream.AsRandomAccessStream())
            {
                var svg = new SvgImageSource { RasterizePixelWidth = 24, RasterizePixelHeight = 24 };
                if (await svg.SetSourceAsync(svgRandom) == SvgImageSourceLoadStatus.Success)
                    Check(true, "Gerenciadores: SVG da entrada integrada carrega no rasterizador do app");
                else
                    RecordKnownPackagedSvgFailure("Assets/Icons/winprovision-store.svg (rasterização de gerenciador)");
            }
            if (packageManagers.ViewModel.ManagerRows.First(row => row.Id == "chocolatey").IconUri == "ms-appx:///Assets/Icons/chocolatey.svg")
                Check(true, "Gerenciadores: ícone SVG do Chocolatey é propagado do catálogo para a linha");
            else
                RecordKnownPackagedSvgFailure("Assets/Icons/chocolatey.svg (asserção de propagação para a linha)");
            Check(packageManagers.ViewModel.ManagerRows.First(row => row.Id == "winget").IconUri == "ms-appx:///Assets/Icons/winget.png",
                "Gerenciadores: ícone SVG do WinGet é propagado do catálogo para a linha");
            Check(packageManagers.ViewModel.IsOfficeModuleEnabled
                && packageManagers.ViewModel.IsOfficeModuleVisible,
                "Módulos: Office aparece habilitado por padrão e pesquisável");
            packageManagers.ViewModel.OpenOfficeSettings();
            await WaitForPageAsync(window, "office-preferences", ElementTheme.Light);
            Check(window.CurrentPage is Views.OfficePreferencesPage officePreferences
                && officePreferences.FindName("OfficePreferencesBackButton") is Button,
                "Office: preferências abrem como página dedicada sem card de prévia XML");
            window.Navigate("package-managers");
            await WaitForPageAsync(window, "package-managers", ElementTheme.Light);
            packageManagers = (Views.PackageManagersPage)window.CurrentPage!;
            DateTime checkDeadline = DateTime.UtcNow.AddSeconds(20);
            while (packageManagers.ViewModel.IsCheckingAllManagers && DateTime.UtcNow < checkDeadline)
                await Task.Delay(100);
            Check(!packageManagers.ViewModel.IsCheckingAllManagers
                && !string.IsNullOrWhiteSpace(packageManagers.ViewModel.LastAllManagersChecked),
                "Gerenciadores: verificar novamente conclui a verificação global e registra quando terminou");
            string checkingLabel = PackageManagerCatalog.Default.GetText("status.checking");
            Check(packageManagers.ViewModel.ManagerRows.All(row => row.StatusText != checkingLabel),
                "Gerenciadores: selo sai do estado de verificação quando os probes concluem");
            var managerRows = (ItemsControl)packageManagers.FindName("ManagerRowsList")!;
            await Task.Delay(100);
            ToggleSwitch[] managerToggles = FindVisualElements<ToggleSwitch>(managerRows).ToArray();
            int visibleManagerButtons = FindVisualElements<Button>(managerRows).Count(button => button.Visibility == Visibility.Visible);
            Check(managerToggles.Length == 3
                && managerToggles.Count(toggle => !toggle.IsEnabled) == 1
                && visibleManagerButtons == 3,
                $"Gerenciadores: template uniforme mostra toggle e navegação em cada linha (toggles={managerToggles.Length}, disabled={managerToggles.Count(toggle => !toggle.IsEnabled)}, buttons={visibleManagerButtons})");
            await Task.Delay(250);
            if (FindVisualElements<Image>(managerRows).Count(image => image.Source is SvgImageSource) >= 2)
                Check(true, "Gerenciadores: SVGs do Chocolatey e WinProvision Store aparecem como imagens rasterizadas");
            else
                RecordKnownPackagedSvgFailure("Assets/Icons/chocolatey.svg e Assets/Icons/winprovision-store.svg (asserção de imagens da lista)");
            await CaptureAsync(window.RootElement, "package-managers-light.png");
            window.RootElement.RequestedTheme = ElementTheme.Dark;
            window.RootElement.UpdateLayout();
            await Task.Delay(100);
            await CaptureAsync(window.RootElement, "package-managers-dark.png");
            window.RootElement.RequestedTheme = ElementTheme.Light;
            window.RootElement.UpdateLayout();
            window.SetTitleBarSearchTextForSmoke("Chocolatey");
            packageManagers.ViewModel.ManagerRows.First(row => row.Id == "chocolatey").OpenDetailsCommand.Execute(null);
            DateTime detailsDeadline = DateTime.UtcNow.AddSeconds(15);
            while ((packageManagers.ViewModel.SelectedActions.Count == 0 || packageManagers.ViewModel.SelectedInstallFields.Count == 0)
                && DateTime.UtcNow < detailsDeadline)
                await Task.Delay(100);
            Check(((FrameworkElement)packageManagers.FindName("ManagerDetails")!).Visibility == Visibility.Visible
                && packageManagers.ViewModel.SelectedManager?.Id == "chocolatey",
                "Gerenciadores: a tela única exibe o adaptador selecionado");
            Check(!window.TitleBarSearchIsVisibleForSmoke
                && !((ITitleBarSearchTarget)packageManagers).SearchContextAvailable
                && packageManagers.ViewModel.ManagerSearchText == "Chocolatey",
                "Pesquisa: detalhes do gerenciador ocultam a caixa e preservam o filtro da lista");
            packageManagers.ViewModel.ManagerSearchText = string.Empty;
            Check(packageManagers.FindName("ManagerSettingsScrollViewer") is ScrollViewer
                && packageManagers.ViewModel.SelectedActions.Count > 0
                && packageManagers.ViewModel.SelectedInstallFields.Count > 0,
                "Gerenciadores: campos tipados e ações comuns carregam");
            string emptyManagerLogs = Path.Combine(OutputDirectory, "empty-manager-logs");
            Directory.CreateDirectory(emptyManagerLogs);
            IAppLog appLog = services.GetRequiredService<IAppLog>();
            Check(await PackageManagerLogReader.ReadAsync("chocolatey", appLog, logDirectoryOverride: emptyManagerLogs) == "Nenhum registro ainda"
                && await PackageManagerLogReader.ReadAsync("winget", appLog, logDirectoryOverride: emptyManagerLogs) == "Nenhum registro ainda",
                "Gerenciadores: estado vazio dos leitores Chocolatey e WinGet é localizado");
            ScrollViewer chocolateySettingsScroll = (ScrollViewer)packageManagers.FindName("ManagerSettingsScrollViewer")!;
            chocolateySettingsScroll.ChangeView(0, chocolateySettingsScroll.ScrollableHeight, null, true);
            await Task.Delay(120);
            ((FrameworkElement)packageManagers.FindName("ManagerDetails")!).UpdateLayout();
            Button[] chocolateyLogActions = FindVisualElements<Button>((DependencyObject)packageManagers.FindName("ManagerDetails")!)
                .Where(button => Equals(button.Tag, "openLogs")).ToArray();
            Button? chocolateyLogLink = chocolateyLogActions.FirstOrDefault(button => button.Visibility == Visibility.Visible);
            ((FrameworkElement)packageManagers.FindName("ManagerDetails")!).UpdateLayout();
            Check(chocolateyLogActions.Length == 2 && chocolateyLogLink?.Content is Grid
                && FindVisualElements<FluentIcons.WinUI.FluentIcon>(chocolateyLogLink).Count() == 2
                && chocolateyLogActions.Any(button => button.Visibility == Visibility.Collapsed),
                $"Gerenciadores: ação link de registros renderiza item de navegação com ícone e seta (buttons={chocolateyLogActions.Length}; visibilities={string.Join(',', chocolateyLogActions.Select(button => button.Visibility))}; content={string.Join(',', chocolateyLogActions.Select(button => button.Content?.GetType().Name ?? string.Empty))}; icons={((chocolateyLogLink?.Content is Grid content) ? FindVisualElements<FluentIcons.WinUI.FluentIcon>(content).Count() : 0)})");
            window.RootElement.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "package-manager-chocolatey-dark.png");
            window.RootElement.RequestedTheme = ElementTheme.Light;
            await Task.Delay(150);
            ToastMessageService pageToasts = services.GetRequiredService<ToastMessageService>();
            foreach (ToastMessageItem toast in pageToasts.Messages.ToArray()) pageToasts.Dismiss(toast);
            await Task.Delay(80);
            PackageManagerSettingsFieldViewModel? chocolateyExecutable = packageManagers.ViewModel.SelectedAdvancedFields.FirstOrDefault(field => field.Id == "executablePath");
            Check(chocolateyExecutable?.IsPath == true && chocolateyExecutable.PathKind == "file"
                && packageManagers.ViewModel.SelectedInstallFields.Count(field => field.Id is "customArguments" or "updateArguments" or "uninstallArguments") == 3,
                "Gerenciadores: Chocolatey usa seletor de executável tipado e argumentos por operação");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 920));
            ((ScrollViewer)packageManagers.FindName("ManagerSettingsScrollViewer")!).ChangeView(0, 0, null, true);
            await Task.Delay(180);
            await CaptureAsync(window.RootElement, "package-manager-chocolatey-wide-light.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 720));
            await Task.Delay(180);
            await CaptureAsync(window.RootElement, "package-manager-chocolatey-compact-light.png");
            await CaptureAsync(window.RootElement, "package-manager-settings-top-light.png");
            ((ScrollViewer)packageManagers.FindName("ManagerSettingsScrollViewer")!).ChangeView(null,
                ((ScrollViewer)packageManagers.FindName("ManagerSettingsScrollViewer")!).ScrollableHeight, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "package-manager-settings-bottom-light.png");

            packageManagers.ViewModel.ManagerRows.First(row => row.Id == "winget").OpenDetailsCommand.Execute(null);
            DateTime wingetDetailsDeadline = DateTime.UtcNow.AddSeconds(15);
            while ((packageManagers.ViewModel.SelectedManager?.Id != "winget"
                || packageManagers.ViewModel.SelectedInstallControlFields.Count < 2
                || packageManagers.ViewModel.SelectedAdvancedFields.Count == 0
                || !packageManagers.ViewModel.SelectedSourceActions.Any(action => action.Id == "resetSources")
                || !packageManagers.ViewModel.CanResetSources)
                && DateTime.UtcNow < wingetDetailsDeadline)
                await Task.Delay(100);
            PackageManagerSettingsFieldViewModel? architectureField = packageManagers.ViewModel.SelectedInstallControlFields.FirstOrDefault(field => field.Id == "architecture");
            PackageManagerSettingsFieldViewModel? scopeField = packageManagers.ViewModel.SelectedInstallControlFields.FirstOrDefault(field => field.Id == "scope");
            PackageManagerSettingsFieldViewModel? locationField = packageManagers.ViewModel.SelectedInstallControlFields.FirstOrDefault(field => field.Id == "installLocation");
            PackageManagerSettingsFieldViewModel? comField = packageManagers.ViewModel.SelectedAdvancedFields.FirstOrDefault(field => field.Id == "comApi");
            var wingetSettingsScroll = (ScrollViewer)packageManagers.FindName("ManagerSettingsScrollViewer")!;
            wingetSettingsScroll.ChangeView(0, 220, null, true);
            await Task.Delay(150);
            Check(architectureField?.Value == "default" && architectureField.Choices.FirstOrDefault()?.Label == "Padrão"
                && scopeField?.Value == "default" && scopeField.Choices.FirstOrDefault()?.Label == "Padrão"
                && locationField?.IsPath == true && locationField.PathKind == "folder"
                && comField?.Value == "cli" && comField.Choices.FirstOrDefault()?.Label == "Padrão",
                $"Gerenciadores: combos WinGet padrão (architecture={architectureField?.Value}; choices={string.Join(',', architectureField?.Choices.Select(choice => $"{choice.Id}:{choice.Label}") ?? [])}; scope={scopeField?.Value}; com={comField?.Value}; comChoices={string.Join(',', comField?.Choices.Select(choice => $"{choice.Id}:{choice.Label}") ?? [])})");
            ComboBox[] managerChoiceBoxes = FindVisualElements<ComboBox>((DependencyObject)packageManagers.FindName("ManagerDetails")!).ToArray();
            Check(managerChoiceBoxes.Any(combo => combo.SelectedItem is PackageManagerSettingsChoiceViewModel choice && choice.Label == "Padrão")
                && managerChoiceBoxes.All(combo => combo.SelectedItem is not PackageManagerSettingsChoiceViewModel choice || choice.Label != "Padrão (automático)"),
                $"Gerenciadores: ComboBox WinGet mantém os itens selecionados e seus rótulos (combos={managerChoiceBoxes.Length})");
            Check(packageManagers.ViewModel.SelectedSourceActions.Any(action => action.Id == "resetSources" && action.RequiresConfirmation)
                && packageManagers.ViewModel.CanResetSources,
                "Gerenciadores: redefinir fontes aparece nas ações da lista com confirmação declarada no modelo");
            Button[] resetSourceActions = FindVisualElements<Button>((DependencyObject)packageManagers.FindName("ManagerDetails")!)
                .Where(button => Equals(button.Tag, "resetSources")).ToArray();
            Check(resetSourceActions.Length == 2
                && resetSourceActions.Any(button => button.Visibility == Visibility.Visible && button.Content is string)
                && resetSourceActions.Any(button => button.Visibility == Visibility.Collapsed),
                "Gerenciadores: ação button de redefinição renderiza como botão comum");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 920));
            wingetSettingsScroll.ChangeView(0, 0, null, true);
            await Task.Delay(180);
            await CaptureAsync(window.RootElement, "package-manager-winget-wide-light.png");
            wingetSettingsScroll.ChangeView(0, 240, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "package-manager-winget-path-wide-light.png");
            wingetSettingsScroll.ChangeView(0, 0, null, true);
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(920, 720));
            await Task.Delay(180);
            await CaptureAsync(window.RootElement, "package-manager-winget-compact-light.png");
            wingetSettingsScroll.ChangeView(0, 330, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "package-manager-winget-path-compact-light.png");
            wingetSettingsScroll.ChangeView(0, 0, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "winget-package-manager-settings-fields-light.png");
            ((ScrollViewer)packageManagers.FindName("ManagerSettingsScrollViewer")!).ChangeView(0, 0, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "winget-package-manager-settings-top-light.png");
            wingetSettingsScroll.ChangeView(null, wingetSettingsScroll.ScrollableHeight, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "winget-package-manager-settings-bottom-light.png");
            wingetSettingsScroll.ChangeView(null, Math.Max(0, wingetSettingsScroll.ScrollableHeight - 160), null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "winget-package-manager-sources-light.png");
            window.RootElement.RequestedTheme = ElementTheme.Dark;
            wingetSettingsScroll.ChangeView(0, 240, null, true);
            await Task.Delay(180);
            await CaptureAsync(window.RootElement, "package-manager-winget-dark.png");
            window.RootElement.RequestedTheme = ElementTheme.Light;
            await Task.Delay(150);
        }
        window.Navigate("operation-history");
        await WaitForPageAsync(window, "operation-history", ElementTheme.Light);
        if (window.CurrentPage is Views.OperationHistoryPage historyPage)
        {
            Check(window.TitleBarSearchIsVisibleForSmoke
                && window.TitleBarSearchBoxForSmoke.PlaceholderText == "Pesquisar histórico de operações"
                && window.IsNavigationSelectionUnchangedForSmoke(null),
                "Histórico: barra global ativa e nenhum item da lateral fica selecionado");
            Check(historyPage.FindName("HistorySearchBox") is null,
                "Histórico: a página não duplica a barra de pesquisa global");
            window.SetTitleBarSearchTextForSmoke("7-Zip");
            Check(historyPage.ViewModel.Rows.Count > 0
                && historyPage.ViewModel.Rows.All(entry => entry.Record.PackageName?.Contains("7-Zip", StringComparison.OrdinalIgnoreCase) == true),
                "Histórico: busca global filtra por aplicativo");
            OperationHistoryRowViewModel historyRow = historyPage.ViewModel.Rows[0];
            Check(historyRow.Record.PackageId == "7zip.7zip" && historyRow.SourceLabel == "winget"
                && historyRow.VersionLabel == "→ 24.09" && historyRow.PackageIdLabel == "7zip.7zip",
                "Histórico: ID, versão e origem das novas operações são exibidos");
            window.SetTitleBarSearchTextForSmoke("Pacote localizado");
            Check(historyPage.ViewModel.Rows.Count > 0, "Histórico: busca global encontra texto da saída");
            window.SetTitleBarSearchTextForSmoke("sem-correspondencia-smoke");
            Check(historyPage.ViewModel.Rows.Count == 0
                && historyPage.ViewModel.EmptyMessage.Contains("Nenhuma operação", StringComparison.Ordinal),
                "Histórico: pesquisa sem resultados apresenta estado vazio");
            window.SetTitleBarSearchTextForSmoke(string.Empty);
            await Task.Delay(200);
            Check(historyPage.ViewModel.Rows.Count > 0 && historyPage.HistoryListForSmoke.ItemsSource is not null,
                "Histórico: linhas registradas são projetadas na tabela");
            await CaptureAsync(window.RootElement, "operation-history-light.png");
            window.RootElement.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "operation-history-dark.png");
            window.RootElement.RequestedTheme = ElementTheme.Light;
            historyPage.HistoryTabsForSmoke.SelectedIndex = 1;
            await historyPage.ViewModel.LoadLogAsync();
            Check(historyPage.ViewModel.LogText.Contains("Operação: Instalar WinGet 7-Zip (24.09) [Bem-sucedida]", StringComparison.Ordinal)
                && historyPage.ViewModel.LogText.Contains("Pacote localizado no catálogo WinGet.", StringComparison.Ordinal)
                && !historyPage.ViewModel.LogText.Contains("schemaVersion", StringComparison.Ordinal)
                && !historyPage.ViewModel.LogText.Contains("\\u00", StringComparison.Ordinal),
                "Histórico: registro mostra saída de operação formatada em português, sem JSON escapado");
            await CaptureAsync(window.RootElement, "operation-history-log-light.png");
            window.RootElement.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "operation-history-log-dark.png");
            window.RootElement.RequestedTheme = ElementTheme.Light;
            string manyHistoryPath = Path.Combine(Path.GetTempPath(), $"history-large-smoke-{Guid.NewGuid():N}.jsonl");
            var manyHistory = new OperationHistoryService(manyHistoryPath);
            for (int index = 0; index < 300; index++)
            {
                var item = new OperationItem($"Pacote de teste {index:D3}", OperationKind.Uninstall)
                {
                    PackageId = $"Test.Package.{index:D3}", ManagerOverride = "winget"
                };
                item.State = OperationState.Running;
                item.AppendLog("Remoção simulada concluída.");
                item.State = OperationState.Completed;
                manyHistory.Record(item);
            }
            await Task.Run(async () =>
            {
                OperationHistoryRecord[] records = manyHistory.Records.ToArray();
                Check(records.Length == 300, "Histórico: fixture contém centenas de operações");
            });
            var largeHistoryViewModel = new OperationHistoryViewModel(manyHistory,
                services.GetRequiredService<IPackageBackend>(), services.GetRequiredService<IPresentationService>(),
                services.GetRequiredService<IDesktopIntegrationService>(), services.GetRequiredService<IClipboardService>(),
                services.GetRequiredService<IAppLog>(), services.GetRequiredService<IUiDispatcher>(),
                services.GetRequiredService<ApplicationPreferencesService>(), services.GetRequiredService<ManagerTaskLogStore>());
            await largeHistoryViewModel.LoadLogAsync();
            Check(largeHistoryViewModel.LogText.Length <= 2 * 1024 * 1024
                && largeHistoryViewModel.LogText.Contains("Pacote de teste 299", StringComparison.Ordinal)
                && !largeHistoryViewModel.LogText.Contains("Pacote de teste 000", StringComparison.Ordinal),
                "Histórico: centenas de operações carregam com limite visual e mais recentes no topo");
            try { Directory.Delete(Path.GetDirectoryName(manyHistoryPath)!, recursive: true); } catch (IOException) { }
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1224, 750));
        }
        window.Navigate("home");
        Check(window.TitleBarSearchIsVisibleForSmoke && window.TitleBarSearchBoxForSmoke.Text.Length == 0,
            "Histórico: ao sair, a busca global volta ao comportamento da tela de pacotes");
        var simulatedBackup = (SmokeAccountBackupService)services.GetRequiredService<IAccountBackupService>();
        simulatedBackup.SimulateConnected = true;
        services.GetRequiredService<AccountSyncViewModel>().Refresh();
        window.Navigate("account");
        await WaitForPageAsync(window, "account", ElementTheme.Light);
        if (window.CurrentPage is Views.AccountSyncPage cloudAccount)
        {
            await cloudAccount.ViewModel.RefreshCloudFilesCommand.ExecuteAsync(null);
            Check(cloudAccount.ViewModel.CloudFiles.Count == 3, "Conta: lista JSON com resumo e arquivo incompatível");
            ((FrameworkElement)cloudAccount.FindName("CloudFilesCard")).StartBringIntoView();
            await Task.Delay(250);
            await CaptureAsync(window.RootElement, "account-cloud-files-light.png");
            var list = (ListView)cloudAccount.FindName("CloudFilesList");
            if (await WaitForCloudExpanderAsync(list, 0) is { } expander)
            {
                expander.IsExpanded = true;
                await Task.Delay(250);
                Check(ContainsText(expander, "7-Zip") && ContainsText(expander, "7zip.7zip")
                    && ContainsText(expander, "VLC") && ContainsText(expander, "VideoLAN.VLC"),
                    "Conta: prévia apresenta aplicativos distintos e seus identificadores");
                await CaptureAsync(window.RootElement, "account-cloud-apps-light.png");
                window.RootElement.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(200);
                await CaptureAsync(window.RootElement, "account-cloud-apps-dark.png");
                window.RootElement.RequestedTheme = ElementTheme.Light;
            }
            else Check(false, "Conta: prévia do backup disponível");

            simulatedBackup.UseMutableBackups = true;
            await cloudAccount.ViewModel.DeleteAllAppGistsCommand.ExecuteAsync(null);
            Check(cloudAccount.ViewModel.CloudFiles.Count == 0,
                "Conta: exclusão atualiza a listagem a partir da nuvem");
            collections.CreateProfile();
            collections.Add(new AppEntry { Id = "Valve.Steam", Name = "Steam", Source = "winget" });
            await cloudAccount.ViewModel.SaveBackupCommand.ExecuteAsync(null);
            Check(simulatedBackup.SavedSnapshot is { Tabs.Count: 1 }
                && simulatedBackup.SavedSnapshot.Tabs[0].Apps.Count == 1
                && simulatedBackup.SavedSnapshot.Tabs[0].Apps[0].Id == "Valve.Steam",
                "Conta: backup contém apenas a coleção selecionada e o aplicativo atual");
            Check(cloudAccount.ViewModel.CloudFiles.Count == 1
                && cloudAccount.ViewModel.CloudFiles[0].GistId == "novo"
                && cloudAccount.ViewModel.CloudFiles[0].Collections[0].Apps[0].Name == "Steam",
                "Conta: lista é atualizada automaticamente após o backup");
            await Task.Delay(250);
            if (await WaitForCloudExpanderAsync(list, 0) is { } updatedExpander)
            {
                updatedExpander.IsExpanded = true;
                await Task.Delay(250);
                Check(ContainsText(updatedExpander, "Steam") && !ContainsText(updatedExpander, "7-Zip"),
                    "Conta: prévia reutilizada mostra o backup novo, sem aplicativos antigos");
                await CaptureAsync(window.RootElement, "account-backup-updated-light.png");
            }
            else Check(false, "Conta: prévia do novo backup disponível");
        }
        simulatedBackup.SimulateConnected = false;
        services.GetRequiredService<AccountSyncViewModel>().Refresh();
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
        window.Navigate("settings");
        await Task.Delay(300);
        if (window.CurrentPage is Views.SettingsPage compactMaintenance)
        {
            compactMaintenance.NavigateToCategory("Maintenance");
            compactMaintenance.ViewModel.ScheduledUpdateFrequency = "Interval";
            await Task.Delay(200);
            await CaptureAsync(window.RootElement, "settings-maintenance-compact.png");
            compactMaintenance.NavigateToCategory("Elevator");
            await Task.Delay(200);
            await CaptureAsync(window.RootElement, "settings-elevator-compact.png");
        }
        window.Navigate("office");
        await Task.Delay(400);
        await CaptureAsync(window.RootElement, "office-compact.png");
        window.Navigate("home");
        await Task.Delay(400);
        await CaptureAsync(window.RootElement, "home-compact.png");
        window.Navigate("collections");
        await Task.Delay(400);
        Check(window.CurrentPage!.ActualWidth > 0, "Coleções: janela compacta permanece utilizável");
        await CaptureAsync(window.RootElement, "collections-compact.png");
        foreach (var (route, imageName) in new[]
        {
            ("updates", "updates-compact.png"),
            ("installed", "installed-compact.png"),
            ("provisioning", "provisioning-compact.png"),
            ("account", "account-compact.png"),
            ("operation-history", "operation-history-compact.png")
        })
        {
            window.Navigate(route);
            await Task.Delay(400);
            Check(window.CurrentPage!.ActualWidth > 0, $"{route}: janela compacta permanece utilizável");
            await CaptureAsync(window.RootElement, imageName);
        }
        await VerifyOperationsPanelAsync(window, services);
        await services.GetRequiredService<PresentationService>().ShowDetailsAsync(app);
        var details = services.GetRequiredService<AppDetailsOverlay>();
        await Task.Delay(300);
        Check(details.IsOpen, "Detalhes: overlay abre");
        var detailsOptions = services.GetRequiredService<AppDetailsViewModel>();
        Check(details.FindName("IgnoreSecurityHashCheckBox") is CheckBox && details.FindName("InstallVersionComboBox") is ComboBox,
            "Detalhes: hash e versões têm controles de instalação");
        await detailsOptions.LoadInstallVersionsAsync(CancellationToken.None);
        detailsOptions.SelectedInstallVersion = "1.2.2";
        detailsOptions.IgnoreSecurityHashCheckBoxIsChecked = true;
        detailsOptions.UpdateInstallCommandPreview();
        Check(detailsOptions.CurrentOptions().Version == "1.2.2" && detailsOptions.CurrentOptions().IgnoreSecurityHash
            && detailsOptions.InstallCommandPreviewTextBox.Contains("--version \"1.2.2\"")
            && detailsOptions.InstallCommandPreviewTextBox.Contains("--ignore-security-hash"),
            "Detalhes: opções selecionadas chegam à instalação e à prévia");
        detailsOptions.SetApp(new AppEntry { Id = "9WZDNCRFJ3TJ", Name = "Aplicativo da Store", Source = "msstore" }, null, null);
        Check(!detailsOptions.CurrentOptions().Interactive && detailsOptions.InstallButtonIsEnabled,
            "Detalhes: Microsoft Store mantém a API COM elegível e a instalação disponível");
        var chocolateyDetailsApp = new AppEntry
        {
            Id = "steamcmd", Name = "SteamCMD", Source = "chocolatey", Version = "3.65.77.95",
            WingetManifestAvailable = true,
            Installers = [new CatalogInstaller { Type = "exe", Url = "https://example.test/setup.exe", Sha256 = "abc" }]
        };
        detailsOptions.SetApp(chocolateyDetailsApp, null, null);
        Check(detailsOptions.PackageManagerText == "Chocolatey: chocolatey"
            && detailsOptions.PackageDetails?.ManifestUrl is null
            && detailsOptions.PackageDetails?.Installer is null
            && !detailsOptions.IsManifestRowVisible
            && !detailsOptions.IsInstallerTypeRowVisible
            && detailsOptions.IsInteractiveOptionVisible
            && !detailsOptions.IsScopeOptionVisible
            && detailsOptions.IsVersionSelectorVisible
            && !detailsOptions.IsCustomVersionOptionVisible
            && detailsOptions.InstallCommandPreviewTextBox.StartsWith("choco.exe install steamcmd", StringComparison.Ordinal),
            "Detalhes: Chocolatey mostra dados e opções do seu próprio gerenciador");
        await detailsOptions.LoadInstallVersionsAsync(CancellationToken.None);
        detailsOptions.SelectedInstallVersion = "1.2.2";
        detailsOptions.UpdateInstallCommandPreview();
        Check(detailsOptions.InstallVersions.Contains("1.2.2")
            && detailsOptions.CurrentOptions().Version == "1.2.2"
            && detailsOptions.InstallCommandPreviewTextBox.Contains("--version 1.2.2", StringComparison.Ordinal),
            "Detalhes: a versão Chocolatey é escolhida na lista e chega ao comando executado");
        await details.ShowAsync(chocolateyDetailsApp);
        await Task.Delay(200);
        await CaptureAsync(window.RootElement, "details-chocolatey.png");
        var detailsScrollViewer = (ScrollViewer)details.FindName("AppDetailsContent")!;
        detailsScrollViewer.ChangeView(null, detailsScrollViewer.ScrollableHeight, null, true);
        await Task.Delay(100);
        detailsScrollViewer.ChangeView(null, 0, null, true);
        Check(detailsOptions.PackageManager.DisplayName == "Chocolatey" && !detailsOptions.IsInstallerUrlRowVisible
            && detailsOptions.IsVersionSelectorVisible && !detailsOptions.IsCustomVersionOptionVisible,
            "Detalhes: Chocolatey permanece íntegro ao rolar e oferece somente a seleção de versões");
        detailsOptions.SetApp(app, null, null);
        var smokeBackend = (SmokePackageBackend)services.GetRequiredService<IPackageBackend>();
        var firstInstall = new TaskCompletionSource<WingetExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInstall = new TaskCompletionSource<WingetExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        smokeBackend.InstallHandler = package => package.Id == "Smoke.First"
            ? firstInstall.Task
            : secondInstall.Task;
        smokeBackend.InstallCount = 0;
        var firstQueuedApp = new AppEntry { Id = "Smoke.First", Name = "Primeiro app", Source = "winget" };
        var secondQueuedApp = new AppEntry { Id = "Smoke.Second", Name = "Segundo app", Source = "winget" };
        detailsOptions.SetApp(firstQueuedApp, null, null);
        Task firstInstallTask = detailsOptions.InstallCommand.ExecuteAsync(null);
        await Task.Yield();
        Check(firstQueuedApp.IsInstalling && detailsOptions.InstallCommand.CanExecute(null),
            "Instalação: comando aceita enfileirar outro pacote enquanto o primeiro está ativo");
        detailsOptions.SetApp(secondQueuedApp, null, null);
        Task secondInstallTask = detailsOptions.InstallCommand.ExecuteAsync(null);
        await Task.Yield();
        Check(smokeBackend.InstallCount == 2 && secondQueuedApp.IsInstalling,
            "Instalação: segundo pacote chega à fila durante a instalação do primeiro");
        firstInstall.SetResult(new WingetExecutionResult { Success = true });
        secondInstall.SetResult(new WingetExecutionResult { Success = true });
        await Task.WhenAll(firstInstallTask, secondInstallTask);
        smokeBackend.InstallHandler = null;
        detailsOptions.IsOptionsBodyPanelVisible = true;
        await Task.Delay(150);
        await CaptureAsync(window.RootElement, "details.png");
        if (details.FindName("AppDetailsContent") is ScrollViewer detailsContent)
        {
            detailsContent.ChangeView(null, 300, null, disableAnimation: true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "details-options.png");
            detailsContent.ChangeView(null, 600, null, disableAnimation: true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, "details-hooks.png");
        }
        details.Close();
        // The close storyboard lasts 150 ms; wait for its completion instead of
        // relying on a fixed delay that can race under slower capture machines.
        for (int attempt = 0; details.IsOpen && attempt < 40; attempt++)
            await Task.Delay(50);
        Check(!details.IsOpen, "Detalhes: overlay fecha e conclui a animação");
        var curated = new AppEntry
        {
            Id = "WinProvision.Store", Name = "WinProvision Store", Source = "winget", CatalogOrigin = "curated",
            Publisher = "WinProvision Services", Author = "WinProvision Services", Version = "0.1.2",
            WingetManifestAvailable = false, ReleaseDate = DateTimeOffset.Parse("2026-10-15T01:53:09Z"),
            Tags = ["office", "cli"], CatalogDetailPath = "manifests/w/winprovision/store/app.01234567.json",
            Media = new CatalogMedia { Screenshots = ["@media/screenshots/screenshot-test.png"] },
            Installers = [new CatalogInstaller { Architecture = "x64", Type = "inno", Url = "https://example.com/setup.exe", Sha256 = new string('a', 64) }]
        };
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
        await Task.Delay(250);
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            window.RootElement.RequestedTheme = theme;
            await details.ShowAsync(curated);
            var model = services.GetRequiredService<AppDetailsViewModel>();
            Check(!model.IsOfficeStoreOffer && model.InstallerTypeText == "Inno Setup", "Detalhes: tag office não transforma aplicativo curado em Microsoft 365");
            Check(model.DeveloperText == "WinProvision Services" && !model.IsManifestRowVisible && !model.IsManifestLinkVisible, "Detalhes: desenvolvedor e manifesto ausente da origem própria");
            Check(model.IsInstallerUrlLinkVisible && model.Sha256Text == new string('A', 64) && model.LastUpdatedText.Contains("2026"), "Detalhes: URL, hash e data vêm do catálogo");
            Check(model.Screenshots.Length == 1, "Detalhes: galeria relativa funciona para pacotes fora da Microsoft Store");
            Check(model.IsSelfPackage && !model.IsInstallSplitGroupVisible && !model.IsUninstallSplitGroupVisible
                && !model.IsUpdateButtonVisible && !model.ShowManagementOptions, "Próprio app: somente adicionar aos Pacotes, sem ações de instalação, remoção ou atualização");
            curated.IsInstalled = false;
            await model.InstallAsync();
            Check(!curated.IsInstalled, "Próprio app: chamada direta de instalação não executa o backend");
            curated.IsInstalled = true;
            await model.UninstallAsync();
            Check(curated.IsInstalled, "Próprio app: chamada direta de remoção não executa o backend");
            model.AddCurrentAppToPackages();
            var selfItem = collections.Items.Single(item => item.Id == curated.Id);
            Check(!selfItem.SupportsInstall && selfItem.SupportsDownload, "Próprio app: permanece nos Pacotes para download e exportação, sem instalação local");
            Check(model.SelfInstalledVersionText.Contains(AppUpdateService.GetCurrentVersion().ToString(3))
                && selfItem.SourceLabel == "Catálogo WinProvision", "Próprio app: versão em execução e origem identificadas");
            await CaptureAsync(window.RootElement, $"details-curated-{theme}.png");
            details.Close();
            await Task.Delay(250);
        }
        foreach (var item in collections.Items.ToArray()) item.IsSelected = item.App.IsSelf;
        await collections.InstallSelectedWithOptionsAsync(new PackageInstallOptions());
        Check(collections.StatusText.Contains("ignorado: aplicativo em execução"), "Pacotes: lote informa por que o próprio app foi ignorado");
        var detailsModel = services.GetRequiredService<AppDetailsViewModel>();
        detailsModel.OpenAppUpdatesCommand.Execute(null);
        await Task.Delay(250);
        Check(window.CurrentPage is Views.SettingsPage, "Próprio app: atualizações abrem as Configurações sem iniciar instalação");
        string automaticCheckPreferencesPath = Path.Combine(
            Path.GetTempPath(), $"winprovision-auto-check-{Guid.NewGuid():N}.json");
        try
        {
            var automaticCheckPreferences = new ApplicationPreferencesService(
                automaticCheckPreferencesPath, persistStartup: false);
            automaticCheckPreferences.SetAutomaticAppUpdateChecksEnabled(false);
            var automaticCheckStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int automaticCheckCount = 0;
            var automaticCheckService = new AutomaticAppUpdateCheckService(
                automaticCheckPreferences,
                services.GetRequiredService<ToastMessageService>(),
                async (token, _) =>
                {
                    Interlocked.Increment(ref automaticCheckCount);
                    automaticCheckStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return AppUpdateCheckResult.Failed("A verificação de teste foi cancelada.");
                });
            using var automaticCheckCancellation = new CancellationTokenSource();
            Task automaticCheckRun = automaticCheckService.RunAsync(automaticCheckCancellation.Token);
            await Task.Delay(50);
            Check(automaticCheckCount == 0,
                "Atualização automática: preferência desligada impede iniciar verificações");
            automaticCheckPreferences.SetAutomaticAppUpdateChecksEnabled(true);
            await automaticCheckStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            automaticCheckPreferences.SetAutomaticAppUpdateChecksEnabled(false);
            await Task.Delay(50);
            automaticCheckCancellation.Cancel();
            try
            {
                await automaticCheckRun;
            }
            catch (OperationCanceledException)
            {
            }
            Check(automaticCheckCount == 1 && automaticCheckPreferences.LastAppUpdateCheckUtc is null,
                "Atualização automática: desligar cancela a verificação em andamento sem marcar sucesso");
        }
        finally
        {
            if (File.Exists(automaticCheckPreferencesPath))
                File.Delete(automaticCheckPreferencesPath);
        }
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            window.RootElement.RequestedTheme = theme;
            var settingsPage = (Views.SettingsPage)window.CurrentPage!;
            settingsPage.NavigateToCategory("General");
            SettingsCatalogGroupOption[] generalGroups = settingsPage.ViewModel.GeneralSettingsGroups.ToArray();
            SettingsCatalogSettingOption updatePreference = generalGroups
                .SelectMany(group => group.Settings)
                .Single(setting => setting.Definition.RequiresUpdateCapability);
            Check(generalGroups.Length == 3 && updatePreference.IsToggleCard
                && updatePreference.HasAuxiliaryAction
                && updatePreference.IsEnabled
                && ReferenceEquals(updatePreference.ActionCommand, settingsPage.ViewModel.UpdateState switch
                {
                    AppUpdateWorkflowState.UpdateAvailable when services.GetRequiredService<AppUpdateService>().CanSelfUpdate
                        => settingsPage.ViewModel.DownloadUpdateCommand,
                    AppUpdateWorkflowState.UpdateAvailable => settingsPage.ViewModel.OpenReleaseCommand,
                    AppUpdateWorkflowState.Downloading => settingsPage.ViewModel.DownloadUpdateCommand,
                    AppUpdateWorkflowState.ReadyToApply or AppUpdateWorkflowState.Applying
                        => settingsPage.ViewModel.ApplyUpdateCommand,
                    _ => settingsPage.ViewModel.CheckUpdateCommand
                }),
                $"Preferências gerais são projetadas pelo catálogo e têm verificação integrada em {theme}");
            Check(generalGroups.SelectMany(group => group.Settings).Count(setting => setting.IsNavigation) == 2,
                $"Links relacionados são gerados pelo catálogo em {theme}");
            Check(updatePreference.Description.Split("Versão", StringSplitOptions.None).Length - 1 == 1
                && updatePreference.HasCancelableOperation == settingsPage.ViewModel.HasCancelableUpdateOperation,
                $"Estado e descrição do atualizador acompanham a operação sem duplicação em {theme}");
            Check(generalGroups.SelectMany(group => group.Settings)
                    .All(setting => setting.Definition.ActionKey != "openLogs")
                && settingsPage.ViewModel.OpenLogsCommand is not null,
                "Preferências gerais não expõem Abrir pasta de logs e preservam o comando compartilhado");
            settingsPage.ViewModel.SearchText = "Abrir pasta de logs";
            Check(settingsPage.ViewModel.SearchResults.Count == 0,
                "Pesquisar configurações não encontra mais Abrir pasta de logs");
            settingsPage.ViewModel.SearchText = string.Empty;
            var syntheticNavigation = new SettingsCatalogSettingOption(
                new ElevationSettingDefinition("invented-navigation", "synthetic-group", "navigation", "normal",
                    default, null, null, true, false, "title", "description", null, "navigation",
                    TargetPage: "SyntheticDestination"),
                "Destino sintético", "Descrição", null, "", new Dictionary<string, string>());
            Check(syntheticNavigation.IsNavigation,
                "Itens de navegação sintéticos são identificados por kind, control e destino, não pela chave");
            Check(string.IsNullOrEmpty(settingsPage.ViewModel.StatusText),
                $"Status de outra categoria não aparece em Preferências gerais em {theme}");
            Check(ReferenceEquals(((Button)settingsPage.FindName("AuthorizeElevationButton")).Command,
                settingsPage.ViewModel.AuthorizeElevationCommand), "Elevação: nova autorização por sessão disponível");
            Check(WinProvision.Elevation.GsudoCacheController.IdleDurationMinutes == 5, "Elevação: cache fixo de 5 minutos por padrão");
            if (settingsPage.ViewModel.HasCancelableUpdateOperation)
                settingsPage.ViewModel.CancelUpdateCommand.Execute(null);
            await Task.Delay(250);
            ToastMessageService generalPageToasts = services.GetRequiredService<ToastMessageService>();
            while (generalPageToasts.Messages.Count > 0)
                generalPageToasts.Dismiss(generalPageToasts.Messages[0]);
            await CaptureAsync(window.RootElement, $"settings-general-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            await Task.Delay(250);
            await CaptureAsync(window.RootElement, $"settings-general-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            settingsPage.NavigateToCategory("Developer");
            await Task.Delay(180);
            var engineSelector = (ComboBox)settingsPage.FindName("WinGetEngineSelector");
            string[] engineChoices = engineSelector.Items.Cast<ComboBoxItem>()
                .Select(item => item.Tag?.ToString() ?? string.Empty).ToArray();
            Check(engineChoices.SequenceEqual(new[] { "Automatic", "ComApi", "WingetCli" })
                && Equals(engineSelector.SelectedValue, settingsPage.ViewModel.SelectedWinGetEngineName)
                && engineSelector.SelectedValue is "Automatic" or "ComApi" or "WingetCli",
                $"WinGet: seletor de mecanismo com valor conservador persistido ({engineSelector.SelectedValue})");
            await CaptureAsync(window.RootElement, $"settings-developer-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            await Task.Delay(250);
            await CaptureAsync(window.RootElement, $"settings-developer-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            await Task.Delay(250);
            settingsPage.NavigateToCategory("Interface");
            await CaptureAsync(window.RootElement, $"settings-startup-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            await Task.Delay(250);
            await CaptureAsync(window.RootElement, $"settings-startup-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            await Task.Delay(250);
            var operationPreferences = services.GetRequiredService<OperationPreferencesService>();
            operationPreferences.Save(new());
            settingsPage.NavigateToCategory("Notifications");
            var settingsModel = settingsPage.ViewModel;
            settingsModel.NotificationLevel = nameof(NotificationLevel.ErrorsOnly);
            settingsModel.BackgroundWindowsNotificationsEnabled = false;
            ((Button)settingsPage.FindName("SaveNotificationCategoriesButton")).Command.Execute(null);
            Check(operationPreferences.Load().NotificationLevel == NotificationLevel.ErrorsOnly
                && !operationPreferences.Load().BackgroundWindowsNotificationsEnabled,
                $"Notificações: gravidade e canal Windows salvos por binding em {theme}");
            await CaptureAsync(window.RootElement, $"settings-notifications-{theme}.png");
            settingsPage.NavigateToCategory("Execution");
            Check(settingsModel.OperationSettingsGroups.Count == 3
                && settingsModel.OperationSettingsGroups.SelectMany(group => group.Settings)
                    .Any(option => option.Key == "parallelDownloads" && option.Options.Any(choice => choice.Value == "3" && choice.Label.Contains("padrão", StringComparison.Ordinal))),
                $"Operações: grupos, controles e opção padrão derivados do catálogo em {theme}");
            settingsModel.SearchText = "downloads avulsos";
            Check(settingsModel.SearchResults.Any(result => result.Key == "parallelDownloads")
                && !settingsModel.SearchResults.Any(result => result.Title.Contains("Office", StringComparison.OrdinalIgnoreCase)),
                $"Pesquisa: catálogo encontra controles novos e não inclui Office removido em {theme}");
            settingsModel.SearchText = "fontes winget";
            Check(settingsModel.SearchResults.Any(result => result.CategoryKey == "PackageManagers"),
                $"Pesquisa: fontes direcionam para Gerenciadores de pacotes em {theme}");
            settingsModel.SearchText = string.Empty;
            var officePreferencesService = services.GetRequiredService<ApplicationPreferencesService>();
            officePreferencesService.SetUseViperOfficeDownload(true);
            Check(services.GetRequiredService<OfficeViewModel>().CreateInstallRequest()?.UseViperDownload == true,
                $"Office: preferência preservada segue aplicada no serviço em {theme}");
            officePreferencesService.SetUseViperOfficeDownload(false);
            SettingsCatalogSettingOption parallelOperations = settingsModel.OperationSettingsGroups
                .SelectMany(group => group.Settings).Single(option => option.Key == "parallelOperations");
            SettingsCatalogSettingOption parallelDownloads = settingsModel.OperationSettingsGroups
                .SelectMany(group => group.Settings).Single(option => option.Key == "parallelDownloads");
            SettingsCatalogSettingOption installerNames = settingsModel.OperationSettingsGroups
                .SelectMany(group => group.Settings).Single(option => option.Key == "installerNames");
            SettingsCatalogSettingOption downloadFolder = settingsModel.OperationSettingsGroups
                .SelectMany(group => group.Settings).Single(option => option.Key == "downloadDirectory");
            SettingsCatalogSettingOption domainWarning = settingsModel.OperationSettingsGroups
                .SelectMany(group => group.Settings).Single(option => option.Key == "warnInstallerHostChange");
            await settingsModel.ApplyCatalogSettingAsync(parallelOperations, "2");
            await settingsModel.ApplyCatalogSettingAsync(parallelDownloads, "4");
            await settingsModel.ApplyCatalogSettingAsync(installerNames, "2");
            string downloadPath = Path.Combine(OutputDirectory, "downloads-test");
            Directory.CreateDirectory(downloadPath);
            await settingsModel.ApplyCatalogSettingAsync(downloadFolder, downloadPath);
            Check(operationPreferences.Load().ParallelOperations == 2 && operationPreferences.Load().ParallelDownloads == 4
                && operationPreferences.Load().InstallerNames == InstallerNameStyle.PackageVersionArchitecture,
                $"Operações: limites e formato aplicados imediatamente a partir do catálogo em {theme}");
            Check(operationPreferences.Load().DownloadDirectory == downloadPath,
                $"Downloads: pasta padrão aplicada imediatamente em {theme}");
            Check(operationPreferences.Load().NotificationLevel == NotificationLevel.ErrorsOnly,
                $"Operações: preferências de notificações preservadas ao gravar um controle do catálogo em {theme}");
            await settingsModel.ApplyCatalogSettingAsync(parallelOperations, "0");
            Check(operationPreferences.Load().ParallelOperations == 2 && parallelOperations.Value == "2",
                $"Operações: opção inexistente mantém valor aplicado em {theme}");
            string previousFolder = downloadFolder.Value;
            await settingsModel.ApplyCatalogSettingAsync(downloadFolder, Path.Combine(OutputDirectory, "missing-folder"));
            Check(downloadFolder.Value == previousFolder && operationPreferences.Load().DownloadDirectory == downloadPath,
                $"Downloads: pasta inexistente rejeitada sem perder o valor anterior em {theme}");
            SmokePresentationService presentation = services.GetRequiredService<IPresentationService>() as SmokePresentationService
                ?? throw new InvalidOperationException("O serviço de apresentação de teste não foi registrado.");
            var delayedFolderPicker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            presentation.FolderTask = delayedFolderPicker.Task;
            using (var folderCancellation = new CancellationTokenSource())
            {
                Task pendingSelection = settingsModel.PickCatalogFolderAsync(downloadFolder, folderCancellation.Token);
                folderCancellation.Cancel();
                await pendingSelection;
            }
            delayedFolderPicker.SetResult(Path.Combine(OutputDirectory, "late-folder"));
            presentation.FolderTask = null;
            Check(downloadFolder.Value == previousFolder && operationPreferences.Load().DownloadDirectory == downloadPath,
                $"Downloads: seleção concluída após cancelamento da página não altera a pasta aplicada em {theme}");
            int confirmations = presentation.ConfirmCallCount;
            await settingsModel.ApplyCatalogSettingAsync(domainWarning, "false");
            Check(operationPreferences.Load().WarnInstallerHostChange == false
                && presentation.ConfirmCallCount == confirmations + 1
                && presentation.LastConfirmMessage?.Contains("reduz", StringComparison.OrdinalIgnoreCase) == true,
                $"Segurança: desligar aviso de domínio confirma uma única vez em {theme}");
            await settingsModel.ApplyCatalogSettingAsync(domainWarning, "true");
            Check(operationPreferences.Load().WarnInstallerHostChange
                && !domainWarning.RiskWarning!.Contains("Office", StringComparison.OrdinalIgnoreCase),
                $"Segurança: aviso volta a ficar ativo e permanece neutro em {theme}");
            using (new FileStream(Path.Combine(OutputDirectory, "operation-preferences-test.json"),
                       FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                await settingsModel.ApplyCatalogSettingAsync(parallelOperations, "3");
            Check(parallelOperations.Value == "2" && operationPreferences.Load().ParallelOperations == 2,
                $"Operações: falha de gravação reverte controle e mantém valor anterior em {theme}");
            string portablePath = Path.Combine(OutputDirectory, "portable-settings-test.json");
            presentation.SavePath = portablePath;
            await settingsModel.ExportSettingsCommand.ExecuteAsync(null);
            string portableJson = await File.ReadAllTextAsync(portablePath);
            using (JsonDocument portableDocument = JsonDocument.Parse(portableJson))
                Check(portableDocument.RootElement.TryGetProperty("Operations", out _)
                    && portableDocument.RootElement.TryGetProperty("Application", out _)
                    && !portableJson.Contains("credential", StringComparison.OrdinalIgnoreCase)
                    && !portableJson.Contains("collection", StringComparison.OrdinalIgnoreCase)
                    && !portableJson.Contains("account", StringComparison.OrdinalIgnoreCase),
                    $"Portabilidade: arquivo inclui preferências e exclui credenciais, coleções e conta em {theme}");
            presentation.OpenPath = portablePath;
            int importConfirmations = presentation.ConfirmCallCount;
            await settingsModel.ImportSettingsCommand.ExecuteAsync(null);
            Check(presentation.ConfirmCallCount == importConfirmations + 1
                && presentation.LastConfirmMessage?.Contains("inicialização do Windows", StringComparison.OrdinalIgnoreCase) == true
                && operationPreferences.Load().ParallelOperations == 2,
                $"Portabilidade: importação confirma uma vez, mostra o escopo e preserva valores em {theme}");
            settingsModel.LoadExecutionOptions();
            await CaptureAsync(window.RootElement, $"settings-execution-{theme}.png");
            var executionScroll = (ScrollViewer)settingsPage.FindName("HomepageScrollViewer");
            executionScroll.ChangeView(null, executionScroll.ScrollableHeight, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, $"settings-downloads-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            await Task.Delay(200);
            executionScroll.ChangeView(null, executionScroll.ScrollableHeight, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, $"settings-downloads-compact-{theme}.png");
            settingsPage.NavigateToCategory("Notifications");
            await CaptureAsync(window.RootElement, $"settings-notifications-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            await Task.Delay(150);
            operationPreferences.Save(new());
            var cacheSettings = (SmokeCacheSettings)services.GetRequiredService<ICacheSettingsService>();
            int cacheMeasurements = cacheSettings.MeasureCallCount;
            await settingsPage.NavigateToCategoryAsync("Operations");
            Check(cacheSettings.MeasureCallCount > cacheMeasurements,
                $"Cache: medição assíncrona atualizada ao reabrir a categoria em {theme}");
            Check(ReferenceEquals(((Button)settingsPage.FindName("RefreshCatalogButton")).Command,
                settingsPage.ViewModel.RefreshCatalogCommand), "Catálogo: atualização manual separada da limpeza de cache");
            cacheSettings.Reset();
            settingsPage.ViewModel.LoadCacheOptions();
            SettingsCatalogSettingOption CacheSetting(string key) =>
                settingsPage.ViewModel.CacheSettingsGroups.SelectMany(group => group.Settings)
                    .Single(option => option.Key == key);
            SettingsCatalogSettingOption imageLimit = CacheSetting("image-cache-limit");
            SettingsCatalogSettingOption imageRevalidation = CacheSetting("image-cache-revalidate");
            Check(imageLimit.IsCombo && imageRevalidation.IsCombo,
                $"Cache: combos compactos gerados pelo catálogo em {theme}");
            Check(imageLimit.Options.Any(option => option.Value == imageLimit.Value
                    && option.Label.Contains("(padrão)", StringComparison.Ordinal))
                && imageRevalidation.Options.Any(option => option.Value == imageRevalidation.Value
                    && option.Label.Contains("(padrão)", StringComparison.Ordinal)),
                $"Cache: as opções padrão são identificadas no catálogo em {theme}");
            await settingsPage.ViewModel.ApplyCatalogSettingAsync(imageLimit, "256");
            await settingsPage.ViewModel.ApplyCatalogSettingAsync(imageRevalidation, "7");
            Check(cacheSettings.Options == new ImageCacheOptions(256, 7),
                $"Cache: cada escolha é aplicada imediatamente em {theme}");
            cacheSettings.FailNextSave = true;
            await settingsPage.ViewModel.ApplyCatalogSettingAsync(imageLimit, "128");
            Check(cacheSettings.Options == new ImageCacheOptions(256, 7) && imageLimit.Value == "256",
                $"Cache: falha restaura valor e preferência anterior em {theme}");
            await settingsPage.ViewModel.MeasureCacheCommand.ExecuteAsync(null);
            Check(settingsPage.ViewModel.ImagesCacheUsage.Contains("120"), $"Cache: medição de imagens exibida em {theme}");
            Check(CacheSetting("image-cache-clear").Description.Contains("O cache de imagens ocupa", StringComparison.Ordinal)
                && CacheSetting("installer-cache-clear").Description.Contains("arquivos", StringComparison.Ordinal),
                $"Cache: descrições de medição atualizadas por provedores do catálogo em {theme}");
            settingsPage.ViewModel.SearchText = "revalidar";
            Check(settingsPage.ViewModel.SearchResults.Any(result => result.Key == "image-cache-revalidate"),
                $"Cache: pesquisa deriva do catálogo em {theme}");
            settingsPage.ViewModel.SearchText = "logs";
            Check(!settingsPage.ViewModel.SearchResults.Any(result => result.Title.Contains("log", StringComparison.OrdinalIgnoreCase)),
                $"Cache: pesquisa não indexa controles de registros removidos da página em {theme}");
            settingsPage.ViewModel.SearchText = string.Empty;
            await CaptureAsync(window.RootElement, $"settings-catalog-{theme}.png");
            var cacheScroll = (ScrollViewer)settingsPage.FindName("HomepageScrollViewer");
            cacheScroll.ChangeView(null, cacheScroll.ScrollableHeight, null, true);
            await Task.Delay(150);
            await CaptureAsync(window.RootElement, $"settings-cache-options-{theme}.png");
            await settingsPage.ViewModel.ClearImagesCacheCommand.ExecuteAsync(null);
            Check(cacheSettings.Images.Bytes == 0 && cacheSettings.Catalog.Bytes > 0 && cacheSettings.Installers.Bytes > 0,
                $"Cache: limpeza de imagens preserva catálogo e instaladores em {theme}");
            Check(settingsPage.ViewModel.HasImageCacheClearNotice,
                $"Cache: aviso de recarga aparece somente após a limpeza em {theme}");
            await settingsPage.ViewModel.ClearCatalogCacheCommand.ExecuteAsync(null);
            Check(cacheSettings.Catalog.Bytes == 0 && cacheSettings.Installers.Bytes > 0,
                $"Cache: limpeza de catálogo preserva instaladores em {theme}");
            long installerBytesBeforeCancel = cacheSettings.Installers.Bytes;
            int installerConfirmations = presentation.ConfirmCallCount;
            presentation.ConfirmResult = false;
            await settingsPage.ViewModel.ClearInstallerCacheCommand.ExecuteAsync(null);
            Check(presentation.ConfirmCallCount == installerConfirmations + 1
                && presentation.LastConfirmMessage?.Contains("256,0 MB", StringComparison.Ordinal) == true
                && cacheSettings.Installers.Bytes == installerBytesBeforeCancel,
                $"Cache: confirmação informa o tamanho e permite cancelar a limpeza em {theme}");
            presentation.ConfirmResult = true;
            await settingsPage.ViewModel.ClearInstallerCacheCommand.ExecuteAsync(null);
            Check(cacheSettings.Installers.Bytes == 0,
                $"Cache: limpeza de instaladores disponível após confirmação em {theme}");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            cacheScroll.ChangeView(null, 0, null, true);
            await Task.Delay(200);
            await CaptureAsync(window.RootElement, $"settings-cache-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
        }
        var host = new ContentControl();
        FluentMotion.SetKind(host, MotionKind.Content);
        Check(host.ContentTransitions.Count == (FluentMotion.AnimationsEnabled ? 1 : 0), "Movimento: preferência do Windows respeitada");
        FluentMotion.SetKind(host, MotionKind.None);
        Check(host.ContentTransitions.Count == 0, "Movimento: transição pode ser desativada");
        Check(BindingErrors.Count == 0, "Bindings: nenhuma falha de resolução");
        WriteResult(true);
    }

    private static void VerifyAppPreferencesContractFixture()
    {
        string path = Path.Combine(Path.GetTempPath(), "winprovision-app-preferences-contract-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using Stream fixture = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinProvision.Store.Validation.app-preferences-pre-refactor.json")
                ?? throw new InvalidOperationException("Fixture de app-preferences anterior ao refactor ausente.");
            using (var target = File.Create(path)) fixture.CopyTo(target);
            var preferences = new ApplicationPreferencesService(path, persistStartup: false);
            Check(preferences.HasSavedOperationsPanelHeight && preferences.OperationsPanelHeight == 333
                && preferences.HasSavedOperationsPanelExpanded && !preferences.OperationsPanelExpanded,
                "Preferências: fixture anterior ao refactor restaura altura e estado do painel");
            preferences.SetOperationsPanelState(333, expanded: false);
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            Check(saved.RootElement.GetProperty("OperationsPanelHeight").GetDouble() == 333
                && !saved.RootElement.GetProperty("OperationsPanelExpanded").GetBoolean()
                && !saved.RootElement.TryGetProperty("operationsPanelHeight", out _),
                "Preferências: formato persistido de app-preferences.json permanece inalterado");
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static async Task VerifyOperationsPanelAsync(MainWindow window, IServiceProvider services)
    {
        var queue = services.GetRequiredService<OperationsQueueService>();
        var queueModel = services.GetRequiredService<OperationsViewModel>();
        var operation = queue.Enqueue("Atualização simulada 1", OperationKind.Update);
        var operations = new List<OperationItem> { operation };
        for (int index = 2; index <= 4; index++)
            operations.Add(queue.Enqueue($"Atualização simulada {index}", OperationKind.Update));
        operation.State = OperationState.Running;
        operation.Method = WingetMethod.OwnApi;
        operation.IsIndeterminate = false;
        operation.Progress = 42.5;
        operation.LiveLine = "Baixando... 42,50%";
        var queuePanel = (OperationsQueuePanel)window.RootElement.FindName("QueuePanel");
        await Task.Delay(150);
        Check(queueModel.Items.Count == 4
            && queueModel.Items[1].QueueStatusText == "Operação na fila (posição 1)..."
            && queueModel.Items[2].QueueStatusText == "Operação na fila (posição 2)..."
            && queueModel.Items[3].QueueStatusText == "Operação na fila (posição 3)...",
            "Operações: quatro atualizações exibem uma em execução e três posições de fila corretas");
        await VerifyRenderedOperationMenuAsync(queuePanel, queueModel, operation,
            "item:Running:Update:manager=none:path=False:cancel=True:elevated=False:interactive=False");
        await VerifyRenderedHeaderMenuAsync(queuePanel, queueModel,
            "header:failed=False:succeeded=False:finished=False:cancelable=True");
        operation.State = OperationState.Completed;
        await Task.Delay(120);
        await VerifyRenderedOperationMenuAsync(queuePanel, queueModel, operation,
            "item:Completed:Update:manager=none:path=False:cancel=False:elevated=False:interactive=False");
        operation.State = OperationState.Failed;
        await Task.Delay(120);
        await VerifyRenderedOperationMenuAsync(queuePanel, queueModel, operation,
            "item:Failed:Update:manager=none:path=False:cancel=False:elevated=False:interactive=False");
        operation.State = OperationState.Running;
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            window.RootElement.RequestedTheme = theme;
            await Task.Delay(350);
            Check(queuePanel.Visibility == Visibility.Visible && queuePanel.IsLoaded,
                $"Operações: painel encaixado visível no tema {theme}");
            Check(ReferenceEquals(queuePanel.DataContext, queueModel) && queuePanel.ActualTheme == theme,
                $"Operações: estado e tema preservados em {theme}");
            var progressBar = FindOperationProgress(queuePanel, operation.Id);
            Check(progressBar is { IsIndeterminate: false, Value: 42.5 },
                $"Operações: barra exibe o percentual determinado em {theme}");
            if (progressBar is not null && VisualTreeHelper.GetParent(progressBar) is DependencyObject descriptionContent
                && VisualTreeHelper.GetParent(descriptionContent) is FrameworkElement descriptionButton)
            {
                var messageWidth = descriptionButton.ActualWidth;
                operation.LiveLine = "Aguardando o WinGet...";
                await Task.Delay(50);
                var shortTextWidth = FindOperationProgress(queuePanel, operation.Id)?.ActualWidth ?? 0;
                operation.LiveLine = new string('X', 500);
                await Task.Delay(50);
                var longTextWidth = FindOperationProgress(queuePanel, operation.Id)?.ActualWidth ?? 0;
                Check(Math.Abs(shortTextWidth - messageWidth) < 1 && Math.Abs(longTextWidth - messageWidth) < 1,
                    $"Operações: barra acompanha toda a área de mensagem com texto curto e longo em {theme}");
                operation.LiveLine = "Baixando... 42,50%";
                await Task.Delay(50);
            }
            else Check(false, $"Operações: contêiner da barra e área de mensagem disponíveis em {theme}");
            Check(progressBar?.Foreground is SolidColorBrush runningBrush
                && runningBrush.Color == Windows.UI.Color.FromArgb(255, 0, 120, 212),
                $"Operações: execução usa a cor de destaque em {theme}");
            Check(queuePanel.ActualWidth > 500 && queuePanel.ActualHeight >= 200 && queuePanel.ItemsControl.Items.Count == 4,
                $"Operações: painel ocupa a área prevista em {theme} (largura={queuePanel.ActualWidth}, altura={queuePanel.ActualHeight}, itens={queuePanel.ItemsControl.Items.Count})");
            var panelOrigin = queuePanel.TransformToVisual(window.RootElement).TransformPoint(new Windows.Foundation.Point(0, 0));
            var pageOrigin = window.RootElement.FindName("PageHost") is FrameworkElement pageHost
                ? pageHost.TransformToVisual(window.RootElement).TransformPoint(new Windows.Foundation.Point(0, 0)) : default;
            Check(panelOrigin.Y >= pageOrigin.Y + ((FrameworkElement)window.RootElement.FindName("PageHost")).ActualHeight,
                $"Operações: conteúdo termina acima do painel encaixado em {theme}");
            await CaptureAsync(queuePanel, $"operations-{theme}.png");
            await CaptureAsync(window.RootElement, $"operations-docked-window-{theme}.png");
        }

        // Baseline visual persistente anterior à migração dos textos do painel.
        // Gerar com WINPROVISION_CAPTURE_OPERATION_BASELINE=1 e copiar os arquivos de smoke-results.
        if (Environment.GetEnvironmentVariable("WINPROVISION_CAPTURE_OPERATION_BASELINE") == "1")
        {
            OperationItem[] baselineOperations = operations.Take(4).ToArray();
            string[] baselineManagers = baselineOperations.Select(item => item.Manager).ToArray();
            foreach (OperationItem baselineItem in baselineOperations) baselineItem.Manager = "unknown";
            OperationItem indeterminate = queue.Enqueue("Instalação simulada", OperationKind.Install);
            indeterminate.Manager = "unknown";
            indeterminate.State = OperationState.Running;
            indeterminate.IsIndeterminate = true;
            indeterminate.LiveLine = "Aguardando o WinGet...";
            OperationItem completed = queue.Enqueue("Atualização concluída", OperationKind.Update);
            completed.Manager = "unknown";
            completed.State = OperationState.Completed;
            OperationItem failed = queue.Enqueue("Remoção com falha", OperationKind.Uninstall);
            failed.Manager = "unknown";
            failed.State = OperationState.Failed;
            OperationItem canceled = queue.Enqueue("Download cancelado", OperationKind.Download);
            canceled.Manager = "unknown";
            canceled.State = OperationState.Canceled;
            operations.AddRange(new[] { indeterminate, completed, failed, canceled });
            await Task.Delay(150);
            string baseline = Path.Combine(OutputDirectory, "operations-baseline");
            Directory.CreateDirectory(baseline);
            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                window.RootElement.RequestedTheme = theme;
                queuePanel.RequestedTheme = theme;
                await Task.Delay(350);
                queueModel.IsQueueExpanded = true;
                await CaptureOperationsFixtureAsync(queuePanel, $"operations-{theme}-states-expanded.png", baseline);
                queueModel.IsQueueExpanded = false;
                await Task.Delay(100);
                await CaptureOperationsFixtureAsync(queuePanel, $"operations-{theme}-states-collapsed.png", baseline);
                queueModel.IsQueueExpanded = true;
                completed.State = OperationState.Running;
                await Task.Delay(100);
                await CaptureOperationsFixtureAsync(queuePanel, $"operations-{theme}-completed-0.png", baseline);
                completed.State = OperationState.Completed;
                await Task.Delay(100);
                await CaptureOperationsFixtureAsync(queuePanel, $"operations-{theme}-completed-1.png", baseline);
                failed.State = OperationState.Completed;
                canceled.State = OperationState.Completed;
                await Task.Delay(100);
                await CaptureOperationsFixtureAsync(queuePanel, $"operations-{theme}-completed-many.png", baseline);
                queueModel.IsQueueExpanded = true;
                foreach (var (stateName, state) in new[]
                         {
                             ("queued", OperationState.Queued), ("indeterminate", OperationState.Running),
                             ("percent", OperationState.Running), ("completed", OperationState.Completed),
                             ("failed", OperationState.Failed), ("canceled", OperationState.Canceled)
                         })
                {
                    OperationItem rowOperation = stateName switch
                    {
                        "queued" => operations[1], "indeterminate" => indeterminate, "percent" => operation,
                        "completed" => completed, "failed" => failed, _ => canceled
                    };
                    rowOperation.State = state;
                    // The persisted pre-refactor baseline captured Queued with the item's default 0% determinate value.
                    rowOperation.IsIndeterminate = stateName == "indeterminate";
                    rowOperation.Progress = stateName == "percent" ? 42.5 : 0;
                    rowOperation.LiveLine = stateName switch
                    {
                        "queued" => "Na fila", "indeterminate" => "Aguardando o WinGet...",
                        "percent" => "Baixando... 42,50%", "completed" => "Concluído",
                        "failed" => "Falhou", _ => "Cancelado"
                    };
                    await Task.Delay(80);
                    FrameworkElement? row = FindOperationRow(queuePanel, rowOperation.Id);
                    if (row is null) throw new InvalidOperationException($"Linha de baseline não renderizada: {stateName}");
                    string expectedStatus = ((OperationViewModel)row.DataContext).QueueStatusText;
                    await CaptureOperationsFixtureAsync(row, $"operations-{theme}-{stateName}-row.png", baseline, expectedStatus);
                }
                foreach (int successCount in new[] { 0, 1, 3 })
                {
                    indeterminate.State = OperationState.Queued;
                    operation.State = OperationState.Running;
                    completed.State = successCount >= 1 ? OperationState.Completed : OperationState.Running;
                    failed.State = successCount >= 2 ? OperationState.Completed : OperationState.Queued;
                    canceled.State = successCount >= 3 ? OperationState.Completed : OperationState.Queued;
                    await Task.Delay(80);
                    await CaptureOperationsFixtureAsync(queuePanel, $"operations-{theme}-header-{successCount}-expanded.png", baseline);
                    queueModel.IsQueueExpanded = false;
                    await Task.Delay(50);
                    await CaptureOperationsFixtureAsync(queuePanel, $"operations-{theme}-header-{successCount}-collapsed.png", baseline);
                    queueModel.IsQueueExpanded = true;
                }
                failed.State = OperationState.Failed;
                canceled.State = OperationState.Canceled;
            }
            for (int index = 0; index < baselineOperations.Length; index++) baselineOperations[index].Manager = baselineManagers[index];
        }
        foreach (OperationItem queued in operations)
        {
            if (!queued.IsFinished) { queued.Cancel(); queued.State = OperationState.Canceled; }
            queue.Remove(queued);
        }
        Check(queueModel.HeaderText != queueModel.GetText("operations.header.title")
            && queueModel.HeaderText.Contains(" de ", StringComparison.Ordinal),
            "Operações: contador mantém os resultados do lote após as linhas saírem da fila");

        await VerifyOperationIconFallbackAsync(queuePanel, queueModel, queue);

        OperationItem menuCloseCandidate = queue.Enqueue("Operação para fechamento do menu", OperationKind.Install);
        menuCloseCandidate.State = OperationState.Completed;
        await Task.Delay(100);
        Check(queueModel.HeaderText.Contains("1 de 1", StringComparison.Ordinal),
            "Operações: lote novo substitui a contagem anterior quando inicia");
        MenuFlyout actionMenu = queuePanel.CreateHeaderMenu();
        var actionMenuClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        actionMenu.Closed += (_, _) => actionMenuClosed.TrySetResult();
        if (queuePanel.FindName("BulkMenuButton") is Button menuButton) actionMenu.ShowAt(menuButton);
        await Task.Delay(100);
        CompareMaterializedMenu(actionMenu, "header:failed=False:succeeded=True:finished=True:cancelable=False", queueModel);
        await queuePanel.InvokeMenuActionForSmokeAsync(actionMenu, queueModel, "clearSuccessful", null);
        await Task.WhenAny(actionMenuClosed.Task, Task.Delay(500));
        Check(actionMenuClosed.Task.IsCompleted && !queueModel.IsAutoDismissPausedForSmoke
            && !queue.Operations.Any(item => item.Id == menuCloseCandidate.Id),
            "Operações: executar ação fecha menu e atualiza fila");

        OperationsPanelCatalog panelCatalog = services.GetRequiredService<OperationsPanelCatalog>();
        OperationsPanelConfiguration defaultPanelConfiguration = panelCatalog.Current;
        queueModel.SetAutoDismissPointerInside(false);
        queueModel.SetAutoDismissKeyboardFocus(false);
        queueModel.SetAutoDismissPopupOpen(false);
        panelCatalog.ReplaceConfiguration(defaultPanelConfiguration with
        { Behavior = defaultPanelConfiguration.Behavior with { PauseOnPointer = false } });
        queueModel.SetAutoDismissPointerInside(true);
        Check(!queueModel.IsAutoDismissPausedForSmoke, "Operações: pauseOnPointer=false desativa somente a pausa do ponteiro");
        queueModel.SetAutoDismissKeyboardFocus(true);
        Check(queueModel.IsAutoDismissPausedForSmoke, "Operações: pauseOnPointer=false preserva a pausa por foco");
        queueModel.SetAutoDismissPointerInside(false);
        queueModel.SetAutoDismissKeyboardFocus(false);
        panelCatalog.ReplaceConfiguration(defaultPanelConfiguration with
        { Behavior = defaultPanelConfiguration.Behavior with { PauseOnFocus = false } });
        queueModel.SetAutoDismissKeyboardFocus(true);
        Check(!queueModel.IsAutoDismissPausedForSmoke, "Operações: pauseOnFocus=false desativa somente a pausa do foco");
        queueModel.SetAutoDismissPopupOpen(true);
        Check(queueModel.IsAutoDismissPausedForSmoke, "Operações: pauseOnFocus=false preserva a pausa do menu");
        queueModel.SetAutoDismissKeyboardFocus(false);
        queueModel.SetAutoDismissPopupOpen(false);
        panelCatalog.ReplaceConfiguration(defaultPanelConfiguration with
        { Behavior = defaultPanelConfiguration.Behavior with { PauseOnPopup = false } });
        queueModel.SetAutoDismissPopupOpen(true);
        Check(!queueModel.IsAutoDismissPausedForSmoke, "Operações: pauseOnPopup=false desativa somente a pausa do menu");
        queueModel.SetAutoDismissPointerInside(true);
        Check(queueModel.IsAutoDismissPausedForSmoke, "Operações: pauseOnPopup=false preserva a pausa do ponteiro");
        queueModel.SetAutoDismissPointerInside(false);
        queueModel.SetAutoDismissPopupOpen(false);
        panelCatalog.ReplaceConfiguration(defaultPanelConfiguration);

        await VerifyOperationsPanelHotReloadAsync(window, queuePanel, queue, queueModel, panelCatalog,
            services.GetRequiredService<OperationTextProvider>());

        async Task VerifyDisabledAutoClosePauseAsync(string condition, Action<bool> setInside, string label)
        {
            OperationPanelBehavior behavior = defaultPanelConfiguration.Behavior with
            {
                AutoCloseDelaySeconds = 1,
                PauseOnPointer = condition != "pointer",
                PauseOnFocus = condition != "focus",
                PauseOnPopup = condition != "popup"
            };
            panelCatalog.ReplaceConfiguration(defaultPanelConfiguration with { Behavior = behavior });
            OperationItem timedSuccess = queue.Enqueue($"Fechamento sem pausa {label}", OperationKind.Install);
            timedSuccess.State = OperationState.Completed;
            await Task.Delay(100);
            setInside(true);
            await Task.Delay(TimeSpan.FromSeconds(queueModel.CompletedOperationsAutoDismissSeconds + .5));
            Check(!queue.Operations.Any(item => item.Id == timedSuccess.Id),
                $"Operações: pauseOn{label}=false permite fechar mesmo com essa condição ativa");
            setInside(false);
            panelCatalog.ReplaceConfiguration(defaultPanelConfiguration);
        }
        await VerifyDisabledAutoClosePauseAsync("pointer", queueModel.SetAutoDismissPointerInside, "Pointer");
        await VerifyDisabledAutoClosePauseAsync("focus", queueModel.SetAutoDismissKeyboardFocus, "Focus");
        await VerifyDisabledAutoClosePauseAsync("popup", queueModel.SetAutoDismissPopupOpen, "Popup");

        var autoClosedSuccess = queue.Enqueue("Operação bem-sucedida temporizada", OperationKind.Install);
        autoClosedSuccess.State = OperationState.Completed;
        await Task.Delay(150);
        queueModel.SetAutoDismissPointerInside(true);
        await Task.Delay(TimeSpan.FromSeconds(queueModel.CompletedOperationsAutoDismissSeconds + 1));
        Check(queue.Operations.Any(item => item.Id == autoClosedSuccess.Id),
            "Operações: o ponteiro pausa o fechamento automático dos sucessos");
        queueModel.SetAutoDismissPointerInside(false);
        await Task.Delay(TimeSpan.FromSeconds(queueModel.CompletedOperationsAutoDismissSeconds + 1));
        Check(!queue.Operations.Any(item => item.Id == autoClosedSuccess.Id),
            "Operações: sucesso sai da fila após cinco segundos");
        Check(services.GetRequiredService<OperationHistoryService>().Records.Any(record => record.Id == autoClosedSuccess.Id),
            "Operações: sucesso removido da fila continua no Histórico");

        var retainedFailure = queue.Enqueue("Falha permanece visível", OperationKind.Update);
        retainedFailure.State = OperationState.Failed;
        await Task.Delay(TimeSpan.FromSeconds(queueModel.CompletedOperationsAutoDismissSeconds + 1));
        Check(queue.Operations.Any(item => item.Id == retainedFailure.Id),
            "Operações: falha não é removida automaticamente");
        queue.Remove(retainedFailure);
    }

    private static async Task VerifyOperationIconFallbackAsync(OperationsQueuePanel panel, OperationsViewModel model,
        OperationsQueueService queue)
    {
        const string fixtureName = "operation-icon-alpha.png";
        string packageIconUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fixtureName)).AbsoluteUri;
        bool capturePreFix = Environment.GetEnvironmentVariable("WINPROVISION_CAPTURE_OPERATION_ICONS_PRE") == "1";
        string outputDirectory = Path.Combine(OutputDirectory, capturePreFix ? "operations-icons-pre-fix" : "operations-icons-v2_1");
        Directory.CreateDirectory(outputDirectory);

        foreach (ElementTheme theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            panel.RequestedTheme = theme;
            foreach (string variant in new[] { "with-icon", "without-icon", "invalid-url" })
            {
                var item = queue.Enqueue($"Ícone de operação {variant}", OperationKind.Update,
                    variant == "with-icon" ? packageIconUri : variant == "invalid-url" ? "https://127.0.0.1:1/invalid-operation-icon.png" : null);
                item.State = OperationState.Running;
                item.Manager = variant == "without-icon" ? "unknown" : "winget";
                FrameworkElement row = await WaitForOperationRowAsync(panel, item.Id);
                if (capturePreFix)
                    await WaitForOperationImageStateAsync(row, hasImage: variant == "with-icon");
                else if (variant == "with-icon")
                    await WaitForOperationIconAsync(row, imageLoaded: true, expectFallback: false);
                else if (variant == "without-icon")
                    await WaitForOperationIconAsync(row, imageLoaded: false, expectFallback: true);
                else
                    await WaitForOperationIconAsync(row, imageLoaded: true, expectFallback: false);

                var image = FindOperationImage(row) ?? throw new InvalidOperationException("Imagem de ícone da operação ausente.");
                var fallback = FindOperationIconFallback(row) ?? throw new InvalidOperationException("Fallback de ícone da operação ausente.");
                bool expectedFallback = variant == "without-icon";
                if (!capturePreFix) Check((fallback.Visibility == Visibility.Visible) == expectedFallback,
                    $"Operações: fallback do ícone {variant} correto em {theme}");
                if (!capturePreFix && variant == "with-icon")
                    Check(image.Source is not null && fallback.Visibility == Visibility.Collapsed,
                        $"Operações: ícone do pacote transparente substitui completamente o fallback em {theme}");
                else if (!capturePreFix && variant == "invalid-url")
                    Check(image.Source is not null && fallback.Visibility == Visibility.Collapsed,
                        $"Operações: URL inválida usa ícone do gerenciador em {theme}");

                await CaptureOperationsFixtureAsync(row, $"operations-icons-{theme}-{variant}.png", outputDirectory);
                item.Cancel(); item.State = OperationState.Canceled; queue.Remove(item);
            }
        }
        panel.RequestedTheme = ElementTheme.Default;
    }

    private static async Task WaitForOperationImageStateAsync(FrameworkElement row, bool hasImage)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if ((FindOperationImage(row)?.Source is not null) == hasImage) return;
            row.UpdateLayout();
            await Task.Delay(50);
        }
        throw new TimeoutException($"A imagem de operação não atingiu o estado de captura (hasImage={hasImage}).");
    }

    private static async Task<FrameworkElement> WaitForOperationRowAsync(OperationsQueuePanel panel, Guid id)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            panel.UpdateLayout();
            if (FindOperationRow(panel, id) is { } row) return row;
            await Task.Delay(50);
        }
        throw new TimeoutException("A linha com ícone de operação não foi materializada.");
    }

    private static async Task WaitForOperationIconAsync(FrameworkElement row, bool imageLoaded, bool expectFallback)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            Image? image = FindOperationImage(row);
            FrameworkElement? fallback = FindOperationIconFallback(row);
            bool loaded = image?.Source is not null && fallback?.Visibility == Visibility.Collapsed;
            bool fallbackVisible = fallback?.Visibility == Visibility.Visible;
            if (imageLoaded ? loaded : !loaded && fallbackVisible == expectFallback) return;
            row.UpdateLayout();
            await Task.Delay(50);
        }
        throw new TimeoutException($"A imagem de operação não atingiu o estado esperado (loaded={imageLoaded}, fallback={expectFallback}, source={FindOperationImage(row)?.Source is not null}, visibility={FindOperationIconFallback(row)?.Visibility}).");
    }

    private static Image? FindOperationImage(DependencyObject root)
    {
        if (root is Image image) return image;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindOperationImage(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    private static FrameworkElement? FindOperationIconFallback(DependencyObject root)
    {
        if (root is FluentIcons.WinUI.FluentIcon icon) return icon;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindOperationIconFallback(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    private static async Task VerifyRenderedOperationMenuAsync(OperationsQueuePanel panel, OperationsViewModel model, OperationItem operation, string context)
    {
        var more = FindMoreOptionsButton(panel.ItemsControl, operation.Id);
        string moreLabel = model.Items.Single(item => item.Id == operation.Id).MoreOptionsAutomationText;
        string actualMoreName = more is null ? string.Empty : AutomationProperties.GetName(more);
        object? actualMoreToolTip = more is null ? null : ToolTipService.GetToolTip(more);
        Check(more is not null && AutomationProperties.GetName(more) == moreLabel
            && Equals(ToolTipService.GetToolTip(more), moreLabel),
            $"Operações: nome acessível e tooltip do botão mais opções ({operation.State}; encontrado={more is not null}; nome={actualMoreName}; dica={actualMoreToolTip}; esperado={moreLabel})");
        if (more is null) return;
        MenuFlyout menu = panel.CreateItemMenu(model.Items.Single(item => item.Id == operation.Id));
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        menu.Opened += (_, _) => opened.TrySetResult(); menu.Closed += (_, _) => closed.TrySetResult();
        menu.ShowAt(more);
        await Task.WhenAny(opened.Task, Task.Delay(500));
        Check(opened.Task.IsCompleted && model.IsAutoDismissPausedForSmoke,
            $"Operações: menu {operation.State} abre e pausa fechamento automático");
        CompareMaterializedMenu(menu, context, model);
        if (menu.Items.OfType<MenuFlyoutItem>().FirstOrDefault() is { } first)
            Check(first.Focus(FocusState.Keyboard), $"Operações: menu {operation.State} aceita foco de teclado");
        else Check(false, $"Operações: menu {operation.State} tem item focalizável");
        menu.Hide();
        await Task.WhenAny(closed.Task, Task.Delay(500));
        Check(closed.Task.IsCompleted && !model.IsAutoDismissPausedForSmoke,
            $"Operações: fechar menu {operation.State} retoma fechamento automático");
    }

    private static Button? FindMoreOptionsButton(DependencyObject root, Guid id)
    {
        if (root is Button { Tag: OperationViewModel model, Content: StackPanel } button && model.Id == id) return button;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindMoreOptionsButton(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }

    private static async Task VerifyRenderedHeaderMenuAsync(OperationsQueuePanel panel, OperationsViewModel model, string context)
    {
        MenuFlyout menu = panel.CreateHeaderMenu();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        menu.Opened += (_, _) => opened.TrySetResult(); menu.Closed += (_, _) => closed.TrySetResult();
        if (panel.FindName("BulkMenuButton") is Button button) menu.ShowAt(button);
        await Task.WhenAny(opened.Task, Task.Delay(500));
        Check(opened.Task.IsCompleted && model.IsAutoDismissPausedForSmoke, "Operações: menu do cabeçalho abre e pausa fechamento automático");
        CompareMaterializedMenu(menu, context, model);
        menu.Hide(); await Task.WhenAny(closed.Task, Task.Delay(500));
        Check(closed.Task.IsCompleted && !model.IsAutoDismissPausedForSmoke, "Operações: menu do cabeçalho fechado retoma fechamento automático");
    }

    private static void CompareMaterializedMenu(MenuFlyout menu, string context, OperationsViewModel model)
    {
        using Stream stream = typeof(SmokeTest).Assembly.GetManifestResourceStream("WinProvision.Store.Validation.operations-menu-golden.json")!;
        using JsonDocument golden = JsonDocument.Parse(stream);
        JsonElement record = golden.RootElement.EnumerateArray().Single(item => item.GetProperty("Context").GetString() == context);
        JsonElement expectedList = record.GetProperty("Menu");
        var expected = expectedList.EnumerateArray().Where(item => item.GetProperty("Visible").GetBoolean()).ToArray();
        Check(menu.Items.Count == expected.Length, $"Operações: menu renderizado corresponde ao golden ({context})");
        for (int index = 0; index < Math.Min(menu.Items.Count, expected.Length); index++)
        {
            JsonElement item = expected[index];
            if (item.GetProperty("IsSeparator").GetBoolean())
            {
                Check(menu.Items[index] is MenuFlyoutSeparator separator && Equals(separator.Tag, item.GetProperty("Id").GetString()),
                    $"Operações: separador do menu corresponde ao golden ({context}/{index})");
                continue;
            }
            if (menu.Items[index] is not MenuFlyoutItem rendered) { Check(false, $"Operações: item renderizado corresponde ao golden ({context}/{index})"); continue; }
            string id = item.GetProperty("Id").GetString()!;
            string text = model.GetText(item.GetProperty("TextKey").GetString()!);
            string iconId = item.GetProperty("IconId").GetString()!;
            Check(Equals(rendered.Tag, id) && rendered.Text == text && rendered.IsEnabled == item.GetProperty("Enabled").GetBoolean()
                && AutomationProperties.GetName(rendered) == text
                && Equals(ToolTipService.GetToolTip(rendered), !item.GetProperty("Enabled").GetBoolean()
                    && OperationActionRegistry.GetUnavailableTooltipKey(id) is { } tipKey ? model.GetText(tipKey) : null)
                && rendered.Icon is FluentIcons.WinUI.FluentIcon icon && icon.Icon == OperationActionIconRegistry.GetIcon(iconId),
                $"Operações: texto, ícone e habilitação conferem com golden ({context}/{id})");
        }
    }

    private static async Task VerifyUpdateSourceIsolationAsync(UpdatesViewModel viewModel, SmokePackageBackend backend,
        ApplicationPreferencesService preferences)
    {
        static UpgradablePackage Package(string id, string source) => new()
        {
            Id = id, Name = id, Source = source, CurrentVersion = "1.0", AvailableVersion = "2.0"
        };
        static async Task ReportAsync(Func<UpdateSourceScanResult, Task>? report, UpdateSourceScanResult result)
        {
            if (report is not null) await report(result);
        }
        static async Task WaitUntilIdleAsync(UpdatesViewModel model)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (model.IsBusy && DateTime.UtcNow < deadline) await Task.Delay(20);
        }

        preferences.SetWingetEnabled(true);
        backend.ScanHandler = async (token, report) =>
        {
            token.ThrowIfCancellationRequested();
            var packages = new List<UpgradablePackage> { Package("Smoke.WingetOnly", "winget") };
            await ReportAsync(report, new UpdateSourceScanResult("WinGet", packages));
            return packages;
        };
        await viewModel.CheckUpdatesAsync();
        Check(viewModel.Packages.Any(package => package.Id == "Smoke.WingetOnly")
            && !viewModel.IsBusy && !viewModel.IsInitialLoading && viewModel.CanReload && viewModel.ScanCommand.CanExecute(null),
            "Atualizações: somente WinGet conclui e reabilita recarga e tentativa");

        viewModel.FilterChocolatey = true;
        preferences.SetWingetEnabled(false);
        backend.ScanHandler = async (token, report) =>
        {
            token.ThrowIfCancellationRequested();
            var packages = new List<UpgradablePackage> { Package("Smoke.ChocolateyOnly", "chocolatey") };
            await ReportAsync(report, new UpdateSourceScanResult("Chocolatey", packages));
            return packages;
        };
        preferences.SetChocolateyEnabled(true);
        await viewModel.CheckUpdatesAsync();
        await WaitUntilIdleAsync(viewModel);
        Check(viewModel.Packages.Any(package => package.Id == "Smoke.ChocolateyOnly")
            && !viewModel.IsBusy && !viewModel.IsInitialLoading && viewModel.CanReload && viewModel.ScanCommand.CanExecute(null),
            "Atualizações: somente Chocolatey conclui sem depender do WinGet");

        preferences.SetWingetEnabled(true);
        viewModel.FilterWinget = true;
        var chocolateyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wingetReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.ScanHandler = async (token, report) =>
        {
            token.ThrowIfCancellationRequested();
            var winget = Package("Smoke.StreamedWinget", "winget");
            await ReportAsync(report, new UpdateSourceScanResult("WinGet", new[] { winget }));
            wingetReported.TrySetResult();
            await chocolateyGate.Task.WaitAsync(token);
            var chocolatey = Package("Smoke.StreamedChocolatey", "chocolatey");
            await ReportAsync(report, new UpdateSourceScanResult("Chocolatey", new[] { chocolatey }));
            return new List<UpgradablePackage> { winget, chocolatey };
        };
        Task bothSources = viewModel.CheckUpdatesAsync();
        await wingetReported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Force an observable UI turn while Chocolatey is held behind its own gate.
        await Task.Delay(250);
        bool streamedHasWinGet = viewModel.Packages.Any(package => package.Id == "Smoke.StreamedWinget");
        bool streamedBusy = viewModel.IsBusy;
        bool streamedNotLoading = !viewModel.IsInitialLoading;
        string streamedStatus = viewModel.StatusText;
        bool streamed = streamedHasWinGet && streamedBusy && streamedNotLoading;
        chocolateyGate.TrySetResult();
        await bothSources;
        Check(streamed && viewModel.Packages.Any(package => package.Id == "Smoke.StreamedChocolatey")
            && !viewModel.IsBusy && viewModel.CanReload && viewModel.ScanCommand.CanExecute(null),
            $"Atualizações: duas fontes executam em paralelo e WinGet aparece antes da resposta Chocolatey (streamed={streamed}, hadWinGet={streamedHasWinGet}, busy={streamedBusy}, notLoading={streamedNotLoading}, status={streamedStatus}, visible={string.Join(",", viewModel.Packages.Select(package => package.Id))})");

        backend.ScanHandler = async (token, report) =>
        {
            token.ThrowIfCancellationRequested();
            var winget = Package("Smoke.WinGetWithChocolateyMissing", "winget");
            await ReportAsync(report, new UpdateSourceScanResult("WinGet", new[] { winget }));
            await ReportAsync(report, new UpdateSourceScanResult("Chocolatey", Array.Empty<UpgradablePackage>(), "CLI não encontrado."));
            return new List<UpgradablePackage> { winget };
        };
        await viewModel.CheckUpdatesAsync();
        Check(viewModel.Packages.Any(package => package.Id == "Smoke.WinGetWithChocolateyMissing")
            && !viewModel.HasError && viewModel.StatusText.Contains("Chocolatey: CLI não encontrado", StringComparison.Ordinal)
            && !viewModel.IsBusy && viewModel.CanReload,
            "Atualizações: Chocolatey ausente mostra aviso sem ocultar o resultado WinGet");

        backend.ScanHandler = async (token, report) =>
        {
            token.ThrowIfCancellationRequested();
            var winget = Package("Smoke.WinGetWithChocolateyTimeout", "winget");
            await ReportAsync(report, new UpdateSourceScanResult("WinGet", new[] { winget }));
            await ReportAsync(report, new UpdateSourceScanResult("Chocolatey", Array.Empty<UpgradablePackage>(), "tempo esgotado."));
            return new List<UpgradablePackage> { winget };
        };
        await viewModel.CheckUpdatesAsync();
        Check(viewModel.Packages.Any(package => package.Id == "Smoke.WinGetWithChocolateyTimeout")
            && !viewModel.HasError && viewModel.StatusText.Contains("Chocolatey: tempo esgotado", StringComparison.Ordinal)
            && !viewModel.IsBusy && viewModel.CanReload && viewModel.ScanCommand.CanExecute(null),
            "Atualizações: timeout Chocolatey preserva WinGet e reabilita os botões");

        preferences.SetWingetEnabled(false);
        backend.ScanHandler = async (token, report) =>
        {
            token.ThrowIfCancellationRequested();
            await ReportAsync(report, new UpdateSourceScanResult("Chocolatey", Array.Empty<UpgradablePackage>(), "tempo esgotado."));
            return [];
        };
        await viewModel.CheckUpdatesAsync();
        Check(viewModel.HasError && viewModel.ErrorMessage.Contains("Chocolatey: tempo esgotado", StringComparison.Ordinal)
            && !viewModel.IsBusy && !viewModel.IsInitialLoading && viewModel.CanReload && viewModel.ScanCommand.CanExecute(null),
            "Atualizações: timeout sem outra fonte termina em erro claro e mantém nova tentativa habilitada");

        backend.ScanHandler = null;
        preferences.SetChocolateyEnabled(false);
        preferences.SetWingetEnabled(true);
        viewModel.ApplyFilters();
    }

    private static void VerifyBatchObservableCollection()
    {
        var first = new AppEntry { Id = "batch.first", Name = "Primeiro", Source = "winget" };
        var second = new AppEntry { Id = "batch.second", Name = "Segundo", Source = "winget" };
        var collection = new WinProvision.Store.Collections.BatchObservableCollection<AppEntry>();
        int notifications = 0;
        NotifyCollectionChangedAction action = NotifyCollectionChangedAction.Add;
        collection.CollectionChanged += (_, args) => { notifications++; action = args.Action; };

        collection.ReplaceAll(new[] { first, second });

        Check(collection.Count == 2 && ReferenceEquals(collection[0], first) && ReferenceEquals(collection[1], second)
            && notifications == 1 && action == NotifyCollectionChangedAction.Reset,
            "Coleção em lote: ReplaceAll preserva instâncias e emite um único Reset");
    }

    private static async Task VerifyInitialNavigationSelectionAsync(MainWindow window)
    {
        var navigation = (NavigationView)window.RootElement.FindName("RootNavigation");
        navigation.UpdateLayout();
        await Task.Delay(250);
        var selected = navigation.SelectedItem as NavigationViewItem
            ?? throw new InvalidOperationException("O menu não tem item selecionado após a abertura da janela.");
        var content = FindNamedVisual(selected, "ContentGrid") as Grid
            ?? throw new InvalidOperationException("O item selecionado não contém a grade do template.");
        var indicator = FindNamedVisual(selected, "SelectionIndicator") as Microsoft.UI.Xaml.Shapes.Rectangle
            ?? throw new InvalidOperationException("O item selecionado não contém a marca de seleção.");
        Windows.Foundation.Point indicatorOrigin = indicator.TransformToVisual(selected).TransformPoint(new Windows.Foundation.Point());
        await CaptureAsync(window.RootElement, "navigation-selection-startup.png");
        await File.WriteAllTextAsync(Path.Combine(OutputDirectory, "navigation-selection-startup.txt"),
            $"Selected={selected.Tag}; PaneWidth={navigation.OpenPaneLength}; Item={selected.ActualWidth:0.##}x{selected.ActualHeight:0.##}; "
            + $"ContentBackground={DescribeBrush(content.Background)}; Indicator={indicatorOrigin.X:0.##},{indicatorOrigin.Y:0.##} "
            + $"{indicator.ActualWidth:0.##}x{indicator.ActualHeight:0.##}; Opacity={indicator.Opacity:0.##}");
        Check(selected.Tag?.ToString() == "home", "Menu: Descobrir é selecionado pela inicialização da MainWindow real");
        Check(!HasVisibleFill(content.Background),
            $"Menu: a grade de conteúdo do item selecionado não cobre o destaque (fundo={DescribeBrush(content.Background)})");
        Check(indicatorOrigin.X < selected.ActualWidth / 2 && indicator.ActualWidth > 0 && indicator.Opacity > 0,
            "Menu: a marca de seleção ocupa a margem interna do item selecionado");

    }

    private static async Task VerifyNavigationSelectionGeometryAsync(MainWindow window)
    {
        var navigation = (NavigationView)window.RootElement.FindName("RootNavigation");
        (int X, int Y, int Width, int Height)? baseline = null;
        foreach (string route in new[] { "home", "updates", "installed", "collections", "settings", "package-managers" })
        {
            window.Navigate(route);
            navigation.UpdateLayout();
            await Task.Delay(250);
            var current = navigation.SelectedItem as NavigationViewItem
                ?? throw new InvalidOperationException($"O menu não selecionou '{route}'.");
            Check(current.Tag?.ToString() == route, $"Menu: '{route}' é selecionado pela janela real");
            RenderedCapture capture = await CaptureAsync(window.RootElement, $"navigation-selection-{route}.png");
            var marker = FindSelectionMarker(capture, current, window.RootElement);
            if (baseline is null)
                baseline = marker;
            else
                Check(Math.Abs(marker.X - baseline.Value.X) <= 1
                    && Math.Abs(marker.Y - baseline.Value.Y) <= 10
                    && Math.Abs(marker.Width - baseline.Value.Width) <= 1
                    && marker.Height is >= 10 and <= 26,
                    $"Menu: '{route}' mantém a geometria renderizada da marca de seleção (base={baseline}; atual={marker})");
        }

        window.Navigate("home");
        navigation.UpdateLayout();
        await Task.Delay(250);
        Check((navigation.SelectedItem as NavigationViewItem)?.Tag?.ToString() == "home",
            "Menu: voltar para Descobrir restaura o item selecionado");
        await CaptureAsync(window.RootElement, "navigation-selection-discover-return.png");
    }

    private static (int X, int Y, int Width, int Height) FindSelectionMarker(
        RenderedCapture capture, FrameworkElement item, FrameworkElement root)
    {
        Windows.Foundation.Point itemOrigin = item.TransformToVisual(root)
            .TransformPoint(new Windows.Foundation.Point());
        int top = Math.Max(0, (int)Math.Floor(itemOrigin.Y));
        int bottom = Math.Min(capture.Height, (int)Math.Ceiling(itemOrigin.Y + item.ActualHeight));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = top; y < bottom; y++)
        for (int x = 0; x < Math.Min(12, capture.Width); x++)
        {
            int offset = (y * capture.Width + x) * 4;
            byte blue = capture.Pixels[offset];
            byte green = capture.Pixels[offset + 1];
            byte red = capture.Pixels[offset + 2];
            if (blue < 150 || green < 70 || red > 120 || blue <= red + 70)
                continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (maxX < minX || maxY < minY)
            throw new InvalidOperationException("Menu: não foi encontrado destaque renderizado no item selecionado.");
        return (minX, minY - top, maxX - minX + 1, maxY - minY + 1);
    }

    private static async Task VerifyWindowBackdropAsync(MainWindow window, IServiceProvider services)
    {
        WindowBackdropCatalog catalog = WindowBackdropCatalog.Default;
        Check(catalog.Document.SchemaVersion == 1
            && catalog.Document.Default == "mica"
            && catalog.Backdrops.Select(backdrop => backdrop.Id)
                .SequenceEqual(new[] { "mica", "mica-alt", "acrylic", "acrylic-thin", "none" }),
            "Plano de fundo: catálogo embutido schema 1 declara as cinco opções em ordem");

        Check(catalog.Resolve("mica", new(false, false)).Backdrop.Id == "none"
            && catalog.Resolve("acrylic-thin", new(true, false)).Backdrop.Id == "mica"
            && catalog.Resolve("mica-alt", new(false, true)).Backdrop.Id == "acrylic"
            && catalog.Resolve("id-invalido", new(false, false)) is { Backdrop.Id: "none", Warning: not null },
            "Plano de fundo: resolução pura usa fallback em ordem e avisa para ID desconhecido");

        string preferencePath = Path.Combine(Path.GetTempPath(), $"winprovision-backdrop-{Guid.NewGuid():N}.json");
        try
        {
            var preferenceProbe = new ApplicationPreferencesService(preferencePath, persistStartup: false);
            preferenceProbe.SetBackdropPreference("acrylic-thin");
            Check(new ApplicationPreferencesService(preferencePath, persistStartup: false).BackdropPreference == "acrylic-thin",
                "Plano de fundo: app-preferences.json persiste appearance.backdrop e recarrega a escolha");
        }
        finally
        {
            if (File.Exists(preferencePath)) File.Delete(preferencePath);
        }

        ApplicationPreferencesService preferences = services.GetRequiredService<ApplicationPreferencesService>();
        string previousPreference = preferences.BackdropPreference;
        window.Navigate("settings");
        await WaitForPageAsync(window, "settings", window.RootElement.ActualTheme);
        var settings = window.CurrentPage as Views.SettingsPage
            ?? throw new InvalidOperationException("A página de Configurações não foi aberta para testar o plano de fundo.");
        settings.NavigateToCategory("Interface");
        await WaitUntilAsync(() => settings.FindName("BackdropComboBox") is ComboBox backdrop
            && backdrop.ItemsSource is IReadOnlyList<ViewModels.WindowBackdropOption>,
            "As opções de plano de fundo não foram associadas ao controle.");

        WindowBackdropSupport support = WindowBackdropSupport.Current;
        var root = (Grid)window.RootElement;
        byte expectedRootAlpha = window.EffectiveBackdropForSmoke == "none" ? (byte)255 : (byte)0;
        Check(root.Background is SolidColorBrush rootBrush
            && ((NavigationView)window.RootElement.FindName("RootNavigation")).Background is SolidColorBrush navigationBrush
            && rootBrush.Color.A == expectedRootAlpha
            && navigationBrush.Color.A == expectedRootAlpha,
            "Plano de fundo: raiz e painel de navegação refletem o backdrop efetivamente aplicado");
        Check(settings.FindName("BackdropComboBox") is ComboBox backdropCombo
            && backdropCombo.ItemsSource is IReadOnlyList<ViewModels.WindowBackdropOption> options
            && options.Count == 5
            && options.All(option => option.IsSupported == (option.Id == "none"
                || (option.Id.StartsWith("mica", StringComparison.Ordinal) ? support.Mica : support.Acrylic))),
            "Plano de fundo: Configurações lista as cinco opções e desabilita efeitos sem suporte runtime");

        settings.ViewModel.SelectedBackdropOption = settings.ViewModel.BackdropOptions.Single(option => option.Id == "none");
        Check(preferences.BackdropPreference == "none" && window.EffectiveBackdropForSmoke == "none"
            && root.Background is SolidColorBrush { Color.A: 255 },
            "Plano de fundo: escolher Nenhum persiste a preferência e atualiza a janela imediatamente");
        preferences.SetBackdropPreference(previousPreference);
        settings.ViewModel.SelectedBackdropOption = settings.ViewModel.BackdropOptions.First(option =>
            option.Id.Equals(previousPreference, StringComparison.OrdinalIgnoreCase));
        Check(window.EffectiveBackdropForSmoke == catalog.Resolve(previousPreference, support).Backdrop.Id,
            "Plano de fundo: restaura o efeito efetivo compatível sem substituir a preferência salva");
        window.Navigate("home");
        await WaitForPageAsync(window, "home", window.RootElement.ActualTheme);
    }

    private static void VerifyMainShellConfiguration(MainWindow window)
    {
        var navigation = (NavigationView)window.RootElement.FindName("RootNavigation");
        string[] topItems = navigation.MenuItems.OfType<NavigationViewItem>()
            .Select(item => item.Tag?.ToString() ?? string.Empty).ToArray();
        string[] footerItems = navigation.FooterMenuItems.OfType<NavigationViewItem>()
            .Select(item => ReferenceEquals(item, window.RootElement.FindName("MoreFlyoutNavigationItem"))
                ? "more-menu" : item.Tag?.ToString() ?? string.Empty).ToArray();
        Check(Math.Abs(navigation.OpenPaneLength - 300) < 0.1,
            "Menu: largura aberta vem do descritor JSON embutido");
        Grid searchVisual = (Grid)window.RootElement.FindName("TitleBarSearchVisual");
        Check(Math.Abs(searchVisual.Width - 480) < 0.1
            && Math.Abs(window.TitleBarSearchBoxForSmoke.Width - 480) < 0.1,
            "Pesquisa: largura máxima vem do descritor JSON embutido");
        Check(topItems.SequenceEqual(new[] { "home", "updates", "installed", "collections", "office" })
            && footerItems.SequenceEqual(new[] { "settings", "package-managers", "more-menu" }),
            "Menu: ordem dos itens visíveis vem do descritor JSON embutido");
        NavigationViewItem packageManagersItem = navigation.FooterMenuItems.OfType<NavigationViewItem>().ElementAt(1);
        Check(packageManagersItem.Content is TextBlock
            { Text: "Gerenciadores de pacotes", TextTrimming: TextTrimming.CharacterEllipsis }
            && ToolTipService.GetToolTip(packageManagersItem) is null,
            "Menu: o rótulo expandido é visível e não repete o tooltip");
        Check(window.RootElement.FindName("MoreFlyoutItems") is StackPanel { Children.Count: 10 },
            "Menu: flyout embutido renderiza os tipos declarados no JSON");
        Check(ReferenceEquals(navigation.FooterMenuItems.LastOrDefault(), window.RootElement.FindName("MoreFlyoutNavigationItem")),
            "Menu: Mais é o último ícone do rodapé da lateral");
        Check(window.RootElement.FindName("MoreFlyoutNavigationItem") is NavigationViewItem { IsHitTestVisible: true },
            "Menu: o item Mais recebe interação do ponteiro");
        Check(window.RootElement.FindName("MoreFlyoutNavigationItem") is NavigationViewItem moreItem
            && !VisualTreeContains<Button>(moreItem),
            "Menu: cada item de navegação contém um único alvo interativo sem botão aninhado");
        Check(ToolTipService.GetToolTip(packageManagersItem) is null,
            "Menu: tooltip compartilhado fica oculto com navegação expandida");
        navigation.IsPaneOpen = false;
        window.RefreshNavigationToolTipsForSmoke();
        object? collapsedTooltip = ToolTipService.GetToolTip(packageManagersItem);
        Check(!navigation.IsPaneOpen && collapsedTooltip is not null,
            "Menu: tooltip compartilhado aparece quando a navegação está recolhida");
        navigation.IsPaneOpen = true;
        window.RefreshNavigationToolTipsForSmoke();
        object? selectedBeforeFlyout = navigation.SelectedItem;
        window.OpenMoreFlyoutForSmoke();
        Check(window.IsMenuFlyoutOpenForSmoke && ReferenceEquals(navigation.SelectedItem, selectedBeforeFlyout),
            "Menu: abrir Mais preserva seleção e página atuais");
        window.CloseMoreFlyoutForSmoke();
        var warnings = new List<string>();
        MainShellDefinition fake = MainShellDefinition.Load(
            """{"schemaVersion":1,"id":"test","title":"shell.title","search":{"placement":"titlebar","minWidth":300,"maxWidth":420,"shortcut":"Ctrl+F","pages":{"fake-page":{"enabled":true,"placeholder":"search.settings","trigger":"live","debounceMs":0,"minLength":0,"keepQuery":true}}},"navigation":{"pane":{"openLength":280,"startsOpen":true},"items":[{"id":"invalid","label":"invalid.label","icon":"unknown","target":{"type":"screen","id":"missing"},"placement":"top","visible":true},{"id":"fake-screen","label":"shell.navigation.discover","icon":"discover","target":{"type":"screen","id":"fake-screen"},"placement":"top","visible":true},{"id":"fake-page","label":"shell.navigation.settings","icon":"settings","target":{"type":"page","id":"fake-page"},"placement":"bottom","visible":true}]}}""",
            warnings.Add,
            screenExists: id => id == "fake-screen",
            pageExists: id => id == "fake-page",
            resolveIcon: key => key == "discover" || key == "settings" ? key : null);
        Check(fake.Items.Select(item => item.Id).SequenceEqual(new[] { "fake-screen", "fake-page" })
            && warnings.Count == 1 && fake.Search.MinWidth == 300 && fake.Search.MaxWidth == 420
            && fake.Search.Pages["fake-page"].Placeholder == "search.settings",
            "Menu: item JSON inválido é omitido com aviso e a referência válida permanece");
        var unsupportedWarnings = new List<string>();
        MainShellDefinition unsupported = MainShellDefinition.Load(
            """{"schemaVersion":99,"title":"Unknown","navigation":{"items":[]}}""",
            unsupportedWarnings.Add);
        Check(unsupported.Items.Any(item => item.Kind == "flyout") && unsupportedWarnings.Count == 1,
            "Menu: schemaVersion desconhecido é avisado sem falhar a janela");

        string synthetic = """{"schemaVersion":1,"navigation":{"items":[{"id":"menu","kind":"flyout","label":"Menu","icon":"details","placement":"bottom","extra":true,"items":[{"type":"navigation","id":"n","label":"Navegar","target":{"type":"page","id":"fake"}},{"type":"command","id":"c","label":"Executar","commandId":"invented"},{"type":"info","id":"i","label":"Informação"},{"type":"separator","id":"s"},{"type":"unknown","id":"x","label":"Ignorar"},{"type":"info","id":"bad-condition","label":"Inválida","dependsOn":{"key":"flag","risk":"normal"}}]}]}}""";
        var syntheticWarnings = new List<string>();
        MainShellDefinition syntheticDefinition = MainShellDefinition.Load(synthetic, syntheticWarnings.Add,
            pageExists: id => id == "fake", resolveIcon: _ => "Info");
        MainShellMenuItem[] syntheticItems = syntheticDefinition.Items.Single().Children!.ToArray();
        Check(syntheticItems.Select(child => child.Type).SequenceEqual(new[] { "navigation", "command", "info", "separator" })
            && syntheticItems[1].CommandId == "invented" && syntheticWarnings.Count == 2,
            "Menu: tipos genéricos aceitam IDs sintéticos, ignoram tipo e condição inválidos e campo extra");
        var invalidJsonWarnings = new List<string>();
        MainShellDefinition invalidJson = MainShellDefinition.Load("{", invalidJsonWarnings.Add,
            screenExists: _ => true, pageExists: _ => true, resolveIcon: _ => "Info");
        Check(invalidJson.Items.Any(item => item.Kind == "flyout") && invalidJsonWarnings.Count == 1,
            "Menu: JSON inválido registra aviso e usa o descritor padrão com Mais");
        Check(!MainWindow.ShouldConfirmExit(false) && MainWindow.ShouldConfirmExit(true)
            && MainWindow.PendingExitMessage.Contains("interromperá", StringComparison.Ordinal),
            "Menu: saída sem operação segue direto e saída com operação confirma o que será interrompido");

        MainShellDefinition embedded = MainShellDefinition.LoadEmbedded();
        MainShellMenuItem[] children = embedded.Items.Single(item => item.Kind == "flyout").Children!.ToArray();
        Check(children.Single(item => item.Id == "history").Icon == "history"
            && children.Single(item => item.Id == "release-notes").Icon == "megaphone"
            && children.Single(item => item.Id == "exit").Icon == "dismissCircle"
            && children.Single(item => item.Id == "about").CommandId == "open-about",
            "Menu: ícones atualizados e Sobre encaminhado pelo registro de comandos");
        VerifyAboutContentCatalog();
    }

    private static void VerifyAboutContentCatalog()
    {
        const string texts = """{"label":"Rótulo","heading":"Título","subtitle":"Subtítulo","paragraph":"Parágrafo","line":"Valor {value}","notice-title":"Aviso","notice-text":"Texto","link":"Abrir","item":"Item"}""";
        const string synthetic = """
        {"schemaVersion":1,"extra":true,"tabs":[
          {"id":"synthetic-tab-a","label":"label","blocks":[
            {"type":"heading","text":"heading","id":"invented-heading"},
            {"type":"subtitle","text":"subtitle","id":"invented-subtitle"},
            {"type":"paragraph","text":"paragraph","id":"invented-paragraph"},
            {"type":"valueLine","text":"line","providerId":"synthetic-provider","id":"invented-value"},
            {"type":"notice","severity":"warning","title":"notice-title","text":"notice-text","id":"invented-notice"},
            {"type":"links","items":[{"label":"link","destination":"https://github.com/winprovision-services/WinProvision-Store"},{"label":"link","destination":"javascript:alert(1)"}],"id":"invented-links"},
            {"type":"list","items":["item",{"name":"Synthetic package","license":"MIT"}],"id":"invented-list"},
            {"type":"separator","id":"invented-separator"},
            {"type":"future-block","id":"unknown-block"},
            {"type":"valueLine","text":"line","providerId":"unknown-provider"}
          ]},
          {"id":"synthetic-tab-b","label":"label","blocks":[
            {"type":"heading","text":"heading"},{"type":"subtitle","text":"subtitle"},{"type":"paragraph","text":"paragraph"},
            {"type":"valueLine","text":"line","providerId":"synthetic-provider"},{"type":"notice","severity":"warning","title":"notice-title","text":"notice-text"},
            {"type":"links","items":[{"label":"link","destination":"https://github.com/winprovision-services/WinProvision-Store"}]},
            {"type":"list","items":["item",{"name":"Synthetic package","license":"MIT"}]},{"type":"separator"}
          ]},
          {"id":"empty-tab","label":"label","blocks":[]}
        ]}
        """;
        var providers = new AboutValueProviderRegistry();
        providers.Register("synthetic-provider", () => "synthetic-value");
        var warnings = new List<string>();
        AboutContentCatalog catalog = AboutContentCatalog.Load(synthetic, texts, providers, warnings.Add);
        StackPanel first = AboutDialogRenderer.RenderTabForSmoke(catalog.Tabs[0], catalog, providers);
        StackPanel second = AboutDialogRenderer.RenderTabForSmoke(catalog.Tabs[1], catalog, providers);
        Check(catalog.Tabs.Count == 2 && first.Children.Count == 8 && second.Children.Count == 8
            && ((TextBlock)first.Children[3]).Text == "Valor synthetic-value"
            && first.Children[5] is WrapPanel { Children.Count: 1 }
            && warnings.Count >= 4,
            "Sobre: tipos de bloco renderizam igual para ids sintéticos; campo extra, link, tipo, provedor e aba inválidos são tratados");

        var invalidWarnings = new List<string>();
        AboutContentCatalog invalid = AboutContentCatalog.Load("{", "{}", AboutValueProviderRegistry.CreateDefault(), invalidWarnings.Add);
        Check(invalid.Tabs.Count == 1 && invalidWarnings.Count == 1,
            "Sobre: JSON inválido registra aviso e usa o conteúdo padrão embutido");
        Check(!AboutContentCatalog.TryValidateDestination("https://example.com/", out _)
            && !AboutContentCatalog.TryValidateDestination("file:///C:/Windows", out _)
            && AboutContentCatalog.TryValidateDestination("https://github.com/winprovision-services/WinProvision-Store/issues", out _),
            "Sobre: links aceitam somente HTTP(S) nos domínios permitidos");

        AboutContentCatalog embedded = AboutContentCatalog.LoadEmbedded();
        AboutDialogTab? thirdParty = embedded.Tabs.SingleOrDefault(tab => tab.Id == "third-party");
        AboutDialogBlock? thirdPartyList = thirdParty?.Blocks.SingleOrDefault(block => block.Type == "list");
        StackPanel? thirdPartyVisual = thirdParty is null ? null : AboutDialogRenderer.RenderTabForSmoke(thirdParty, embedded,
            AboutValueProviderRegistry.CreateDefault());
        bool allThirdPartyLinksValid = thirdPartyList?.Items?.All(item =>
            AboutContentCatalog.TryValidateDestination(item.SourceUrl ?? string.Empty, out _)
            && AboutContentCatalog.TryValidateDestination(item.LicenseUrl ?? string.Empty, out _)) == true;
        Check(thirdParty is { Footer.Length: > 0 } && thirdPartyList?.Items?.Count == 7
            && thirdPartyVisual?.Children.SingleOrDefault() is StackPanel { Children.Count: 7 }
            && ((StackPanel)thirdPartyVisual.Children[0]).Children.All(element => element is Expander { MaxWidth: <= 480 })
            && allThirdPartyLinksValid,
            "Sobre: Licença de Terceiros carrega sete expansores, rodapé fixo e links permitidos");
    }

    private static FrameworkElement? FindNamedVisual(DependencyObject root, string name)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement element && element.Name == name) return element;
            if (FindNamedVisual(child, name) is { } descendant) return descendant;
        }
        return null;
    }

    private static bool VisualTreeContains<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T) return true;
            if (VisualTreeContains<T>(child)) return true;
        }
        return false;
    }

    private static bool HasVisibleFill(Brush? brush) => brush switch
    {
        null => false,
        SolidColorBrush solid => solid.Color.A > 0 && solid.Opacity > 0,
        _ => true
    };

    private static string DescribeBrush(Brush? brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2} opacity={solid.Opacity:0.##}",
        null => "none",
        _ => brush.GetType().Name
    };

    private static async Task VerifyPackageManagerAdapterExtensibilityAsync()
    {
        var fake = new FakePackageManagerSettingsAdapter();
        var registry = new PackageManagerSettingsAdapterRegistry(new[] { fake });
        var viewModel = new PackageManagersViewModel(registry);
        Check(viewModel.ManagerRows.Any(row => row.Id == fake.Definition.Id),
            "Gerenciadores: adaptador registrado aparece sem catálogo ou alteração de tela");
        await viewModel.OpenManagerDetails(fake.Definition.Id);
        Check(viewModel.SelectedInstallFields.Count == 1 && viewModel.SelectedSettingsDescriptor?.Sources.Supported == false,
            "Gerenciadores: descritor falso renderiza campos e distingue fontes não suportadas");
        Check(!viewModel.ApplySelectedSettingsCommand.CanExecute(null) && !viewModel.ResetSelectedSettingsCommand.CanExecute(null),
            "Gerenciadores: aplicar e redefinir iniciam desabilitados quando os valores seguem o padrão");
        viewModel.SelectedInstallFields[0].Value = "alterado";
        Check(viewModel.ApplySelectedSettingsCommand.CanExecute(null) && viewModel.ResetSelectedSettingsCommand.CanExecute(null),
            "Gerenciadores: alteração pendente habilita aplicar e uma diferença do padrão habilita redefinir");
        await viewModel.ApplySelectedSettingsCommand.ExecuteAsync(null);
        Check(fake.AppliedValue == "alterado" && viewModel.SelectedSettingsDescriptor?.Fields[0].Value == "alterado",
            "Gerenciadores: alteração genérica é encaminhada ao adaptador e reaparece no descritor");
        Check(!viewModel.ApplySelectedSettingsCommand.CanExecute(null) && viewModel.ResetSelectedSettingsCommand.CanExecute(null),
            "Gerenciadores: aplicar fica desabilitado após salvar e redefinir permanece disponível fora do padrão");
        await viewModel.ResetSelectedSettingsCommand.ExecuteAsync(null);
        Check(viewModel.SelectedSettingsDescriptor?.Fields[0].Value == "original"
            && !viewModel.ApplySelectedSettingsCommand.CanExecute(null) && !viewModel.ResetSelectedSettingsCommand.CanExecute(null),
            "Gerenciadores: redefinição restaura o padrão e desabilita ambas as ações");

        var alpha = new FakePackageManagerSettingsAdapter(new PackageManagerDescriptor
        {
            Id = "test.alpha",
            Source = "test.alpha",
            DisplayName = "Gerenciador Alfa",
            Description = "Busca por detalhe especial",
            ShowInSettings = true,
            CanToggle = true,
            Check = new PackageManagerCheckDefinition { Kind = "none" }
        });
        var beta = new FakePackageManagerSettingsAdapter(new PackageManagerDescriptor
        {
            Id = "test.beta",
            Source = "test.beta",
            DisplayName = "Gerenciador Beta",
            Description = "Outra descrição",
            ShowInSettings = true,
            CanToggle = true,
            Check = new PackageManagerCheckDefinition { Kind = "none" }
        });
        var searchViewModel = new PackageManagersViewModel(new PackageManagerSettingsAdapterRegistry(new[] { alpha, beta }));
        searchViewModel.ManagerSearchText = "ESPECIAL";
        Check(searchViewModel.VisibleManagerRows.Select(row => row.Id).SequenceEqual(new[] { "test.alpha" }),
            "Gerenciadores: pesquisa ignora maiúsculas e filtra descrições");
        searchViewModel.ManagerSearchText = "beta";
        Check(searchViewModel.VisibleManagerRows.Select(row => row.Id).SequenceEqual(new[] { "test.beta" }),
            "Gerenciadores: pesquisa filtra nomes");

        var catalog = PackageManagerCatalog.Load("""
            { "schemaVersion": 1,
              "managers": [
                { "schemaVersion": 1, "id": "test.fake", "source": "test.fake",
                  "displayName": "Adaptador falso", "showInSettings": true },
                { "schemaVersion": 2, "id": "test.rejected", "source": "test.rejected",
                  "displayName": "Versão incompatível", "showInSettings": true }
              ] }
            """);
        var incompatibleAdapter = new FakePackageManagerSettingsAdapter(new PackageManagerDescriptor
        {
            Id = "test.rejected",
            Source = "test.rejected",
            DisplayName = "Versão incompatível",
            ShowInSettings = true,
            CanToggle = true
        });
        var filteredRegistry = new PackageManagerSettingsAdapterRegistry(new[] { fake, incompatibleAdapter }, catalog);
        Check(filteredRegistry.Adapters.Select(adapter => adapter.Definition.Id).SequenceEqual(new[] { fake.Definition.Id })
            && catalog.IsSchemaRejected(incompatibleAdapter.Definition.Id),
            "Gerenciadores: versão individual incompatível não aparece na página e adaptadores válidos permanecem");
    }
    private static void InvokeUpdatesHandler(Views.UpdatesPage page, string handlerName, params object?[] arguments)
    {
        var handler = typeof(UpdatesDialogs).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Views.UpdatesPage).FullName, handlerName);
        handler.Invoke(page.Dialogs, arguments);
    }

    private static async Task VerifyInstalledActionsAsync(
        Views.InstalledPackagesPage page,
        SmokePackageBackend backend,
        IServiceProvider services)
    {
        var model = page.ViewModel;
        var provider = page.ScreenProvider;
        var screen = (PackageScreen)page.Content;
        var package = model.Packages.Single();
        var presentation = (SmokePresentationService)services.GetRequiredService<IPresentationService>();
        Check(screen.FindName("PrimaryActionButton") is SplitButton
            && screen.FindName("MoreButton") is Button
            && screen.FindName("ReloadButton") is Button,
            "Instalados: tela migrada apresenta ação principal, mais ações e recarregar no shell compartilhado");

        var installedService = (SmokePackageBackend.SmokeInstalledPackagesService)services.GetRequiredService<IInstalledPackagesService>();
        var refreshButton = (Button)screen.FindName("ReloadButton");
        int listBeforeRefresh = installedService.ListCount;
        Check(ReferenceEquals(refreshButton.Command, provider.RefreshCommand),
            "Instalados: botão Recarregar está ligado ao comando de atualização");
        await provider.RefreshCommand.ExecuteAsync(null);
        Check(installedService.ListCount == listBeforeRefresh + 1 && model.Packages.Count == 1,
            "Instalados: Recarregar consulta o serviço de pacotes e atualiza a lista");

        var localSource = provider.Filters.Single(filter => filter.Id == "source:local");
        Check(localSource.Icon == PackageScreenCatalog.Default.ResolveIcon("local")
            && localSource.Icon != PackageScreenCatalog.Default.ResolveIcon("installed"),
            "Instalados: PC Local usa um ícone próprio do catálogo");
        Check(provider.Filters.Single(filter => filter.Id == "searchMode").Title == "Modo de pesquisa",
            "Instalados: rótulo interno das opções de pesquisa é Modo de pesquisa");

        var filterButton = (ToggleButton)screen.FindName("FiltersButton");
        InvokePackageScreenHandler(screen, "FiltersButton_Click", filterButton, new RoutedEventArgs());
        Check(!provider.IsFilterOpen && ((FrameworkElement)screen.FindName("Sidebar")).Visibility == Visibility.Collapsed,
            "Instalados: botão Filtros oculta o painel lateral compartilhado");
        InvokePackageScreenHandler(screen, "FiltersButton_Click", filterButton, new RoutedEventArgs());
        Check(provider.IsFilterOpen && ((FrameworkElement)screen.FindName("Sidebar")).Visibility == Visibility.Visible,
            "Instalados: botão Filtros reabre o painel lateral compartilhado");

        var gridButton = (ToggleButton)screen.FindName("DetailsModeButton");
        InvokePackageScreenHandler(screen, "ViewMode_Click", gridButton, new RoutedEventArgs());
        Check(model.ViewMode == "grid" && ((GridView)screen.FindName("GridView")).ItemsSource is not null,
            "Instalados: botão Grade ativa a visualização em cartões");
        var iconsButton = (ToggleButton)screen.FindName("GridModeButton");
        InvokePackageScreenHandler(screen, "ViewMode_Click", iconsButton, new RoutedEventArgs());
        Check(model.ViewMode == "details" && ((GridView)screen.FindName("DetailsView")).ItemsSource is not null,
            "Instalados: botão Ícones ativa a visualização em mosaico");
        var listButton = (ToggleButton)screen.FindName("ListModeButton");
        InvokePackageScreenHandler(screen, "ViewMode_Click", listButton, new RoutedEventArgs());

        PackageScreenFilterOption Filter(string id) => provider.Filters.Single(filter => filter.Id == id);
        void SetFilter(string id, bool value)
        {
            PackageScreenFilterOption filter = Filter(id);
            filter.IsChecked = value;
            provider.SetFilterCommand.Execute(filter);
        }
        SetFilter("source:winget", false);
        Check(model.VisiblePackages.Count == 0, "Instalados: filtro WinGet remove os resultados WinGet");
        SetFilter("source:winget", true);
        Check(model.VisiblePackages.Count == 1, "Instalados: reativar WinGet restaura os resultados");
        SetFilter("source:msstore", false);
        Check(model.VisiblePackages.Count == 1, "Instalados: filtro Microsoft Store mantém apenas as outras fontes");
        SetFilter("source:msstore", true);
        SetFilter("source:local", true);
        Check(model.FilterLocal && model.VisiblePackages.Count == 1, "Instalados: filtro PC Local pode ser ativado sem ocultar WinGet");
        SetFilter("source:local", false);

        PackageScreenFilterOption searchMode = Filter("searchMode");
        searchMode.SelectedValue = "id";
        provider.SetFilterCommand.Execute(searchMode);
        model.SearchText = "ausente";
        Check(model.SearchMode == "ID" && model.VisiblePackages.Count == 0,
            "Instalados: pesquisa por ID e texto filtra os pacotes");
        model.SearchText = "Test.Package";
        Check(model.VisiblePackages.Count == 1, "Instalados: pesquisa encontra o ID exato informado");
        model.SearchText = "Test";
        searchMode.SelectedValue = "exact";
        provider.SetFilterCommand.Execute(searchMode);
        Check(model.VisiblePackages.Count == 0, "Instalados: correspondência exata não aceita trecho parcial");
        model.SearchText = string.Empty;
        searchMode.SelectedValue = "both";
        provider.SetFilterCommand.Execute(searchMode);

        SetFilter("removable", true);
        Check(model.VisiblePackages.Count == 1, "Instalados: filtro Somente removíveis mantém pacotes removíveis");
        SetFilter("system", true);
        Check(model.VisiblePackages.Count == 1, "Instalados: filtro de componentes de sistema não oculta aplicativos comuns");
        SetFilter("selected", true);
        Check(model.VisiblePackages.Count == 0, "Instalados: filtro Somente selecionados respeita a seleção");
        model.Packages.Single().IsSelected = true;
        Check(model.VisiblePackages.Count == 1, "Instalados: selecionar libera o filtro Somente selecionados");
        model.Packages.Single().IsSelected = false;
        SetFilter("selected", false);
        SetFilter("removable", false);
        SetFilter("system", false);

        provider.SetSortCommand.Execute("id");
        Check(model.SortColumn == "Id", "Instalados: cabeçalho do ID altera a ordenação");
        provider.SetSortCommand.Execute("name");
        Check(model.SortColumn == "Name", "Instalados: cabeçalho do Nome altera a ordenação");
        provider.SetSortCommand.Execute("version");
        Check(model.SortColumn == "Version", "Instalados: cabeçalho da Versão altera a ordenação");
        provider.SetSortCommand.Execute("source");
        Check(model.SortColumn == "Source", "Instalados: cabeçalho da Origem altera a ordenação");
        var sortSelector = (ComboBox)screen.FindName("SortSelector");
        sortSelector.SelectedValue = "version";
        provider.SetSortCommand.Execute("version");
        Check(model.SortColumn == "Version", "Instalados: seletor Classificar por altera o campo");
        provider.ToggleSelectAllCommand.Execute(null);
        Check(model.Packages.Single().IsSelected, "Instalados: caixa do cabeçalho seleciona os resultados visíveis");
        provider.ToggleSelectAllCommand.Execute(null);
        Check(!model.Packages.Single().IsSelected, "Instalados: caixa do cabeçalho limpa a seleção");

        package = model.Packages.Single();
        package.IsSelected = true;
        var clipboard = (SmokeClipboardService)services.GetRequiredService<IClipboardService>();
        InvokeInstalledHandler(page, "ManualUninstall_Click", page.FindName("ManualUninstallButton"), new RoutedEventArgs());
        Check(clipboard.LastText == "winget uninstall --id \"Test.Package\"",
            "Instalados: Desinstalação manual copia um comando WinGet executável");

        var contextItem = new MenuFlyoutItem { DataContext = package };
        InvokeInstalledHandler(page, "ContextCopyId_Click", contextItem, new RoutedEventArgs());
        Check(clipboard.LastText == "Test.Package", "Instalados: menu de contexto copia o ID do pacote");
        InvokeInstalledHandler(page, "ContextCopyName_Click", contextItem, new RoutedEventArgs());
        Check(clipboard.LastText == "Aplicativo de teste", "Instalados: menu de contexto copia o nome do pacote");
        InvokeInstalledHandler(page, "ContextCopyUninstall_Click", contextItem, new RoutedEventArgs());
        Check(clipboard.LastText == "winget uninstall --id \"Test.Package\"",
            "Instalados: menu de contexto copia o comando manual");
        InvokeInstalledHandler(page, "ContextOpenLocation_Click", contextItem, new RoutedEventArgs());
        Check(model.Status.Contains("não registrado", StringComparison.OrdinalIgnoreCase),
            "Instalados: Abrir local informa quando o pacote não registra uma pasta");

        bool autoUpdateBefore = model.IsAutoUpdateSelected(package);
        InvokeInstalledHandler(page, "ContextAutoUpdate_Click", contextItem, new RoutedEventArgs());
        Check(model.IsAutoUpdateSelected(package) != autoUpdateBefore,
            "Instalados: menu de contexto alterna inclusão nas atualizações automáticas");
        InvokeInstalledHandler(page, "ContextAutoUpdate_Click", contextItem, new RoutedEventArgs());
        Check(model.IsAutoUpdateSelected(package) == autoUpdateBefore,
            "Instalados: menu de contexto restaura a configuração anterior de atualização automática");

        var contextMenu = new MenuFlyout();
        contextMenu.Items.Add(new MenuFlyoutItem { Text = "Desinstalar" });
        var autoUpdateMenuItem = new ToggleMenuFlyoutItem { Text = "Incluir nas atualizações automáticas" };
        contextMenu.Items.Add(autoUpdateMenuItem);
        typeof(Views.InstalledPackagesPage).GetField("_contextPackage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, package);
        InvokeInstalledHandler(page, "ContextMenu_Opening", contextMenu, new object());
        Check(autoUpdateMenuItem.IsChecked == model.IsAutoUpdateSelected(package)
            && contextMenu.Items.OfType<MenuFlyoutItem>().First().Visibility == Visibility.Visible,
            "Instalados: menu de contexto reflete estado e disponibilidade do pacote");

        var listView = (ListView)page.FindName("InstalledListView");
        listView.UpdateLayout();
        var packageRow = FindVisualElements<Border>(listView).FirstOrDefault(row => row.Tag is InstalledPackageRow);
        Check(packageRow is not null && FlyoutBase.GetAttachedFlyout(packageRow) is MenuFlyout,
            "Instalados: botão direito na linha tem o menu de contexto anexado");

        presentation.RecordDetailsOnly = true;
        int contextDetailsBefore = presentation.DetailsCallCount;
        InvokeInstalledHandler(page, "ContextDetails_Click", contextItem, new RoutedEventArgs());
        presentation.RecordDetailsOnly = false;
        Check(presentation.DetailsCallCount == contextDetailsBefore + 1,
            "Instalados: Detalhes do menu de contexto abre o pacote correto");

        InvokeInstalledHandler(page, "ContextIgnore_Click", contextItem, new RoutedEventArgs());
        Check(model.IgnoredUpdates.Any(entry => entry.AppId == "Test.Package"),
            "Instalados: Ignorar atualizações pelo menu de contexto registra o pacote");
        var ignoredEntry = model.IgnoredUpdates.Single(entry => entry.AppId == "Test.Package");
        InvokeInstalledHandler(page, "RestoreIgnoredUpdate_Click", new Button { DataContext = ignoredEntry }, new RoutedEventArgs());
        Check(model.IgnoredUpdates.All(entry => entry.AppId != "Test.Package"),
            "Instalados: restaurar uma regra ignorada pelo painel funciona");

        presentation.RecordDetailsOnly = true;
        int detailsBefore = presentation.DetailsCallCount;
        InvokeInstalledHandler(page, "PackageDetails_Click", page.FindName("PackageDetailsButton"), new RoutedEventArgs());
        presentation.RecordDetailsOnly = false;
        Check(presentation.DetailsCallCount == detailsBefore + 1 && presentation.LastDetailsApp?.Id == "Test.Package",
            "Instalados: botão Detalhes abre o pacote selecionado");

        var collections = services.GetRequiredService<CollectionsViewModel>();
        InvokeInstalledHandler(page, "AddSelectedToCollection_Click", page.FindName("AddSelectedToCollectionButton"), new RoutedEventArgs());
        Check(collections.Items.Any(item => item.Id == "Test.Package") && model.Status.Contains("adicionado", StringComparison.OrdinalIgnoreCase),
            "Instalados: Adicionar à coleção inclui o pacote selecionado");
        InvokeInstalledHandler(page, "ContextAddToCollection_Click", contextItem, new RoutedEventArgs());
        Check(model.Status.Contains("adicionado", StringComparison.OrdinalIgnoreCase),
            "Instalados: Adicionar à coleção pelo menu de contexto funciona");

        backend.DownloadHandler = (_, _, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new WingetExecutionResult { Success = true });
        };
        var splitButton = (SplitButton)page.FindName("UninstallSelectedButton");
        var downloadItem = ((MenuFlyout)splitButton.Flyout).Items.OfType<MenuFlyoutItem>()
            .Single(item => item.Text.Contains("Baixar instaladores", StringComparison.Ordinal));
        Check(ReferenceEquals(downloadItem.Command, model.DownloadSelectedCommand),
            "Instalados: opção Baixar instaladores está ligada ao comando de download");
        int downloadsBefore = backend.DownloadCount;
        await model.DownloadSelectedCommand.ExecuteAsync(null);
        Check(backend.DownloadCount == downloadsBefore + 1 && backend.LastDownloadId == "Test.Package"
            && backend.LastDownloadSource == "winget" && backend.LastDownloadDirectory == "C:\\Apps\\Exemplo",
            "Instalados: Baixar instalador envia ID, fonte e pasta ao backend");
        downloadsBefore = backend.DownloadCount;
        InvokeInstalledHandler(page, "ContextDownload_Click", contextItem, new RoutedEventArgs());
        await WaitUntilAsync(() => backend.DownloadCount == downloadsBefore + 1 && !model.IsBusy,
            "Download pelo menu de contexto não chamou o backend.");
        Check(backend.DownloadCount == downloadsBefore + 1,
            "Instalados: Baixar instalador do menu de contexto funciona");

        InvokeInstalledHandler(page, "IgnoreSelected_Click", page.FindName("IgnoreSelectedButton"), new RoutedEventArgs());
        Check(model.IgnoredUpdates.Any(entry => entry.AppId == "Test.Package"),
            "Instalados: Ignorar seleção registra o pacote");
        InvokeInstalledHandler(page, "ManageIgnored_Click", page.FindName("ManageIgnoredButton"), new RoutedEventArgs());
        Check(((FrameworkElement)page.FindName("IgnoredUpdatesOverlay")).Visibility == Visibility.Visible,
            "Instalados: Gerenciar ignorados abre o painel");
        InvokeInstalledHandler(page, "RestoreAllIgnored_Click", page.FindName("RestoreAllIgnoredButton"), new RoutedEventArgs());
        Check(model.IgnoredUpdates.Count == 0, "Instalados: restaurar ignorados remove a regra");

        string csvPath = Path.Combine(OutputDirectory, "installed-buttons.csv");
        if (File.Exists(csvPath)) File.Delete(csvPath);
        presentation.SavePath = csvPath;
        var exportButton = (Button)page.FindName("ExportCsvButton");
        Check(ReferenceEquals(exportButton.Command, model.ExportCsvCommand),
            "Instalados: botão Exportar CSV está ligado ao comando de exportação");
        exportButton.Command.Execute(null);
        await WaitUntilAsync(() => File.Exists(csvPath), "Exportar CSV não criou o arquivo de teste.");
        Check(File.Exists(csvPath) && File.ReadAllText(csvPath).Contains("Test.Package", StringComparison.Ordinal),
            "Instalados: Exportar CSV cria o arquivo com o pacote");

        InvokeInstalledHandler(page, "UninstallOptions_Click", page.FindName("UninstallOptionsButton"), new RoutedEventArgs());
        await WaitUntilAsync(() => page.UninstallOptionsDialog is not null,
            "Opções de desinstalação não abriram o diálogo.");
        Check(Equals(page.UninstallOptionsDialog?.Title, "Opções de desinstalação")
            && page.UninstallOptionsDialog.CloseButtonText == "Fechar",
            "Instalados: botão Opções abre o diálogo de ajuda e pode ser fechado");
        page.UninstallOptionsDialog?.Hide();

        backend.UninstallHandler = (_, _, _, _) => Task.FromResult(new WingetExecutionResult { Success = true });
        foreach (string variant in new[] { "Normal", "Admin", "Interactive" })
        {
            model.Packages.Single().IsSelected = true;
            int before = backend.UninstallCount;
            if (variant == "Normal")
                await model.RemoveSelectedCommand.ExecuteAsync(null);
            else
                InvokeInstalledHandler(page, variant == "Admin" ? "RemoveSelectedAdmin_Click" : "RemoveSelectedInteractive_Click",
                    page.FindName("UninstallSelectedButton"), new RoutedEventArgs());
            await WaitUntilAsync(() => backend.UninstallCount == before + 1 && !model.IsBusy,
                $"Desinstalação {variant} não chamou o backend de teste.");
            Check(backend.UninstallCount == before + 1
                && (variant != "Admin" || backend.LastUninstallRequiresElevation)
                && (variant != "Interactive" || backend.LastUninstallInteractive),
                $"Instalados: desinstalação {variant} envia a opção correta ao backend simulado");
        }
        foreach (string variant in new[] { "Normal", "Admin", "Interactive" })
        {
            contextItem.DataContext = model.Packages.Single();
            int before = backend.UninstallCount;
            string handler = variant switch
            {
                "Admin" => "ContextUninstallAdmin_Click",
                "Interactive" => "ContextInteractiveUninstall_Click",
                _ => "ContextUninstall_Click"
            };
            InvokeInstalledHandler(page, handler, contextItem, new RoutedEventArgs());
            await WaitUntilAsync(() => backend.UninstallCount == before + 1 && !model.IsBusy,
                $"Desinstalação de contexto {variant} não chamou o backend de teste.");
            Check(backend.UninstallCount == before + 1
                && (variant != "Admin" || backend.LastUninstallRequiresElevation)
                && (variant != "Interactive" || backend.LastUninstallInteractive),
                $"Instalados: desinstalação {variant} do menu de contexto envia a opção correta");
        }
        foreach (var item in model.Packages) item.IsSelected = false;
        model.Status = string.Empty;
    }

    private static void InvokeInstalledHandler(Views.InstalledPackagesPage page, string handlerName, params object?[] arguments)
    {
        var handler = typeof(Views.InstalledPackagesPage).GetMethod(handlerName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Views.InstalledPackagesPage).FullName, handlerName);
        handler.Invoke(page, arguments);
    }

    private static IEnumerable<T> FindVisualElements<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualElements<T>(child)) yield return descendant;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!condition()) throw new TimeoutException(failureMessage);
    }

    private static async Task VerifyDiscoverSortAsync(Views.HomePage home, AppEntry app)
    {
        AppEntry[] samples = Enumerable.Range(0, 18).Reverse().Select(index => index == 0
            ? app
            : new AppEntry
            {
                Id = $"Sort.Test.Package.{index:D2}",
                Name = $"Aplicativo de teste {index:D2}",
                Version = "1.0.0",
                Source = "winget"
            }).ToArray();
        home.ViewModel.LoadPreview(samples);
        StorefrontViewModel storefront = home.ViewModel.Storefront;
        storefront.SearchText = "Aplicativo de teste";
        storefront.ApplyFilter();
        await Task.Delay(250);
        var provider = home.ScreenProvider;

        bool RowsMatchSort(bool ascending)
        {
            string[] names = provider.Rows.Select(row => row.Name).ToArray();
            string[] expected = ascending
                ? names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray()
                : names.OrderByDescending(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            return names.Length == samples.Length && names.SequenceEqual(expected);
        }

        Check(provider.ViewMode == "list" && provider.SortBy == "relevance" && !provider.SortAscending
            && provider.Rows.Count == samples.Length,
            "Descobrir: ordenação padrão é Relevância e apresenta todos os resultados sem paginação");
        provider.SetSortCommand.Execute("name");
        Check(provider.SortAscending && RowsMatchSort(true),
            "Descobrir: ordenação Nome permanece disponível");
        provider.SetSortCommand.Execute("name");
        Check(!provider.SortAscending && RowsMatchSort(false),
            "Descobrir: alternar Nome aplica ordenação descendente");
        provider.SetSortCommand.Execute("name");
        Check(provider.SortAscending && RowsMatchSort(true),
            "Descobrir: retornar a Nome ascendente restaura a ordem alfabética");

        home.SetAppsViewMode("grid");
        await Task.Delay(80);
        Check(((FrameworkElement)((PackageScreen)home.Content).FindName("GridView")).Visibility == Visibility.Visible
            && RowsMatchSort(true), "Descobrir: grade apresenta a mesma ordenação alfabética");
        home.SetAppsViewMode("details");
        await Task.Delay(80);
        Check(((FrameworkElement)((PackageScreen)home.Content).FindName("DetailsView")).Visibility == Visibility.Visible
            && RowsMatchSort(true), "Descobrir: lista detalhada apresenta a mesma ordenação alfabética");

        home.ViewModel.LoadPreview(new[] { app });
        storefront.SearchText = string.Empty;
        storefront.ApplyFilter();
        home.SetAppsViewMode("list");
    }

    private static async Task WaitForPageAsync(MainWindow window, string tag, ElementTheme theme)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            window.RootElement.UpdateLayout();
            if (window.CurrentPage is { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 })
            {
                Check(true, $"Navegação: {tag} renderiza no tema {theme}");
                return;
            }
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        await CaptureAsync(window.RootElement, "navigation-failure.png");
        var page = window.CurrentPage;
        Check(false, $"Navegação: {tag} não renderizou em 10 segundos no tema {theme}; página={page?.GetType().Name}, carregada={page?.IsLoaded}, tamanho={page?.ActualWidth}x{page?.ActualHeight}, raiz={window.RootElement.ActualWidth}x{window.RootElement.ActualHeight}");
    }
    private static ProgressBar? FindOperationProgress(DependencyObject root, Guid id)
    {
        if (root is ProgressBar { Name: "OperationProgress", DataContext: OperationViewModel item } bar && item.Id == id)
            return bar;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindOperationProgress(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }

    private static FrameworkElement? FindOperationRow(DependencyObject root, Guid id)
    {
        if (root is FrameworkElement element && element.DataContext is OperationViewModel item && item.Id == id)
            return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindOperationRow(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }

    private static Border? FindOperationRowBorder(DependencyObject root, Guid id)
    {
        if (root is Border border && border.DataContext is OperationViewModel item && item.Id == id)
            return border;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindOperationRowBorder(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }

    private static CheckBox? FindPackageCheckBox(DependencyObject root, AppEntry app)
    {
        if (root is CheckBox { DataContext: PackageScreenRow { Payload: AppEntry payload } } normalized
            && payload.Id == app.Id && payload.Source == app.Source) return normalized;
        if (root is CheckBox { Tag: AppEntry tagged } checkBox
            && tagged.Id == app.Id && tagged.Source == app.Source)
            return checkBox;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindPackageCheckBox(VisualTreeHelper.GetChild(root, index), app) is { } found) return found;
        return null;
    }

    private static bool ContainsText(DependencyObject root, string text)
    {
        if (root is TextBlock block && block.Text.Contains(text, StringComparison.Ordinal)) return true;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (ContainsText(VisualTreeHelper.GetChild(root, index), text)) return true;
        return false;
    }

    private static async Task VerifyOperationsPanelHotReloadAsync(MainWindow window, OperationsQueuePanel panel,
        OperationsQueueService queue, OperationsViewModel viewModel, OperationsPanelCatalog catalog,
        OperationTextProvider texts)
    {
        string directory = Path.Combine(OutputDirectory, "operations-hot-reload");
        string localization = Path.Combine(directory, "Localization");
        Directory.CreateDirectory(localization);
        string panelPath = Path.Combine(directory, "operations-panel.json");
        string textPath = Path.Combine(localization, "operations.pt-BR.json");
        string[] preferencePaths =
        [
            Path.Combine(OutputDirectory, "updates-preferences-test.json"),
            Path.Combine(OutputDirectory, "operation-preferences-test.json")
        ];
        byte[]?[] preferencesBefore = preferencePaths.Select(path => File.Exists(path) ? File.ReadAllBytes(path) : null).ToArray();
        OperationsPanelConfiguration originalConfiguration = catalog.Current;
        ElementTheme previousRootTheme = window.RootElement.RequestedTheme;
        ElementTheme previousPanelTheme = panel.RequestedTheme;
        OperationsPanelReloadService? watcher = null;
        OperationItem operation = queue.Enqueue("Reload visual verification", OperationKind.Uninstall);
        operation.State = OperationState.Failed;
        operation.LiveLine = "Falha controlada";
        try
        {
            if (!catalog.TryBuildReloadCandidate(null, out OperationsPanelConfiguration embeddedConfiguration))
                throw new InvalidOperationException("Não foi possível resolver os padrões embutidos do catálogo.");
            catalog.ReplaceConfiguration(embeddedConfiguration);
            await WaitForUiAsync(() => FindOperationRow(panel, operation.Id) is not null);
            if (FindOperationRow(panel, operation.Id) is not FrameworkElement row)
                throw new InvalidOperationException("A linha usada para validar a recarga não foi materializada.");
            string expectedStatus = ((OperationViewModel)row.DataContext).QueueStatusText;
            window.RootElement.RequestedTheme = ElementTheme.Dark;
            panel.RequestedTheme = ElementTheme.Dark;
            DateTime themeDeadline = DateTime.UtcNow.AddSeconds(5);
            while ((window.RootElement.ActualTheme != ElementTheme.Dark || panel.ActualTheme != ElementTheme.Dark)
                && DateTime.UtcNow < themeDeadline)
            {
                window.RootElement.UpdateLayout();
                panel.UpdateLayout();
                await Task.Delay(50);
            }
            if (window.RootElement.ActualTheme != ElementTheme.Dark || panel.ActualTheme != ElementTheme.Dark)
                throw new TimeoutException("O tema escuro não foi aplicado à raiz e ao painel antes da captura de recarga.");
            string captures = Path.Combine(OutputDirectory, "operations-hot-reload");
            // Capture the opaque queue panel host; CaptureAsync waits for matching
            // consecutive renders within each captured frame.
            RenderedCapture before = await CaptureOperationsFixtureAsync(panel, "reload-before.png", captures, expectedStatus);
            watcher = new OperationsPanelReloadService(catalog, catalog, texts, DispatcherQueue.GetForCurrentThread(),
                panelPath, textPath);
            watcher.Start();

            int burstChanges = 0;
            EventHandler burstChanged = (_, _) => burstChanges++;
            catalog.Changed += burstChanged;
            await File.WriteAllTextAsync(panelPath, "{\"schemaVersion\":2,\"theme\":{\"rowCornerRadius\":5}}");
            await Task.Delay(20);
            await File.WriteAllTextAsync(panelPath, "{\"schemaVersion\":2,\"theme\":{\"rowCornerRadius\":6}}");
            await Task.Delay(20);
            await File.WriteAllTextAsync(panelPath, "{\"schemaVersion\":2,\"theme\":{\"rowCornerRadius\":7}}");
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 7) < .001);
            Check(burstChanges == 1, "Operações: gravações próximas são agrupadas pelo debounce");
            catalog.Changed -= burstChanged;
            File.Delete(panelPath);
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 4) < .001);

            byte[] bomJson = System.Text.Encoding.UTF8.GetBytes("\uFEFF{\"schemaVersion\":2,\"theme\":{\"rowCornerRadius\":6}}");
            await File.WriteAllBytesAsync(panelPath, bomJson);
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 6) < .001);
            Check(true, "Operações: recarga aceita arquivo UTF-8 com BOM");
            await File.WriteAllTextAsync(panelPath,
                "{\"schemaVersion\":2, // comentário permitido\n\"theme\":{\"rowCornerRadius\":7}}" );
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 7) < .001);
            Check(true, "Operações: recarga aceita comentários JSON");
            File.Delete(panelPath);
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 4) < .001);

            var reloadSamplesMs = new List<double>(10);
            Border operationRow = FindOperationRowBorder(panel, operation.Id)
                ?? throw new InvalidOperationException("O Border da linha não foi localizado para medir a aplicação visual.");
            for (int index = 0; index < 10; index++)
            {
                double radius = index + 5;
                var timer = Stopwatch.StartNew();
                await File.WriteAllTextAsync(panelPath,
                    $"{{\"schemaVersion\":2,\"theme\":{{\"rowCornerRadius\":{radius}}}}}");
                await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - radius) < .001
                    && Math.Abs(operationRow.CornerRadius.TopLeft - radius) < .001);
                timer.Stop();
                reloadSamplesMs.Add(timer.Elapsed.TotalMilliseconds);
            }
            double reloadAverageMs = reloadSamplesMs.Average();
            double reloadWorstMs = reloadSamplesMs.Max();
            Check(reloadSamplesMs.Count == 10,
                $"Operações: latência em 10 gravações; média {reloadAverageMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} ms, pior caso {reloadWorstMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} ms");
            File.Delete(panelPath);
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 4) < .001
                && Math.Abs(operationRow.CornerRadius.TopLeft - 4) < .001);

            const string changedState = "{\"schemaVersion\":2,\"states\":{\"failed\":{\"colorToken\":\"AppOperationFailureBrush\"}}}";
            string temporaryPath = panelPath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, changedState);
            File.Move(temporaryPath, panelPath, overwrite: true);
            await WaitForUiAsync(() => catalog.Current.States["failed"].ColorToken == "AppOperationFailureBrush");
            await Task.Delay(120);
            RenderedCapture during = await CaptureOperationsFixtureAsync(panel, "reload-during.png", captures, expectedStatus);
            Check(during.Width == before.Width && during.Height == before.Height
                && catalog.Current.States["failed"].ColorToken == "AppOperationFailureBrush"
                && catalog.Current.States["queued"].ColorToken == OperationsPanelCatalog.BuiltInDefaults.States["queued"].ColorToken,
                "Operações: recarga atualiza ao vivo somente o estado alterado");

            File.Delete(panelPath);
            await WaitForUiAsync(() => catalog.Current.States["failed"].ColorToken == defaultPanelColor());
            await Task.Delay(120);
            RenderedCapture after = await CaptureOperationsFixtureAsync(panel, "reload-after-delete.png", captures, expectedStatus);
            Check(after.Width == before.Width && after.Height == before.Height
                && catalog.Current.States["failed"].ColorToken == defaultPanelColor(),
                "Operações: exclusão restaura o snapshot embutido antes da comparação exata das capturas");

            int textEvents = 0;
            EventHandler textChanged = (_, _) => textEvents++;
            texts.Changed += textChanged;
            await File.WriteAllTextAsync(textPath, "{\"operations.button.close\":\"Encerrar agora\"}");
            await WaitForUiAsync(() => texts.GetText("operations.button.close") == "Encerrar agora");
            Check(textEvents > 0, "Operações: sobreposição de idioma publica Changed para linhas existentes");
            File.Delete(textPath);
            await WaitForUiAsync(() => texts.GetText("operations.button.close") != "Encerrar agora");
            texts.Changed -= textChanged;

            string invalid = "{ truncated";
            await File.WriteAllTextAsync(panelPath, invalid);
            await Task.Delay(700);
            Check(catalog.Current.States["failed"].ColorToken == defaultPanelColor(), "Operações: sobreposição inválida mantém o catálogo válido");
            RenderedCapture invalidCapture = await CaptureOperationsFixtureAsync(panel, "reload-invalid-overlay.png", captures, expectedStatus);
            Check(invalidCapture.Width == before.Width && invalidCapture.Height == before.Height
                && catalog.Current.States["failed"].ColorToken == defaultPanelColor(),
                "Operações: sobreposição inválida mantém o snapshot embutido para comparação exata das capturas");

            await File.WriteAllTextAsync(panelPath, string.Empty);
            await Task.Delay(700);
            Check(catalog.Current.States["failed"].ColorToken == defaultPanelColor()
                && Math.Abs(catalog.Current.Theme.RowCornerRadius - 4) < .001,
                "Operações: sobreposição vazia mantém os padrões embutidos");

            string locked = "{\"schemaVersion\":2,\"theme\":{\"rowCornerRadius\":7}}";
            await using (var file = new FileStream(panelPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(locked);
                await file.WriteAsync(data);
                await file.FlushAsync();
                await Task.Delay(230);
            }
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 7) < .001);
            Check(true, "Operações: leitura é repetida até arquivo bloqueado ficar disponível");
            File.Delete(panelPath);
            await WaitForUiAsync(() => Math.Abs(catalog.Current.Theme.RowCornerRadius - 4) < .001);
            await File.WriteAllTextAsync(panelPath, changedState);
            await WaitForUiAsync(() => catalog.Current.States["failed"].ColorToken == "AppOperationFailureBrush");
            Check(true, "Operações: exclusão e recriação durante execução reaplicam padrão e sobreposição");
            bool preferencesUnchanged = true;
            for (int index = 0; index < preferencePaths.Length; index++)
            {
                byte[]? current = File.Exists(preferencePaths[index]) ? File.ReadAllBytes(preferencePaths[index]) : null;
                byte[]? previous = preferencesBefore[index];
                preferencesUnchanged &= current is null ? previous is null : previous is not null && current.SequenceEqual(previous);
            }
            Check(preferencesUnchanged, "Operações: recarga não altera os arquivos de preferências do painel nem da operação");
            catalog.ReplaceConfiguration(catalog.Current with { Theme = catalog.Current.Theme with { RowCornerRadius = 4 } });
        }
        finally
        {
            watcher?.Dispose();
            try { if (File.Exists(panelPath)) File.Delete(panelPath); if (File.Exists(panelPath + ".tmp")) File.Delete(panelPath + ".tmp"); if (File.Exists(textPath)) File.Delete(textPath); } catch { }
            catalog.ReplaceConfiguration(originalConfiguration);
            window.RootElement.RequestedTheme = previousRootTheme;
            panel.RequestedTheme = previousPanelTheme;
            if (!operation.IsFinished) { operation.Cancel(); operation.State = OperationState.Canceled; }
            queue.Remove(operation);
            await Task.Delay(100);
        }

        string defaultPanelColor() => OperationsPanelCatalog.BuiltInDefaults.States["failed"].ColorToken;

        async Task WaitForUiAsync(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            if (!condition()) throw new TimeoutException("A recarga do catálogo não foi observada em cinco segundos.");
        }
    }

    private static async Task<RenderedCapture> CaptureOperationsFixtureAsync(FrameworkElement root, string name,
        string outputDirectory, string? expectedStatus = null)
    {
        // Keep the pointer outside the panel so Button hover/pressed transitions cannot race captures.
        _ = SetCursorPos(0, 0);
        await Task.Delay(350);
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (expectedStatus is not null && !ContainsText(root, expectedStatus) && DateTime.UtcNow < deadline)
        {
            root.UpdateLayout();
            await Task.Delay(50);
        }
        if (expectedStatus is not null && !ContainsText(root, expectedStatus))
            throw new InvalidOperationException($"Captura de operações: o status '{expectedStatus}' não foi renderizado.");

        var progressBars = new List<(ProgressBar Bar, OperationViewModel? ViewModel, bool IsIndeterminate, double Value)>();
        CollectProgressBars(root, progressBars);
        try
        {
            for (int index = 0; index < progressBars.Count; index++)
            {
                var item = progressBars[index];
                if (!item.Bar.IsIndeterminate) continue;
                bool modelIndeterminate = item.ViewModel?.Operation.IsIndeterminate ?? item.IsIndeterminate;
                double modelValue = item.ViewModel?.Operation.Progress ?? item.Value;
                progressBars[index] = (item.Bar, item.ViewModel, modelIndeterminate, modelValue);
                if (item.ViewModel is not null)
                {
                    item.ViewModel.Operation.IsIndeterminate = false;
                    item.ViewModel.Operation.Progress = 50;
                }
                item.Bar.IsIndeterminate = false;
                item.Bar.Value = 50;
            }
            root.UpdateLayout();
            await Task.Delay(100);
            return await CaptureAsync(root, name, outputDirectory, requireStableFrame: true);
        }
        finally
        {
            foreach (var item in progressBars)
            {
                if (item.ViewModel is not null)
                {
                    item.ViewModel.Operation.IsIndeterminate = item.IsIndeterminate;
                    item.ViewModel.Operation.Progress = item.Value;
                }
                item.Bar.IsIndeterminate = item.IsIndeterminate;
                item.Bar.Value = item.Value;
            }
        }
    }


    private static void CollectProgressBars(DependencyObject root, List<(ProgressBar Bar, OperationViewModel? ViewModel, bool IsIndeterminate, double Value)> bars)
    {
        if (root is ProgressBar bar)
        {
            bars.Add((bar, bar.DataContext as OperationViewModel, bar.IsIndeterminate, bar.Value));
            return;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            CollectProgressBars(VisualTreeHelper.GetChild(root, index), bars);
    }

    private static Expander? FindCloudExpander(DependencyObject root)
    {
        if (root is Expander expander) return expander;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindCloudExpander(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    private static async Task<Expander?> WaitForCloudExpanderAsync(ListView list, int index)
    {
        if (index >= 0 && index < list.Items.Count)
            list.ScrollIntoView(list.Items[index]);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            list.UpdateLayout();
            if (list.ContainerFromIndex(index) is DependencyObject container
                && FindCloudExpander(container) is { } expander)
                return expander;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    private static CheckBox? FindOfficeSelection(DependencyObject root, string id)
    {
        if (root is CheckBox { DataContext: OfficeAppToggleViewModel item } selection && item.Id == id)
            return selection;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindOfficeSelection(VisualTreeHelper.GetChild(root, i), id) is { } found)
                return found;
        return null;
    }
    internal static async Task<RenderedCapture> CaptureAsync(FrameworkElement root, string name, string? outputDirectory = null,
        bool requireStableFrame = false)
    {
        // RenderTargetBitmap omits the compositor-owned Mica backdrop. Capture
        // every subtree against the same opaque black/white surface as its theme.
        ElementTheme theme = root.ActualTheme;
        FrameworkElement? appRoot = root;
        while (appRoot is not null && appRoot.Parent is FrameworkElement parentElement)
            appRoot = parentElement;
        if (appRoot is not null)
        {
            theme = appRoot.ActualTheme;
            appRoot.RequestedTheme = theme;
            root.RequestedTheme = theme;
        }
        Panel? panel = root as Panel;
        Brush? backdrop = panel?.Background;
        Control? controlRoot = panel is null ? root as Control : null;
        Brush? controlBackdrop = controlRoot?.Background;
        Border? operationsSurface = root is OperationsQueuePanel queuePanel
            ? queuePanel.FindName("QueueSurface") as Border
            : null;
        Brush? operationsSurfaceBackdrop = operationsSurface?.Background;
        var appResources = Microsoft.UI.Xaml.Application.Current.Resources;
        const string primaryTextBrushKey = "TextFillColorPrimaryBrush";
        bool hadPrimaryTextBrush = appResources.ContainsKey(primaryTextBrushKey);
        object? primaryTextBrush = hadPrimaryTextBrush ? appResources[primaryTextBrushKey] : null;
        var themeTextBrush = new SolidColorBrush(theme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
            : Windows.UI.Color.FromArgb(255, 26, 26, 26));
        appResources[primaryTextBrushKey] = themeTextBrush;
        RefreshOperationTextBrushes(root, themeTextBrush);
        Windows.UI.Color surface = theme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        if (panel is not null) panel.Background = new SolidColorBrush(surface);
        else if (controlRoot is not null) controlRoot.Background = new SolidColorBrush(surface);
        if (operationsSurface is not null) operationsSurface.Background = new SolidColorBrush(surface);
        root.UpdateLayout();
        RenderTargetBitmap? bitmap = null;
        byte[]? pixelBytes = null;
        try
        {
            if (!requireStableFrame)
            {
                bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(root);
                await Task.Delay(150);
                await bitmap.RenderAsync(root);
                var pixels = await bitmap.GetPixelsAsync();
                pixelBytes = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels);
            }
            else
            {
                byte[]? previousPixels = null;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    bitmap = new RenderTargetBitmap();
                    await bitmap.RenderAsync(root);
                    var pixels = await bitmap.GetPixelsAsync();
                    byte[] currentPixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels);
                    if (previousPixels is not null && currentPixels.AsSpan().SequenceEqual(previousPixels))
                    {
                        pixelBytes = currentPixels;
                        break;
                    }
                    previousPixels = currentPixels;
                    await Task.Delay(80);
                }
            }
            if (pixelBytes is null || bitmap is null)
                throw new InvalidOperationException($"Captura controlada '{name}' não estabilizou em duas renderizações consecutivas idênticas.");
        }
        finally
        {
            if (panel is not null) panel.Background = backdrop;
            else if (controlRoot is not null) controlRoot.Background = controlBackdrop;
            if (operationsSurface is not null) operationsSurface.Background = operationsSurfaceBackdrop;
            if (hadPrimaryTextBrush) appResources[primaryTextBrushKey] = primaryTextBrush;
            else appResources.Remove(primaryTextBrushKey);
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(outputDirectory ?? OutputDirectory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        CompositeOverSurface(pixelBytes, surface);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixelBytes);
        await encoder.FlushAsync();
        return new(bitmap.PixelWidth, bitmap.PixelHeight, pixelBytes);
    }

    private static void CompositeOverSurface(byte[] pixels, Windows.UI.Color surface)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            int inverse = 255 - alpha;
            pixels[index] = (byte)(pixels[index] + (surface.B * inverse + 127) / 255);
            pixels[index + 1] = (byte)(pixels[index + 1] + (surface.G * inverse + 127) / 255);
            pixels[index + 2] = (byte)(pixels[index + 2] + (surface.R * inverse + 127) / 255);
            pixels[index + 3] = 255;
        }
    }

    private static void RefreshOperationTextBrushes(DependencyObject root, Brush themeTextBrush)
    {
        if (root is TextBlock textBlock && textBlock.DataContext is OperationViewModel)
            textBlock.Foreground = themeTextBrush;
        else if (root is Button button && button.DataContext is OperationViewModel)
            button.Foreground = themeTextBrush;
        else if (root is Border { Width: 3, Height: 3 } dot)
            dot.Background = themeTextBrush;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            RefreshOperationTextBrushes(VisualTreeHelper.GetChild(root, index), themeTextBrush);
    }
}
