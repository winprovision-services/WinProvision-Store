using Microsoft.UI.Xaml;

namespace WinProvision.Store.Services;

public sealed partial class ApplicationThemeService(ApplicationPreferencesService preferences)
{
    private FrameworkElement? _root;

    public AppThemePreference Preference => preferences.Theme;

    public void Attach(FrameworkElement root)
    {
        _root = root;
        Apply(preferences.Theme);
    }

    public void SetTheme(AppThemePreference theme)
    {
        preferences.SetTheme(theme);
        Apply(theme);
    }

    private void Apply(AppThemePreference theme)
    {
        if (_root is not null)
        {
            _root.RequestedTheme = theme switch
            {
                AppThemePreference.Light => ElementTheme.Light,
                AppThemePreference.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }
    }
}
