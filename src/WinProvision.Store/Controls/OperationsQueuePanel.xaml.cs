using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Controls;

public sealed partial class OperationsQueuePanel : UserControl
{
    private SpriteVisual? _shadowVisual;
    private DropShadow? _shadow;
    private OperationsViewModel? ViewModel => DataContext as OperationsViewModel;
    internal ItemsControl ItemsControl => OperationsList;
    internal bool IsOutputOpen => OutputModalHost.Visibility == Visibility.Visible;
    public OperationsQueuePanel()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachShadow();
        SizeChanged += (_, _) => { if (_shadowVisual is not null) _shadowVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight); };
        Unloaded += (_, _) => DetachShadow();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => { if (Visibility != Visibility.Visible) CloseOutput(); });
    }
    private void AttachShadow()
    {
        if (_shadowVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;
        _shadow = compositor.CreateDropShadow();
        _shadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);
        _shadow.Opacity = 0.06f;
        _shadow.BlurRadius = 18;
        _shadow.Offset = Vector3.Zero;
        _shadow.Mask = ShadowCaster.GetAlphaMask();
        _shadowVisual = compositor.CreateSpriteVisual();
        _shadowVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        _shadowVisual.Shadow = _shadow;
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, _shadowVisual);
    }
    private void DetachShadow()
    {
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, null);
        _shadowVisual?.Dispose(); _shadowVisual = null;
        _shadow?.Dispose(); _shadow = null;
    }
    private void ClosePanelButton_Click(object sender, RoutedEventArgs args) { if (ViewModel is { } model) model.IsQueueOpen = false; }
    private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape) return;
        if (!CloseOutput() && ViewModel is { } model) model.IsQueueOpen = false;
        args.Handled = true;
    }
    private static FontIcon Icon(Symbol symbol) => new() { Glyph = char.ConvertFromUtf32((int)symbol), FontSize = 16 };
    private static void Add(MenuFlyout menu, string text, Symbol symbol, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = Icon(symbol) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
    internal MenuFlyout CreateBulkMenu()
    {
        var menu = new MenuFlyout();
        Add(menu, "Limpar operações com êxito", Symbol.Accept, () => ViewModel?.ClearSuccessfulCommand.Execute(null));
        Add(menu, "Limpar todas as concluídas", Symbol.Clear, () => ViewModel?.ClearCommand.Execute(null));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "Cancelar todas as operações", Symbol.Cancel, () => ViewModel?.CancelAllCommand.Execute(null));
        return menu;
    }
    private void BulkMenuButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement button) CreateBulkMenu().ShowAt(button);
    }
    internal MenuFlyout CreateItemMenu(OperationViewModel item)
    {
        var menu = new MenuFlyout();
        Add(menu, "Ver saída do terminal", Symbol.Document, () => ShowOutput(item));
        Add(menu, "Copiar saída do terminal", Symbol.Copy, () => ViewModel?.CopyLogCommand.Execute(item));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (item.Operation.CanCancel) Add(menu, "Cancelar operação", Symbol.Cancel, () => item.CancelCommand.Execute(null));
        Add(menu, "Remover da lista", Symbol.Delete, () => ViewModel?.RemoveCommand.Execute(item));
        return menu;
    }
    private void ItemMoreButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: OperationViewModel item } button) CreateItemMenu(item).ShowAt(button);
    }
    private void LiveLineButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { Tag: OperationViewModel item }) ShowOutput(item);
    }
    internal void ShowOutput(OperationViewModel item)
    {
        ViewModel?.SelectOutput(item);
        OutputModalHost.Visibility = Visibility.Visible;
        OutputModalTextBox.Focus(FocusState.Programmatic);
    }
    public bool CloseOutput()
    {
        if (!IsOutputOpen) return false;
        OutputModalHost.Visibility = Visibility.Collapsed;
        BulkMenuButton.Focus(FocusState.Programmatic);
        return true;
    }
    private void CloseOutputModal_Click(object sender, RoutedEventArgs args) => CloseOutput();
    private void CopyOutputLog_Click(object sender, RoutedEventArgs args) => ViewModel?.CopyLogCommand.Execute(null);
}
