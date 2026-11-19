using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class LanInstallerOptionViewModel : ObservableObject
{
    internal LanCachedInstaller? CachedInstaller { get; }
    private LanInstallerEntry Installer { get; }
    public string FileName => Installer.File;
    public string PackageId => Installer.PackageId;
    public string? Version => LanInstallerMetadataReader.NormalizeVersion(
        CachedInstaller?.Metadata?.Version ?? Installer.Version);
    public string DisplayName => CachedInstaller?.Metadata?.Name ?? LanInstallerMetadataReader.CleanFileName(Path.GetFileNameWithoutExtension(Installer.File));
    public string Sha256 => Installer.Sha256;
    public string DetectedType => CachedInstaller?.Detection.DisplayName ?? "Não detectado";
    public bool HashVerified => CachedInstaller?.HashVerified == true;
    public string TypeBadge => DetectedType switch
    {
        "MSI" => "MSI",
        "NSIS" => "NSIS",
        "Inno Setup" => "Inno",
        "WiX Burn" => "Burn",
        "InstallShield" => "InstallShield",
        _ => "EXE"
    };
    public string HashStatus => CachedInstaller?.HashVerified == true ? "SHA-256 verificado" : "SHA-256 não verificado";
    public bool HasValidSignature => Signature.StartsWith("Assinatura Authenticode válida", StringComparison.OrdinalIgnoreCase);
    public string SignatureStatus => HasValidSignature
        ? "Assinatura válida"
        : Signature.StartsWith("Sem assinatura", StringComparison.OrdinalIgnoreCase)
            ? "Sem assinatura digital"
            : Signature.Contains("não verificada", StringComparison.OrdinalIgnoreCase)
                ? "Não verificada"
                : "Assinatura inválida";
    public string ArgumentsOrigin => _argumentsEdited
        ? "Origem: manual"
        : _initialArguments.Source switch
        {
            LanInstallerArgumentSource.Server => "Origem: servidor",
            LanInstallerArgumentSource.Manifest => "Origem: manifesto",
            LanInstallerArgumentSource.Zip => "Origem: ZIP",
            LanInstallerArgumentSource.Collection => "Origem: coleção",
            LanInstallerArgumentSource.TypeDefault => "Origem: padrão do tipo",
            _ => "Origem: sem padrão"
        };
    public string SupportStatus => CachedInstaller is null
        ? _failure ?? "Sincronização não concluída."
        : !CachedInstaller.TypeMatches
            ? CachedInstaller.Availability.UnsupportedReason ?? "Tipo detectado diverge do servidor."
        : !CachedInstaller.HashVerified
            ? "Instalação bloqueada: hash não verificado."
            : CanInstall ? "Instalação silenciosa disponível" : "Sem suporte silencioso: informe argumentos válidos.";
    public bool CanEditArguments => _allowManualArguments;
    public bool CanInstall => CachedInstaller is not null && CachedInstaller.TypeMatches && CachedInstaller.HashVerified
        && LanInstallerValidation.IsSafeSilentArguments(SilentArguments);
    public bool IsVersionVisible => Version is not null;
    private readonly string? _failure;
    private LanInstallerArguments _initialArguments;
    private readonly LanInstallerKind _installerKind;
    private bool _allowManualArguments;
    private bool _argumentsEdited;
    private string _silentArguments = string.Empty;
    public string SilentArguments
    {
        get => _silentArguments;
        set
        {
            if (!SetProperty(ref _silentArguments, value))
                return;
            if (!_allowManualArguments)
            {
                _silentArguments = _initialArguments.Value ?? string.Empty;
                OnPropertyChanged(nameof(SilentArguments));
                OnPropertyChanged(nameof(ArgumentsOrigin));
                OnPropertyChanged(nameof(SupportStatus));
                OnPropertyChanged(nameof(CanInstall));
                return;
            }
            _argumentsEdited = true;
            OnPropertyChanged(nameof(ArgumentsOrigin));
            OnPropertyChanged(nameof(SupportStatus));
            OnPropertyChanged(nameof(CanInstall));
            Status = CanInstall ? "Sincronizado; argumentos atualizados." : "Argumentos inválidos ou insuficientes.";
        }
    }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string Signature { get; set; } = "Assinatura ainda não verificada.";
    [ObservableProperty] public partial bool HasZoneIdentifier { get; set; }
    [ObservableProperty] public partial bool RemoveZoneIdentifier { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = string.Empty;
    [ObservableProperty] public partial int TimeoutSeconds { get; set; } = 7200;

    public LanInstallerOptionViewModel(
        LanCachedInstaller installer,
        LanInstallerSecurityInfo security,
        bool allowManualArguments = true,
        bool allowExternalArguments = true)
    {
        CachedInstaller = installer;
        Installer = installer.Availability.Installer;
        _installerKind = installer.Detection.Kind;
        _allowManualArguments = allowManualArguments;
        _initialArguments = LanInstallerArgumentsPolicy.Resolve(
            installer.Detection.Kind,
            installer.Availability.Installer.SilentArgs,
            manualArguments: null,
            externalSource: installer.Availability.Installer.SilentSource,
            allowManualArguments: allowManualArguments,
            allowExternalArguments: allowExternalArguments);
        _silentArguments = _initialArguments.Value ?? string.Empty;
        IsSelected = LanInstallerSelectionPolicy.SelectByDefault(
            installer.HashVerified,
            installer.TypeMatches,
            installer.Availability.SilentSupported,
            LanInstallerValidation.IsSafeSilentArguments(_silentArguments));
        Signature = security.Signature;
        HasZoneIdentifier = security.HasZoneIdentifier;
        Status = installer.Availability.UnsupportedReason is null && installer.TypeMatches
            ? "Pronto"
            : installer.Availability.UnsupportedReason ?? "Tipo divergente; instalação bloqueada.";
    }

    public LanInstallerOptionViewModel(
        LanInstallerSyncItem item,
        bool allowManualArguments = true,
        bool allowExternalArguments = true)
    {
        Installer = item.Installer;
        _installerKind = LanInstallerKind.Unknown;
        _allowManualArguments = allowManualArguments;
        _failure = item.Failure ?? item.Status;
        _initialArguments = LanInstallerArgumentsPolicy.Resolve(LanInstallerKind.Unknown,
            item.Installer.SilentArgs,
            manualArguments: null,
            externalSource: item.Installer.SilentSource,
            allowManualArguments: allowManualArguments,
            allowExternalArguments: allowExternalArguments);
        _silentArguments = _initialArguments.Value ?? string.Empty;
        IsSelected = false;
        Signature = "Não verificada: sincronização falhou.";
        Status = item.Status;
    }

    public void UpdateArgumentPreferences(bool allowManualArguments, bool allowExternalArguments)
    {
        _allowManualArguments = allowManualArguments;
        _initialArguments = LanInstallerArgumentsPolicy.Resolve(
            _installerKind,
            Installer.SilentArgs,
            manualArguments: null,
            externalSource: Installer.SilentSource,
            allowManualArguments: allowManualArguments,
            allowExternalArguments: allowExternalArguments);
        if (!_argumentsEdited || !allowManualArguments)
        {
            _argumentsEdited = false;
            SilentArguments = _initialArguments.Value ?? string.Empty;
        }
        OnPropertyChanged(nameof(CanEditArguments));
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class LanServedInstallerViewModel(LanServedInstaller installer) : ObservableObject
{
    public string Name { get; } = installer.Name;
    public string File { get; } = installer.File;
    public string? Version { get; } = installer.Version;
    public string Type { get; } = installer.InstallerType;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class LanRequestLogViewModel(LanRequestLog entry) : ObservableObject
{
    public string Timestamp { get; } = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string IpAddress { get; } = entry.IpAddress;
    public string File { get; } = entry.File;
    public string StatusCode { get; } = entry.StatusCode.ToString(CultureInfo.InvariantCulture);
}

public sealed partial class SettingsViewModel
{
    private LanProvisioningService? _lanProvisioning;
    private IClipboardService? _lanClipboard;
    private IUiDispatcher? _lanDispatcher;
    private bool _lanSettingsInitialized;

    [ObservableProperty] public partial string LanServerFolder { get; set; } = string.Empty;
    [ObservableProperty] public partial string LanServerPort { get; set; } = "8080";
    [ObservableProperty] public partial string LanServerToken { get; set; } = string.Empty;
    [ObservableProperty] public partial string LanServerStatus { get; set; } = "Servidor LAN parado.";
    [ObservableProperty] public partial string LanPrivateAddressesText { get; set; } = "Nenhum endereço privado disponível.";
    [ObservableProperty] public partial string LanPrimaryAddress { get; set; } = "Nenhum endereço privado disponível.";
    [ObservableProperty] public partial bool IsLanServerRunning { get; set; }
    [ObservableProperty] public partial string LanClientAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial string LanClientToken { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsLanClientTokenVisible { get; set; }
    [ObservableProperty] public partial bool IsLanServerTokenVisible { get; set; }
    [ObservableProperty] public partial bool RememberLanServer { get; set; }
    [ObservableProperty] public partial bool IsLanConnected { get; set; }
    [ObservableProperty] public partial string LanConnectionStatus { get; set; } = "Desconectado";
    public string LanClientTokenVisibilityLabel => IsLanClientTokenVisible ? "Ocultar token" : "Mostrar token";
    public string LanServerTokenVisibilityLabel => IsLanServerTokenVisible ? "Ocultar token" : "Mostrar token";
    public string InstallLanPackagesLabel => $"Instalar selecionados ({SelectedLanInstallerCount})";
    [ObservableProperty] public partial int LanSyncProgress { get; set; }
    [ObservableProperty] public partial string LanSyncFile { get; set; } = string.Empty;
    [ObservableProperty] public partial int LanInstallProgress { get; set; }
    [ObservableProperty] public partial string LanInstallCurrentItem { get; set; } = string.Empty;
    [ObservableProperty] public partial string LanClientStatus { get; set; } = "Informe o endereço e o token do servidor.";
    [ObservableProperty] public partial bool IsLanBusy { get; private set; }
    [ObservableProperty] public partial bool HasLanInstallers { get; private set; }

    public ObservableCollection<LanRequestLogViewModel> LanRequestLog { get; } = new();
    public ObservableCollection<LanInstallerOptionViewModel> LanClientInstallers { get; } = new();
    public ObservableCollection<LanServedInstallerViewModel> LanServedInstallers { get; } = new();
    public ObservableCollection<string> LanOtherAddresses { get; } = new();
    public int SelectedLanInstallerCount => LanClientInstallers.Count(item => item.IsSelected && item.CanInstall);
    public int SelectedLanInstallerUnsignedCount => LanClientInstallers.Count(item =>
        item.IsSelected && !item.HasValidSignature);
    public bool CanStartLanServer => !IsLanBusy && !IsLanServerRunning;
    public bool CanStopLanServer => !IsLanBusy && IsLanServerRunning;
    public bool CanRunLanClient => !IsLanBusy;

    private AsyncRelayCommand? _pickLanFolderCommand;
    public IAsyncRelayCommand PickLanFolderCommand => _pickLanFolderCommand ??= new AsyncRelayCommand(PickLanFolderAsync, () => !IsLanBusy);
    private RelayCommand? _generateLanTokenCommand;
    public IRelayCommand GenerateLanTokenCommand => _generateLanTokenCommand ??= new RelayCommand(GenerateLanToken, () => !IsLanBusy && !IsLanServerRunning);
    private RelayCommand? _copyLanTokenCommand;
    public IRelayCommand CopyLanTokenCommand => _copyLanTokenCommand ??= new RelayCommand(CopyLanToken, () => !string.IsNullOrEmpty(LanServerToken));
    private AsyncRelayCommand? _startLanServerCommand;
    public IAsyncRelayCommand StartLanServerCommand => _startLanServerCommand ??= new AsyncRelayCommand(StartLanServerAsync, () => CanStartLanServer);
    private AsyncRelayCommand? _stopLanServerCommand;
    public IAsyncRelayCommand StopLanServerCommand => _stopLanServerCommand ??= new AsyncRelayCommand(StopLanServerAsync, () => CanStopLanServer);
    private AsyncRelayCommand? _testLanConnectionCommand;
    public IAsyncRelayCommand TestLanConnectionCommand => _testLanConnectionCommand ??= new AsyncRelayCommand(TestLanConnectionAsync, () => CanRunLanClient);
    private AsyncRelayCommand? _connectLanClientCommand;
    public IAsyncRelayCommand ConnectLanClientCommand => _connectLanClientCommand ??= new AsyncRelayCommand(ConnectLanClientAsync, () => CanRunLanClient);
    private RelayCommand? _copyLanServerAddressCommand;
    public IRelayCommand CopyLanServerAddressCommand => _copyLanServerAddressCommand ??= new RelayCommand(CopyLanServerAddress, () => !string.IsNullOrEmpty(LanPrimaryAddress) && !LanPrimaryAddress.StartsWith("Nenhum", StringComparison.Ordinal));
    private RelayCommand? _copyLanConnectionCodeCommand;
    public IRelayCommand CopyLanConnectionCodeCommand => _copyLanConnectionCodeCommand ??= new RelayCommand(CopyLanConnectionCode, () => !string.IsNullOrEmpty(LanServerToken) && !LanPrimaryAddress.StartsWith("Nenhum", StringComparison.Ordinal));
    private AsyncRelayCommand? _pasteLanConnectionCodeCommand;
    public IAsyncRelayCommand PasteLanConnectionCodeCommand => _pasteLanConnectionCodeCommand ??= new AsyncRelayCommand(PasteLanConnectionCodeAsync, () => !IsLanBusy);
    private RelayCommand? _toggleAllLanInstallersCommand;
    public IRelayCommand ToggleAllLanInstallersCommand => _toggleAllLanInstallersCommand ??= new RelayCommand(ToggleAllLanInstallers, () => LanClientInstallers.Any(item => item.CanInstall));
    private AsyncRelayCommand? _syncLanFolderCommand;
    public IAsyncRelayCommand SyncLanFolderCommand => _syncLanFolderCommand ??= new AsyncRelayCommand(SyncLanFolderAsync, () => CanRunLanClient);
    private AsyncRelayCommand? _installLanPackagesCommand;
    public IAsyncRelayCommand InstallLanPackagesCommand => _installLanPackagesCommand ??= new AsyncRelayCommand(InstallLanPackagesAsync, () => CanRunLanClient && LanClientInstallers.Any(item => item.CanInstall && item.IsSelected));
    private RelayCommand? _toggleLanClientTokenCommand;
    public IRelayCommand ToggleLanClientTokenCommand => _toggleLanClientTokenCommand ??= new RelayCommand(() => IsLanClientTokenVisible = !IsLanClientTokenVisible);
    private RelayCommand? _toggleLanServerTokenCommand;
    public IRelayCommand ToggleLanServerTokenCommand => _toggleLanServerTokenCommand ??= new RelayCommand(() => IsLanServerTokenVisible = !IsLanServerTokenVisible);

    internal void ConfigureLanProvisioning(LanProvisioningService? service, IClipboardService? clipboard, IUiDispatcher? dispatcher)
    {
        _lanProvisioning = service;
        _lanClipboard = clipboard;
        _lanDispatcher = dispatcher;
        LanClientInstallers.CollectionChanged += OnLanInstallersChanged;
        if (service is not null)
            service.RequestCompleted += OnLanRequestCompleted;
    }

    public void InitializeLanSettings()
    {
        if (_lanSettingsInitialized || _lanProvisioning is null)
            return;
        _lanSettingsInitialized = true;
        LanServerToken = _lanProvisioning.ServerToken ?? string.Empty;
        LanClientConnection? savedConnection = LanTokenStorage.LoadClientConnection();
        if (savedConnection is not null)
        {
            LanClientAddress = savedConnection.Address;
            LanClientToken = savedConnection.Token;
            RememberLanServer = true;
        }
        else
            LanClientToken = _lanProvisioning.ClientToken ?? string.Empty;
        RefreshLanAddresses();
        IsLanServerRunning = _lanProvisioning.IsServerRunning;
        LanServerStatus = IsLanServerRunning
            ? $"Servidor ativo na porta {_lanProvisioning.ActivePort}."
            : "Servidor LAN parado.";
        NotifyLanCommands();
    }

    partial void OnLanServerPortChanged(string value)
    {
        RefreshLanAddresses();
    }

    partial void OnLanServerTokenChanged(string value)
    {
        _copyLanTokenCommand?.NotifyCanExecuteChanged();
        _copyLanConnectionCodeCommand?.NotifyCanExecuteChanged();
    }
    partial void OnLanPrimaryAddressChanged(string value)
    {
        _copyLanServerAddressCommand?.NotifyCanExecuteChanged();
        _copyLanConnectionCodeCommand?.NotifyCanExecuteChanged();
    }
    partial void OnIsLanClientTokenVisibleChanged(bool value) => OnPropertyChanged(nameof(LanClientTokenVisibilityLabel));
    partial void OnIsLanServerTokenVisibleChanged(bool value) => OnPropertyChanged(nameof(LanServerTokenVisibilityLabel));
    partial void OnIsLanServerRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartLanServer));
        OnPropertyChanged(nameof(CanStopLanServer));
        NotifyLanCommands();
    }
    partial void OnIsLanBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartLanServer));
        OnPropertyChanged(nameof(CanStopLanServer));
        OnPropertyChanged(nameof(CanRunLanClient));
        NotifyLanCommands();
    }
    private void RefreshLanAddresses()
    {
        if (_lanProvisioning is null)
            return;
        LanNetworkAddress[] addresses = _lanProvisioning.NetworkAddresses.ToArray();
        LanNetworkAddress? primary = addresses.FirstOrDefault(item => !item.IsVirtual);
        LanPrimaryAddress = primary is null
            ? "Nenhum endereço privado disponível."
            : $"http://{FormatLanHost(primary.Address)}:{LanServerPort}";
        LanOtherAddresses.Clear();
        foreach (LanNetworkAddress item in addresses.Where(item => item.IsVirtual || item != primary))
            LanOtherAddresses.Add($"{item.InterfaceName}: http://{FormatLanHost(item.Address)}:{LanServerPort}");
        LanPrivateAddressesText = string.Join(Environment.NewLine,
            (primary is null ? [] : new[] { LanPrimaryAddress }).Concat(LanOtherAddresses));
    }

    private static string FormatLanHost(string address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address;
    private async Task PickLanFolderAsync()
    {
        try
        {
            if (await _presentation.PickFolderAsync() is { } folder)
                LanServerFolder = folder;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.PickFolder");
            LanServerStatus = "Não foi possível selecionar a pasta.";
        }
    }

    private void GenerateLanToken()
    {
        if (_lanProvisioning is null)
            return;
        try
        {
            LanServerToken = _lanProvisioning.GenerateServerToken();
            LanServerStatus = "Novo token de 256 bits gerado e salvo com proteção DPAPI.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.GenerateToken");
            LanServerStatus = "Não foi possível gerar ou proteger o token.";
        }
    }

    private void CopyLanToken()
    {
        if (string.IsNullOrEmpty(LanServerToken))
            return;
        LanServerStatus = _lanClipboard?.TrySetText(LanServerToken) == true
            ? "Token copiado para a área de transferência."
            : "Não foi possível copiar o token.";
    }

    private async Task StartLanServerAsync(CancellationToken token)
    {
        if (_lanProvisioning is null)
            return;
        if (!int.TryParse(LanServerPort, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            LanServerStatus = "Informe uma porta entre 1 e 65535.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LanServerFolder) || !Directory.Exists(LanServerFolder))
        {
            LanServerStatus = "Selecione uma pasta de instaladores existente.";
            return;
        }

        SetLanBusy(true);
        try
        {
            await _lanProvisioning.StartServerAsync(LanServerFolder, port, token);
            IsLanServerRunning = true;
            LanServerToken = _lanProvisioning.ServerToken ?? LanServerToken;
            IReadOnlyList<LanServedInstaller> served = await _lanProvisioning.GetServedInstallersAsync(LanServerFolder, token);
            LanServedInstallers.Clear();
            foreach (LanServedInstaller installer in served)
                LanServedInstallers.Add(new LanServedInstallerViewModel(installer));
            LanServerStatus = $"Servidor ativo · porta {port} · {served.Count} instaladores.";
        }
        catch (Exception exception) when (exception is HttpListenerException or Win32Exception or InvalidOperationException
            or IOException or UnauthorizedAccessException or ArgumentException)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.StartServer");
            LanServerStatus = exception is HttpListenerException
                ? $"Não foi possível abrir a porta {port}; ela pode estar ocupada ou a URL ACL não foi autorizada."
                : $"Não foi possível iniciar o servidor: {exception.Message}";
        }
        finally { SetLanBusy(false); }
    }

    private async Task StopLanServerAsync(CancellationToken token)
    {
        if (_lanProvisioning is null)
            return;
        SetLanBusy(true);
        try
        {
            await _lanProvisioning.StopServerAsync(token);
            IsLanServerRunning = false;
            LanServerStatus = "Servidor parado; regra de firewall e URL ACL removidas.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.StopServer");
            IsLanServerRunning = _lanProvisioning.IsServerRunning;
            LanServerStatus = $"O servidor parou, mas a limpeza de rede falhou: {exception.Message}";
        }
        finally { SetLanBusy(false); }
    }

    private async Task TestLanConnectionAsync(CancellationToken token)
    {
        if (_lanProvisioning is null)
            return;
        if (string.IsNullOrWhiteSpace(LanClientAddress) || string.IsNullOrWhiteSpace(LanClientToken))
        {
            LanClientStatus = "Informe o endereço HTTP privado e o token.";
            return;
        }
        SetLanBusy(true);
        try
        {
            await _lanProvisioning.TestConnectionAsync(LanClientAddress, LanClientToken, token);
            LanClientStatus = "Conexão autenticada e manifesto LAN válido.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.TestConnection");
            LanClientStatus = $"Falha na conexão: {exception.Message}";
        }
        finally { SetLanBusy(false); }
    }

    private async Task ConnectLanClientAsync(CancellationToken token)
    {
        if (_lanProvisioning is null)
            return;
        if (string.IsNullOrWhiteSpace(LanClientAddress) || string.IsNullOrWhiteSpace(LanClientToken))
        {
            LanClientStatus = "Informe o endereço e o token ou cole um código de conexão.";
            return;
        }

        SetLanBusy(true);
        IsLanConnected = false;
        LanClientStatus = "Conectando e sincronizando instaladores...";
        try
        {
            await _lanProvisioning.TestConnectionAsync(LanClientAddress, LanClientToken, token);
            if (RememberLanServer)
                _lanProvisioning.SaveClientConnection(LanClientAddress.TrimEnd('/'), LanClientToken);
            else
                _lanProvisioning.ForgetClientConnection();
            await SynchronizeLanFolderCoreAsync(token);
            IsLanConnected = true;
            string host = Uri.TryCreate(LanClientAddress, UriKind.Absolute, out Uri? address)
                ? address.Host
                : LanClientAddress;
            LanConnectionStatus = $"Conectado a {host} · {LanClientInstallers.Count} instaladores";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
            or ArgumentException or InvalidOperationException)
        {
            IsLanConnected = false;
            LanConnectionStatus = "Desconectado";
            WinProvisionLog.Warning(exception, "Settings.Lan.Connect");
            LanClientStatus = $"Falha ao conectar: {exception.Message}";
        }
        finally { SetLanBusy(false); }
    }

    private async Task SyncLanFolderAsync(CancellationToken token)
    {
        if (_lanProvisioning is null)
            return;
        SetLanBusy(true);
        try { await SynchronizeLanFolderCoreAsync(token); }
        finally { SetLanBusy(false); }
    }

    private async Task SynchronizeLanFolderCoreAsync(CancellationToken token)
    {
        if (_lanProvisioning is null || string.IsNullOrWhiteSpace(LanClientAddress) || string.IsNullOrWhiteSpace(LanClientToken))
            throw new InvalidOperationException("Informe o endereço e o token do servidor.");
        LanClientInstallers.Clear();
        LanSyncProgress = 0;
        LanSyncFile = "Lendo manifesto...";
        try
        {
            var progress = new Progress<LanDownloadProgress>(item =>
            {
                LanSyncFile = item.TotalBytes is > 0
                    ? $"Baixando {item.File}: {item.BytesReceived:N0} / {item.TotalBytes:N0} bytes"
                    : $"Baixando {item.File}: {item.BytesReceived:N0} bytes";
                LanSyncProgress = item.TotalBytes is > 0
                    ? (int)Math.Clamp(item.BytesReceived * 100d / item.TotalBytes.Value, 0, 100)
                    : 0;
            });
            LanSyncResult result = await _lanProvisioning.SyncAsync(LanClientAddress, LanClientToken, progress, token);
            (bool allowManualArguments, bool allowExternalArguments) = GetLanArgumentPreferences();
            IReadOnlyList<LanInstallerSyncItem> syncItems = result.Items
                ?? result.Installers.Select(installer => new LanInstallerSyncItem(installer.Availability.Installer,
                    installer, "Sincronizado")).ToArray();
            foreach (LanInstallerSyncItem item in syncItems)
            {
                if (item.CachedInstaller is null)
                {
                    LanClientInstallers.Add(new LanInstallerOptionViewModel(item, allowManualArguments, allowExternalArguments));
                    continue;
                }
                LanInstallerSecurityInfo security = item.CachedInstaller.SecurityInfo
                    ?? new LanInstallerSecurityInfo(item.CachedInstaller.Availability.Installer.Sha256,
                        "Assinatura não verificada.", false);
                LanClientInstallers.Add(new LanInstallerOptionViewModel(
                    item.CachedInstaller, security, allowManualArguments, allowExternalArguments));
            }

            int total = syncItems.Count;
            int synced = syncItems.Count(item => item.CachedInstaller is not null);
            LanSyncFile = string.Empty;
            LanClientStatus = total == 0
                ? "Nenhum instalador sincronizado ainda."
                : $"{synced} de {total} instaladores sincronizados.";
            _installLanPackagesCommand?.NotifyCanExecuteChanged();
            NotifyLanSelection();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.Synchronize");
            LanSyncFile = string.Empty;
            LanClientStatus = $"Falha ao sincronizar: {exception.Message}";
            throw;
        }
    }

    private (bool AllowManualArguments, bool AllowExternalArguments) GetLanArgumentPreferences() =>
        (_elevationPreferences.GetBoolean("allowManualSilentArguments", defaultValue: true),
            _elevationPreferences.GetBoolean("allowImportedSilentArguments", defaultValue: true));

    private void ApplyLanArgumentPreferences()
    {
        (bool allowManualArguments, bool allowExternalArguments) = GetLanArgumentPreferences();
        foreach (LanInstallerOptionViewModel option in LanClientInstallers)
            option.UpdateArgumentPreferences(allowManualArguments, allowExternalArguments);
    }

    private async Task PasteLanConnectionCodeAsync(CancellationToken token)
    {
        if (_lanClipboard is null)
            return;
        string? code = await _lanClipboard.TryGetTextAsync();
        if (!LanConnectionCode.TryDecode(code, out LanClientConnection? connection) || connection is null)
        {
            LanClientStatus = "O código de conexão da área de transferência é inválido.";
            return;
        }
        LanClientAddress = connection.Address;
        LanClientToken = connection.Token;
        RememberLanServer = true;
        LanClientStatus = "Código preenchido. Conecte para sincronizar os instaladores.";
    }

    private void CopyLanConnectionCode()
    {
        if (string.IsNullOrEmpty(LanServerToken) || LanPrimaryAddress.StartsWith("Nenhum", StringComparison.Ordinal))
            return;
        try
        {
            string code = LanConnectionCode.Encode(LanPrimaryAddress, LanServerToken);
            LanServerStatus = _lanClipboard?.TrySetText(code) == true
                ? "Código de conexão copiado. Trate-o como um segredo."
                : "Não foi possível copiar o código de conexão.";
        }
        catch (ArgumentException exception)
        {
            LanServerStatus = exception.Message;
        }
    }

    private void CopyLanServerAddress()
    {
        if (_lanClipboard?.TrySetText(LanPrimaryAddress) != true)
            LanServerStatus = "Não foi possível copiar o endereço principal.";
    }

    private async Task InstallLanPackagesAsync(CancellationToken token)
    {
        if (_lanProvisioning is null)
            return;

        List<LanInstallerInstallRequest> selected = [];
        int unsignedCount = 0;
        int batchCompleted = 0;
        try
        {
            await InvokeLanUiAsync(() =>
            {
                selected = LanClientInstallers
                    .Where(item => item.IsSelected && item.CanInstall && item.CachedInstaller is not null)
                    .Select(item =>
                    {
                        LanCachedInstaller cached = item.CachedInstaller
                            ?? throw new InvalidOperationException("O instalador selecionado não está no cache local.");
                        LanInstallerEntry installer = cached.Availability.Installer;
                        return new LanInstallerInstallRequest(
                            cached.LocalPath,
                            installer.File,
                            installer.PackageId,
                            installer.Version,
                            installer.Sha256,
                            installer.InstallerType ?? string.Empty,
                            installer.Scope,
                            item.SilentArguments,
                            item.HasZoneIdentifier,
                            item.RemoveZoneIdentifier,
                            item.TimeoutSeconds);
                    })
                    .ToList();
                unsignedCount = LanClientInstallers.Count(item =>
                    selected.Any(request => request.File.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))
                    && !item.HasValidSignature);
            });
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.Install.Selection");
            await InvokeLanUiAsync(() => LanClientStatus = $"Falha na etapa de leitura da seleção: {exception.Message}");
            return;
        }

        List<string> zoneIdentifierFiles = [];
        await InvokeLanUiAsync(() =>
        {
            zoneIdentifierFiles = LanClientInstallers
                .Where(item => selected.Any(request =>
                    request.File.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))
                    && item.HasZoneIdentifier && !item.RemoveZoneIdentifier)
                .Select(item => item.FileName)
                .ToList();
        });
        if (zoneIdentifierFiles.Count > 0)
        {
            await InvokeLanUiAsync(() =>
            {
                foreach (string file in zoneIdentifierFiles)
                {
                    LanInstallerOptionViewModel? item = LanClientInstallers.FirstOrDefault(candidate =>
                        candidate.FileName.Equals(file, StringComparison.OrdinalIgnoreCase));
                    if (item is not null)
                        item.Status = "Bloqueado: confirme a remoção de Zone.Identifier nos detalhes.";
                }
                LanClientStatus = "Instalação bloqueada: confirme a remoção de Zone.Identifier para os itens marcados.";
            });
            return;
        }

        if (selected.Count == 0)
        {
            await InvokeLanUiAsync(() => LanClientStatus = "Selecione ao menos um instalador com suporte silencioso.");
            return;
        }

        await InvokeLanUiAsync(() => SetLanBusy(true));
        try
        {
            await InvokeLanUiAsync(() => LanClientStatus = "Aguardando confirmação antes de solicitar uma única elevação...");
            bool confirmed = await ConfirmLanInstallAsync(selected.Count, unsignedCount);
            if (!confirmed)
            {
                await InvokeLanUiAsync(() => LanClientStatus = "Instalação cancelada na confirmação.");
                return;
            }

            IProgress<LanInstallBatchProgress>? progress = null;
            await InvokeLanUiAsync(() =>
            {
                LanInstallProgress = 0;
                progress = new Progress<LanInstallBatchProgress>(item =>
                {
                    PostLanUi(() =>
                    {
                        if (Volatile.Read(ref batchCompleted) != 0)
                            return;
                        LanInstallCurrentItem = $"{item.ItemIndex} de {item.ItemCount}: {item.File} — {item.Stage}";
                        LanInstallProgress = item.ItemCount > 0
                            ? (int)Math.Clamp(item.ItemIndex * 100d / item.ItemCount, 0, 100)
                            : 0;
                        LanInstallerOptionViewModel? viewModel = LanClientInstallers.FirstOrDefault(candidate =>
                            candidate.FileName.Equals(item.File, StringComparison.OrdinalIgnoreCase));
                        if (viewModel is not null)
                            viewModel.Status = $"{item.Stage}: {item.Message}";
                        LanClientStatus = $"{item.File}: {item.Stage} — {item.Message}";
                    });
                });
            });

            LanInstallBatchResult batch = await _lanProvisioning.InstallAsync(selected, progress, token);
            IReadOnlyList<LanInstallItemResult> results = batch.Items;
            Interlocked.Exchange(ref batchCompleted, 1);
            int installed = 0;
            int failed = 0;
            int canceled = 0;
            await InvokeLanUiAsync(() =>
            {
                foreach (LanInstallItemResult result in results)
                {
                    LanInstallerOptionViewModel? item = LanClientInstallers.FirstOrDefault(candidate =>
                        candidate.FileName.Equals(result.File, StringComparison.OrdinalIgnoreCase));
                    string status = LanInstallerExitCodePolicy.Describe(result.Outcome, result.ExitCode);
                    if (batch.Message is not null && result.Stage == LanInstallerExecutionStage.StartingWorker)
                        status = "Falhou: componente de instalação indisponível.";
                    else if (result.Outcome == LanInstallerOutcome.Failed && !string.IsNullOrWhiteSpace(result.Message))
                        status += $": {result.Message}";
                    if (item is not null)
                        item.Status = status;
                    switch (result.Outcome)
                    {
                        case LanInstallerOutcome.Installed:
                        case LanInstallerOutcome.RestartPending:
                            installed++;
                            break;
                        case LanInstallerOutcome.Canceled:
                            canceled++;
                            break;
                        default:
                            failed++;
                            break;
                    }
                    if (batch.Message is null && !string.IsNullOrWhiteSpace(result.ExceptionDetails))
                        WinProvisionLog.Warning(new InvalidOperationException(result.ExceptionDetails), "Settings.Lan.Install");
                }
                LanInstallProgress = 100;
                LanClientStatus = batch.Message ?? (canceled > 0
                    ? $"{installed} instalados, {failed} falharam, {canceled} cancelados."
                    : $"{installed} instalados, {failed} falharam.");
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await InvokeLanUiAsync(() => LanClientStatus = "Instalação cancelada; o helper encerrou o instalador atual.");
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Settings.Lan.Install");
            await InvokeLanUiAsync(() => LanClientStatus =
                $"Falha na instalação em lote: {exception.Message} (0x{exception.HResult:X8}).");
        }
        finally
        {
            Interlocked.Exchange(ref batchCompleted, 1);
            await InvokeLanUiAsync(() => SetLanBusy(false));
        }
    }

    private async Task<bool> ConfirmLanInstallAsync(int count, int unsignedCount)
    {
        Task<bool>? confirmation = null;
        await InvokeLanUiAsync(() => confirmation = _presentation.ConfirmAsync(
            "Instalar instaladores LAN",
            $"{count} instaladores serão verificados novamente e executados em sequência após uma única autorização do WinProvision Elevator. {unsignedCount} não têm assinatura digital válida. Confira os argumentos silenciosos e os detalhes antes de continuar.",
            "Continuar", "Cancelar"));
        return await confirmation!;
    }

    private Task InvokeLanUiAsync(Action action)
    {
        if (_lanDispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }
        return _lanDispatcher.InvokeAsync(action);
    }

    private void PostLanUi(Action action)
    {
        if (_lanDispatcher is null || _lanDispatcher.CheckAccess())
            action();
        else
            _lanDispatcher.Post(action);
    }

    private void OnLanRequestCompleted(LanRequestLog entry)
    {
        void AddEntry()
        {
            LanRequestLog.Insert(0, new LanRequestLogViewModel(entry));
            while (LanRequestLog.Count > 20)
                LanRequestLog.RemoveAt(LanRequestLog.Count - 1);
        }

        if (_lanDispatcher is null || _lanDispatcher.CheckAccess()) AddEntry();
        else _lanDispatcher.Post(AddEntry);
    }

    private void OnLanInstallersChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is not null)
            foreach (LanInstallerOptionViewModel item in args.NewItems)
                item.PropertyChanged += OnLanInstallerPropertyChanged;
        if (args.OldItems is not null)
            foreach (LanInstallerOptionViewModel item in args.OldItems)
                item.PropertyChanged -= OnLanInstallerPropertyChanged;
        HasLanInstallers = LanClientInstallers.Count > 0;
        NotifyLanSelection();
    }

    private void OnLanInstallerPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LanInstallerOptionViewModel.IsSelected)
            or nameof(LanInstallerOptionViewModel.CanInstall)
            or nameof(LanInstallerOptionViewModel.SilentArguments))
            NotifyLanSelection();
    }

    private void ToggleAllLanInstallers()
    {
        bool select = SelectedLanInstallerCount < LanClientInstallers.Count(item => item.CanInstall);
        foreach (LanInstallerOptionViewModel item in LanClientInstallers.Where(item => item.CanInstall))
            item.IsSelected = select;
        NotifyLanSelection();
    }

    private void NotifyLanSelection()
    {
        OnPropertyChanged(nameof(SelectedLanInstallerCount));
        OnPropertyChanged(nameof(SelectedLanInstallerUnsignedCount));
        OnPropertyChanged(nameof(InstallLanPackagesLabel));
        _installLanPackagesCommand?.NotifyCanExecuteChanged();
        _toggleAllLanInstallersCommand?.NotifyCanExecuteChanged();
    }

    private void SetLanBusy(bool value)
    {
        IsLanBusy = value;
        NotifyLanCommands();
    }

    private void NotifyLanCommands()
    {
        _pickLanFolderCommand?.NotifyCanExecuteChanged();
        _generateLanTokenCommand?.NotifyCanExecuteChanged();
        _copyLanTokenCommand?.NotifyCanExecuteChanged();
        _startLanServerCommand?.NotifyCanExecuteChanged();
        _stopLanServerCommand?.NotifyCanExecuteChanged();
        _testLanConnectionCommand?.NotifyCanExecuteChanged();
        _connectLanClientCommand?.NotifyCanExecuteChanged();
        _syncLanFolderCommand?.NotifyCanExecuteChanged();
        _installLanPackagesCommand?.NotifyCanExecuteChanged();
        _toggleLanClientTokenCommand?.NotifyCanExecuteChanged();
        _toggleLanServerTokenCommand?.NotifyCanExecuteChanged();
        _pasteLanConnectionCodeCommand?.NotifyCanExecuteChanged();
    }
}
