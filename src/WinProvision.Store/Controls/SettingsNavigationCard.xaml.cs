using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinProvision.Store.Controls;

public sealed partial class SettingsNavigationCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingsNavigationCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsNavigationCard),
            new PropertyMetadata(string.Empty, OnDescriptionChanged));

    public static readonly DependencyProperty TargetPageProperty =
        DependencyProperty.Register(nameof(TargetPage), typeof(string), typeof(SettingsNavigationCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty HasDescriptionProperty =
        DependencyProperty.Register(nameof(HasDescription), typeof(bool), typeof(SettingsNavigationCard), new PropertyMetadata(false));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool HasDescription
    {
        get => (bool)GetValue(HasDescriptionProperty);
        private set => SetValue(HasDescriptionProperty, value);
    }

    public string TargetPage
    {
        get => (string)GetValue(TargetPageProperty);
        set => SetValue(TargetPageProperty, value);
    }

    public event EventHandler? NavigationRequested;

    public SettingsNavigationCard() => InitializeComponent();

    private static void OnDescriptionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((SettingsNavigationCard)sender).HasDescription = !string.IsNullOrWhiteSpace(args.NewValue as string);

    private void NavigationButton_Click(object sender, RoutedEventArgs e) =>
        NavigationRequested?.Invoke(this, EventArgs.Empty);
}
