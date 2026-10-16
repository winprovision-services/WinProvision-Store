using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    public partial string ElevationCacheDuration { get; set; } = "5";

    [ObservableProperty]
    public partial string ElevationCacheMessage { get; private set; } = "Desativado. A primeira autorização é solicitada pelo Windows; o cache vale apenas para este aplicativo e seus processos filhos.";

    [ObservableProperty]
    public partial bool IsElevationBusy { get; private set; }

    private AsyncRelayCommand? _authorizeElevationCommand;
    private AsyncRelayCommand? _stopElevationCommand;
    public IAsyncRelayCommand AuthorizeElevationCommand => _authorizeElevationCommand ??= new AsyncRelayCommand(AuthorizeElevationAsync, () => !IsElevationBusy);
    public IAsyncRelayCommand StopElevationCommand => _stopElevationCommand ??= new AsyncRelayCommand(StopElevationAsync, () => !IsElevationBusy);

    partial void OnIsElevationBusyChanged(bool value)
    {
        _authorizeElevationCommand?.NotifyCanExecuteChanged();
        _stopElevationCommand?.NotifyCanExecuteChanged();
    }

    private async Task AuthorizeElevationAsync(CancellationToken token)
    {
        IsElevationBusy = true;
        try
        {
            int minutes = int.Parse(ElevationCacheDuration, System.Globalization.CultureInfo.InvariantCulture);
            await GsudoElevationService.AuthorizeCacheAsync(minutes, token);
            ElevationCacheMessage = $"Autorização solicitada para esta sessão. Expira após {minutes} minutos de inatividade ou ao fechar o aplicativo. Novas operações podem pedir autorização novamente após a expiração.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ElevationCacheMessage = "Solicitação de autorização cancelada."; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.AuthorizeElevation"); ElevationCacheMessage = "A autorização não foi concluída. Você pode tentar autorizar esta sessão novamente."; }
        finally { IsElevationBusy = false; }
    }

    private async Task StopElevationAsync()
    {
        IsElevationBusy = true;
        try { await GsudoElevationService.StopCacheAsync(); ElevationCacheMessage = "Autorização temporária encerrada."; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.StopElevation"); ElevationCacheMessage = "Não foi possível confirmar o encerramento. Feche o aplicativo para invalidar a autorização vinculada ao processo."; }
        finally { IsElevationBusy = false; }
    }
}
