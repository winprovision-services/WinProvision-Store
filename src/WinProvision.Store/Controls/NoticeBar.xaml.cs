using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Controls;

public sealed partial class NoticeBar : UserControl
{
    public NoticeBar() => InitializeComponent();

    public event EventHandler<string>? ActionInvoked;

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is NoticeOption { HasAction: true } notice)
            ActionInvoked?.Invoke(this, notice.Definition.ActionKey!);
    }
}
