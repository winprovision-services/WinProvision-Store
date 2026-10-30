using WinProvision.Core.Services;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace WinProvision.Store.Services;

/// <summary>Notificações nativas do Windows para operações que terminam sem janela aberta.</summary>
public sealed partial class WindowsAppNotificationService : IDisposable
{
    private readonly OperationPreferencesService _preferences;
    public WindowsAppNotificationService(OperationPreferencesService? preferences = null) => _preferences = preferences ?? new();
    private bool Allows(NotificationCategory category) => !SuppressNotifications && _preferences.Load().Allows(category);
    private bool AllowsSuccessfulOperation(bool isBatch) => !SuppressNotifications && _preferences.Load().AllowsSuccessfulOperation(isBatch);
    public Action<string> Log { get; set; } = message => WinProvision.Core.Services.CoreLog.Debug(message);
    public bool SuppressNotifications { get; set; }
    private bool _registered;
    private bool _activationHandlerRegistered;
    private Action? _onNotificationInvoked;

    public bool TryRegister(Action? onNotificationInvoked = null)
    {
        if (SuppressNotifications) return false;
        try
        {
            var manager = AppNotificationManager.Default;
            _onNotificationInvoked = onNotificationInvoked;

            // A documentação do Windows App SDK exige o handler antes do Register.
            // Isso também permite trazer a janela para frente quando o usuário clica
            // no resumo de uma execução em segundo plano.
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
        catch (Exception ex)
        {
            Log($"APP NOTIFICATIONS registration failed type={ex.GetType().Name} message={ex.Message}");
            return false;
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
        catch (Exception ex)
        {
            Log($"APP NOTIFICATIONS activation failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public void ShowCollectionResult(string title, string message, bool failed = false)
    {
        if (!Allows(failed ? NotificationCategory.Errors : NotificationCategory.Success) || (!_registered && !TryRegister())) return;
        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder()
                .AddText("WinProvision Store").AddText(title).AddText(message).BuildNotification());
        }
        catch (Exception exception) { WinProvision.Core.Services.CoreLog.Warning(exception, "CollectionNotification"); }
    }

    public void ShowBackgroundUpdateSummary(int updated, int failed)
        => ShowBackgroundUpdateSummary(updated, failed, 0);

    public void ShowBackgroundUpdateSummary(int updated, int failed, int unselected)
    {
        if (!(failed > 0 ? Allows(NotificationCategory.Errors) : AllowsSuccessfulOperation(isBatch: true)) || (!_registered && !TryRegister()))
            return;

        string message = updated == 0 && failed == 0
            ? unselected > 0
                ? $"Nenhum aplicativo selecionado para atualizar; {unselected} atualização(ões) ficou(aram) de fora."
                : "Seus aplicativos já estão atualizados."
            : failed == 0
                ? unselected > 0
                    ? $"{updated} atualizado(s); {unselected} atualização(ões) ficou(aram) de fora."
                    : $"{updated} aplicativo(s) atualizado(s) com sucesso."
                : $"{updated} atualizado(s); {failed} falharam; {unselected} ficaram de fora. Abra o app para ver os detalhes.";

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("WinProvision Store")
                .AddText("Atualização em segundo plano concluída")
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            Log($"APP NOTIFICATIONS background-update updated={updated} failed={failed}");
        }
        catch (Exception ex)
        {
            Log($"APP NOTIFICATIONS show failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public void ShowBackgroundUpdateCheckSummary(int available)
    {
        if (!Allows(NotificationCategory.Updates) || (!_registered && !TryRegister()))
            return;

        string message = available == 0
            ? "Seus aplicativos já estão atualizados."
            : $"{available} atualização(ões) disponível(is). Nenhum aplicativo foi alterado.";
        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("WinProvision Store")
                .AddText("Verificação de atualizações concluída")
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            Log($"APP NOTIFICATIONS update-check available={available}");
        }
        catch (Exception ex)
        {
            Log($"APP NOTIFICATIONS check summary failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public void ShowBackgroundUpdateFailure()
    {
        if (!Allows(NotificationCategory.Errors) || (!_registered && !TryRegister()))
            return;

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText("WinProvision Store")
                .AddText("Não foi possível concluir as atualizações em segundo plano")
                .AddText("Abra o app e tente verificar as atualizações novamente.")
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            Log("APP NOTIFICATIONS background-update failure=true");
        }
        catch (Exception ex)
        {
            Log($"APP NOTIFICATIONS failure notification failed type={ex.GetType().Name} message={ex.Message}");
        }
    }

    public void ShowMaintenanceSummary(string message)
    {
        if (!AllowsSuccessfulOperation(isBatch: true) || (!_registered && !TryRegister())) return;
        try
        {
            var notification = new AppNotificationBuilder().AddText("WinProvision Store")
                .AddText("Manutenção do Windows em segundo plano").AddText(message).BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception exception) { WinProvision.Core.Services.CoreLog.Warning(exception, "Maintenance.ShowNotification"); }
    }

    public void ShowDownloadSummary(int downloaded, int failed)
    {
        bool allowed = failed > 0 ? Allows(NotificationCategory.Errors) : AllowsSuccessfulOperation(isBatch: downloaded > 1);
        if (!allowed || (!_registered && !TryRegister())) return;
        try
        {
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("WinProvision Store")
                .AddText("Download de instaladores concluído").AddText($"{downloaded} baixado(s); {failed} falha(s).").BuildNotification());
        }
        catch (Exception ex) { CoreLog.Warning(ex, "Downloads.Notification"); }
    }

    public void ShowOperationBatchResult(IReadOnlyList<WinProvision.Core.Models.OperationItem> items)
    {
        int failed = items.Count(item => item.State == WinProvision.Core.Models.OperationState.Failed);
        int succeeded = items.Count(item => item.State == WinProvision.Core.Models.OperationState.Completed);
        if (failed == 0 && succeeded == 0) return;
        bool allowed = failed > 0 ? Allows(NotificationCategory.Errors) : AllowsSuccessfulOperation(isBatch: items.Count > 1);
        if (!allowed || (!_registered && !TryRegister())) return;
        try
        {
            string title = items.Count == 1 ? items[0].AppName : "Lote de operações concluído";
            string message = items.Count == 1 ? failed > 0 ? "A operação falhou. Abra o app para ver a causa." : "Operação concluída com sucesso."
                : $"{succeeded} concluída(s); {failed} falha(s); {items.Count - succeeded - failed} cancelada(s).";
            AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText("WinProvision Store").AddText(title).AddText(message).BuildNotification());
        }
        catch (Exception ex) { CoreLog.Warning(ex, "Operations.Notification"); }
    }

    public void Unregister()
    {
        if (!_registered)
            return;

        try
        {
            var manager = AppNotificationManager.Default;
            manager.Unregister();
            if (_activationHandlerRegistered)
            {
                manager.NotificationInvoked -= OnNotificationInvoked;
                _activationHandlerRegistered = false;
            }
            Log("APP NOTIFICATIONS unregistered=true");
        }
        catch (Exception ex)
        {
            Log($"APP NOTIFICATIONS unregister failed type={ex.GetType().Name} message={ex.Message}");
        }
        finally
        {
            _registered = false;
        }
    }
    public void Dispose() => Unregister();

}
