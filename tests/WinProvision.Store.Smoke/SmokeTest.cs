using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
namespace WinProvision.Store;

// Valida saúde da interface, navegação, bindings e serialização.
internal static partial class SmokeTest
{
    internal static string OutputDirectory => Path.Combine(AppContext.BaseDirectory, "smoke-results");
    internal static readonly List<string> BindingErrors = [];
    private static readonly List<string> Checks = [];
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Checks.Add(message);
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
        window.HideStartupSplash();
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
        await services.GetRequiredService<UpdatesViewModel>().ScanCommand.ExecuteAsync(null);
        await services.GetRequiredService<InstalledPackagesViewModel>().RefreshCommand.ExecuteAsync(null);
        foreach (ElementTheme theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            window.RootElement.RequestedTheme = theme;
            foreach (string tag in new[] { "home", "updates", "installed", "collections", "office", "account", "settings", "more" })
            {
                window.Navigate(tag);
                await WaitForPageAsync(window, tag, theme);
                if (window.CurrentPage is Views.HomePage home)
                {
                    var rotation = (Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)home.FindName("HeroRotationToggleButton");
                    var banner = (Border)home.FindName("HeroBanner");
                    var pauseIcon = (FontIcon)home.FindName("HeroRotationIcon");
                    Check(Views.HomePage.IsInteractiveChild(pauseIcon, banner)
                        && !Views.HomePage.IsInteractiveChild(home.FindName("HeroBannerImage") as DependencyObject, banner),
                        $"Descobrir: controles do banner não acionam detalhes em {theme}");
                    rotation.IsChecked = false;
                    Check(home.IsHeroRotationPaused, $"Descobrir: pausa interrompe a rotação em {theme}");
                    rotation.IsChecked = true;
                    Check(!home.IsHeroRotationPaused, $"Descobrir: retomada restaura a rotação em {theme}");
                }
                await CaptureAsync(window.RootElement, $"{tag}-{theme}.png");
                if (window.CurrentPage is Views.MorePage more && theme == ElementTheme.Dark
                    && Environment.GetEnvironmentVariable("WINPROVISION_VALIDATE_BROWSER") == "1")
                {
                    more.ViewModel.OpenAboutCommand.Execute(null);
                    await Task.Delay(250);
                    ((FrameworkElement)more.FindName("AboutPanel")).StartBringIntoView();
                    var browserDeadline = DateTime.UtcNow.AddSeconds(30);
                    while (more.ViewModel.IsBrowserStatusVisible && DateTime.UtcNow < browserDeadline)
                        await Task.Delay(250);
                    await CaptureAsync(window.RootElement, "about-browser.png");
                    Check(!more.ViewModel.IsBrowserStatusVisible,
                        $"Sobre: site real carregou no WebView2 ({more.ViewModel.BrowserStatus})");
                    var projectBrowser = ((Grid)more.FindName("ProjectBrowserHost")).Children.OfType<WebView2>().Single();
                    Check(projectBrowser.CoreWebView2.DocumentTitle.Contains("WinProvision Store", StringComparison.Ordinal),
                        "Sobre: documento real do WinProvision Store recebido");
                    more.ViewModel.CloseAboutCommand.Execute(null);
                }
                if (window.CurrentPage is Views.OfficePage office)
                {
                    var selectedApps = office.ViewModel.AppToggleItems.Where(item => item.IsOn).Select(item => item.Id).ToHashSet();
                    office.ViewModel.IsAdvancedExpanded = true;
                    await Task.Delay(200);
                    var offlineToggle = (ToggleSwitch)office.FindName("OfficeOfflineToggle");
                    offlineToggle.IsOn = true;
                    await Task.Delay(50);
                    Check(office.ViewModel.CreateInstallRequest() is { DownloadBeforeInstall: true, SourcePath: not null }
                        && office.ViewModel.BuildPreviewXml()!.Contains("AllowCdnFallback=\"FALSE\""),
                        $"Office: modo offline prepara uma origem local no XML em {theme}");
                    office.ViewModel.AddToCatalogCommand.Execute(null);
                    int collectionCount = collections.Items.Count;
                    Check(collections.Items.Last().App.Office?.DownloadBeforeInstall == true,
                        $"Office: modo offline preservado na coleção em {theme}");
                    var exportedOffice = ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(
                        ProfileDocumentService.Serialize(collections.SelectedProfile!.Snapshot()))).Last();
                    Check(exportedOffice.Source == "office" && exportedOffice.OfficeOptions?.DownloadBeforeInstall == true,
                        $"Office: perfil exportado usa o mesmo contrato de execução da CLI em {theme}");
                    offlineToggle.IsOn = false;
                    office.ViewModel.AddToCatalogCommand.Execute(null);
                    Check(collections.Items.Count == collectionCount && collections.Items.Last().App.Office?.DownloadBeforeInstall == false,
                        $"Office: configuração existente atualiza o modo sem duplicar em {theme}");
                    offlineToggle.IsOn = true;
                    office.ViewModel.AddToCatalogCommand.Execute(null);
                    string officeScript = new CollectionScriptService(null).Build((AppEntry[])[collections.Items.Last().App]);
                    File.WriteAllText(Path.Combine(OutputDirectory, "office-collection.ps1"), officeScript);
                    var officeScroller = (ScrollViewer)office.FindName("OfficeContentScrollViewer");
                    officeScroller.UpdateLayout();
                    officeScroller.ChangeView(null, officeScroller.ScrollableHeight, null, true);
                    await Task.Delay(150);
                    await CaptureAsync(window.RootElement, $"office-offline-{theme}.png");
                    offlineToggle.IsOn = false;
                    office.ViewModel.IsAdvancedExpanded = false;
                    ((ScrollViewer)office.FindName("OfficeContentScrollViewer")).ChangeView(null, 0, null, true);
                    foreach (var mode in Enum.GetValues<OfficeAppsViewMode>())
                    {
                        office.ViewModel.AppsViewMode = mode;
                        await Task.Delay(150);
                        Check(FindOfficeSelection(office, "Word") is { IsChecked: true }
                            && office.ViewModel.SelectedAppCount == selectedApps.Count,
                            $"Office: seleção preservada na visualização {mode} em {theme}");
                        await CaptureAsync(window.RootElement, $"office-{mode}-{theme}.png");
                    }
                    office.ViewModel.AppsViewMode = OfficeAppsViewMode.Cards;
                    await Task.Delay(100);
                    ((ToggleSwitch)office.FindName("AutoUpdatesToggle")).StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.65 });
                    await Task.Delay(150);
                    await CaptureAsync(window.RootElement, $"office-configuration-{theme}.png");
                    ((ScrollViewer)office.FindName("OfficeContentScrollViewer")).ChangeView(null, 0, null, true);
                    await Task.Delay(100);
                    var word = FindOfficeSelection(office, "Word") ?? throw new InvalidOperationException("Seletor do Word não renderizado.");
                    var wordModel = (OfficeAppToggleViewModel)word.DataContext;
                    word.IsChecked = false;
                    Check(!wordModel.IsOn && office.ViewModel.BuildPreviewXml()?.Contains("ID=\"Word\"") == true,
                        $"Office: desseleção individual reflete a exclusão no XML em {theme}");
                    word.IsChecked = true;
                    Check(wordModel.IsOn, $"Office: seleção individual pode ser restaurada em {theme}");
                    office.ViewModel.SelectAllAppsCommand.Execute(null);
                    Check(office.ViewModel.AppToggleItems.Where(item => item.IsEnabled).All(item => item.IsOn),
                        $"Office: selecionar todos inclui os aplicativos habilitados em {theme}");
                    office.ViewModel.SelectAllAppsCommand.Execute(null);
                    Check(office.ViewModel.AppToggleItems.Where(item => item.IsEnabled).All(item => !item.IsOn),
                        $"Office: a mesma ação desmarca todos em {theme}");
                    word.IsChecked = true;
                    Check(office.FindName("ApplyUpdatesButton") is null,
                        $"Office: preferência de atualizações possui um único controle em {theme}");
                    office.ViewModel.PreviewConfigurationCommand.Execute(null);
                    await Task.Delay(250);
                    var closePreview = (Button)office.FindName("ClosePreviewButton");
                    Check(office.ViewModel.IsPreviewOpen && closePreview.IsLoaded,
                        $"Office: fechamento disponível no cabeçalho da prévia XML em {theme}");
                    await CaptureAsync(window.RootElement, $"office-preview-{theme}.png");
                    closePreview.Command.Execute(null);
                    await Task.Delay(100);
                    Check(!office.ViewModel.IsPreviewOpen && ((Border)office.FindName("PreviewPanel")).Visibility == Visibility.Collapsed,
                        $"Office: botão fecha a prévia XML em {theme}");
                    foreach (var item in office.ViewModel.AppToggleItems) item.IsOn = selectedApps.Contains(item.Id);
                }
                if (window.CurrentPage is Views.SettingsPage settings)
                {
                    settings.ViewModel.SearchText = "backup";
                    Check(settings.FindName("CardBackup") is null && settings.FindName("PanelBackup") is null && settings.ViewModel.SearchResults.Count == 0,
                        $"Configurações: backup ausente da página e da pesquisa em {theme}");
                    settings.ViewModel.SearchText = "";
                }
                if (window.CurrentPage is Views.CollectionsPage packages)
                {
                    var cloudImport = (MenuFlyoutItem)packages.FindName("ImportCloudMenuItem");
                    Check(ReferenceEquals(cloudImport.Command, packages.Account.RestoreFromCloudCommand) && !cloudImport.IsEnabled,
                        $"Pacotes: importação da nuvem exige conta conectada em {theme}");
                }
                if (window.CurrentPage is Views.AccountSyncPage accountPage)
                    Check(accountPage.FindName("ImportJsonButton") is null && accountPage.FindName("ExportJsonButton") is null,
                        $"Conta: ações JSON concentradas em Pacotes em {theme}");
            }
        }
        window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
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
        var details = services.GetRequiredService<AppDetailsOverlay>();
        await details.ShowAsync(app);
        await Task.Delay(300);
        Check(details.IsOpen, "Detalhes: overlay abre");
        Check(details.FindName("SkipHashCheckCheckBox") is null, "Integridade: opção de ignorar hash ausente dos detalhes");
        services.GetRequiredService<AppDetailsViewModel>().IsOptionsBodyPanelVisible = true;
        await Task.Delay(150);
        await CaptureAsync(window.RootElement, "details.png");
        details.Close();
        await Task.Delay(300);
        Check(!details.IsOpen, "Detalhes: overlay fecha e conclui a animação");
        var curated = new AppEntry
        {
            Id = "WinProvision.Store", Name = "WinProvision Store", Source = "winget", CatalogOrigin = "curated",
            Publisher = "WinProvision Services", Author = "WinProvision Services", Version = "0.1.2",
            WingetManifestAvailable = false, ReleaseDate = DateTimeOffset.Parse("2026-10-15T01:53:09Z"),
            Tags = ["office", "cli"], CatalogDetailPath = "apps/w/winprovision/store/app.json",
            Media = new CatalogMedia { Screenshots = ["media/curated/screenshot-test.png"] },
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
            Check(model.DeveloperText == "WinProvision Services" && model.IsManifestNotFoundVisible && !model.IsManifestLinkVisible, "Detalhes: desenvolvedor e manifesto ausente da origem própria");
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
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            window.RootElement.RequestedTheme = theme;
            var settingsPage = (Views.SettingsPage)window.CurrentPage!;
            settingsPage.NavigateToCategory("General");
            Check(ReferenceEquals(((Button)settingsPage.FindName("AuthorizeElevationButton")).Command,
                settingsPage.ViewModel.AuthorizeElevationCommand), "Elevação: nova autorização por sessão disponível");
            Check(WinProvision.Core.Services.GsudoElevationService.CacheMinutes == 5, "Elevação: cache fixo de 5 minutos por padrão");
            await CaptureAsync(window.RootElement, $"settings-elevation-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            await Task.Delay(250);
            await CaptureAsync(window.RootElement, $"settings-elevation-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            await Task.Delay(250);
            settingsPage.NavigateToCategory("Interface");
            await CaptureAsync(window.RootElement, $"settings-startup-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            await Task.Delay(250);
            await CaptureAsync(window.RootElement, $"settings-startup-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            await Task.Delay(250);
            settingsPage.NavigateToCategory("Operations");
            Check(ReferenceEquals(((Button)settingsPage.FindName("RefreshCatalogButton")).Command,
                settingsPage.ViewModel.RefreshCatalogCommand), "Catálogo: atualização manual separada da limpeza de cache");
            await CaptureAsync(window.RootElement, $"settings-catalog-{theme}.png");
            Check(ReferenceEquals(((Button)settingsPage.FindName("SaveCacheSettingsButton")).Command,
                settingsPage.ViewModel.SaveCacheSettingsCommand), "Cache: configuração compartilhada disponível");
            Check(ReferenceEquals(((Button)settingsPage.FindName("PruneCacheButton")).Command,
                settingsPage.ViewModel.PruneCacheCommand), "Cache: limpeza seletiva disponível");
            await settingsPage.ViewModel.CacheUsageCommand.ExecuteAsync(null);
            Check(settingsPage.ViewModel.CacheUsageSummary.Contains("Catálogo local:"), "Cache: consulta de espaço local funciona sem rede");
            var cacheScroll = (ScrollViewer)settingsPage.FindName("SubCategoryScrollViewer");
            cacheScroll.ChangeView(null, cacheScroll.ScrollableHeight, null, true);
            await Task.Delay(200);
            await CaptureAsync(window.RootElement, $"settings-cache-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));
            await Task.Delay(250);
            cacheScroll.ChangeView(null, 0, null, true);
            ((FrameworkElement)settingsPage.FindName("CacheSharedDirectoryBox")).StartBringIntoView();
            await Task.Delay(250);
            await CaptureAsync(window.RootElement, $"settings-cache-compact-{theme}.png");
            cacheScroll.ChangeView(null, cacheScroll.ScrollableHeight, null, true);
            await Task.Delay(200);
            await CaptureAsync(window.RootElement, $"settings-cache-actions-compact-{theme}.png");
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1000));
            await Task.Delay(200);
        }
        var queue = services.GetRequiredService<OperationsQueueService>();
        var queueModel = services.GetRequiredService<OperationsViewModel>();
        var operation = queue.Enqueue("Operação de demonstração", OperationKind.Install);
        var popup = (Microsoft.UI.Xaml.Controls.Primitives.Popup)window.RootElement.FindName("QueuePopup");
        var queuePanel = (OperationsQueuePanel)popup.Child;
        foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
        {
            window.RootElement.RequestedTheme = theme;
            queueModel.IsQueueOpen = true;
            await Task.Delay(350);
            Check(popup.IsOpen && popup.SystemBackdrop is MicaBackdrop && queuePanel.IsLoaded,
                $"Operações: superfície Mica abre no tema {theme}");
            Check(ReferenceEquals(queuePanel.DataContext, queueModel) && queuePanel.ActualTheme == theme,
                $"Operações: estado e tema preservados em {theme}");
            Check(popup.HorizontalOffset >= 0 && popup.HorizontalOffset + queuePanel.ActualWidth <= window.RootElement.ActualWidth
                && popup.VerticalOffset + queuePanel.ActualHeight <= window.RootElement.ActualHeight,
                $"Operações: painel permanece dentro da janela em {theme}");
            // RenderTargetBitmap excludes the popup's native Mica; capture its theme fallback.
            var surface = (Border)queuePanel.FindName("QueueSurface");
            var original = surface.Background;
            try
            {
                surface.Background = new SolidColorBrush(theme == ElementTheme.Dark
                    ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
                await CaptureAsync(queuePanel, $"operations-{theme}.png");
            }
            finally { surface.Background = original; }
            queueModel.IsQueueOpen = false;
        }
        operation.State = OperationState.Canceled;
        queue.Remove(operation);
        var host = new ContentControl();
        FluentMotion.SetKind(host, MotionKind.Content);
        Check(host.ContentTransitions.Count == (FluentMotion.AnimationsEnabled ? 1 : 0), "Movimento: preferência do Windows respeitada");
        FluentMotion.SetKind(host, MotionKind.None);
        Check(host.ContentTransitions.Count == 0, "Movimento: transição pode ser desativada");
        Check(BindingErrors.Count == 0, "Bindings: nenhuma falha de resolução");
        WriteResult(true);
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
    private static CheckBox? FindOfficeSelection(DependencyObject root, string id)
    {
        if (root is CheckBox { DataContext: OfficeAppToggleViewModel item } selection && item.Id == id)
            return selection;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindOfficeSelection(VisualTreeHelper.GetChild(root, i), id) is { } found)
                return found;
        return null;
    }
    private static async Task CaptureAsync(FrameworkElement root, string name, string? outputDirectory = null)
    {
        // RenderTargetBitmap omits the compositor-owned Mica backdrop. Use its
        // opaque theme fallback only while rendering, then restore the real brush.
        Panel? panel = root as Panel;
        Brush? backdrop = panel?.Background;
        if (panel is not null) panel.Background = new SolidColorBrush(root.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 243, 243, 243));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        try
        {
            await bitmap.RenderAsync(root);
            await Task.Delay(150);
            await bitmap.RenderAsync(root);
        }
        finally { if (panel is not null) panel.Background = backdrop; }
        var pixels = await bitmap.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(outputDirectory ?? OutputDirectory);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
    }
}
