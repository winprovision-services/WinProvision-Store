using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class AccountSyncPage : Page
{
    private readonly IClipboardService _clipboard;

    public AccountSyncViewModel ViewModel { get; }

    public AccountSyncPage(AccountSyncViewModel viewModel, IClipboardService clipboard)
    {
        InitializeComponent();
        ViewModel = viewModel;
        _clipboard = clipboard;
        DataContext = viewModel;
        SyncDetailsGrid.SizeChanged += (_, _) => UpdateSyncLayout();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.VerificationUriRequested += ViewModel_VerificationUriRequested;
        ViewModel.Refresh();
        RefreshConnectionPresentation();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.VerificationUriRequested -= ViewModel_VerificationUriRequested;
        ViewModel.ConnectCommand.Cancel();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountSyncViewModel.IsConnected))
            RefreshConnectionPresentation();
    }

    private void RefreshConnectionPresentation()
    {
        SyncActiveText.Visibility = ViewModel.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        DisconnectButton.IsEnabled = ViewModel.IsConnected;
        UpdateSyncLayout();
    }

    private void UpdateSyncLayout()
    {
        bool compact = SyncDetailsGrid.ActualWidth < 680;
        SyncDetailsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(LastSyncPanel, compact ? 1 : 0);
        Grid.SetColumn(LastSyncPanel, compact ? 0 : 1);
        LastSyncPanel.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    private async void ViewModel_VerificationUriRequested(object? sender, Uri uri)
        => await OpenVerificationUriAsync(uri);

    private async void VerificationLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string value } && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            await OpenVerificationUriAsync(uri);
    }

    private void CopyOAuthCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.UserCode))
            return;
        ViewModel.StatusText = _clipboard.TrySetText(ViewModel.UserCode)
            ? "Código de autorização copiado."
            : "Não foi possível copiar o código de autorização.";
    }

    private async Task OpenVerificationUriAsync(Uri uri)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(uri))
                ViewModel.StatusText = "Não foi possível abrir o navegador. Acesse github.com/login/device manualmente.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "AccountSyncPage.OpenVerificationUri");
            ViewModel.StatusText = "Não foi possível abrir o navegador. Acesse github.com/login/device manualmente.";
        }
    }
}

