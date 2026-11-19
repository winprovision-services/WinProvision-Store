using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
namespace WinProvision.Store.Services;
public sealed partial class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isVisible = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            isVisible = !isVisible;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        bool isVisible = value is Visibility.Visible;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            isVisible = !isVisible;
        return isVisible;
    }
}
