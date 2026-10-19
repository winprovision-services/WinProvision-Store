using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly IElevatorSettingsService _elevator;
    [ObservableProperty] public partial string ElevationCacheMessage { get; private set; } = "Consulte o estado da autorização desta sessão.";
    [ObservableProperty] public partial string ElevatorIntegrityMessage { get; private set; } = "Integridade ainda não verificada.";
    [ObservableProperty] public partial bool IsElevationBusy { get; private set; }
    [ObservableProperty] public partial bool AllowElevator { get; set; } = true;
    public bool CanEditElevator => !IsElevationBusy;
    public IAsyncRelayCommand AuthorizeElevationCommand => field ??= new AsyncRelayCommand(AuthorizeElevationAsync, () => !IsElevationBusy);
    public IAsyncRelayCommand RefreshElevatorCommand => field ??= new AsyncRelayCommand(RefreshElevatorAsync, () => !IsElevationBusy);
    public IAsyncRelayCommand RevokeElevationCommand => field ??= new AsyncRelayCommand(RevokeElevationAsync, () => !IsElevationBusy);
    public IAsyncRelayCommand SaveElevatorOptionsCommand => field ??= new AsyncRelayCommand(SaveElevatorOptionsAsync, () => !IsElevationBusy);

    partial void OnIsElevationBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditElevator));
        AuthorizeElevationCommand.NotifyCanExecuteChanged();
        RefreshElevatorCommand.NotifyCanExecuteChanged();
        RevokeElevationCommand.NotifyCanExecuteChanged();
        SaveElevatorOptionsCommand.NotifyCanExecuteChanged();
    }

    public void LoadElevatorOptions() => AllowElevator = _elevator.IsAllowed;

    private async Task InspectElevatorCoreAsync(CancellationToken token)
    {
        var status = await _elevator.InspectAsync(token);
        ElevatorIntegrityMessage = !status.Present ? "WinProvision Elevator não encontrado. Reinstale o aplicativo."
            : !status.Verified ? "A integridade do Elevator não confere. Reinstale o aplicativo."
            : "WinProvision Elevator verificado por SHA-256.";
        ElevationCacheMessage = !_elevator.IsAllowed ? status.Authorized
            ? "Pedidos pelo Elevator bloqueados; há um cache ativo nesta sessão. Use Encerrar autorização para invalidá-lo."
            : "Pedidos de elevação pelo Elevator estão bloqueados."
            : status.Authorized ? "Autorização disponível nesta sessão. Expira após 5 minutos de inatividade."
            : "Sem autorização ativa nesta sessão. A próxima operação administrativa pode solicitar o UAC.";
    }

    private async Task RefreshElevatorAsync(CancellationToken token)
    {
        if (IsElevationBusy) return;
        IsElevationBusy = true;
        try { LoadElevatorOptions(); await InspectElevatorCoreAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.Elevator.Inspect"); ElevatorIntegrityMessage = "Não foi possível verificar o Elevator. Consulte os logs."; }
        finally { IsElevationBusy = false; }
    }

    private async Task AuthorizeElevationAsync(CancellationToken token)
    {
        if (IsElevationBusy) return;
        IsElevationBusy = true;
        try
        {
            await _elevator.AuthorizeAsync(token);
            await InspectElevatorCoreAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ElevationCacheMessage = "Solicitação de autorização cancelada."; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.AuthorizeElevation"); ElevationCacheMessage = _elevator.IsAllowed ? "A autorização não foi concluída. Você pode tentar novamente." : "Pedidos de elevação pelo Elevator estão bloqueados. Altere e salve a opção para autorizar."; }
        finally { IsElevationBusy = false; }
    }

    private async Task RevokeElevationAsync()
    {
        if (IsElevationBusy) return;
        IsElevationBusy = true;
        try
        {
            await _elevator.RevokeAsync();
            ElevationCacheMessage = "Autorização desta sessão encerrada. A próxima operação administrativa poderá solicitar o UAC novamente.";
        }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.Elevator.Revoke"); ElevationCacheMessage = "Não foi possível encerrar a autorização. Consulte os logs."; }
        finally { IsElevationBusy = false; }
    }

    private async Task SaveElevatorOptionsAsync()
    {
        if (IsElevationBusy) return;
        IsElevationBusy = true;
        try
        {
            await _elevator.SetAllowedAsync(AllowElevator);
            ElevationCacheMessage = AllowElevator ? "Pedidos pelo Elevator permitidos. A autorização continua sendo solicitada pelo Windows."
                : "Pedidos pelo Elevator bloqueados. O cache desta sessão foi encerrado.";
        }
        catch (Exception ex)
        {
            WinProvisionLog.Warning(ex, "Settings.Elevator.Save");
            LoadElevatorOptions();
            ElevationCacheMessage = "Não foi possível concluir a alteração. Consulte os logs e verifique o estado da sessão.";
        }
        finally { IsElevationBusy = false; }
    }
}
