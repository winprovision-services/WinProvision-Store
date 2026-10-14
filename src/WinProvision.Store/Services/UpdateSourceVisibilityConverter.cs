using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
namespace WinProvision.Store.Services;
public sealed partial class UpdateSourceVisibilityConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,string language)
    {
        string source=value?.ToString()?.Trim().ToLowerInvariant()??"";
        string kind=source switch { "winget"=>"winget","msstore"=>"msstore",""=>"local",_=>"other" };
        return kind==parameter?.ToString()?Visibility.Visible:Visibility.Collapsed;
    }
    public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();
}
