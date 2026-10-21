using System.ComponentModel;
using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinProvision.Core.Models;

namespace WinProvision.Store.Controls;

public sealed partial class InstallAction : DependencyObject
{
    public static readonly DependencyProperty AppProperty = DependencyProperty.RegisterAttached("App", typeof(AppEntry), typeof(InstallAction), new PropertyMetadata(null, AppChanged));
    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached("Subscription", typeof(Subscription), typeof(InstallAction), new PropertyMetadata(null));
    public static AppEntry? GetApp(DependencyObject element) => (AppEntry?)element.GetValue(AppProperty);
    public static void SetApp(DependencyObject element, AppEntry? app) => element.SetValue(AppProperty, app);
    private static void AppChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Button button) return;
        if (button.GetValue(SubscriptionProperty) is Subscription previous) previous.Dispose();
        if (args.NewValue is AppEntry app) button.SetValue(SubscriptionProperty, new Subscription(button, app));
    }
    private sealed partial class Subscription : IDisposable
    {
        private readonly Button _button;
        private readonly AppEntry _app;
        private bool _attached;
        public Subscription(Button button, AppEntry app)
        {
            _button = button; _app = app; button.Loaded += Loaded; button.Unloaded += Unloaded;
            Attach(); Refresh();
        }
        private void Loaded(object sender, RoutedEventArgs args) { Attach(); Refresh(); }
        private void Unloaded(object sender, RoutedEventArgs args) => Detach();
        private void Attach() { if (_attached) return; _app.PropertyChanged += Changed; _attached = true; }
        private void Detach() { if (!_attached) return; _app.PropertyChanged -= Changed; _attached = false; }
        private void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(AppEntry.IsInstalled) or nameof(AppEntry.IsInstalling))
                if (_button.DispatcherQueue.HasThreadAccess) Refresh(); else _button.DispatcherQueue.TryEnqueue(Refresh);
        }
        private void Refresh()
        {
            if (_app.IsSelf)
            {
                _button.Visibility = Visibility.Collapsed;
                _button.IsEnabled = false;
                return;
            }
            string text = _app.IsInstalled ? "Instalado" : _app.IsInstalling ? "Instalando..." : "Instalar";
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new FluentIcon
            {
                Icon = _app.IsInstalled ? Icon.CheckmarkCircle : Icon.ArrowDownload,
                IconVariant = IconVariant.Regular,
                IconSize = IconSize.Size16,
                FontSize = 16,
            });
            content.Children.Add(new TextBlock { Text = text });
            _button.Content = content;
            _button.IsEnabled = !_app.IsInstalled && !_app.IsInstalling;
        }
        public void Dispose() { Detach(); _button.Loaded -= Loaded; _button.Unloaded -= Unloaded; }
    }
}
