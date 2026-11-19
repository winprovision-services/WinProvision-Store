using CommunityToolkit.Mvvm.ComponentModel;
namespace WinProvision.Store.ViewModels;
[WinRT.GeneratedBindableCustomProperty]
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    partial void OnIsBusyChanged(bool value) => OnBusyChanged(value);
    protected virtual void OnBusyChanged(bool value) { }
    protected void ReportError(Exception exception)
    {
        ErrorMessage = exception.Message; HasError = true;
        Services.WinProvisionLog.Error(exception, GetType().Name);
    }
}
