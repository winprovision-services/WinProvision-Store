using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class AccountSyncViewModel : ObservableObject, IDisposable
{
    private const string GitHubClientId = "Ov23lii0QqRFfDSt5UA8";
    private const string DeviceCodeEndpoint = "https://github.com/login/device/code";
    private const string AccessTokenEndpoint = "https://github.com/login/oauth/access_token";
    private readonly IAccountBackupService _backup;
    private readonly WinUiBackupSyncService _syncService;
    private readonly CollectionsViewModel _collections;
    private readonly IPresentationService _presentation;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    public partial string ConnectedLogin { get; private set; } = "";

    [ObservableProperty]
    public partial string? AvatarUrl { get; private set; }

    [ObservableProperty]
    public partial string LastSyncText { get; private set; } = "—";

    [ObservableProperty]
    public partial string LocalBackupStatus { get; private set; } = "Nenhum backup realizado ainda.";

    [ObservableProperty]
    public partial bool IsAuthenticating { get; private set; }

    [ObservableProperty]
    public partial bool IsWaitingForAuthorization { get; private set; }

    [ObservableProperty]
    public partial string UserCode { get; private set; } = "";

    [ObservableProperty]
    public partial string OAuthStatus { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public bool CanStartAuthentication => !IsAuthenticating;
    public bool CanSaveBackup => !IsBusy;
    public bool CanImportCloudBackup => IsConnected && !IsBusy;
    public bool CanImportProfile => !IsBusy;
    public bool CanExportProfile => !IsBusy;
    public event EventHandler<Uri>? VerificationUriRequested;

    public AccountSyncViewModel(
        IAccountBackupService backup,
        WinUiBackupSyncService syncService,
        CollectionsViewModel collections,
        IPresentationService presentation,
        IUiDispatcher dispatcher)
    {
        _backup = backup;
        _syncService = syncService;
        _collections = collections;
        _presentation = presentation;
        _dispatcher = dispatcher;
        _syncService.SyncAttempted += SyncService_SyncAttempted;
        Refresh();
    }

    partial void OnIsAuthenticatingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartAuthentication));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSaveBackup));
        OnPropertyChanged(nameof(CanImportCloudBackup));
        OnPropertyChanged(nameof(CanImportProfile));
        OnPropertyChanged(nameof(CanExportProfile));
        SaveBackupCommand.NotifyCanExecuteChanged();
        RestoreFromCloudCommand.NotifyCanExecuteChanged();
        ImportJsonCommand.NotifyCanExecuteChanged();
        ExportJsonCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportCloudBackup));
        RestoreFromCloudCommand.NotifyCanExecuteChanged();
    }

    public void Refresh()
    {
        IsConnected = _backup.IsConnected;
        ConnectedLogin = IsConnected && !string.IsNullOrWhiteSpace(_backup.ConnectedLogin)
            ? $"@{_backup.ConnectedLogin}"
            : "";
        AvatarUrl = IsConnected ? _backup.AvatarUrl : null;
        LastSyncText = _backup.LastSyncUtc is { } syncedUtc
            ? syncedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
            : "—";
        LocalBackupStatus = _backup.LastBackupUtc is { } backupUtc
            ? $"Último backup: {backupUtc.ToLocalTime():dd/MM/yyyy HH:mm}"
            : "Nenhum backup realizado ainda.";
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsAuthenticating = true;
        StatusText = "Conectando ao GitHub...";

        try
        {
            using var http = new HttpClient(new WinProvision.Core.Services.RequestRateLimitHandler(new SocketsHttpHandler())) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvision-Store", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var deviceRequest = new HttpRequestMessage(HttpMethod.Post, DeviceCodeEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = GitHubClientId,
                    ["scope"] = "gist"
                })
            };
            using HttpResponseMessage deviceResponse = await http.SendAsync(deviceRequest, cancellationToken);
            if (!deviceResponse.IsSuccessStatusCode)
            {
                StatusText = "Não foi possível iniciar a conexão com o GitHub. Verifique sua internet e tente novamente.";
                return;
            }

            DeviceCodeResponse? deviceData = await deviceResponse.Content.ReadFromJsonAsync(WinProvision.Core.Services.AotJson.TypeInfo<DeviceCodeResponse>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(deviceData?.DeviceCode) || string.IsNullOrWhiteSpace(deviceData.UserCode))
            {
                StatusText = "O GitHub não iniciou o login. Tente novamente.";
                return;
            }

            UserCode = deviceData.UserCode;
            IsWaitingForAuthorization = true;
            OAuthStatus = "Aguardando confirmação no navegador...";
            StatusText = "Confirme o código de acesso no GitHub.";

            Uri verificationUri = Uri.TryCreate(deviceData.VerificationUri, UriKind.Absolute, out Uri? returnedUri)
                && returnedUri.Scheme == Uri.UriSchemeHttps
                && returnedUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    ? returnedUri
                    : new Uri("https://github.com/login/device");
            VerificationUriRequested?.Invoke(this, verificationUri);

            int intervalSeconds = Math.Max(5, deviceData.Interval);
            DateTime expiresAt = DateTime.UtcNow.AddSeconds(deviceData.ExpiresIn > 0 ? deviceData.ExpiresIn : 900);
            while (DateTime.UtcNow < expiresAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
                using var pollRequest = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint)
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = GitHubClientId,
                        ["device_code"] = deviceData.DeviceCode,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                    })
                };
                using HttpResponseMessage pollResponse = await http.SendAsync(pollRequest, cancellationToken);
                if (!pollResponse.IsSuccessStatusCode)
                    continue;

                TokenResponse? tokenData = await pollResponse.Content.ReadFromJsonAsync(WinProvision.Core.Services.AotJson.TypeInfo<TokenResponse>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), cancellationToken: cancellationToken);
                if (!string.IsNullOrWhiteSpace(tokenData?.AccessToken))
                {
                    OAuthStatus = "Autorizado. Conectando...";
                    GitHubConnectResult result = await _backup.ConnectAsync(tokenData.AccessToken, cancellationToken);
                    if (!result.Success)
                    {
                        StatusText = result.ErrorMessage ?? "Não foi possível conectar a conta GitHub.";
                        return;
                    }

                    Refresh();
                    StatusText = $"Conta vinculada como {ConnectedLogin}.";
                    return;
                }

                switch (tokenData?.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        intervalSeconds += 5;
                        continue;
                    case "expired_token":
                        StatusText = "O código expirou. Inicie o login novamente.";
                        return;
                    case "access_denied":
                        StatusText = "Login cancelado no GitHub.";
                        return;
                    case { Length: > 0 }:
                        StatusText = "Não foi possível concluir o login. Tente novamente.";
                        return;
                }
            }

            StatusText = "O login expirou. Inicie a conexão novamente.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Login cancelado.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.Connect");
            StatusText = "Não foi possível conectar sua conta. Verifique a conexão e tente novamente.";
        }
        finally
        {
            IsAuthenticating = false;
            IsWaitingForAuthorization = false;
            UserCode = "";
            Refresh();
        }
    }

    private void CancelAuthentication() => ConnectCommand.Cancel();

    private void Disconnect()
    {
        _backup.Disconnect();
        Refresh();
        StatusText = "Conta desconectada. O backup local continua disponível.";
    }

    private async Task SaveBackupAsync(CancellationToken cancellationToken)
    {
        ProfileBackupSet snapshot = _collections.CreateBackupSnapshot();
        if (snapshot.Tabs.Count == 0 && snapshot.Provisioning is null)
        {
            StatusText = "Não há dados de coleção para salvar no backup.";
            return;
        }

        IsBusy = true;
        StatusText = "Salvando backup...";
        try
        {
            WinUiBackupSyncResult result = await _syncService.RunSyncAsync(snapshot, cancellationToken);
            Refresh();
            StatusText = result.AlreadyRunning
                ? "Backup em andamento. Aguarde a conclusão."
                : !result.LocalSucceeded
                    ? $"Não foi possível salvar o backup local. {result.ErrorMessage}"
                    : result.CloudSucceeded
                        ? "Backup salvo no computador e na nuvem."
                        : result.CloudAttempted
                            ? $"Backup salvo no computador, mas não na nuvem. {result.ErrorMessage}"
                            : "Backup salvo no computador. Conecte o GitHub para sincronizar.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Backup cancelado.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.SaveBackup");
            StatusText = "Não foi possível salvar o backup. Verifique o espaço disponível e tente novamente.";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task RestoreFromCloudAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusText = "Baixando backup da nuvem...";
        _collections.StatusText = StatusText;
        try
        {
            GitHubBackupDownloadResult result = await _backup.DownloadCloudAsync(cancellationToken);
            if (!result.Success || result.BackupSet is null)
            {
                StatusText = result.ErrorMessage ?? "Ainda não existe um backup de perfil na nuvem.";
                return;
            }

            int imported = _collections.ImportJsonBackup(
                WinProvision.Core.Services.AotJson.Serialize(result.BackupSet, WinProvisionJsonOptions.Profile),
                "Backup da nuvem");
            StatusText = $"Backup da nuvem restaurado: {imported} coleção(ões) adicionada(s).";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Restauração cancelada.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.RestoreFromCloud");
            StatusText = $"Não foi possível restaurar o backup da nuvem. {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
            _collections.StatusText = StatusText;
        }
    }

    private async Task ImportJsonAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        StatusText = "Selecione um arquivo JSON para importar.";
        try
        {
            string? path = await _presentation.PickOpenAsync();
            if (path is null)
                return;

            string json = await File.ReadAllTextAsync(path, cancellationToken);
            int imported = _collections.ImportJsonBackup(json, Path.GetFileNameWithoutExtension(path));
            StatusText = $"{imported} coleção(ões) importada(s) de {Path.GetFileName(path)}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Importação cancelada.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.ImportJson");
            StatusText = $"Não foi possível importar o arquivo JSON. {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task ExportJsonAsync(CancellationToken cancellationToken)
    {
        ProfileBackupSet snapshot = _collections.CreateBackupSnapshot();
        if (snapshot.Tabs.Count == 0 && snapshot.Provisioning is null)
        {
            StatusText = "Não há dados de coleção para exportar.";
            return;
        }

        IsBusy = true;
        try
        {
            string? path = await _presentation.PickSaveAsync("perfil-completo", ".json");
            if (path is null)
                return;

            string json = WinProvision.Core.Services.AotJson.Serialize(snapshot, WinProvisionJsonOptions.Profile);
            await AtomicFile.WriteAllTextAsync(path, json, cancellationToken);
            StatusText = $"Perfil completo exportado para {Path.GetFileName(path)}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Exportação cancelada.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.ExportJson");
            StatusText = $"Não foi possível exportar o perfil. {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SyncService_SyncAttempted(WinUiBackupSyncResult result)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed)
                return;
            Refresh();
            if (!result.LocalSucceeded)
                StatusText = $"A sincronização automática não salvou o backup local. {result.ErrorMessage}";
            else if (result.CloudAttempted && !result.CloudSucceeded)
                StatusText = $"Backup local atualizado; a sincronização com o GitHub falhou. {result.ErrorMessage}";
            else if (result.CloudSucceeded)
                StatusText = "Backup sincronizado automaticamente.";
        });
    }

    internal sealed partial class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")] public string? DeviceCode { get; set; }
        [JsonPropertyName("user_code")] public string? UserCode { get; set; }
        [JsonPropertyName("verification_uri")] public string? VerificationUri { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    internal sealed partial class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ConnectCommand.Cancel();
        _syncService.SyncAttempted -= SyncService_SyncAttempted;
    }
}
