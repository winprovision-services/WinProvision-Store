using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Runtime.InteropServices;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>Canal nativo do Windows; o roteamento e as preferências pertencem à ToastMessageService.</summary>
public sealed partial class WindowsAppNotificationService : IDisposable
{
    private bool _registered;
    private bool _activationHandlerRegistered;
    private Action? _onNotificationInvoked;

    public Action<string> Log { get; set; } = message => CoreLog.Debug(message);
    public bool SuppressNotifications { get; set; }

    public static bool ShouldSuppressToast()
    {
        int result = SHQueryUserNotificationState(out QueryUserNotificationState state);
        if (result < 0)
        {
            CoreLog.Warning(Marshal.GetExceptionForHR(result) ?? new InvalidOperationException("Windows não informou o estado de notificações."),
                "WindowsNotification.State");
            return true;
        }
        return state != QueryUserNotificationState.AcceptsNotifications;
    }

    public bool TryRegister(Action? onNotificationInvoked = null)
    {
        if (SuppressNotifications)
            return false;
        try
        {
            AppNotificationManager manager = AppNotificationManager.Default;
            _onNotificationInvoked = onNotificationInvoked;
            if (!_activationHandlerRegistered)
            {
                manager.NotificationInvoked += OnNotificationInvoked;
                _activationHandlerRegistered = true;
            }

            manager.Register();
            _registered = true;
            Log("APP NOTIFICATIONS registered=true");
            return true;
        }
        catch (Exception exception)
        {
            Log($"APP NOTIFICATIONS registration failed type={exception.GetType().Name} message={exception.Message}");
            return false;
        }
    }

    internal ValueTask ShowAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SuppressNotifications)
            return ValueTask.CompletedTask;
        if (ShouldSuppressToast())
            return ValueTask.CompletedTask;
        if (!_registered && !TryRegister())
            throw new InvalidOperationException("O Windows não aceitou o registro do canal de notificações.");

        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddText("WinProvision Store")
                .AddText(notification.Title)
                .AddText(notification.Message)
                .BuildNotification());
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "WindowsNotification.Show");
            throw;
        }
    }

    private void OnNotificationInvoked(
        AppNotificationManager sender,
        AppNotificationActivatedEventArgs args)
    {
        Log("APP NOTIFICATIONS activated=true");
        try
        {
            _onNotificationInvoked?.Invoke();
        }
        catch (Exception exception)
        {
            Log($"APP NOTIFICATIONS activation failed type={exception.GetType().Name} message={exception.Message}");
        }
    }

    public void Unregister()
    {
        if (!_registered)
            return;

        try
        {
            AppNotificationManager manager = AppNotificationManager.Default;
            manager.Unregister();
            if (_activationHandlerRegistered)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
                _activationHandlerRegistered = false;
            }
            Log("APP NOTIFICATIONS unregistered=true");
        }
        catch (Exception exception)
        {
            Log($"APP NOTIFICATIONS unregister failed type={exception.GetType().Name} message={exception.Message}");
        }
        finally
        {
            _registered = false;
        }
    }

    public void Dispose() => Unregister();

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);

    private enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        App = 7
    }
}
