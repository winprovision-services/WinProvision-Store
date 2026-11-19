using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Controls;

[ContentProperty(Name = nameof(GradientStops))]
public sealed partial class RadialGradientBrush : XamlCompositionBrushBase
{
    public IList<GradientStop> GradientStops { get; } = new List<GradientStop>();
    protected override void OnConnected()
    {
        if (CompositionBrush is not null) return;
        var compositor = CompositionTarget.GetCompositorForCurrentThread();
        var brush = compositor.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.CenterPoint = new Vector2(0.5f, 0.5f);
        brush.EllipseRadius = new Vector2(0.5f, 0.5f);
        foreach (var stop in GradientStops) brush.ColorStops.Add(compositor.CreateColorGradientStop((float)stop.Offset, stop.Color));
        CompositionBrush = brush;
    }
    protected override void OnDisconnected() { CompositionBrush?.Dispose(); CompositionBrush = null; }
}
