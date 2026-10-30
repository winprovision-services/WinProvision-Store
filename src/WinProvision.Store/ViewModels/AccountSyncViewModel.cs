using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.ObjectModel;
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
    private readonly ApplicationPreferencesService _preferences;
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

    public ObservableCollection<CloudBackupFileRow> CloudFiles { get; } = new();

    [ObservableProperty]
    public partial CloudBackupFileRow? SelectedCloudFile { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingCloudFiles { get; private set; }

    [ObservableProperty]
    public partial bool AutomaticCloudBackupEnabled { get; set; }

    public bool CanRefreshCloudFiles => IsConnected && !IsBusy && !IsLoadingCloudFiles;
    public bool CanRestoreSelectedCloudFile => CanRefreshCloudFiles && SelectedCloudFile?.CanImport == true;
    public bool CanDeleteSelectedCloudFile => CanRefreshCloudFiles && SelectedCloudFile is not null;

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
        IUiDispatcher dispatcher,
        ApplicationPreferencesService preferences)
    {
        _backup = backup;
        _syncService = syncService;
        _collections = collections;
        _presentation = presentation;
        _dispatcher = dispatcher;
        _preferences = preferences;
        AutomaticCloudBackupEnabled = preferences.AutomaticCloudBackupEnabled;
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
        NotifyCloudCommands();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanImportCloudBackup));
        RestoreFromCloudCommand.NotifyCanExecuteChanged();
        NotifyCloudCommands();
    }

    partial void OnSelectedCloudFileChanged(CloudBackupFileRow? value) => NotifyCloudCommands();
    partial void OnIsLoadingCloudFilesChanged(bool value) => NotifyCloudCommands();

    partial void OnAutomaticCloudBackupEnabledChanged(bool value)
    {
        if (_preferences is not null && _preferences.AutomaticCloudBackupEnabled != value)
        {
            try { _preferences.SetAutomaticCloudBackup(value); }
            catch (Exception exception)
            {
                WinProvisionLog.Error(exception, "AccountSyncViewModel.AutomaticCloudBackup");
                StatusText = $"Não foi possível salvar a preferência de backup automático. {exception.Message}";
                AutomaticCloudBackupEnabled = _preferences.AutomaticCloudBackupEnabled;
            }
        }
    }

    private void NotifyCloudCommands()
    {
        OnPropertyChanged(nameof(CanRefreshCloudFiles));
        OnPropertyChanged(nameof(CanRestoreSelectedCloudFile));
        OnPropertyChanged(nameof(CanDeleteSelectedCloudFile));
        RefreshCloudFilesCommand.NotifyCanExecuteChanged();
        RestoreSelectedCloudFileCommand.NotifyCanExecuteChanged();
        DeleteSelectedCloudFileCommand.NotifyCanExecuteChanged();
        DeleteAllAppGistsCommand.NotifyCanExecuteChanged();
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
                    ["scope"] = "gist offline_access"
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
                    CloudConnectResult result = await _backup.ConnectAsync(tokenData.AccessToken, cancellationToken, tokenData.RefreshToken);
                    if (!result.Success)
                    {
                        StatusText = result.ErrorMessage ?? "Não foi possível conectar a conta GitHub.";
                        return;
                    }

                    Refresh();
                    StatusText = $"Conta vinculada como {ConnectedLogin}.";
                    await RefreshCloudFilesAsync(cancellationToken);
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
        CloudFiles.Clear();
        SelectedCloudFile = null;
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
                            : _backup.IsConnected
                                ? "Backup salvo no computador. O envio automático ao Gist está desativado."
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
        StatusText = "Consultando arquivos JSON na nuvem...";
        _collections.StatusText = StatusText;
        try
        {
            IReadOnlyList<CloudBackupFile> files = await _backup.ListCloudFilesAsync(cancellationToken);
            CloudBackupFile[] importable = files.Where(file => file.CanImport).ToArray();
            if (importable.Length == 0)
            {
                StatusText = "Não há arquivos JSON compatíveis para importar no Gist de backup.";
                return;
            }
            int? selectedIndex = await _presentation.ChooseAsync("Escolha o JSON para importar",
                importable.Select(file => $"{file.FileName} — {file.Description} — {file.CollectionCount} coleção(ões), {file.AppCount} aplicativo(s)").ToArray());
            if (selectedIndex is null || selectedIndex < 0 || selectedIndex >= importable.Length)
            {
                StatusText = "Importação cancelada.";
                return;
            }
            CloudBackupFile selectedFile = importable[selectedIndex.Value];
            StatusText = $"Baixando {selectedFile.FileName}...";
            CloudBackupDownloadResult result = await _backup.DownloadCloudFileAsync(selectedFile.FileName, cancellationToken);
            if (!result.Success || result.BackupSet is null)
            {
                StatusText = result.ErrorMessage ?? "Ainda não existe um backup de perfil na nuvem.";
                return;
            }

            int imported = _collections.ImportJsonBackup(
                WinProvision.Core.Services.AotJson.Serialize(result.BackupSet, WinProvisionJsonOptions.Profile),
                selectedFile.FileName);
            StatusText = $"{selectedFile.FileName} importado: {imported} coleção(ões) adicionada(s).";
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

    private async Task RefreshCloudFilesAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected) return;
        IsLoadingCloudFiles = true;
        try
        {
            var files = await _backup.ListCloudFilesAsync(cancellationToken);
            string? selectedName = SelectedCloudFile?.FileName;
            CloudFiles.Clear();
            foreach (var file in files)
                CloudFiles.Add(new CloudBackupFileRow(file));
            SelectedCloudFile = CloudFiles.FirstOrDefault(file => file.FileName == selectedName)
                ?? CloudFiles.FirstOrDefault(file => file.IsPrimary && file.CanImport)
                ?? CloudFiles.FirstOrDefault(file => file.CanImport);
            StatusText = files.Count == 0 ? "Ainda não há arquivos JSON no Gist de backup." :
                $"{files.Count} arquivo(s) JSON encontrado(s) no Gist de backup.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.RefreshCloudFiles");
            StatusText = $"Não foi possível listar os arquivos do Gist. {exception.Message}";
        }
        finally { IsLoadingCloudFiles = false; Refresh(); }
    }

    private async Task RestoreSelectedCloudFileAsync(CancellationToken cancellationToken)
    {
        if (SelectedCloudFile is not { CanImport: true } selected) return;
        IsBusy = true;
        try
        {
            var result = await _backup.DownloadCloudFileAsync(selected.FileName, cancellationToken);
            if (!result.Success || result.BackupSet is null)
            {
                StatusText = result.ErrorMessage ?? "O JSON selecionado não pôde ser importado.";
                return;
            }
            int imported = _collections.ImportJsonBackup(AotJson.Serialize(result.BackupSet, WinProvisionJsonOptions.Profile), selected.FileName);
            StatusText = $"{imported} coleção(ões) importada(s) de {selected.FileName}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { StatusText = "Importação cancelada."; }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.RestoreSelectedCloudFile");
            StatusText = $"Não foi possível importar o JSON. {exception.Message}";
        }
        finally { IsBusy = false; Refresh(); }
    }

    private async Task DeleteSelectedCloudFileAsync(CancellationToken cancellationToken)
    {
        if (SelectedCloudFile is not { } selected) return;
        bool confirmed = await _presentation.ConfirmAsync("Excluir JSON do Gist",
            $"Excluir '{selected.FileName}' do Gist de backup? Esta ação não apaga as coleções deste computador. Se for o backup principal, a próxima sincronização poderá criá-lo novamente.",
            "Excluir arquivo");
        if (!confirmed) return;
        IsBusy = true;
        try
        {
            var result = await _backup.DeleteCloudFileAsync(selected.FileName, cancellationToken);
            if (!result.Success)
            {
                StatusText = result.Error ?? "O GitHub não confirmou a exclusão.";
                return;
            }
            CloudFiles.Remove(selected);
            SelectedCloudFile = CloudFiles.FirstOrDefault(file => file.CanImport);
            StatusText = $"{selected.FileName} excluído do Gist.";
            Refresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { StatusText = "Exclusão cancelada."; }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.DeleteSelectedCloudFile");
            StatusText = $"Não foi possível excluir o JSON. {exception.Message}";
        }
        finally { IsBusy = false; Refresh(); }
    }

    private async Task DeleteAllAppGistsAsync(CancellationToken cancellationToken)
    {
        bool confirmed = await _presentation.ConfirmAsync("Excluir todos os Gists do WinProvision",
            "Excluir permanentemente todos os Gists de backup e bootstrap criados pelo WinProvision nesta conta GitHub? Os backups locais e as coleções deste computador serão preservados. O envio automático ao Gist será desativado.",
            "Excluir todos");
        if (!confirmed) return;
        IsBusy = true;
        try
        {
            _preferences.SetAutomaticCloudBackup(false);
            AutomaticCloudBackupEnabled = false;
            var result = await _backup.DeleteAllAppGistsAsync(cancellationToken);
            CloudFiles.Clear();
            SelectedCloudFile = null;
            StatusText = result.Error is null
                ? $"{result.DeletedCount} Gist(s) do WinProvision excluído(s). O backup automático na nuvem está desativado."
                : result.TotalCount == 0 ? result.Error
                : $"{result.DeletedCount} de {result.TotalCount} Gist(s) excluído(s). {result.Error}";
            Refresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { StatusText = "Exclusão cancelada. Atualize a lista para conferir os Gists restantes."; }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "AccountSyncViewModel.DeleteAllAppGists");
            StatusText = $"Não foi possível excluir todos os Gists. {exception.Message}";
        }
        finally { IsBusy = false; }
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
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
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
