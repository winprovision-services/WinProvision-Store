using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Controls;

/// <summary>Provides a consistent set of native WinUI 3 symbols for package actions and sources.</summary>
public sealed partial class WinUiIcon : ContentControl
{
    public static readonly DependencyProperty IconNameProperty = DependencyProperty.Register(
        nameof(IconName), typeof(string), typeof(WinUiIcon), new PropertyMetadata(string.Empty, OnIconChanged));

    public string IconName
    {
        get => (string)GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public WinUiIcon()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        RefreshIcon();
    }

    private static void OnIconChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        ((WinUiIcon)sender).RefreshIcon();
    }

    private void RefreshIcon()
    {
        var icon = new SymbolIcon
        {
            Symbol = ResolveSymbol(IconName),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        if (IconName.Equals("chevron_down", StringComparison.OrdinalIgnoreCase))
            icon.RenderTransform = new RotateTransform { Angle = 180 };
        icon.SetBinding(Control.ForegroundProperty, new Binding
        {
            Source = this,
            Path = new PropertyPath(nameof(Foreground))
        });
        var viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Child = icon
        };
        Content = viewbox;
    }

    private static Symbol ResolveSymbol(string? name) => name?.ToLowerInvariant() switch
    {
        "download" or "winget" => Symbol.Download,
        "add_to" => Symbol.Add,
        "open_folder" => Symbol.Folder,
        "save_as" => Symbol.Save,
        "console" => Symbol.Document,
        "delete" => Symbol.Delete,
        "settings" or "setting" => Symbol.Setting,
        "info_round" => Symbol.Help,
        "options" => Symbol.Setting,
        "uac" => Symbol.Admin,
        "interactive" => Symbol.TouchPointer,
        "copy" => Symbol.Copy,
        "dismiss" or "close" => Symbol.Cancel,
        "chevron_down" or "chevron_up" => Symbol.Up,
        "chevron_left" => Symbol.Back,
        "chevron_right" => Symbol.Forward,
        "folder" or "open_folder" => Symbol.Folder,
        "code" or "code_text" => Symbol.Document,
        "window_apps" => Symbol.AllApps,
        "cross" => Symbol.Cancel,
        "success_round" => Symbol.Accept,
        "filter" => Symbol.Filter,
        "search" => Symbol.Find,
        "sources" => Symbol.AllApps,
        "ms_store" => Symbol.Shop,
        "local_pc" => Symbol.View,
        "python" or "powershell" => Symbol.Document,
        "packagesbundle" => Symbol.Library,
        "package" => Symbol.Shop,
        _ => Symbol.OpenFile
    };
}
