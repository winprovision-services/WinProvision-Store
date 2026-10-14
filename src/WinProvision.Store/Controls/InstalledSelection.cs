using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Controls;

// Brush inputs retain their ThemeResource expressions while interaction changes.
public sealed partial class InstalledSelection : DependencyObject
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(InstalledSelection), new PropertyMetadata(false, Changed));
    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(InstalledSelection), new PropertyMetadata("", Changed));
    public static string GetKind(DependencyObject element) => (string)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element, string value) => element.SetValue(KindProperty, value);
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.RegisterAttached("Surface", typeof(Brush), typeof(InstalledSelection), new PropertyMetadata(null, Changed));
    public static Brush? GetSurface(DependencyObject element) => (Brush?)element.GetValue(SurfaceProperty);
    public static void SetSurface(DependencyObject element, Brush value) => element.SetValue(SurfaceProperty, value);
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.RegisterAttached("Stroke", typeof(Brush), typeof(InstalledSelection), new PropertyMetadata(null, Changed));
    public static Brush? GetStroke(DependencyObject element) => (Brush?)element.GetValue(StrokeProperty);
    public static void SetStroke(DependencyObject element, Brush value) => element.SetValue(StrokeProperty, value);
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached("Hover", typeof(Brush), typeof(InstalledSelection), new PropertyMetadata(null, Changed));
    public static Brush? GetHover(DependencyObject element) => (Brush?)element.GetValue(HoverProperty);
    public static void SetHover(DependencyObject element, Brush value) => element.SetValue(HoverProperty, value);
    public static readonly DependencyProperty SelectedSurfaceProperty = DependencyProperty.RegisterAttached("SelectedSurface", typeof(Brush), typeof(InstalledSelection), new PropertyMetadata(null, Changed));
    public static Brush? GetSelectedSurface(DependencyObject element) => (Brush?)element.GetValue(SelectedSurfaceProperty);
    public static void SetSelectedSurface(DependencyObject element, Brush value) => element.SetValue(SelectedSurfaceProperty, value);
    public static readonly DependencyProperty SelectedStrokeProperty = DependencyProperty.RegisterAttached("SelectedStroke", typeof(Brush), typeof(InstalledSelection), new PropertyMetadata(null, Changed));
    public static Brush? GetSelectedStroke(DependencyObject element) => (Brush?)element.GetValue(SelectedStrokeProperty);
    public static void SetSelectedStroke(DependencyObject element, Brush value) => element.SetValue(SelectedStrokeProperty, value);
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(InstalledSelection), new PropertyMetadata(false));
    private static readonly DependencyProperty PointerOverProperty = DependencyProperty.RegisterAttached("PointerOver", typeof(bool), typeof(InstalledSelection), new PropertyMetadata(false));

    private static void Changed(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not Border surface || GetKind(surface).Length == 0) return;
        if (!(bool)surface.GetValue(HookedProperty))
        {
            surface.SetValue(HookedProperty, true);
            surface.Loaded += (_, _) => Refresh(surface);
            surface.PointerEntered += (_, _) => { surface.SetValue(PointerOverProperty, true); Refresh(surface); };
            surface.PointerExited += (_, _) => { surface.SetValue(PointerOverProperty, false); Refresh(surface); };
        }
        Refresh(surface);
    }

    private static void Refresh(Border surface)
    {
        bool selected = GetIsSelected(surface);
        surface.Background = selected ? GetSelectedSurface(surface)
            : (bool)surface.GetValue(PointerOverProperty) ? GetHover(surface) : GetSurface(surface);
        surface.BorderBrush = selected ? GetSelectedStroke(surface) : GetStroke(surface);
        surface.BorderThickness = new Thickness(selected && GetKind(surface) == "Card" ? 1.5 : 1);
    }
}
