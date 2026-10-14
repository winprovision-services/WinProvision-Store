using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace WinProvision.Store.Controls;

public sealed partial class RoundedClip : DependencyObject
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(RoundedClip), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(RoundedClip), new PropertyMetadata(null));
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Border border) return;
        if (border.GetValue(StateProperty) is State old) old.Dispose();
        border.SetValue(StateProperty, args.NewValue is true ? new State(border) : null);
    }
    private sealed partial class State : IDisposable
    {
        private readonly Border _border;
        private CompositionRoundedRectangleGeometry? _geometry;
        private CompositionGeometricClip? _clip;
        internal State(Border border)
        {
            _border = border; border.Loaded += Loaded; border.Unloaded += Unloaded; border.SizeChanged += SizeChanged;
            if (border.IsLoaded) Update();
        }
        private void Loaded(object sender, RoutedEventArgs args) => Update();
        private void SizeChanged(object sender, SizeChangedEventArgs args) => Update();
        private void Unloaded(object sender, RoutedEventArgs args) => Release();
        private void Update()
        {
            if (!_border.IsLoaded) return;
            var visual = ElementCompositionPreview.GetElementVisual(_border);
            _geometry ??= visual.Compositor.CreateRoundedRectangleGeometry();
            _clip ??= visual.Compositor.CreateGeometricClip(_geometry);
            _geometry.Size = new Vector2((float)_border.ActualWidth, (float)_border.ActualHeight);
            _geometry.CornerRadius = new Vector2((float)_border.CornerRadius.TopLeft);
            visual.Clip = _clip;
        }
        private void Release()
        {
            ElementCompositionPreview.GetElementVisual(_border).Clip = null;
            _clip?.Dispose(); _clip = null; _geometry?.Dispose(); _geometry = null;
        }
        public void Dispose() { Release(); _border.Loaded -= Loaded; _border.Unloaded -= Unloaded; _border.SizeChanged -= SizeChanged; }
    }
}
