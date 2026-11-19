using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinProvision.Store.Services;

public sealed partial class OfflineReportStageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string status && (status.Equals("Em andamento", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Falhou", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
