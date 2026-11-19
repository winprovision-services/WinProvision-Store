using System.ComponentModel;
using System.Windows.Input;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Controls;

/// <summary>Shared row and card presentation; only realized containers create these elements.</summary>
public sealed partial class PackageScreenRowView : UserControl
{
    public static readonly DependencyProperty DefinitionProperty = DependencyProperty.Register(
        nameof(Definition), typeof(PackageScreenDefinition), typeof(PackageScreenRowView), new PropertyMetadata(null, LayoutChanged));
    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(PackageScreenRowView), new PropertyMetadata("list", LayoutChanged));
    public static readonly DependencyProperty ProviderProperty = DependencyProperty.Register(
        nameof(Provider), typeof(IPackageScreenProvider), typeof(PackageScreenRowView), new PropertyMetadata(null));
    public PackageScreenDefinition? Definition { get => (PackageScreenDefinition?)GetValue(DefinitionProperty); set => SetValue(DefinitionProperty, value); }
    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public IPackageScreenProvider? Provider { get => (IPackageScreenProvider?)GetValue(ProviderProperty); set => SetValue(ProviderProperty, value); }

    private PackageScreenRow? _observed;
    private Border? _surface;
    private CheckBox? _check;
    private Image? _image;
    private ContentControl? _sourceIcon;
    private string? _renderedSourceGlyph;
    private string? _renderedSourceIconUri;
    private string? _renderedPackageIconUri;
    private readonly List<(string Id, TextBlock Text)> _texts = [];
    private readonly List<(string Id, Grid Host)> _statusIcons = [];
    private bool _updating;
    private static PackageScreenCatalog Catalog => PackageScreenCatalog.Default;

    public PackageScreenRowView()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        DataContextChanged += ContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => UpdateValues();
    }
    private static void LayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((PackageScreenRowView)sender).Render();
    private void ContextChanged(FrameworkElement sender, DataContextChangedEventArgs e) { Observe(); ResolveOwner(); Render(); }
    private void OnLoaded(object sender, RoutedEventArgs e) { Observe(); ResolveOwner(); Render(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) { if (_observed is not null) _observed.PropertyChanged -= RowChanged; _observed = null; }
    private void Observe()
    {
        if (_observed is not null) _observed.PropertyChanged -= RowChanged;
        _observed = DataContext as PackageScreenRow;
        if (_observed is not null) _observed.PropertyChanged += RowChanged;
    }
    private void ResolveOwner()
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(this);
        while (parent is not null && parent is not PackageScreen) parent = VisualTreeHelper.GetParent(parent);
        if (parent is PackageScreen owner)
        {
            Definition = owner.Definition;
            Provider = owner.Provider;
        }
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackageScreenRow.ShowPackageIcon))
            Render();
        else
            UpdateValues();
    }

    private void Render()
    {
        _texts.Clear(); _statusIcons.Clear(); _check = null; _image = null; _sourceIcon = null; _renderedSourceGlyph = null; _renderedSourceIconUri = null; _renderedPackageIconUri = null; _surface = null;
        if (DataContext is not PackageScreenRow row || Definition is not { } definition) { Content = null; return; }
        var content = Mode is "list" or "details" ? BuildList(row, definition) : BuildCard(row, definition, false);
        _surface = new Border
        {
            Style = (Style)Application.Current.Resources["PackageScreenSurfaceStyle"],
            Child = content,
            Padding = Mode is "list" or "details" ? new Thickness(0, 6, 8, 6) : new Thickness(14),
            MinHeight = Mode == "list" ? 48 : Mode == "details" ? 64 : 0,
            Tag = row,
            IsTabStop = true
        };
        if (Mode == "grid")
        {
            _surface.Width = 214;
            _surface.Height = 248;
            _surface.Margin = new Thickness(0, 0, 12, 12);
        }
        InstalledSelection.SetKind(_surface, Mode == "grid" ? "Card" : "Row");
        _surface.Tapped += SurfaceTapped;
        _surface.DoubleTapped += SurfaceDoubleTapped;
        _surface.RightTapped += SurfaceRightTapped;
        _surface.KeyDown += SurfaceKeyDown;
        Content = _surface;
        UpdateValues();
    }
    private Grid BuildList(PackageScreenRow row, PackageScreenDefinition definition)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        _check = CreateCheck(); grid.Children.Add(_check);
        int index = 1;
        foreach (string id in GetViewColumns(definition, Mode))
        {
            var column = Catalog.GetColumn(id);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column.Width ?? 100, GridUnitType.Star) });
            FrameworkElement cell;
            if (column.CellTemplate == "package")
            {
                var name = new Grid { ColumnSpacing = row.ShowPackageIcon ? 8 : 0, VerticalAlignment = VerticalAlignment.Center };
                if (row.ShowPackageIcon)
                {
                    name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    name.Children.Add(CreatePackageIcon(32));
                }
                name.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var text = Text(id, 14); text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                string? subtitle = Mode == "details" ? definition.Views.Details.Subtitle : null;
                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    var detail = new StackPanel { Spacing = 2 };
                    detail.Children.Add(text);
                    var status = Text(subtitle, 11);
                    status.Style = (Style)Application.Current.Resources["PageStatusTextStyle"];
                    detail.Children.Add(status);
                    Grid.SetColumn(detail, row.ShowPackageIcon ? 1 : 0); name.Children.Add(detail);
                }
                else { Grid.SetColumn(text, row.ShowPackageIcon ? 1 : 0); name.Children.Add(text); }
                cell = name;
            }
            else if (column.CellTemplate == "source") cell = CreateSource();
            else if (column.CellTemplate == "status") cell = CreateStatus(id);
            else cell = Text(id, 12);
            Grid.SetColumn(cell, index++); grid.Children.Add(cell);
        }
        return grid;
    }
    private static IReadOnlyList<string> GetViewColumns(PackageScreenDefinition definition, string mode)
    {
        IReadOnlyList<string> columns = mode == "details" ? definition.Views.Details.Columns : definition.Views.List.Columns;
        return columns.Count > 0 ? columns : definition.Columns;
    }

    private Grid BuildCard(PackageScreenRow row, PackageScreenDefinition definition, bool compact)
    {
        var grid = new Grid { RowSpacing = 6 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (row.ShowPackageIcon)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = row.ShowPackageIcon ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        var top = new Grid(); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        if (definition.Views.Grid.Badge == "source") top.Children.Add(CreateSource());
        _check = CreateCheck(); Grid.SetColumn(_check, 1); top.Children.Add(_check); grid.Children.Add(top);
        if (row.ShowPackageIcon)
        {
            var icon = CreatePackageIcon(compact ? 46 : 80);
            Grid.SetRow(icon, 1);
            grid.Children.Add(icon);
        }
        var details = new StackPanel { Spacing = 2 };
        IReadOnlyList<string> fields = definition.Views.Grid.Card.Count > 0
            ? definition.Views.Grid.Card
            : new[] { "name", "id", "versionTransition" };
        for (int index = 0; index < fields.Count; index++)
        {
            string fieldId = fields[index];
            FrameworkElement field;
            if (Catalog.GetColumn(fieldId).CellTemplate == "status") field = CreateStatus(fieldId);
            else
            {
                TextBlock text = Text(fieldId, index == 0 ? compact ? 12 : 14 : 11);
                if (index == 0) text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                field = text;
            }
            details.Children.Add(field);
        }
        Grid.SetRow(details, row.ShowPackageIcon ? 2 : 1);
        details.VerticalAlignment = row.ShowPackageIcon ? VerticalAlignment.Top : VerticalAlignment.Center;
        grid.Children.Add(details);
        return grid;
    }
    private CheckBox CreateCheck()
    {
        var check = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        check.Resources["ControlCornerRadius"] = new CornerRadius(4);
        check.Checked += SelectionChanged; check.Unchecked += SelectionChanged;
        return check;
    }
    private void SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_updating && sender is CheckBox check && DataContext is PackageScreenRow { CanSelect: true, IsWorking: false } row)
            row.IsSelected = check.IsChecked == true;
    }
    private TextBlock Text(string id, double size)
    {
        var text = new TextBlock { FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 6, 0) };
        _texts.Add((id, text));
        return text;
    }
    private Grid CreateSource()
    {
        var grid = new Grid { ColumnSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _sourceIcon = new ContentControl { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(_sourceIcon); var text = Text("source", 11); Grid.SetColumn(text, 1); grid.Children.Add(text);
        return grid;
    }
    private FrameworkElement CreateStatus(string id)
    {
        var host = new Grid { Width = 36, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _statusIcons.Add((id, host));
        UpdateStatusIcon(host, DataContext is PackageScreenRow row ? row.GetValue(id) ?? string.Empty : string.Empty);
        return host;
    }

    private static void UpdateStatusIcon(Grid host, string label)
    {
        host.Children.Clear();
        KeyValuePair<string, OfficeCatalogState>? entry = OfficeCatalogRuntime.Current.States
            .FirstOrDefault(item => item.Value.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        OfficeCatalogState? state = entry?.Value;
        string tooltip = state?.Tooltip ?? label;
        FrameworkElement glyph;
        Brush foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        if (state?.Icon.Equals("ProgressRing", StringComparison.OrdinalIgnoreCase) == true)
        {
            glyph = new ProgressRing { Width = 18, Height = 18, IsActive = true, IsIndeterminate = true };
        }
        else
        {
            string iconName = state?.Icon.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') ?? string.Empty;
            Icon icon = Enum.TryParse(iconName, true, out Icon parsedIcon) ? parsedIcon : Icon.CircleLine;
            foreground = state?.Foreground switch
            {
                "success" => (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
                "critical" => (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                "tertiary" => (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                "accent" => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                _ => foreground
            };
            glyph = new FluentIcon { Icon = icon, IconVariant = IconVariant.Regular, IconSize = IconSize.Size24, FontSize = 24, Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (state?.Foreground == "tertiary") glyph.Opacity = 0.58;
        }
        AutomationProperties.SetName(glyph, label);
        ToolTipService.SetToolTip(glyph, tooltip);
        host.Children.Add(glyph);
        AutomationProperties.SetName(host, label);
        ToolTipService.SetToolTip(host, tooltip);
    }
    private Grid CreatePackageIcon(int size)
    {
        var host = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Icon fallbackIcon = Enum.TryParse(Catalog.ResolveIcon("packageFallback"), true, out Icon parsedFallback)
            ? parsedFallback : Icon.Box;
        var fallback = new FluentIcon { Icon = fallbackIcon, IconVariant = IconVariant.Regular, IconSize = IconSize.Size24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(fallback);
        var image = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        AsyncImage.SetImageKind(image, PackageImageKind.PackageIcon);
        AsyncImage.SetDecodePixelWidth(image, size * 2);
        image.RegisterPropertyChangedCallback(AsyncImage.HasContentProperty, (sender, _) => fallback.Visibility = AsyncImage.GetHasContent(sender) ? Visibility.Collapsed : Visibility.Visible);
        host.Children.Add(image); _image = image;
        return host;
    }
    private void UpdateValues()
    {
        if (DataContext is not PackageScreenRow row || _surface is null) return;
        _updating = true;
        try
        {
            foreach (var cell in _texts)
            {
                cell.Text.Text = Catalog.GetColumn(cell.Id).CellTemplate == "version-transition"
                    ? string.IsNullOrWhiteSpace(row.NewVersion) ? row.Version ?? string.Empty : $"{row.Version} → {row.NewVersion}"
                    : row.GetValue(cell.Id) ?? string.Empty;
                ToolTipService.SetToolTip(cell.Text, cell.Text.Text);
            }
            foreach (var (id, host) in _statusIcons)
                UpdateStatusIcon(host, row.GetValue(id) ?? string.Empty);
            if (_check is not null)
            {
                _check.Visibility = Definition?.Selection == "none" ? Visibility.Collapsed : Visibility.Visible;
                _check.IsChecked = row.IsSelected;
                _check.IsEnabled = row.CanSelect && !row.IsWorking;
                AutomationProperties.SetName(_check, string.IsNullOrWhiteSpace(row.Status) ? row.Name : $"{row.Name}, {row.Status}");
                ToolTipService.SetToolTip(_check, row.SelectionHelpText);
            }
            string? sourceIconUri = ActualTheme == ElementTheme.Dark
                ? row.SourceIconUriDark ?? row.SourceIconUri
                : row.SourceIconUri ?? row.SourceIconUriDark;
            if (_sourceIcon is not null && (_renderedSourceGlyph != row.SourceGlyph || _renderedSourceIconUri != sourceIconUri))
            {
                _renderedSourceGlyph = row.SourceGlyph;
                _renderedSourceIconUri = sourceIconUri;
                _sourceIcon.Visibility = string.IsNullOrEmpty(sourceIconUri) && string.IsNullOrEmpty(row.SourceGlyph) ? Visibility.Collapsed : Visibility.Visible;
                if (Uri.TryCreate(sourceIconUri, UriKind.Absolute, out Uri? iconUri))
                {
                    var sourceImage = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform };
                    AutomationProperties.SetName(sourceImage, row.Source);
                    sourceImage.Source = iconUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                        ? new SvgImageSource { UriSource = iconUri }
                        : new BitmapImage { UriSource = iconUri };
                    _sourceIcon.Content = sourceImage;
                }
                else
                {
                    _sourceIcon.Content = Enum.TryParse<Icon>(row.SourceGlyph, out var icon)
                        ? new FluentIcon { Icon = icon, IconSize = IconSize.Size16 }
                        : new FontIcon { Glyph = row.SourceGlyph ?? string.Empty, FontSize = 14 };
                }
            }
            if (_image is not null)
            {
                string? uri = ActualTheme == ElementTheme.Dark
                    ? row.IconUriDark ?? row.IconUri
                    : row.IconUri ?? row.IconUriDark;
                if (uri == WinProvision.Core.Services.IconService.DefaultIconPackUri || uri == "ms-appx:///Assets/Icons/default_app.png") uri = null;
                if (_renderedPackageIconUri != uri && Uri.TryCreate(uri, UriKind.Absolute, out Uri? packageUri)
                    && packageUri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase)
                    && packageUri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    if (AsyncImage.GetSourceUrl(_image) is not null) AsyncImage.SetSourceUrl(_image, null);
                    _image.Source = new SvgImageSource { UriSource = packageUri };
                    AsyncImage.SetHasContent(_image, true);
                    _renderedPackageIconUri = uri;
                }
                else if (_renderedPackageIconUri != uri)
                {
                    if (AsyncImage.GetSourceUrl(_image) != uri) AsyncImage.SetSourceUrl(_image, uri);
                    AsyncImage.SetFallbackUrl(_image, row.IconFallbackUri);
                    _renderedPackageIconUri = uri;
                }
            }
            InstalledSelection.SetIsSelected(_surface, row.IsSelected);
        }
        finally { _updating = false; }
    }
    private void SurfaceTapped(object sender, TappedRoutedEventArgs e)
    {
        if (DataContext is not PackageScreenRow row || Interactive(e.OriginalSource as DependencyObject)) return;
        if (Definition?.Row.Tap == "select") Execute(Provider?.ToggleRowSelectionCommand, row);
        e.Handled = true;
    }
    private void SurfaceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (DataContext is PackageScreenRow row && Definition?.Row.DoubleTap is { } actionId) ExecuteAction(actionId, row);
        e.Handled = true;
    }
    private void SurfaceRightTapped(object sender, RightTappedRoutedEventArgs e) { ShowMenu(); e.Handled = true; }
    private void ShowMenu()
    {
        if (DataContext is not PackageScreenRow row || Provider is null || _surface is null) return;
        MenuFlyout menu = CreateContextMenu(row);
        if (menu.Items.Count > 0) menu.ShowAt(_surface);
    }
    internal MenuFlyout CreateContextMenu(PackageScreenRow row)
    {
        var menu = new MenuFlyout();
        if (Provider is null || Definition is null) return menu;
        foreach (string actionId in Definition.Row.ContextActions)
        {
            PackageScreenActionDefinition definition = Catalog.GetAction(actionId);
            var item = new MenuFlyoutItem { Text = Catalog.GetText(definition.Title, definition.Title), Tag = actionId,
                IsEnabled = Provider.GetActionCommand(actionId, row)?.CanExecute(Provider.GetActionParameter(actionId, row)) == true };
            item.Click += ContextAction_Click;
            string icon = Catalog.ResolveIcon(definition.Icon ?? string.Empty);
            if (Enum.TryParse<Icon>(icon, out var parsed)) item.Icon = new IconSourceElement { IconSource = new FluentIconSource { Icon = parsed } };
            menu.Items.Add(item);
        }
        return menu;
    }
    private void SurfaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DataContext is not PackageScreenRow row) return;
        if (e.Key == Windows.System.VirtualKey.Space) { Execute(Provider?.ToggleRowSelectionCommand, row); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (Definition?.Row.DoubleTap is { } actionId) ExecuteAction(actionId, row);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Application || e.Key == Windows.System.VirtualKey.F10 && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) { ShowMenu(); e.Handled = true; }
    }
    private async void ContextAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string id }
            && DataContext is PackageScreenRow row
            && Provider is { } provider)
            await PackageScreenActionExecutor.ExecuteAsync(provider, id, row);
    }

    private async void ExecuteAction(string id, PackageScreenRow row)
    {
        if (Provider is { } provider) await PackageScreenActionExecutor.ExecuteAsync(provider, id, row);
    }
    private static void Execute(ICommand? command, object? parameter) { if (command?.CanExecute(parameter) == true) command.Execute(parameter); }
    private bool Interactive(DependencyObject? element)
    {
        while (element is not null && !ReferenceEquals(element, _surface))
        {
            if (element is ButtonBase or TextBox or ComboBox) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}
