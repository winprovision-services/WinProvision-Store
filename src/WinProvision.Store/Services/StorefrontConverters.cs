using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinProvision.Store.Services;

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
    public object Convert(object value, Type targetType, object parameter, string language) => value is "msstore" ? "Origem: Microsoft Store" : "Origem: WinGet";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
public sealed partial class FormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is null ? "" : string.Format(CultureInfo.CurrentCulture, parameter?.ToString() ?? "{0}", value);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
