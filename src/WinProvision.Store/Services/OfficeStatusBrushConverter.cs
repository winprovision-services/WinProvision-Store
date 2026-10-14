using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinProvision.Store.Services;

public sealed partial class OfficeStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Application.Current.Resources[value?.ToString() switch
        {
            "Installed" => "OfficeStatusInstalledBrush",
            "Excluded" => "OfficeStatusExcludedBrush",
            _ => "OfficeStatusNeutralBrush"
        }];

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
