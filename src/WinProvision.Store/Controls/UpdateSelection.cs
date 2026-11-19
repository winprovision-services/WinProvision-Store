using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace WinProvision.Store.Controls;
public sealed partial class UpdateSelection : DependencyObject
{
    public static readonly DependencyProperty IsSelectedProperty=DependencyProperty.RegisterAttached("IsSelected",typeof(bool),typeof(UpdateSelection),new PropertyMetadata(false,Changed));
    public static bool GetIsSelected(DependencyObject d)=>(bool)d.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject d,bool value)=>d.SetValue(IsSelectedProperty,value);
    public static readonly DependencyProperty KindProperty=DependencyProperty.RegisterAttached("Kind",typeof(string),typeof(UpdateSelection),new PropertyMetadata("Row",Changed));
    public static string GetKind(DependencyObject d)=>(string)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d,string value)=>d.SetValue(KindProperty,value);
    public static readonly DependencyProperty SelectedSurfaceProperty=DependencyProperty.RegisterAttached("SelectedSurface",typeof(Brush),typeof(UpdateSelection),new PropertyMetadata(null,Changed));
    public static Brush? GetSelectedSurface(DependencyObject d)=>(Brush?)d.GetValue(SelectedSurfaceProperty);
    public static void SetSelectedSurface(DependencyObject d,Brush value)=>d.SetValue(SelectedSurfaceProperty,value);
    public static readonly DependencyProperty SelectedStrokeProperty=DependencyProperty.RegisterAttached("SelectedStroke",typeof(Brush),typeof(UpdateSelection),new PropertyMetadata(null,Changed));
    public static Brush? GetSelectedStroke(DependencyObject d)=>(Brush?)d.GetValue(SelectedStrokeProperty);
    public static void SetSelectedStroke(DependencyObject d,Brush value)=>d.SetValue(SelectedStrokeProperty,value);
    private static readonly DependencyProperty HookedProperty=DependencyProperty.RegisterAttached("Hooked",typeof(bool),typeof(UpdateSelection),new PropertyMetadata(false));
    private static readonly DependencyProperty HoverProperty=DependencyProperty.RegisterAttached("Hover",typeof(bool),typeof(UpdateSelection),new PropertyMetadata(false));
    private static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        if(d is not Border border)return;
        if(!(bool)border.GetValue(HookedProperty))
        {
            border.SetValue(HookedProperty,true);
            border.Loaded+=(_,_)=>Refresh(border); border.ActualThemeChanged+=(_,_)=>Refresh(border);
            border.PointerEntered+=(_,_)=>{border.SetValue(HoverProperty,true);Refresh(border);}; border.PointerExited+=(_,_)=>{border.SetValue(HoverProperty,false);Refresh(border);};
        }
        Refresh(border);
    }
    private static Brush Resource(string key)=>(Brush)Application.Current.Resources[key];
    private static void Refresh(Border b)
    {
        bool selected=GetIsSelected(b),hover=(bool)b.GetValue(HoverProperty),card=GetKind(b)!="Row";
        bool dark=b.ActualTheme==ElementTheme.Dark;
        // Selection brushes come from XAML ThemeResource expressions; neutral surfaces follow the current theme.
        b.Background=selected?GetSelectedSurface(b)??Resource("AccentFillColorDefaultBrush"):
            hover?new SolidColorBrush(dark?Windows.UI.Color.FromArgb(255,56,56,56):Windows.UI.Color.FromArgb(255,247,247,247)):
            card?new SolidColorBrush(dark?Windows.UI.Color.FromArgb(255,32,32,32):Windows.UI.Color.FromArgb(255,245,245,245)):new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0));
        b.BorderBrush=selected?GetSelectedStroke(b)??Resource("AccentFillColorDefaultBrush"):
            card?new SolidColorBrush(dark?Windows.UI.Color.FromArgb(255,58,58,58):Windows.UI.Color.FromArgb(255,226,226,226)):new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0));
        b.BorderThickness=new(selected&&GetKind(b)=="Card"?1.5:1);
    }
    public static readonly DependencyProperty FiltersActiveProperty=DependencyProperty.RegisterAttached("FiltersActive",typeof(bool),typeof(UpdateSelection),new PropertyMetadata(false,(d,e)=>{ }));
    public static bool GetFiltersActive(DependencyObject d)=>(bool)d.GetValue(FiltersActiveProperty);
    public static void SetFiltersActive(DependencyObject d,bool value) { d.SetValue(FiltersActiveProperty,value); if(d is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton b)b.BorderBrush=value?Resource("InstallActionBrush"):Resource("ControlStrokeColorDefaultBrush"); }
}
