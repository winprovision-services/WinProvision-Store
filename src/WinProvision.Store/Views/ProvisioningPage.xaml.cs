using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI;
using Windows.UI;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class ProvisioningPage : Page
{
    public ProvisioningPage(ProvisioningViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ProvisioningViewModel.SelectedSystemTheme)
                or nameof(ProvisioningViewModel.IsWallpaperPickerExpanded)
                or nameof(ProvisioningViewModel.SelectedAppsTheme)
                or nameof(ProvisioningViewModel.SelectedTaskbarAlignment)
                or nameof(ProvisioningViewModel.SelectedTaskbarSearch)
                or nameof(ProvisioningViewModel.SelectedTaskbarAutoHide)
                or nameof(ProvisioningViewModel.HasWallpaper)
                or nameof(ProvisioningViewModel.WallpaperPreview))
                UpdateDesktopPreview(viewModel);
        };
        Loaded += (_, _) => UpdateDesktopPreview(viewModel);
        ActualThemeChanged += (_, _) => UpdateDesktopPreview(viewModel);
        UpdateDesktopPreview(viewModel);
    }

    private void DesktopPreviewFrame_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width <= 0) return;
        SetPreviewHeight(args.NewSize.Width);
    }

    private void ProvisioningAccentColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (DataContext is ProvisioningViewModel viewModel)
            viewModel.AccentColorValue = args.NewColor;
    }

    private void WallpaperOption_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ProvisioningWallpaperOption option }
            || DataContext is not ProvisioningViewModel viewModel)
            return;
        if (option.IsLocalChoice)
            viewModel.SelectWallpaperCommand.Execute(null);
        else
            viewModel.SelectCatalogWallpaperCommand.Execute(option);
    }

    private void SetPreviewHeight(double width)
    {
        double height = width * 9d / 16d;
        if (Math.Abs(DesktopPreviewFrame.Height - height) > 0.5)
            DesktopPreviewFrame.Height = height;
        PreviewStartPanel.Height = Math.Min(474, height - 54);
        HiddenTaskbarSwipeLine.Width = Math.Clamp(width * 120 / 1500, 48, 120);
    }

    private void UpdateDesktopPreview(ProvisioningViewModel viewModel)
    {
        if (DesktopPreviewFrame.ActualWidth > 0)
            SetPreviewHeight(DesktopPreviewFrame.ActualWidth);
        bool light = viewModel.SelectedSystemTheme?.Value switch
        {
            WinProvision.Core.Models.Provisioning.SystemThemeMode.Claro => true,
            WinProvision.Core.Models.Provisioning.SystemThemeMode.Escuro => false,
            _ => ActualTheme == ElementTheme.Light
        };
        if (!viewModel.HasWallpaper)
        {
            string asset = light ? "wallpaper-light.png" : "wallpaper-dark.png";
            DesktopPreviewWallpaper.Source = new BitmapImage(new Uri($"ms-appx:///Assets/Windows11Preview/{asset}"));
        }
        else
            DesktopPreviewWallpaper.Source = viewModel.WallpaperPreview;
        bool appLight = viewModel.SelectedAppsTheme?.Value switch
        {
            WinProvision.Core.Models.Provisioning.SystemThemeMode.Claro => true,
            WinProvision.Core.Models.Provisioning.SystemThemeMode.Escuro => false,
            _ => light
        };
        var searchMode = viewModel.SelectedTaskbarSearch?.Value;
        PreviewSearchIcon.Visibility = searchMode == WinProvision.Core.Models.Provisioning.TaskbarSearchBoxMode.ApenasIcone
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewSearchBox.Visibility = searchMode is null or WinProvision.Core.Models.Provisioning.TaskbarSearchBoxMode.CaixaCompleta
            ? Visibility.Visible : Visibility.Collapsed;
        PreviewClock.Text = DateTime.Now.ToString("HH:mm");
        PreviewDate.Text = DateTime.Now.ToString("dd/MM/yyyy");
        string variant = light ? "light" : "dark";
        SvgImageSource Vector(string asset) => new(new Uri($"ms-appx:///Assets/FigmaTaskbar/{asset}-{variant}.svg"));
        PreviewStartIcon.Source = Vector("start");
        PreviewSearchIconGlyph.Source = Vector("search");
        PreviewSearchBoxGlyph.Source = Vector("search");
        PreviewSearchGleam.Source = Vector("gleam");
        PreviewOverflowIcon.Source = Vector("overflow");
        PreviewStoreIcon.Source = new BitmapImage(new Uri($"ms-appx:///Assets/FigmaTaskbar/app4-{variant}.png"));
        DesktopPreviewTaskbar.Background = new SolidColorBrush(light
            ? Color.FromArgb(230, 243, 243, 243)
            : Color.FromArgb(245, 32, 32, 32));
        DesktopPreviewTaskbar.BorderBrush = new SolidColorBrush(light
            ? Color.FromArgb(30, 0, 0, 0)
            : Color.FromArgb(55, 255, 255, 255));
        PreviewSearchBox.Background = new SolidColorBrush(light
            ? Color.FromArgb(128, 255, 255, 255)
            : Color.FromArgb(15, 255, 255, 255));
        PreviewSearchBox.BorderBrush = new SolidColorBrush(light
            ? Color.FromArgb(15, 0, 0, 0)
            : Color.FromArgb(33, 255, 255, 255));
        PreviewSearchLabel.Foreground = new SolidColorBrush(light
            ? Color.FromArgb(156, 0, 0, 0)
            : Color.FromArgb(201, 255, 255, 255));
        bool leftAligned = viewModel.SelectedTaskbarAlignment?.Value
            == WinProvision.Core.Models.Provisioning.TaskbarAlignmentMode.Esquerda;
        bool taskbarAutoHide = viewModel.SelectedTaskbarAutoHide?.Value == true;
        DesktopPreviewTaskbar.Height = taskbarAutoHide ? 24 : 48;
        DesktopPreviewTaskbar.BorderThickness = taskbarAutoHide
            ? new Microsoft.UI.Xaml.Thickness(0)
            : new Microsoft.UI.Xaml.Thickness(0, 1, 0, 0);
        DesktopPreviewTaskbar.Background = taskbarAutoHide
            ? new SolidColorBrush(Colors.Transparent)
            : new SolidColorBrush(light
                ? Color.FromArgb(230, 243, 243, 243)
                : Color.FromArgb(245, 32, 32, 32));
        NormalTaskbarContent.Visibility = taskbarAutoHide ? Visibility.Collapsed : Visibility.Visible;
        HiddenTaskbarContent.Visibility = taskbarAutoHide ? Visibility.Visible : Visibility.Collapsed;
        HiddenTaskbarClock.Text = DateTime.Now.ToString("HH:mm");
        DesktopPreviewTaskbarIcons.HorizontalAlignment = leftAligned
            ? Microsoft.UI.Xaml.HorizontalAlignment.Left
            : Microsoft.UI.Xaml.HorizontalAlignment.Center;
        DesktopPreviewTaskbarIcons.Margin = leftAligned
            ? new Microsoft.UI.Xaml.Thickness(4, 0, 0, 0)
            : new Microsoft.UI.Xaml.Thickness(0);
        var surface = new SolidColorBrush(appLight ? Color.FromArgb(244, 246, 248, 252) : Color.FromArgb(244, 31, 35, 46));
        var stroke = new SolidColorBrush(appLight ? Color.FromArgb(255, 210, 214, 222) : Color.FromArgb(255, 73, 77, 89));
        foreach (var panel in new[] { PreviewStartPanel, PreviewSearchPanel, PreviewQuickSettingsPanel, PreviewNotificationsPanel, PreviewWidgetsPanel })
        {
            panel.Background = surface;
            panel.BorderBrush = stroke;
            panel.RequestedTheme = appLight ? ElementTheme.Light : ElementTheme.Dark;
        }
        DesktopPreviewTaskbar.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
    }
}
