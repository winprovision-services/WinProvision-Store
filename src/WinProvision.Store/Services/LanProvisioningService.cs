using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Elevation;

namespace WinProvision.Store.Services;

public sealed class LanProvisioningService : IAsyncDisposable
{
    private readonly IEnumerable<IServerModule> _serverModules;
    private readonly LanInstallersServerModule _installersModule;
    private readonly ILanTransportSecurityPolicy _transport;
    private readonly LanInstallService _installService;
    private LanInstallerHttpServer? _server;
    private FileSystemWatcher? _folderWatcher;
    private int? _activePort;
    private string? _activeServerRoot;

    public event Action<LanRequestLog>? RequestCompleted;
    public bool IsServerRunning => _server?.IsRunning == true;
    public int? ActivePort => _activePort;
    public string? ServerToken => LanTokenStorage.LoadServerToken();
    public string? ClientToken => LanTokenStorage.LoadClientToken();
    public IReadOnlyList<string> PrivateAddresses => GetNetworkAddresses().Select(item => item.Address).ToArray();
    public IReadOnlyList<LanNetworkAddress> NetworkAddresses => GetNetworkAddresses();

    public LanProvisioningService(
        IEnumerable<IServerModule> serverModules,
        LanInstallersServerModule installersModule,
        ILanTransportSecurityPolicy transport,
        LanInstallService installService)
    {
        _serverModules = serverModules;
        _installersModule = installersModule;
        _transport = transport;
        _installService = installService;
    }

    public string GenerateServerToken()
    {
        string token = LanTokenAuthenticator.GenerateToken();
        LanTokenStorage.SaveServerToken(token);
        return token;
    }

    public void SaveClientToken(string token) => LanTokenStorage.SaveClientToken(token);
    public void SaveClientConnection(string address, string token) =>
        LanTokenStorage.SaveClientConnection(address, token);
    public void ForgetClientConnection() => LanTokenStorage.DeleteClientConnection();

    public async Task StartServerAsync(string rootDirectory, int port, CancellationToken token = default)
    {
        if (IsServerRunning)
            throw new InvalidOperationException("O servidor LAN já está em execução.");
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "A porta precisa estar entre 1 e 65535.");
        if (!Directory.Exists(rootDirectory))
            throw new DirectoryNotFoundException("Selecione uma pasta de instaladores existente.");

        string? serverToken = ServerToken;
        if (string.IsNullOrEmpty(serverToken))
            serverToken = GenerateServerToken();

        string urlAclUrl = _transport.GetListenerPrefix(port);
        string firewallName = $"WinProvisionStore-Lan-{port}";
        string? account = WindowsIdentity.GetCurrent().Name;
        if (string.IsNullOrWhiteSpace(account))
            throw new InvalidOperationException("Não foi possível identificar a conta atual para registrar a URL ACL.");

        await RunNetshAsync($"http add urlacl url={Quote(urlAclUrl)} user={Quote(account)} listen=yes", token)
            .ConfigureAwait(false);
        try
        {
            await RunNetshAsync($"advfirewall firewall add rule name={Quote(firewallName)} dir=in action=allow protocol=TCP localport={port} profile=private remoteip=localsubnet", token)
                .ConfigureAwait(false);
        }
        catch
        {
            await TryRunNetshCleanupAsync($"http delete urlacl url={Quote(urlAclUrl)}");
            throw;
        }

        _installersModule.Invalidate();
        _server = new LanInstallerHttpServer(_serverModules, serverToken, _transport);
        _server.RequestCompleted += OnRequestCompleted;
        try
        {
            await _server.StartAsync(rootDirectory, port, token).ConfigureAwait(false);
            _activePort = port;
            _activeServerRoot = Path.GetFullPath(rootDirectory);
            _folderWatcher = new FileSystemWatcher(_activeServerRoot)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _folderWatcher.Changed += OnFolderChanged;
            _folderWatcher.Created += OnFolderChanged;
            _folderWatcher.Deleted += OnFolderChanged;
            _folderWatcher.Renamed += OnFolderRenamed;
            _folderWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            _server.RequestCompleted -= OnRequestCompleted;
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
            _activeServerRoot = null;
            await TryRunNetshCleanupAsync($"advfirewall firewall delete rule name={Quote(firewallName)}");
            await TryRunNetshCleanupAsync($"http delete urlacl url={Quote(urlAclUrl)}");
            throw;
        }
    }

    public async Task<IReadOnlyList<LanServedInstaller>> GetServedInstallersAsync(
        string? rootDirectory = null,
        CancellationToken token = default)
    {
        string root = rootDirectory ?? _activeServerRoot
            ?? throw new InvalidOperationException("Selecione uma pasta para indexar os instaladores.");
        LanModulePayload payload = await _installersModule.BuildAsync(root, token).ConfigureAwait(false);
        LanInstallersModuleDocument module = LanInstallerValidation.DeserializeModule(payload.Content);
        return module.Installers.Select(item =>
        {
            string path = Path.Combine(root, item.File);
            LanInstallerMetadata metadata = LanInstallerMetadataReader.Read(path);
            LanInstallerDetection detection = LanInstallerTypeDetector.Detect(path);
            return new LanServedInstaller(item.File, metadata.Name, metadata.Version, detection.DisplayName);
        }).ToArray();
    }

    public async Task StopServerAsync(CancellationToken token = default)
    {
        if (_server is null)
            return;
        int port = _activePort ?? throw new InvalidOperationException("A porta do servidor LAN não está disponível.");
        Exception? stopFailure = null;
        _folderWatcher?.Dispose();
        _folderWatcher = null;
        _server.RequestCompleted -= OnRequestCompleted;
        try { await _server.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { stopFailure = exception; }
        _server = null;
        _activePort = null;
        _activeServerRoot = null;

        string firewallName = $"WinProvisionStore-Lan-{port}";
        try
        {
            await RunNetshAsync($"advfirewall firewall delete rule name={Quote(firewallName)}", token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure ??= exception;
            WinProvisionLog.Warning(exception, "LanServer.FirewallCleanup");
        }
        try
        {
            await RunNetshAsync($"http delete urlacl url={Quote(_transport.GetListenerPrefix(port))}", token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure ??= exception;
            WinProvisionLog.Warning(exception, "LanServer.UrlAclCleanup");
        }
        if (stopFailure is not null)
            throw new InvalidOperationException("O servidor parou, mas não foi possível remover todas as permissões de rede.", stopFailure);
    }

    public async Task TestConnectionAsync(string serverAddress, string token, CancellationToken cancellationToken = default)
    {
        Uri baseUri = _transport.ValidateServerAddress(serverAddress);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/v1/index.json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpClient client = _transport.CreateClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!TryGetProperty(document.RootElement, "schema", out JsonElement schema)
            || schema.GetString() != "winprovision.lan"
            || !TryGetProperty(document.RootElement, "schemaVersion", out JsonElement version)
            || !version.TryGetInt32(out int schemaVersion) || schemaVersion != 1)
            throw new InvalidDataException("A resposta não é um servidor WinProvision LAN.");
    }

    public Task<LanSyncResult> SyncAsync(
        string serverAddress,
        string token,
        IProgress<LanDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _installService.SyncAsync(serverAddress, token, progress, cancellationToken, _activeServerRoot);

    public Task<LanInstallBatchResult> InstallAsync(
        IReadOnlyList<LanInstallerInstallRequest> requests,
        IProgress<LanInstallBatchProgress>? progress = null,
        CancellationToken token = default) =>
        _installService.InstallAsync(requests, progress, token);

    public async ValueTask DisposeAsync()
    {
        _folderWatcher?.Dispose();
        if (_server is not null)
        {
            try { await _server.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "LanServer.Dispose"); }
            _server = null;
            _activeServerRoot = null;
        }
    }

    private async Task RunNetshAsync(string arguments, CancellationToken token)
    {
        string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
        WingetExecutionResult result = await ElevatedProcessRunner.RunElevatedAsync(netsh,
            WindowsArgumentParser.Parse(arguments), token, ElevationCategory.Network).ConfigureAwait(false);
        int exitCode = result.ExitCode;
        if (!result.InstallerStarted) throw new Win32Exception(exitCode, result.Output);
        if (exitCode != 0)
            throw new Win32Exception(exitCode, $"O WinProvision Elevator não conseguiu configurar a rede (netsh retornou {exitCode}).");
    }

    private async Task TryRunNetshCleanupAsync(string arguments)
    {
        try { await RunNetshAsync(arguments, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "LanServer.NetworkRollback"); }
    }

    private void OnRequestCompleted(LanRequestLog entry) => RequestCompleted?.Invoke(entry);
    private void OnFolderChanged(object sender, FileSystemEventArgs args) => _installersModule.Invalidate();
    private void OnFolderRenamed(object sender, RenamedEventArgs args) => _installersModule.Invalidate();

    private static IReadOnlyList<LanNetworkAddress> GetNetworkAddresses()
    {
        var addresses = new List<LanNetworkAddress>();
        foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            bool isVirtual = IsVirtualAdapter(network);
            foreach (UnicastIPAddressInformation item in network.GetIPProperties().UnicastAddresses)
                if (LanTokenAuthenticator.IsPrivateAddress(item.Address))
                    addresses.Add(new(network.Name, item.Address.ToString(), isVirtual));
        }
        return addresses
            .DistinctBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.IsVirtual)
            .ThenBy(item => item.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsVirtualAdapter(NetworkInterface network)
    {
        string descriptor = $"{network.Name} {network.Description}";
        string[] virtualMarkers = ["VMware", "VirtualBox", "Hyper-V", "vEthernet", "WSL", "VPN", "TAP", "TUN", "WireGuard", "OpenVPN", "Virtual"];
        return network.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            || virtualMarkers.Any(marker => descriptor.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }
}
