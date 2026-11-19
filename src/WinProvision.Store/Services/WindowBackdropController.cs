using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinRT;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed partial class WindowBackdropController : IDisposable
{
    private readonly Window _window;
    private readonly Grid _root;
    private readonly WindowBackdropCatalog _catalog;
    private readonly SystemBackdropConfiguration _configuration = new();
    private MicaController? _micaController;
    private DesktopAcrylicController? _acrylicController;
    private bool _disposed;

    public string EffectiveBackdropId { get; private set; } = "none";

    public WindowBackdropController(Window window, Grid root, WindowBackdropCatalog catalog, string preference)
    {
        _window = window;
        _root = root;
        _catalog = catalog;
        _window.Activated += Window_Activated;
        _root.ActualThemeChanged += Root_ActualThemeChanged;
        _configuration.IsInputActive = true;
        UpdateTheme();
        Apply(preference);
    }

    public WindowBackdropResolution Resolve(string? preference) => _catalog.Resolve(preference, WindowBackdropSupport.Current);

    public void RefreshTheme()
    {
        UpdateTheme();
        if (_micaController is null && _acrylicController is null)
            _root.Background = (Brush)Application.Current.Resources["AppWindowBackgroundBrush"];
    }

    public void Apply(string? preference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowBackdropResolution resolution = Resolve(preference);
        if (resolution.Warning is not null)
            CoreLog.Warning(new InvalidOperationException(resolution.Warning), "WindowBackdrop.Resolve");
        if (resolution.UsedFallback)
            CoreLog.Information("WindowBackdrop.Resolve", $"Plano de fundo '{preference}' indisponível; aplicado '{resolution.Backdrop.Id}'.");

        EffectiveBackdropId = resolution.Backdrop.Id;
        ClearControllers();
        _window.SystemBackdrop = null;
        if (resolution.Backdrop.Kind == "none")
        {
            _root.Background = (Brush)Application.Current.Resources["AppWindowBackgroundBrush"];
            return;
        }

        _root.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        try
        {
            if (resolution.Backdrop.Kind == "mica")
            {
                _micaController = new MicaController
                {
                    Kind = resolution.Backdrop.Variant == "baseAlt" ? MicaKind.BaseAlt : MicaKind.Base
                };
                _micaController.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
                _micaController.SetSystemBackdropConfiguration(_configuration);
            }
            else
            {
                _acrylicController = new DesktopAcrylicController
                {
                    Kind = resolution.Backdrop.Variant == "thin" ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base
                };
                _acrylicController.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
                _acrylicController.SetSystemBackdropConfiguration(_configuration);
            }
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "WindowBackdrop.Apply");
            ClearControllers();
            _window.SystemBackdrop = null;
            EffectiveBackdropId = "none";
            _root.Background = (Brush)Application.Current.Resources["AppWindowBackgroundBrush"];
        }
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args) =>
        _configuration.IsInputActive = args.WindowActivationState != WindowActivationState.Deactivated;

    private void Root_ActualThemeChanged(FrameworkElement sender, object args) => UpdateTheme();

    private void UpdateTheme() => _configuration.Theme = _root.ActualTheme switch
    {
        ElementTheme.Light => SystemBackdropTheme.Light,
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        _ => SystemBackdropTheme.Default
    };

    private void ClearControllers()
    {
        _micaController?.Dispose();
        _micaController = null;
        _acrylicController?.Dispose();
        _acrylicController = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Activated -= Window_Activated;
        _root.ActualThemeChanged -= Root_ActualThemeChanged;
        ClearControllers();
        _window.SystemBackdrop = null;
    }
}
