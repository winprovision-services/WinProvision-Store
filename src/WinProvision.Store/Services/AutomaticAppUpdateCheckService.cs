using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class AutomaticAppUpdateCheckService
{
    private static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan PreferencePollInterval = TimeSpan.FromHours(1);
    private readonly ApplicationPreferencesService preferences;
    private readonly ToastMessageService notifications;
    private readonly Func<CancellationToken, bool, Task<AppUpdateCheckResult>> checkForUpdate;
    private readonly SemaphoreSlim _preferenceChanged = new(0, 1);
    private readonly object _activeCheckGate = new();
    private CancellationTokenSource? _activeCheckCancellation;
    private DateTimeOffset? _lastAttemptUtc;

    public AutomaticAppUpdateCheckService(
        ApplicationPreferencesService preferences,
        AppUpdateService updater,
        ToastMessageService notifications)
        : this(preferences, notifications, updater.CheckForUpdateAsync)
    {
    }

    internal AutomaticAppUpdateCheckService(
        ApplicationPreferencesService preferences,
        ToastMessageService notifications,
        Func<CancellationToken, bool, Task<AppUpdateCheckResult>> checkForUpdate)
    {
        this.preferences = preferences;
        this.notifications = notifications;
        this.checkForUpdate = checkForUpdate;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        preferences.AppUpdateCheckPreferencesChanged += PreferencesChanged;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (preferences.AutomaticAppUpdateChecksEnabled && IsDue(DateTimeOffset.UtcNow))
                    await CheckAsync(cancellationToken);

                await _preferenceChanged.WaitAsync(PreferencePollInterval, cancellationToken);
            }
        }
        finally
        {
            preferences.AppUpdateCheckPreferencesChanged -= PreferencesChanged;
        }
    }

    private bool IsDue(DateTimeOffset now)
    {
        DateTimeOffset? lastCheck = _lastAttemptUtc is { } attempted
            && (preferences.LastAppUpdateCheckUtc is null || attempted > preferences.LastAppUpdateCheckUtc)
                ? attempted
                : preferences.LastAppUpdateCheckUtc;
        return lastCheck is null || now - lastCheck.Value >= MinimumCheckInterval;
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var checkCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_activeCheckGate)
        {
            _activeCheckCancellation = checkCancellation;
            if (!preferences.AutomaticAppUpdateChecksEnabled)
                checkCancellation.Cancel();
        }

        try
        {
            if (checkCancellation.IsCancellationRequested)
                return;

            DateTimeOffset attemptedAt = DateTimeOffset.UtcNow;
            AppUpdateCheckResult result;
            try
            {
                result = await checkForUpdate(
                    checkCancellation.Token, preferences.IncludePrereleaseAppUpdates);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (checkCancellation.IsCancellationRequested)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!preferences.AutomaticAppUpdateChecksEnabled || checkCancellation.IsCancellationRequested)
                return;

            _lastAttemptUtc = attemptedAt;
            try
            {
                preferences.MarkAppUpdateCheck(attemptedAt);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WinProvisionLog.Warning(exception, "AppUpdate.SaveCheckTimestamp");
            }

            if (!result.Success)
            {
                WinProvisionLog.Warning(
                    new InvalidOperationException(result.Error ?? "A verificação automática não foi concluída."),
                    "AppUpdate.AutomaticCheck");
                return;
            }

            if (!result.UpdateAvailable)
                return;

            string version = result.LatestVersion?.ToString(3) ?? result.ReleaseLabel ?? "nova versão";
            try
            {
                await notifications.PublishAsync(new Notification(
                    Guid.NewGuid().ToString("N"),
                    NotificationCategories.AppUpdateAvailable,
                    NotificationSeverity.Information,
                    "Atualização do WinProvision Store disponível",
                    $"A versão {version} está disponível. Abra Configurações para conferir os detalhes.",
                    DeduplicationKey: "winprovision-store-update-available",
                    Origin: nameof(AutomaticAppUpdateCheckService)), checkCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (checkCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException
                or UnauthorizedAccessException)
            {
                WinProvisionLog.Warning(exception, "AppUpdate.PublishNotification");
            }
        }
        finally
        {
            lock (_activeCheckGate)
            {
                if (ReferenceEquals(_activeCheckCancellation, checkCancellation))
                    _activeCheckCancellation = null;
            }
        }
    }

    private void PreferencesChanged(object? sender, EventArgs args)
    {
        if (!preferences.AutomaticAppUpdateChecksEnabled)
        {
            lock (_activeCheckGate)
                _activeCheckCancellation?.Cancel();
        }

        if (_preferenceChanged.CurrentCount == 0)
            _preferenceChanged.Release();
    }
}
