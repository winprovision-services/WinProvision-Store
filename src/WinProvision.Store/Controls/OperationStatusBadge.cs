using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinProvision.Core.Models;

namespace WinProvision.Store.Controls;

public sealed partial class OperationStatusBadge : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(OperationStatusBadge), new PropertyMetadata(""));
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(OperationState), typeof(OperationStatusBadge), new PropertyMetadata(OperationState.Completed, StateChanged));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public OperationState State { get => (OperationState)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public OperationStatusBadge() { DefaultStyleKey = typeof(OperationStatusBadge); IsTabStop = false; }
    protected override void OnApplyTemplate() { base.OnApplyTemplate(); UpdateState(); }
    private static void StateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((OperationStatusBadge)sender).UpdateState();
    private void UpdateState() => VisualStateManager.GoToState(this, State switch
    {
        OperationState.Failed => "Failed",
        OperationState.Canceled => "Canceled",
        _ => "Completed"
    }, false);
}
