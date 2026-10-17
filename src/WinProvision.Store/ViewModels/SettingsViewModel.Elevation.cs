using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    public partial string ElevationCacheMessage { get; private set; } = "Ativado automaticamente por 5 minutos de inatividade. A primeira operação administrativa solicita autorização pelo Windows; ela expira também ao fechar o aplicativo.";

    [ObservableProperty]
    public partial bool IsElevationBusy { get; private set; }

    private AsyncRelayCommand? _authorizeElevationCommand;
    public IAsyncRelayCommand AuthorizeElevationCommand => _authorizeElevationCommand ??= new AsyncRelayCommand(AuthorizeElevationAsync, () => !IsElevationBusy);

    partial void OnIsElevationBusyChanged(bool value)
    {
        _authorizeElevationCommand?.NotifyCanExecuteChanged();
    }

    private async Task AuthorizeElevationAsync(CancellationToken token)
    {
        IsElevationBusy = true;
        try
        {
            await GsudoElevationService.AuthorizeCacheAsync(token);
            ElevationCacheMessage = $"Autorização solicitada para esta sessão. Expira após 5 minutos de inatividade ou ao fechar o aplicativo. Novas operações podem pedir autorização novamente após a expiração.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ElevationCacheMessage = "Solicitação de autorização cancelada."; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.AuthorizeElevation"); ElevationCacheMessage = "A autorização não foi concluída. Você pode tentar autorizar esta sessão novamente."; }
        finally { IsElevationBusy = false; }
    }

}
