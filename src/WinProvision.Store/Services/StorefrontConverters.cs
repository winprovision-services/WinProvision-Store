using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Services;

public sealed partial class ManagerStatusBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => new SolidColorBrush(ColorFor(value?.ToString(), true));
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();

    internal static Windows.UI.Color ColorFor(string? style, bool background) => (style?.ToLowerInvariant(), background) switch
    {
        ("success", true) => Windows.UI.Color.FromArgb(255, 52, 72, 32),
        ("success", false) => Windows.UI.Color.FromArgb(255, 142, 214, 112),
        ("warning", true) => Windows.UI.Color.FromArgb(255, 76, 61, 18),
        ("warning", false) => Windows.UI.Color.FromArgb(255, 255, 220, 90),
        ("error", true) => Windows.UI.Color.FromArgb(255, 80, 35, 35),
        ("error", false) => Windows.UI.Color.FromArgb(255, 255, 160, 150),
        (_, true) => Windows.UI.Color.FromArgb(255, 39, 56, 72),
        _ => Windows.UI.Color.FromArgb(255, 145, 205, 255)
    };
}

public sealed partial class ManagerStatusForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => new SolidColorBrush(ManagerStatusBackgroundConverter.ColorFor(value?.ToString(), false));
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class SourceIconSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is "msstore" ? 48d : 40d;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed partial class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is null || value is string text && string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed partial class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is not true;
}
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => (value is true) != Equals(parameter, "Invert") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is Visibility.Visible;
}
public sealed partial class EmptyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed partial class SourceLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is WinProvision.Core.Models.AppEntry app
        ? $"Origem: {app.CatalogSourceLabel}" : value is "msstore" ? "Origem: Microsoft Store" : "Origem: WinGet";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed partial class FormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is null ? "" : string.Format(CultureInfo.CurrentCulture, parameter?.ToString() ?? "{0}", value);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
