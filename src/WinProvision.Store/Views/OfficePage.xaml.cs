using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.ComponentModel;
using WinProvision.Store.ViewModels;
using WinRT;

namespace WinProvision.Store.Views;

public sealed partial class OfficePage : Page
{
    private bool _loaded;

    public OfficeViewModel ViewModel { get; }

    public OfficePage(OfficeViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        RefreshAppsView();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            _loaded = true;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }
        ViewModel.RefreshInstalledProducts();
        UpdateColumns(ActualWidth);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        _loaded = false;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns(e.NewSize.Width);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OfficeViewModel.AppsViewMode)) RefreshAppsView();
        if (e.PropertyName == nameof(OfficeViewModel.IsPreviewOpen) && !ViewModel.IsPreviewOpen)
            PreviewXmlButton.Focus(FocusState.Programmatic);
        if (e.PropertyName == nameof(OfficeViewModel.IsPreviewOpen) && ViewModel.IsPreviewOpen)
            DispatcherQueue.TryEnqueue(() =>
            {
                OfficeContentScrollViewer.UpdateLayout();
                OfficeContentScrollViewer.ChangeView(null, OfficeContentScrollViewer.ScrollableHeight, null);
                ClosePreviewButton.Focus(FocusState.Programmatic);
            });
    }
    private void Page_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && ViewModel.IsPreviewOpen)
        {
            ViewModel.ClosePreviewCommand.Execute(null);
            e.Handled = true;
        }
    }
    private async void AutoUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loaded && sender is ToggleSwitch toggle && ViewModel.CanEditUpdatePreference && !ViewModel.ApplyAutoUpdatesCommand.IsRunning)
        {
            ViewModel.RequestedAutoUpdatesEnabled = toggle.IsOn;
            await ViewModel.ApplyAutoUpdatesCommand.ExecuteAsync(null);
        }
    }

    private void UpdateColumns(double width)
    {
        bool compact = width < 900;
        OfficeMainColumn.Width = new GridLength(compact ? 1 : 7.25, GridUnitType.Star);
        OfficeColumnGap.Width = new GridLength(compact ? 0 : 8);
        OfficeInstalledColumn.Width = new GridLength(compact ? 0 : 2.75, compact ? GridUnitType.Pixel : GridUnitType.Star);
        Grid.SetColumn(InstalledProductsPanel, compact ? 0 : 2);
        Grid.SetRow(InstalledProductsPanel, compact ? 1 : 0);
    }
    private void OfficeViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<OfficeAppsViewMode>(tag, out var mode))
            ViewModel.AppsViewMode = mode;
        RefreshAppsView();
    }
    private void RefreshAppsView()
    {
        var mode = ViewModel.AppsViewMode;
        OfficeListViewButton.IsChecked = mode == OfficeAppsViewMode.List;
        OfficeCardsViewButton.IsChecked = mode == OfficeAppsViewMode.Cards;
        OfficeIconsViewButton.IsChecked = mode == OfficeAppsViewMode.Icons;
        OfficeAppsItemsControl.ItemTemplate = Resources[mode == OfficeAppsViewMode.List ? "OfficeListTemplate" : mode == OfficeAppsViewMode.Icons ? "OfficeIconsTemplate" : "OfficeToggleTemplate"].As<DataTemplate>();
        OfficeAppsItemsControl.ItemsPanel = Resources[mode == OfficeAppsViewMode.List ? "OfficeListPanel" : "OfficeWrapPanel"].As<ItemsPanelTemplate>();
    }
}
