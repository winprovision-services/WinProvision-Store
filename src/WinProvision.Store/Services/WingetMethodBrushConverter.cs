using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

public sealed partial class WingetMethodBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => Application.Current.Resources[value switch
    {
        WingetMethod.ComApi => "AppOperationComBrush",
        WingetMethod.OwnApi => "AppOperationApiBrush",
        WingetMethod.WingetExe => "AppOperationWingetBrush",
        _ => "AppOperationDefaultBrush"
    }];
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
