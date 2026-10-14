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
    private bool _coreEventsAttached;

    public MoreViewModel ViewModel { get; }

    public MorePage(MoreViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
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

        WebView2? browser = null;
        _initializationInProgress = true;
        ViewModel.ShowBrowserConnecting(retry);

        try
        {
            browser = _browser ??= CreateBrowser();
            if (browser.CoreWebView2 is null)
                await browser.EnsureCoreWebView2Async();

            if (!ReferenceEquals(_browser, browser) || !IsLoaded || !ViewModel.IsAboutVisible)
                return;

            CoreWebView2 core = browser.CoreWebView2
                ?? throw new InvalidOperationException("O WebView2 não disponibilizou o navegador interno.");

            if (!_coreEventsAttached)
            {
                core.NavigationCompleted += CoreWebView2_NavigationCompleted;
                _coreEventsAttached = true;
            }

            if (retry && _navigationStarted)
            {
                core.Reload();
            }
            else
            {
                _navigationStarted = true;
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
        ProjectBrowserHost.Children.Add(browser);
        return browser;
    }

    private void ReleaseBrowser()
    {
        var browser = _browser;
        _browser = null;
        if (browser is not null)
        {
            if (browser.CoreWebView2 is { } core && _coreEventsAttached)
                core.NavigationCompleted -= CoreWebView2_NavigationCompleted;
            ProjectBrowserHost.Children.Remove(browser);
            browser.Close();
        }

        _coreEventsAttached = false;
        _navigationStarted = false;
        _initializationInProgress = false;
    }

    private void CoreWebView2_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!IsLoaded || !ViewModel.IsAboutVisible || !ReferenceEquals(sender, _browser?.CoreWebView2))
            return;

        if (!e.IsSuccess)
        {
            ViewModel.ShowBrowserError(
                $"Não foi possível carregar o GitHub Pages ({e.WebErrorStatus}). Verifique sua conexão e tente novamente.");
            return;
        }

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
