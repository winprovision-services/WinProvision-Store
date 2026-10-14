using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinProvision.Store.Controls;

// Painéis nativos para organização e dimensionamento dos elementos da interface.
public sealed partial class UniformGrid : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(nameof(Columns), typeof(int), typeof(UniformGrid), new PropertyMetadata(0, Invalidate));
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(nameof(Rows), typeof(int), typeof(UniformGrid), new PropertyMetadata(0, Invalidate));
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public int Rows { get => (int)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    private static void Invalidate(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((Panel)sender).InvalidateMeasure();
    private (int Rows, int Columns) Dimensions()
    {
        int count = Math.Max(1, Children.Count(child => child.Visibility == Visibility.Visible));
        int columns = Columns > 0 ? Columns : Rows > 0 ? (int)Math.Ceiling((double)count / Rows) : (int)Math.Ceiling(Math.Sqrt(count));
        return (Rows > 0 ? Rows : (int)Math.Ceiling((double)count / columns), columns);
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        var (rows, columns) = Dimensions();
        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width / columns, availableSize.Height / rows));
            if (child.Visibility == Visibility.Collapsed) continue;
            width = Math.Max(width, child.DesiredSize.Width); height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width * columns, height * rows);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var (rows, columns) = Dimensions();
        double width = finalSize.Width / columns, height = finalSize.Height / rows;
        int i = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) { child.Arrange(new Rect()); continue; }
            child.Arrange(new Rect(i % columns * width, i / columns * height, width, height)); i++;
        }
        return finalSize;
    }
}

public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(WrapPanel),
        new PropertyMetadata(Orientation.Horizontal, OrientationChanged));

    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    private static void OrientationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((WrapPanel)sender).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, width = 0, height = 0;
        bool horizontal = Orientation == Orientation.Horizontal;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            child.Measure(availableSize);
            double primarySize = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            double crossSize = horizontal ? child.DesiredSize.Height : child.DesiredSize.Width;
            double availablePrimarySize = horizontal ? availableSize.Width : availableSize.Height;
            if (lineWidth > 0 && lineWidth + primarySize > availablePrimarySize)
            {
                width = Math.Max(width, lineWidth);
                height += lineHeight;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += primarySize;
            lineHeight = Math.Max(lineHeight, crossSize);
        }

        width = Math.Max(width, lineWidth);
        height += lineHeight;
        return horizontal ? new Size(width, height) : new Size(height, width);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double primaryOffset = 0, crossOffset = 0, lineCrossSize = 0;
        bool horizontal = Orientation == Orientation.Horizontal;
        double availablePrimarySize = horizontal ? finalSize.Width : finalSize.Height;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new Rect());
                continue;
            }

            double primarySize = horizontal ? child.DesiredSize.Width : child.DesiredSize.Height;
            double crossSize = horizontal ? child.DesiredSize.Height : child.DesiredSize.Width;
            if (primaryOffset > 0 && primaryOffset + primarySize > availablePrimarySize)
            {
                crossOffset += lineCrossSize;
                primaryOffset = 0;
                lineCrossSize = 0;
            }

            child.Arrange(horizontal
                ? new Rect(primaryOffset, crossOffset, child.DesiredSize.Width, child.DesiredSize.Height)
                : new Rect(crossOffset, primaryOffset, child.DesiredSize.Width, child.DesiredSize.Height));
            primaryOffset += primarySize;
            lineCrossSize = Math.Max(lineCrossSize, crossSize);
        }

        return finalSize;
    }
}

public enum Dock { Left, Top, Right, Bottom }
public sealed partial class DockPanel : Panel
{
    public static readonly DependencyProperty DockProperty = DependencyProperty.RegisterAttached("Dock", typeof(Dock), typeof(DockPanel), new PropertyMetadata(Dock.Left));
    public static Dock GetDock(DependencyObject element) => (Dock)element.GetValue(DockProperty);
    public static void SetDock(DependencyObject element, Dock value) => element.SetValue(DockProperty, value);
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0, usedWidth = 0, usedHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(Math.Max(0, availableSize.Width - usedWidth), Math.Max(0, availableSize.Height - usedHeight)));
            if (GetDock(child) is Dock.Left or Dock.Right) { height = Math.Max(height, usedHeight + child.DesiredSize.Height); usedWidth += child.DesiredSize.Width; }
            else { width = Math.Max(width, usedWidth + child.DesiredSize.Width); usedHeight += child.DesiredSize.Height; }
        }
        return new Size(Math.Max(width, usedWidth), Math.Max(height, usedHeight));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double left = 0, top = 0, width = finalSize.Width, height = finalSize.Height;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            if (i == Children.Count - 1) { child.Arrange(new Rect(left, top, Math.Max(0, width), Math.Max(0, height))); break; }
            double w = Math.Min(width, child.DesiredSize.Width), h = Math.Min(height, child.DesiredSize.Height);
            switch (GetDock(child))
            {
                case Dock.Left: child.Arrange(new Rect(left, top, w, height)); left += w; width -= w; break;
                case Dock.Right: child.Arrange(new Rect(left + width - w, top, w, height)); width -= w; break;
                case Dock.Top: child.Arrange(new Rect(left, top, width, h)); top += h; height -= h; break;
                case Dock.Bottom: child.Arrange(new Rect(left, top + height - h, width, h)); height -= h; break;
            }
        }
        return finalSize;
    }
}
