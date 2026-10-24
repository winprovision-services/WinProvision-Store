using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class MorePage : Page
{
    private WebView2? _browser;
    private bool _initializationInProgress;
    private bool _navigationStarted;
    private Exception? _browserInitializationError;

    public MoreViewModel ViewModel { get; }

    public MorePage(MoreViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    private void MoreContentScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = e.NewSize.Width;
        double cardWidth = width >= 704
            ? Math.Min(420, (width - 80) / 2)
            : Math.Max(0, width - 24);

        StatusCard.Width = AboutCard.Width = HistoryCard.Width = LogCard.Width = cardWidth;
    }

    private void MorePage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.AboutRetryRequested += ViewModel_AboutRetryRequested;
        if (ViewModel.IsAboutVisible)
            ObserveBrowserOperation(InitializeBrowserAsync(retry: false));
    }

    private void MorePage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.AboutRetryRequested -= ViewModel_AboutRetryRequested;
        ViewModel.CancelLogLoad();
        ReleaseBrowser();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MoreViewModel.LogText))
        {
            DispatcherQueue.TryEnqueue(() =>
                LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null));
        }
        else if (e.PropertyName == nameof(MoreViewModel.IsAboutVisible))
        {
            if (ViewModel.IsAboutVisible)
                ObserveBrowserOperation(InitializeBrowserAsync(retry: false));
            else
                ReleaseBrowser();
        }
    }

    private void ViewModel_AboutRetryRequested(object? sender, System.EventArgs e) =>
        ObserveBrowserOperation(InitializeBrowserAsync(retry: true));

    private async Task InitializeBrowserAsync(bool retry)
    {
        if (_initializationInProgress || (!retry && _navigationStarted))
            return;

        if (retry && _browser?.CoreWebView2 is null)
            ReleaseBrowser();

        WebView2? browser = null;
        _initializationInProgress = true;
        ViewModel.ShowBrowserConnecting(retry);

        try
        {
            browser = _browser ??= CreateBrowser();
            if (browser.CoreWebView2 is null)
            {
                _browserInitializationError = null;
                // An unpackaged app may be installed in Program Files. The browser
                // profile must remain writable without administrative privileges.
                string profileDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinProvisionStoreWinUI3", "WebView2");
                Directory.CreateDirectory(profileDirectory);
                WinProvisionLog.Write("WebView2: preparando o perfil do navegador do projeto.");
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profileDirectory, null);
                if (!ReferenceEquals(_browser, browser) || !IsLoaded || !ViewModel.IsAboutVisible)
                    return;
                await browser.EnsureCoreWebView2Async(environment);
                WinProvisionLog.Write("WebView2: inicialização do navegador do projeto concluída.");
            }

            if (!ReferenceEquals(_browser, browser) || !IsLoaded || !ViewModel.IsAboutVisible)
                return;

            CoreWebView2 core = browser.CoreWebView2
                ?? throw _browserInitializationError
                ?? new InvalidOperationException("O WebView2 não disponibilizou o navegador interno.");

            if (retry && _navigationStarted)
            {
                core.Reload();
            }
            else
            {
                _navigationStarted = true;
                WinProvisionLog.Write("WebView2: carregando o site do projeto.");
                core.Navigate(MoreViewModel.ProjectUri.AbsoluteUri);
            }
        }
        catch (Exception exception)
        {
            if (!IsLoaded || !ViewModel.IsAboutVisible ||
                (browser is not null && !ReferenceEquals(_browser, browser)))
                return;

            _navigationStarted = false;
            WinProvisionLog.Warning(exception, "MorePage.InitializeBrowser");
            ViewModel.ShowBrowserError(
                "Não foi possível iniciar o navegador interno. Verifique o Microsoft Edge WebView2 Runtime e tente novamente.");
        }
        finally
        {
            if (browser is null || ReferenceEquals(_browser, browser))
                _initializationInProgress = false;
        }
    }

    private WebView2 CreateBrowser()
    {
        var browser = new WebView2();
        browser.CoreWebView2Initialized += Browser_CoreWebView2Initialized;
        browser.NavigationCompleted += Browser_NavigationCompleted;
        ProjectBrowserHost.Children.Add(browser);
        return browser;
    }

    private void Browser_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        if (ReferenceEquals(sender, _browser))
            _browserInitializationError = args.Exception;
    }

    private void ReleaseBrowser()
    {
        var browser = _browser;
        _browser = null;
        if (browser is not null)
        {
            browser.CoreWebView2Initialized -= Browser_CoreWebView2Initialized;
            browser.NavigationCompleted -= Browser_NavigationCompleted;
            ProjectBrowserHost.Children.Remove(browser);
            browser.Close();
        }

        _navigationStarted = false;
        _initializationInProgress = false;
    }

    private void Browser_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!IsLoaded || !ViewModel.IsAboutVisible || !ReferenceEquals(sender, _browser))
            return;

        if (!e.IsSuccess)
        {
            ViewModel.ShowBrowserError(
                $"Não foi possível carregar o GitHub Pages ({e.WebErrorStatus}). Verifique sua conexão e tente novamente.");
            return;
        }

        WinProvisionLog.Write("WebView2: site do projeto carregado.");
        ViewModel.HideBrowserStatus();
    }

    private void ObserveBrowserOperation(Task operation) =>
        _ = ObserveBrowserOperationAsync(operation);

    private async Task ObserveBrowserOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "MorePage.BrowserOperation");
            ViewModel.ShowBrowserError("Não foi possível carregar o navegador interno. Tente novamente.");
        }
    }
}
