using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using Windows.System;

namespace WinProvision.Store.Controls;

public sealed partial class AppDetailsOverlay : UserControl
{
    private readonly AppDetailsViewModel _model;
    private readonly IPresentationService _presentation;
    private readonly IDetailsImageService _images;
    private readonly List<Task> _retired = [];
    private Session? _session;
    private string[] _screenshots = [];
    private int _index, _previewGeneration, _lightboxGeneration;
    private UIElement? _previousFocus;
    private Storyboard? _animation;
    private SpriteVisual? _shadowVisual;
    private DropShadow? _shadow;
    public bool IsOpen => Visibility == Visibility.Visible;
    internal bool IsLightboxOpen => ScreenshotLightbox.Visibility == Visibility.Visible;
    internal Border DetailsCard => Card;
    internal bool IsSingleColumn => Grid.GetColumn(AppDetailsRightColumn) == 0;
    internal int ScreenshotIndex => _index;
    public AppDetailsOverlay(AppDetailsViewModel model, IPresentationService presentation, IDetailsImageService images)
    {
        _model = model; _presentation = presentation; _images = images;
        InitializeComponent(); DataContext = model;
        Loaded += (_, _) => _model.PropertyChanged += OptionsStateChanged;
        Unloaded += (_, _) => _model.PropertyChanged -= OptionsStateChanged;
        Visibility = Visibility.Collapsed;
        SizeChanged += (_, _) => UpdateSize();
        Loaded += (_, _) => UpdateShadow();
        Card.SizeChanged += (_, _) => UpdateShadow();
        ActualThemeChanged += (_, _) => UpdateShadow();
        Unloaded += (_, _) => ReleaseShadow();
    }
    private void UpdateShadow()
    {
        if (!IsLoaded || Card.ActualWidth <= 0 || Card.ActualHeight <= 0) return;
        var compositor = ElementCompositionPreview.GetElementVisual(DetailsShadowHost).Compositor;
        if (_shadowVisual is null)
        {
            _shadow = compositor.CreateDropShadow(); _shadow.Opacity = 0.24f; _shadow.BlurRadius = 24; _shadow.Offset = Vector3.Zero; _shadow.Mask = DetailsShadowShape.GetAlphaMask();
            _shadowVisual = compositor.CreateSpriteVisual(); _shadowVisual.Shadow = _shadow;
            ElementCompositionPreview.SetElementChildVisual(DetailsShadowHost, _shadowVisual);
        }
        _shadow!.Color = ActualTheme == ElementTheme.Light ? Windows.UI.Color.FromArgb(255, 0x53, 0x62, 0x73) : Windows.UI.Color.FromArgb(255, 0, 0, 0);
        DetailsShadowHost.Width = Card.ActualWidth; DetailsShadowHost.Height = Card.ActualHeight;
        _shadowVisual.Size = new Vector2((float)Card.ActualWidth, (float)Card.ActualHeight);
    }
    private void ReleaseShadow()
    {
        ElementCompositionPreview.SetElementChildVisual(DetailsShadowHost, null);
        _shadowVisual?.Dispose(); _shadowVisual = null; _shadow?.Dispose(); _shadow = null;
    }
    private sealed partial class Session : IDisposable
    {
        internal CancellationTokenSource Source { get; } = new();
        internal HashSet<Task> Tasks { get; } = [];
        public void Dispose() => Source.Dispose();
    }
    private async Task RetireAsync(Session session)
    {
        session.Source.Cancel();
        try { await Task.WhenAll(session.Tasks.ToArray()); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "DetailsClose"); }
        finally { session.Dispose(); }
    }
    private static void Track(Session session, Task task)
    {
        session.Tasks.Add(task);
        _ = ObserveAsync(session, task);
    }
    private static async Task ObserveAsync(Session session, Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "DetailsTask"); }
        finally { session.Tasks.Remove(task); }
    }
    public Task ShowAsync(AppEntry app, string? currentVersion = null, string? availableVersion = null)
    {
        if (!IsOpen) _previousFocus = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as UIElement;
        _retired.RemoveAll(task => task.IsCompleted);
        if (_session is { } previous) _retired.Add(RetireAsync(previous));
        var session = _session = new Session();
        _model.SetApp(app, currentVersion, availableVersion);
        _model.IsOptionsBodyPanelVisible = false;
        OptionsChevronIcon.Glyph = "\uE70D";
        ScreenshotLightbox.Visibility = Visibility.Collapsed;
        ScreenshotLightboxImage.Source = null;
        UpdateScreenshots(session, false);
        Visibility = Visibility.Visible; UpdateSize();
        AppDetailsContent.ChangeView(0, 0, null, true);
        Animate(true);
        CloseButton.Focus(FocusState.Programmatic);
        var load = LoadDetailsAsync(session, app, currentVersion, availableVersion);
        Track(session, load);
        return load;
    }
    private async Task LoadDetailsAsync(Session session, AppEntry app, string? current, string? available)
    {
        try
        {
            var details = await _model.LoadCatalogDetailsAsync(app, session.Source.Token);
            if (_session != session || session.Source.IsCancellationRequested) return;
            _model.SetApp(details, current, available, preserveOptions: true);
            UpdateScreenshots(session, true);
            if (!_model.IsOfficeStoreOffer)
                await _model.LoadExtendedDetailsAsync(details, session.Source.Token);
        }
        catch (OperationCanceledException) when (session.Source.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "DetailsLoad"); }
    }
    private void UpdateSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Card.Margin = new Thickness(ActualWidth < 641 ? 12 : 24);
        Card.Width = Math.Max(0, Math.Min(1120, ActualWidth - Card.Margin.Left - Card.Margin.Right));
        Card.MaxHeight = Math.Max(0, Math.Min(880, ActualHeight - Card.Margin.Top - Card.Margin.Bottom));
        bool single = Card.Width < 950;
        foreach (var pair in new[] { (AppDetailsColumns, (FrameworkElement)AppDetailsRightColumn), (WallpaperDetailsColumns, (FrameworkElement)WallpaperDetailsRightColumn) })
        {
            pair.Item1.ColumnDefinitions[1].Width = new GridLength(single ? 0 : 36);
            pair.Item1.ColumnDefinitions[2].Width = new GridLength(single ? 0 : 1, GridUnitType.Star);
            pair.Item1.RowDefinitions[1].Height = new GridLength(single ? 16 : 0);
            Grid.SetColumn(pair.Item2, single ? 0 : 2); Grid.SetRow(pair.Item2, single ? 2 : 0);
        }
    }
    private void Animate(bool opening)
    {
        _animation?.Stop();
        if (!FluentMotion.AnimationsEnabled)
        {
            _animation = null;
            Scrim.Opacity = Card.Opacity = 1;
            CardTransform.Y = 0;
            if (!opening) FinishClose();
            return;
        }
        var storyboard = _animation = new Storyboard();
        void Add(DependencyObject target, string property, double from, double to, int milliseconds)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)), EasingFunction = new QuadraticEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn } };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, property); storyboard.Children.Add(animation);
        }
        Add(Scrim, "Opacity", opening ? 0 : 1, opening ? 1 : 0, opening ? 160 : 130);
        Add(Card, "Opacity", opening ? 0 : 1, opening ? 1 : 0, opening ? 190 : 130);
        Add(CardTransform, "Y", opening ? 12 : 0, opening ? 0 : 12, opening ? 220 : 150);
        if (!opening) storyboard.Completed += (_, _) =>
        {
            if (_animation != storyboard) return;
            FinishClose();
        };
        storyboard.Begin();
    }
    internal void RefreshMotionPreference()
    {
        if (FluentMotion.AnimationsEnabled) return;
        _animation?.Stop();
        _animation = null;
        Scrim.Opacity = Card.Opacity = 1;
        CardTransform.Y = 0;
        if (_session is null) FinishClose();
    }
    private void FinishClose()
    {
        Visibility = Visibility.Collapsed;
        if (_previousFocus is FrameworkElement { IsLoaded: true, Visibility: Visibility.Visible } focus)
            focus.Focus(FocusState.Programmatic);
        _previousFocus = null;
    }
    public void Close()
    {
        if (!IsOpen) return;
        if (_session is { } session) { _session = null; _retired.Add(RetireAsync(session)); }
        _previewGeneration++; _screenshots = [];
        CloseLightbox(); ScreenshotImage.Source = null;
        _model.Clear();
        Animate(false);
    }
    public async Task ShutdownAsync()
    {
        Close(); _animation?.Stop(); Visibility = Visibility.Collapsed;
        await Task.WhenAll(_retired);
        _retired.Clear();
        ReleaseShadow();
    }
    public bool HandleBack()
    {
        if (!IsOpen) return false;
        if (IsLightboxOpen) CloseLightbox(); else Close();
        return true;
    }
    private void Overlay_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool altLeft = args.Key == VirtualKey.Left && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (args.Key != VirtualKey.Escape && !altLeft) return;
        HandleBack(); args.Handled = true;
    }
    private void Scrim_Tapped(object sender, TappedRoutedEventArgs args) { Close(); args.Handled = true; }
    private void Card_Tapped(object sender, TappedRoutedEventArgs args) => args.Handled = true;
    private void CloseButton_Click(object sender, RoutedEventArgs args) => Close();
    private void UpdateScreenshots(Session session, bool preserve)
    {
        if (preserve && _screenshots.SequenceEqual(_model.Screenshots, StringComparer.OrdinalIgnoreCase)) return;
        _screenshots = _model.Screenshots;
        _index = preserve ? Math.Clamp(_index, 0, Math.Max(0, _screenshots.Length - 1)) : 0;
        ScreenshotsEmptyText.Text = "Este pacote ainda não tem capturas de tela no catálogo.";
        ScreenshotsEmptyText.Visibility = _screenshots.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotPreviewButton.Visibility = _screenshots.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PreviousScreenshotButton.Visibility = NextScreenshotButton.Visibility = _screenshots.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotLoadingBar.Visibility = Visibility.Collapsed;
        ScreenshotCounterText.Text = _screenshots.Length == 0 ? "" : $"{_index + 1} / {_screenshots.Length}";
        ScreenshotImage.Source = null;
        if (_screenshots.Length > 0) Track(session, LoadScreenshotAsync(session));
    }
    private async Task LoadScreenshotAsync(Session session)
    {
        int generation = ++_previewGeneration, index = _index;
        int lightboxGeneration = ++_lightboxGeneration;
        string url = _screenshots[index];
        ScreenshotImage.Source = null; ScreenshotLightboxImage.Source = null;
        ScreenshotsEmptyText.Visibility = Visibility.Collapsed;
        ScreenshotLoadingBar.Visibility = Visibility.Visible;
        ScreenshotCounterText.Text = $"{index + 1} / {_screenshots.Length}";
        try
        {
            var image = await _images.LoadAsync(url, 720, session.Source.Token);
            if (_session != session || generation != _previewGeneration || session.Source.IsCancellationRequested) return;
            ScreenshotLoadingBar.Visibility = Visibility.Collapsed;
            if (image is null)
            {
                ScreenshotsEmptyText.Text = "Não foi possível carregar esta captura. Verifique o endereço da imagem no catálogo.";
                ScreenshotsEmptyText.Visibility = Visibility.Visible;
                return;
            }
            ScreenshotImage.Source = image;
            if (IsLightboxOpen) await LoadLightboxAsync(session, url, lightboxGeneration);
        }
        catch (OperationCanceledException) when (session.Source.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "DetailsScreenshot"); }
    }
    internal void MoveScreenshot(int offset)
    {
        if (_screenshots.Length < 2 || _session is null) return;
        _index = (_index + offset + _screenshots.Length) % _screenshots.Length;
        Track(_session, LoadScreenshotAsync(_session));
    }
    internal Task OpenLightboxAsync()
    {
        if (ScreenshotImage.Source is null || _session is null || _screenshots.Length == 0) return Task.CompletedTask;
        ScreenshotLightboxImage.Source = ScreenshotImage.Source;
        ScreenshotLightbox.Visibility = Visibility.Visible; CloseScreenshotLightboxButton.Focus(FocusState.Programmatic);
        var task = LoadLightboxAsync(_session, _screenshots[_index], ++_lightboxGeneration);
        Track(_session, task);
        return task;
    }
    private async Task LoadLightboxAsync(Session session, string url, int generation)
    {
        try
        {
            var image = await _images.LoadAsync(url, 1600, session.Source.Token);
            if (image is not null && _session == session && generation == _lightboxGeneration && IsLightboxOpen && !session.Source.IsCancellationRequested) ScreenshotLightboxImage.Source = image;
        }
        catch (OperationCanceledException) when (session.Source.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "DetailsLightbox"); }
    }
    internal void CloseLightbox()
    {
        _lightboxGeneration++;
        ScreenshotLightbox.Visibility = Visibility.Collapsed; ScreenshotLightboxImage.Source = null;
        if (IsOpen) ScreenshotPreviewButton.Focus(FocusState.Programmatic);
    }
    private void PreviousScreenshotButton_Click(object sender, RoutedEventArgs args) => MoveScreenshot(-1);
    private void NextScreenshotButton_Click(object sender, RoutedEventArgs args) => MoveScreenshot(1);
    private async void ScreenshotPreviewButton_Click(object sender, RoutedEventArgs args) => await OpenLightboxAsync();
    private void CloseScreenshotLightboxButton_Click(object sender, RoutedEventArgs args) => CloseLightbox();
    private void ScreenshotLightbox_Tapped(object sender, TappedRoutedEventArgs args) { if (ReferenceEquals(args.OriginalSource, ScreenshotLightbox)) CloseLightbox(); args.Handled = true; }
    private void OptionsStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppDetailsViewModel.IsOptionsBodyPanelVisible))
            OptionsChevronIcon.Glyph = _model.IsOptionsBodyPanelVisible ? "\uE70E" : "\uE70D";
    }
    internal void ToggleOptions()
    {
        _model.IsOptionsBodyPanelVisible = !_model.IsOptionsBodyPanelVisible;
        OptionsChevronIcon.Glyph = _model.IsOptionsBodyPanelVisible ? "\uE70E" : "\uE70D";
    }
    private void OptionsHeader_Click(object sender, TappedRoutedEventArgs args)
    {
        for (DependencyObject? item = args.OriginalSource as DependencyObject; item is not null && item != OptionsHeader; item = VisualTreeHelper.GetParent(item)) if (item is Button) return;
        ToggleOptions(); args.Handled = true;
    }
    private void OptionsToggle_Click(object sender, RoutedEventArgs args) => ToggleOptions();
    private void SaveOptionsButton_Click(object sender, RoutedEventArgs args) { _model.SaveOptions(); _model.IsOptionsBodyPanelVisible = false; OptionsChevronIcon.Glyph = "\uE70D"; }
    private void InstallOptions_Changed(object sender, RoutedEventArgs args) { if (_model.App is null) return; if (ReferenceEquals(sender, InstallAllUsersCheckBox) && InstallAllUsersCheckBox.IsChecked == true) _model.SetUserScope(true); _model.UpdateInstallCommandPreview(); }
    private void InstallArchitectureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args) => _model.UpdateInstallCommandPreview();
    private void CopyInstallCommandButton_Click(object sender, RoutedEventArgs args) => _model.CopyInstallCommand();
    private async void SelectLocationButton_Click(object sender, RoutedEventArgs args) { if (await _presentation.PickFolderAsync() is { } location) _model.SetLocation(location); }
    private static MenuFlyoutItem MenuItem(string text, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled }; item.Click += (_, _) => action(); return item;
    }
    private MenuFlyoutItem AddCollectionItem() => MenuItem(!_model.CanAddCurrentAppToPackages ? "Disponível apenas para WinGet e Microsoft Store" : _model.AlreadyAdded ? "Já está na coleção" : "Adicionar à Coleção", _model.AddCurrentAppToPackages, _model.CanAddCurrentAppToPackages && !_model.AlreadyAdded);
    private void AddSelfToPackages_Click(object sender, RoutedEventArgs args) => _model.AddCurrentAppToPackages();
    private void InstallChevronButton_Click(object sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(MenuItem("Instalar para o usuário atual", () => { _model.SetUserScope(false); _model.InstallCommand.Execute(null); }));
        menu.Items.Add(MenuItem("Instalar para todos os usuários (Admin)", () => { _model.SetUserScope(true); _model.InstallCommand.Execute(null); }));
        if (!string.IsNullOrWhiteSpace(_model.InstallerUrlLinkTag)) menu.Items.Add(MenuItem("Baixar instalador apenas", () => _ = OpenUrlAsync(_model.InstallerUrlLinkTag)));
        menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(AddCollectionItem()); menu.ShowAt((FrameworkElement)sender);
    }
    private void UninstallChevronButton_Click(object sender, RoutedEventArgs args) { var menu = new MenuFlyout(); menu.Items.Add(AddCollectionItem()); menu.ShowAt((FrameworkElement)sender); }
    private async void OpenLink_Click(object sender, RoutedEventArgs args) { if (sender is FrameworkElement { Tag: string url }) await OpenUrlAsync(url); }
    private static async Task OpenUrlAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try { await Launcher.LaunchUriAsync(uri); }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "DetailsLink"); }
    }
}
