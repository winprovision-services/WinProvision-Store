using Microsoft.UI.Xaml.Data;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

// Legacy bitmap placeholders should use the page's native Package icon instead.
public sealed partial class UpdatePackageIconUrlConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string source = value?.ToString() ?? string.Empty;
        return source.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase)
            || source.Equals("ms-appx:///Assets/Icons/default_app.png", StringComparison.OrdinalIgnoreCase)
            ? string.Empty : source;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
