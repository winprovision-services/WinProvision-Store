using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace WinProvision.Store.Controls;

public enum MotionKind { None, Content, Entrance, Items, Panel }

/// <summary>Transições nativas sem timers, cópias de páginas ou animações de layout.</summary>
public sealed partial class FluentMotion : DependencyObject
{
    private static readonly UISettings Settings = new();
    public static bool AnimationsEnabled => Settings.AnimationsEnabled;
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(MotionKind), typeof(FluentMotion), new PropertyMetadata(MotionKind.None, OnKindChanged));
    public static MotionKind GetKind(DependencyObject element) => (MotionKind)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element, MotionKind value) => element.SetValue(KindProperty, value);

    private static void OnKindChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element) return;
        element.Loaded -= Element_Loaded;
        if ((MotionKind)args.NewValue != MotionKind.None) element.Loaded += Element_Loaded;
        Apply(element);
    }

    private static void Element_Loaded(object sender, RoutedEventArgs args) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement element)
    {
        element.Transitions = new TransitionCollection();
        if (element is ContentControl previousContent) previousContent.ContentTransitions = new TransitionCollection();
        MotionKind kind = AnimationsEnabled ? GetKind(element) : MotionKind.None;
        var transitions = new TransitionCollection();
        switch (kind)
        {
            case MotionKind.Content:
                transitions.Add(new ContentThemeTransition { HorizontalOffset = 0, VerticalOffset = 16 });
                break;
            case MotionKind.Items:
                transitions.Add(new AddDeleteThemeTransition());
                transitions.Add(new RepositionThemeTransition { IsStaggeringEnabled = false });
                break;
            case MotionKind.Entrance:
            case MotionKind.Panel:
                transitions.Add(new EntranceThemeTransition { FromHorizontalOffset = 0,
                    FromVerticalOffset = kind == MotionKind.Panel ? 24 : 12, IsStaggeringEnabled = false });
                break;
        }
        if (element is ListViewBase list) list.ItemContainerTransitions = transitions;
        else if (element is ContentControl content && GetKind(element) == MotionKind.Content)
            content.ContentTransitions = transitions;
        else element.Transitions = transitions;
    }

    internal static void RefreshTree(DependencyObject root)
    {
        if (root is FrameworkElement element && GetKind(element) != MotionKind.None) Apply(element);
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            RefreshTree(VisualTreeHelper.GetChild(root, index));
    }
}
