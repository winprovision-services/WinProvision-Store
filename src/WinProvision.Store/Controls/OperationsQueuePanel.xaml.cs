using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Controls;

public sealed partial class OperationsQueuePanel : UserControl
{
    private sealed record OperationIconRequest(Guid OperationId, FrameworkElement Root, FrameworkElement Fallback, bool IsManagerIcon);

    private OperationsViewModel? ViewModel => DataContext as OperationsViewModel;
    internal ItemsControl ItemsControl => OperationsList;
    private IOperationsPanelCatalog? _catalog;
    public OperationsQueuePanel()
    {
        InitializeComponent();
        DataContextChanged += Panel_DataContextChanged;
        Loaded += (_, _) => { if (_catalog is not null) _catalog.Changed -= Catalog_Changed; _catalog = ViewModel?.PanelCatalog; if (_catalog is not null) _catalog.Changed += Catalog_Changed; RefreshVisualResources(); };
        Unloaded += (_, _) => { if (_catalog is not null) _catalog.Changed -= Catalog_Changed; };
        ActualThemeChanged += (_, _) => RefreshVisualResources();
        OperationsList.Loaded += (_, _) => DispatcherQueue.TryEnqueue(LoadVisibleIcons);
        OperationsList.LayoutUpdated += (_, _) => LoadVisibleIcons();
    }
    private void RefreshVisualResources()
    {
        if (Resources is null) return;
        OperationsPanelTheme? theme = ViewModel?.PanelConfiguration.Theme;
        if (theme is null) return;
        bool light = ActualTheme == ElementTheme.Light;
        var message = CreateSurfaceBrush(theme.MessageBackgroundAlpha, light);
        var neutral = CreateSurfaceBrush(theme.NeutralBackgroundAlpha, light);
        var attention = CreateAccentBrush(theme.AttentionBackgroundAlpha,
            (SolidColorBrush)Application.Current.Resources[OperationStatePresentationCatalog.AttentionBaseBrushKey]);
        Resources[OperationStatePresentationCatalog.DescriptionBackgroundBrushKey] = message;
        Resources[OperationStatePresentationCatalog.NeutralBackgroundBrushKey] = neutral;
        Resources[OperationStatePresentationCatalog.AttentionBackgroundBrushKey] = attention;
        Application.Current.Resources[OperationStatePresentationCatalog.DescriptionBackgroundBrushKey] = message;
        Application.Current.Resources[OperationStatePresentationCatalog.NeutralBackgroundBrushKey] = neutral;
        Application.Current.Resources[OperationStatePresentationCatalog.AttentionBackgroundBrushKey] = attention;
        foreach (var row in FindRows(OperationsList))
        {
            if (row.DataContext is not OperationViewModel item) continue;
            if (row.FindName("DescriptionSurface") is Border surface) surface.Background = item.DescriptionBackgroundBrush;
        }
        LoadVisibleIcons();
    }
    private void Panel_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_catalog is not null) _catalog.Changed -= Catalog_Changed;
        _catalog = ViewModel?.PanelCatalog;
        if (_catalog is not null) _catalog.Changed += Catalog_Changed;
        RefreshVisualResources();
    }
    private void Catalog_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshVisualResources);
    private static IEnumerable<FrameworkElement> FindRows(DependencyObject root)
    {
        if (root is FrameworkElement { DataContext: OperationViewModel } row) yield return row;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (FrameworkElement childRow in FindRows(VisualTreeHelper.GetChild(root, index))) yield return childRow;
    }
    private static Microsoft.UI.Xaml.Media.SolidColorBrush CreateSurfaceBrush(double alpha, bool light)
    {
        var baseColor = light ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        byte opacity = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(opacity, baseColor.R, baseColor.G, baseColor.B));
    }
    private static Microsoft.UI.Xaml.Media.SolidColorBrush CreateAccentBrush(double alpha, SolidColorBrush baseBrush)
    {
        byte opacity = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(opacity, baseBrush.Color.R, baseBrush.Color.G, baseBrush.Color.B));
    }
    private void Panel_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ViewModel?.SetAutoDismissPointerInside(true);
    private void Panel_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ViewModel?.SetAutoDismissPointerInside(false);
    private void Panel_GotFocus(object sender, RoutedEventArgs e) => ViewModel?.SetAutoDismissKeyboardFocus(true);
    private void Panel_LostFocus(object sender, RoutedEventArgs e) => DispatcherQueue.TryEnqueue(() => ViewModel?.SetAutoDismissKeyboardFocus(QueueSurface.FocusState != FocusState.Unfocused));
    private void FlexibleRegion_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OperationViewModel model } && e.NewSize.Width > 0)
        {
            model.FlexibleRegionWidth = e.NewSize.Width;
            model.NotifyFlexibleWidthChanged();
        }
    }
    private void BulkMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || ViewModel is not { } vm) return;
        MenuFlyout menu = CreateHeaderMenu(vm);
        menu.ShowAt(button);
    }
    internal MenuFlyout CreateHeaderMenu() => ViewModel is { } vm ? CreateHeaderMenu(vm) : new MenuFlyout();
    private MenuFlyout CreateHeaderMenu(OperationsViewModel vm)
    {
        var menu = new MenuFlyout();
        var context = new OperationActionContext(OperationActionScope.Header, HasFailed: vm.HasFailedOperations,
            HasSucceeded: vm.HasSuccessfulOperations, HasFinished: vm.HasFinishedOperations, HasCancelable: vm.HasPending);
        foreach (ResolvedAction action in vm.ResolveActions(OperationActionScope.Header, context))
        {
            if (!action.Visible) continue;
            if (action.IsSeparator) { menu.Items.Add(new MenuFlyoutSeparator { Tag = action.Id }); continue; }
            AddResolved(menu, vm, action, null);
        }
        Track(menu);
        return menu;
    }
    private async void ItemMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OperationViewModel item } button || ViewModel is not { } vm) return;
        MenuFlyout menu = CreateItemMenu(vm, item);
        menu.ShowAt(button);
        await vm.ResolveInstallLocationIfNeededAsync(item);
    }
    internal MenuFlyout CreateItemMenu(OperationViewModel item) => ViewModel is { } vm ? CreateItemMenu(vm, item) : new MenuFlyout();
    private MenuFlyout CreateItemMenu(OperationsViewModel vm, OperationViewModel item)
    {
        var menu = new MenuFlyout();
        foreach (ResolvedAction action in vm.ResolveActions(OperationActionScope.Item, item.ActionContext)
                     .Where(action => action.Placement == OperationActionPlacement.Menu && action.Visible))
            AddResolved(menu, vm, action, item);
        Track(menu);
        return menu;
    }
    private async void LiveLineButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OperationViewModel item }) await ShowOutputAsync(item);
    }
    internal async Task ShowOutputAsync(OperationViewModel item)
    {
        if (ViewModel is not { } vm) return;
        vm.SelectOutput(item); vm.SetAutoDismissPopupOpen(true);
        try { await OperationOutputDialog.ShowAsync(XamlRoot, vm.OutputTitle, vm.OutputText, vm.GetText); }
        finally { vm.SetAutoDismissPopupOpen(false); }
    }
    private void LoadVisibleIcons()
    {
        foreach (OperationViewModel model in OperationsList.Items)
        {
            var container = FindOperationContainer(OperationsList, model.Id);
            if (container is not null && FindImage(container) is { } image)
            {
                container.Tag = model.Id;
                LoadIcon(container, image, model);
            }
        }
    }
    private static FrameworkElement? FindOperationContainer(DependencyObject root, Guid id)
    {
        if (root is FrameworkElement element && element.DataContext is OperationViewModel model && model.Id == id) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindOperationContainer(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }
    private static async void LoadIcon(FrameworkElement root, Image image, OperationViewModel model)
    {
        var fallback = FindFallback(root);
        if (image is null || fallback is null) return;
        if (fallback is FluentIcons.WinUI.FluentIcon stateIcon)
            stateIcon.Icon = OperationActionIconRegistry.GetIcon(model.StatePresentation.IconId);
        if (image.Tag is OperationIconRequest loaded && loaded.OperationId == model.Id) return;
        image.Tag = new OperationIconRequest(model.Id, root, fallback, false);
        image.Source = null;
        fallback.Visibility = OperationIconFallbackResolver.ShouldShowFallback(OperationIconLoadState.Loading)
            ? Visibility.Visible : Visibility.Collapsed;
        var preferences = App.CurrentApp.Services.GetRequiredService<Services.PackageVisualPreferencesService>();
        if (preferences.GetValue("showPackageIcons") && !string.IsNullOrWhiteSpace(model.IconUrl))
        {
            try
            {
                var source = await preferences.LoadAsync(PackageImageKind.PackageIcon, model.IconUrl, 64, CancellationToken.None);
                if (source is not null && IsCurrentIconRequest(image, model.Id, root))
                {
                    image.Tag = new OperationIconRequest(model.Id, root, fallback, false);
                    image.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!IsCurrentIconRequest(image, model.Id, root)) return;
                        image.Source = source;
                        fallback.Visibility = Visibility.Collapsed;
                    });
                    return;
                }
            }
            catch { }
        }
        await LoadManagerIconAsync(root, image, fallback, model);
    }
    private static bool IsCurrentIconRequest(Image image, Guid operationId, FrameworkElement root) =>
        Equals(root.Tag, operationId) && image.Tag is OperationIconRequest request && request.OperationId == operationId;

    private static async Task LoadManagerIconAsync(FrameworkElement root, Image image, FrameworkElement fallback, OperationViewModel model)
    {
        if (string.IsNullOrWhiteSpace(model.ManagerIconUrl) || !IsCurrentIconRequest(image, model.Id, root)) return;
        image.Tag = new OperationIconRequest(model.Id, root, fallback, true);
        try
        {
            ImageSource? source;
            if (Uri.TryCreate(model.ManagerIconUrl, UriKind.Absolute, out Uri? managerUri)
                && managerUri.Scheme is "ms-appx" or "ms-appdata")
                source = new BitmapImage { DecodePixelWidth = 64, UriSource = managerUri };
            else
                source = await Services.AsyncImage.LoadImageAsync(model.ManagerIconUrl, 64);
            if (source is not null && IsCurrentIconRequest(image, model.Id, root))
                image.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!IsCurrentIconRequest(image, model.Id, root)) return;
                    image.Source = source;
                    fallback.Visibility = Visibility.Collapsed;
                });
        }
        catch { }
    }
    private void OperationImage_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not Image { Tag: OperationIconRequest request } image || !IsCurrentIconRequest(image, request.OperationId, request.Root)) return;
        request.Fallback.Visibility = OperationIconFallbackResolver.ShouldShowFallback(OperationIconLoadState.Loaded)
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OperationImage_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not Image { Tag: OperationIconRequest request } image || !IsCurrentIconRequest(image, request.OperationId, request.Root)) return;
        request.Fallback.Visibility = OperationIconFallbackResolver.ShouldShowFallback(OperationIconLoadState.Failed)
            ? Visibility.Visible : Visibility.Collapsed;
        image.Source = null;
        if (!request.IsManagerIcon && request.Root.DataContext is OperationViewModel model && model.Id == request.OperationId)
            _ = LoadManagerIconAsync(request.Root, image, request.Fallback, model);
    }
    private static Image? FindImage(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Image image) return image;
            if (FindImage(child) is { } nested) return nested;
        }
        return null;
    }
    private static FrameworkElement? FindFallback(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FluentIcons.WinUI.FluentIcon icon) return icon;
            if (FindFallback(child) is { } nested) return nested;
        }
        return null;
    }
    private void AddResolved(MenuFlyout menu, OperationsViewModel vm, ResolvedAction action, OperationViewModel? operation)
    {
        var item = new MenuFlyoutItem
        {
            Text = vm.GetText(action.TextKey),
            Icon = OperationActionIconRegistry.CreateIcon(action.IconId),
            IsEnabled = action.Enabled,
            Tag = action.Id
        };
        if (!action.Enabled && OperationActionRegistry.GetUnavailableTooltipKey(action.Id) is { } tooltipKey)
            ToolTipService.SetToolTip(item, vm.GetText(tooltipKey));
        item.Click += async (_, _) => await ExecuteMenuActionAsync(menu, vm, action, operation);
        AutomationProperties.SetName(item, item.Text);
        menu.Items.Add(item);
    }
    private async Task ExecuteMenuActionAsync(MenuFlyout menu, OperationsViewModel vm, ResolvedAction action, OperationViewModel? operation)
    {
        try { await vm.ExecuteResolvedActionAsync(action.CommandId, operation, ShowOutputAsync); }
        finally { menu.Hide(); }
    }
    internal async Task InvokeMenuActionForSmokeAsync(MenuFlyout menu, OperationsViewModel vm, string actionId, OperationViewModel? operation)
    {
        OperationActionScope scope = operation is null ? OperationActionScope.Header : OperationActionScope.Item;
        OperationActionContext context = operation?.ActionContext ?? new(OperationActionScope.Header,
            HasFailed: vm.HasFailedOperations, HasSucceeded: vm.HasSuccessfulOperations,
            HasFinished: vm.HasFinishedOperations, HasCancelable: vm.HasPending);
        ResolvedAction action = vm.ResolveActions(scope, context).Single(candidate => candidate.Id == actionId && candidate.Visible);
        await ExecuteMenuActionAsync(menu, vm, action, operation);
    }
    private void Track(MenuFlyout menu)
    {
        menu.Opened += (_, _) => ViewModel?.SetAutoDismissPopupOpen(true);
        menu.Closed += (_, _) => ViewModel?.SetAutoDismissPopupOpen(false);
    }
}


