using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Services;
using WinProvision.Core.Models;

namespace WinProvision.Store.Views;

public sealed partial class InstalledPackagesPage : Page
{
    private Task? _load;
    private ContentDialog? _uninstallOptionsDialog;
    private ContentDialog? _ignoredUpdatesDialog;

    public InstalledPackagesViewModel ViewModel { get; }
    public InstalledPackageScreenProvider ScreenProvider { get; }
    public Task InitialContentReady => _load ?? Task.CompletedTask;
    internal ContentDialog? UninstallOptionsDialog => _uninstallOptionsDialog;

    public InstalledPackagesPage(InstalledPackagesViewModel viewModel, PackageScreenProviderRegistry registry)
    {
        ViewModel = viewModel;
        ScreenProvider = (InstalledPackageScreenProvider)registry.Resolve(PackageScreenCatalog.Default.Get("installed"));
        InitializeComponent();
        ScreenProvider.PresentationActionRequested += HandlePresentationAction;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Packages.Count == 0)
        {
            _load = ScreenProvider.RefreshCommand.ExecuteAsync(null);
            await _load;
        }
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        ScreenProvider.PresentationActionRequested -= HandlePresentationAction;
        CloseIgnoredOverlay();
        _uninstallOptionsDialog?.Hide();
    }

    private async void HandlePresentationAction(string actionId)
    {
        try
        {
            if (actionId == "showIgnored") ShowIgnoredUpdates();
            else if (actionId == "uninstallOptions") await ShowUninstallOptionsAsync();
        }
        catch (Exception exception)
        {
            ViewModel.Status = $"Não foi possível abrir a janela: {exception.Message}";
        }
    }

    internal async void ShowIgnoredUpdates()
    {
        if (_ignoredUpdatesDialog is not null) return;
        ViewModel.RefreshIgnoredUpdates();
        _ignoredUpdatesDialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Atualizações ignoradas",
            Content = CreateIgnoredUpdatesContent(),
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close
        };
        try { await ContentDialogCoordinator.ShowAsync(_ignoredUpdatesDialog); }
        catch (Exception exception) { ViewModel.Status = $"Não foi possível abrir as atualizações ignoradas: {exception.Message}"; }
        finally { _ignoredUpdatesDialog = null; }
    }

    private UIElement CreateIgnoredUpdatesContent()
    {
        var content = new StackPanel { Spacing = 8, MaxWidth = 520 };
        if (ViewModel.IgnoredUpdates.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Nenhuma atualização está sendo ignorada no momento.",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            foreach (var entry in ViewModel.IgnoredUpdates.ToArray())
            {
                var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock
                {
                    Text = $"{entry.AppId}  ·  {entry.Version}",
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                var restore = new Button { Content = "Restaurar", Tag = entry.AppId };
                restore.Click += (_, _) =>
                {
                    if (restore.Tag is string id) ViewModel.RestoreIgnored(id);
                    ViewModel.RefreshIgnoredUpdates();
                    if (_ignoredUpdatesDialog is not null)
                        _ignoredUpdatesDialog.Content = new ScrollViewer { Content = CreateIgnoredUpdatesContent(), MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                };
                Grid.SetColumn(restore, 1);
                row.Children.Add(restore);
                content.Children.Add(row);
            }
        }
        var restoreAll = new Button { Content = "Restaurar todas", HorizontalAlignment = HorizontalAlignment.Left };
        restoreAll.Click += (_, _) =>
        {
            ViewModel.RestoreAllIgnored();
            CloseIgnoredOverlay();
        };
        content.Children.Add(restoreAll);
        return new ScrollViewer { Content = content, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    internal void CloseIgnoredOverlay()
    {
        _ignoredUpdatesDialog?.Hide();
        _ignoredUpdatesDialog = null;
    }

    private async Task ShowUninstallOptionsAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Opções de desinstalação",
            Content = new TextBlock
            {
                Text = "O WinProvision usa o WinGet, a API própria e os comandos de desinstalação registrados no Windows como alternativas.",
                TextWrapping = TextWrapping.Wrap
            },
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close
        };
        _uninstallOptionsDialog = dialog;
        try { await ContentDialogCoordinator.ShowAsync(dialog); }
        finally { _uninstallOptionsDialog = null; }
    }
}
