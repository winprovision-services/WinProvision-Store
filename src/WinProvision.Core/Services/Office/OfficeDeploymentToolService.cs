using System.Runtime.Versioning;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Obtém o Office Deployment Tool pelo fluxo de instalação do WinGet (incluindo a API
/// própria); se esse fluxo falhar, baixa o instalador atual do Centro de Download Microsoft.
///
/// O configuration.xml não precisa ficar do lado do setup.exe: passamos o caminho
/// completo de cada um como argumento, então tanto faz onde o setup.exe está localizado.
/// </summary>
[SupportedOSPlatform("windows")]
public class OfficeDeploymentToolService : IDisposable
{
    internal const string OdtWingetPackageId = "Microsoft.OfficeDeploymentTool";
    private const string OdtOfficialDownloadUrl = "https://download.microsoft.com/download/6c1eeb25-cf8b-41d9-8d0d-cc1dbc032140/officedeploymenttool_20326-20112.exe";
    private static readonly SemaphoreSlim ConfigurationGate = new(1, 1);
    private static readonly SemaphoreSlim SetupGate = new(1, 1);
    private static readonly SemaphoreSlim SourceGate = new(1, 1);
    private static readonly Version MinimumOdtVersion = new(16, 0, 20326, 20112);
    private const long OfficeCacheLimitBytes = 8L * 1024 * 1024 * 1024;
    private static readonly TimeSpan OfficeCacheRetention = TimeSpan.FromDays(30);

    private readonly WingetExecutor _wingetExecutor;
    private readonly Func<string, Action<string>?, CancellationToken, Task<WingetExecutionResult>>? _installWithPreferredFlowAsync;
    private readonly string _workRoot;
    private readonly HttpClient _httpClient;
    private readonly OfficeUninstallService _uninstallService;
    private bool _disposed;

    /// <summary>Pasta onde ficam o configuration.xml gerado e os logs desta ferramenta (não do ODT em si).</summary>
    public string WorkRoot => _workRoot;

    public OfficeDeploymentToolService(
        WingetExecutor wingetExecutor,
        string? workRoot = null,
        Func<string, Action<string>?, CancellationToken, Task<WingetExecutionResult>>? installWithPreferredFlowAsync = null)
    {
        _wingetExecutor = wingetExecutor;
        _installWithPreferredFlowAsync = installWithPreferredFlowAsync;
        _workRoot = workRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Office");
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _uninstallService = new OfficeUninstallService();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Locais conhecidos onde o setup.exe pode estar encontrado:
    /// 1. Pasta local do WinProvision (extração do instalador oficial Microsoft)
    /// 2. Program Files (instalação via winget)
    /// 3. Program Files (x86) (instalação via winget em sistemas 64-bit)
    /// </summary>
    private static IEnumerable<string> KnownInstallPaths()
    {
        // Local da extração do instalador oficial Microsoft
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "OfficeDeploymentTool", "setup.exe");

        // Locais de instalação via winget
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "OfficeDeploymentTool", "setup.exe");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "OfficeDeploymentTool", "setup.exe");
    }

    internal static bool IsCurrentSetup(string path)
    {
        if (!File.Exists(path)) return false;
        try { WinProvision.Core.Services.Provisioning.MicrosoftSignedToolVerifier.Verify(path); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            CoreLog.Warning(exception, "OfficeDeploymentTool.ValidateSignature");
            return false;
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart) >= MinimumOdtVersion;
    }

    /// <summary>
    /// Garante que setup.exe existe localmente, tentando primeiro a cadeia de instalação
    /// do WinGet/API e usando o Centro de Download Microsoft como alternativa.
    /// </summary>
    public async Task<string> EnsureSetupExeAsync(Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await EnsureSetupExeCoreAsync(onStatus, cancellationToken).ConfigureAwait(false); }
        finally { SetupGate.Release(); }
    }

    private async Task<string> EnsureSetupExeCoreAsync(Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string? existing = KnownInstallPaths().FirstOrDefault(IsCurrentSetup);
        if (existing != null)
        {
            return existing;
        }

        string wingetFailure;
        onStatus?.Invoke("Obtendo Office Deployment Tool pelo WinGet...");
        try
        {
            var result = _installWithPreferredFlowAsync is not null
                ? await _installWithPreferredFlowAsync(
                    OdtWingetPackageId,
                    line => onStatus?.Invoke(line),
                    cancellationToken)
                : await _wingetExecutor.InstallAppAsync(
                    OdtWingetPackageId,
                    onLogReceived: line => onStatus?.Invoke(line),
                    cancellationToken: cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (result.Success)
            {
                string? installedPath = KnownInstallPaths().FirstOrDefault(IsCurrentSetup);
                if (installedPath is not null)
                {
                    return installedPath;
                }

                wingetFailure = "A instalação pelo WinGet terminou, mas setup.exe não foi encontrado nos locais conhecidos.";
            }
            else
            {
                wingetFailure = $"A instalação pelo WinGet falhou (código {result.ExitCode}). {result.Output}";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            wingetFailure = $"Falha ao obter o ODT pelo WinGet: {ex.Message}";
        }

        onStatus?.Invoke($"{wingetFailure} Tentando o download oficial da Microsoft...");
        string? downloadedPath = await DownloadOfficialOdtAsync(onStatus, cancellationToken);
        if (downloadedPath is not null)
        {
            return downloadedPath;
        }

        throw new InvalidOperationException(
            $"Não foi possível obter o Office Deployment Tool. {wingetFailure} O download oficial da Microsoft também falhou.");
    }

    /// <summary>
    /// Baixa o instalador oficial atual do ODT e extrai setup.exe para um local conhecido.
    /// </summary>
    private async Task<string?> DownloadOfficialOdtAsync(Action<string>? onStatus, CancellationToken cancellationToken)
    {
        string localFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "OfficeDeploymentTool");
        string installerPath = Path.Combine(localFolder, "officedeploymenttool.exe");
        string extractFolder = Path.Combine(localFolder, "official-extract");

        try
        {
            Directory.CreateDirectory(localFolder);
            if (Directory.Exists(extractFolder))
                Directory.Delete(extractFolder, recursive: true);
            Directory.CreateDirectory(extractFolder);

            onStatus?.Invoke("Baixando o instalador atual do ODT do Centro de Download Microsoft...");
            using var response = await _httpClient.GetAsync(OdtOfficialDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { Scheme: "https", Host: "download.microsoft.com" })
                throw new InvalidDataException("O ODT foi redirecionado para um destino não autorizado.");
            const long maxOdtBytes = 64L * 1024 * 1024;
            if (response.Content.Headers.ContentLength is <= 0 or > maxOdtBytes)
                throw new InvalidDataException("Tamanho inválido para o instalador ODT.");
            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
                !finalUri.Host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O download do ODT não terminou no domínio oficial download.microsoft.com.");
            }

            long totalBytes = response.Content.Headers.ContentLength ?? 0;
            long totalBytesRead = 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await AtomicFile.WriteAsync(installerPath, async (fileStream, token) =>
            {
                var buffer = new byte[8192];
                int bytesRead;
                var lastProgressUpdate = DateTime.UtcNow;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > maxOdtBytes) throw new InvalidDataException("O download ODT excedeu o limite de tamanho.");

                    if ((DateTime.UtcNow - lastProgressUpdate).TotalSeconds >= 0.5 && totalBytes > 0)
                    {
                        double percent = (totalBytesRead * 100.0) / totalBytes;
                        onStatus?.Invoke($"Baixando instalador ODT: {percent:F1}%");
                        lastProgressUpdate = DateTime.UtcNow;
                    }
                }
            }, cancellationToken);

            WinProvision.Core.Services.Provisioning.MicrosoftSignedToolVerifier.Verify(installerPath);
            onStatus?.Invoke("Extraindo setup.exe do instalador oficial...");
            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { "/quiet", $"/extract:{extractFolder}" },
                },
            })
            {
                if (!process.Start())
                    throw new InvalidOperationException("Não foi possível iniciar o extrator oficial do ODT.");
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"O extrator oficial do ODT retornou código {process.ExitCode}.");
            }

            string? extractedSetup = Directory
                .EnumerateFiles(extractFolder, "setup.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (extractedSetup is null)
                throw new InvalidOperationException("O instalador oficial terminou sem produzir setup.exe.");

            string localPath = Path.Combine(localFolder, "setup.exe");
            if (!IsCurrentSetup(extractedSetup))
                throw new InvalidOperationException("O ODT baixado é anterior à versão oficial exigida pelo aplicativo.");
            File.Copy(extractedSetup, localPath, overwrite: true);
            onStatus?.Invoke("Office Deployment Tool obtido do site oficial da Microsoft.");
            return localPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Erro ao obter o ODT do site oficial da Microsoft: {ex.Message}");
            return null;
        }
        finally
        {
            if (File.Exists(installerPath))
            {
                try { File.Delete(installerPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (Directory.Exists(extractFolder))
            {
                try { Directory.Delete(extractFolder, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>
    /// Gera o configuration.xml (na pasta de trabalho da WinProvision, não do ODT) e
    /// dispara setup.exe /configure com o caminho completo do arquivo usando OdtProcessRunner
    /// com elevação administrativa isolada (UAC prompt).
    /// </summary>
    public async Task<bool> RunConfigureAsync(OfficeInstallRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunConfigureDetailedAsync(request, onStatus, cancellationToken).ConfigureAwait(false);
            return result.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Falha ao configurar o Office: {ex.Message}");
            return false;
        }
    }

    /// <summary>Executa a configuração do Office e preserva código de saída, cancelamento UAC e início do processo.</summary>
    public async Task<OdtProcessResult> RunConfigureDetailedAsync(OfficeInstallRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RunConfigureDetailedCoreAsync(request, onStatus, cancellationToken).ConfigureAwait(false); }
        finally { ConfigurationGate.Release(); }
    }

    private async Task<OdtProcessResult> RunConfigureDetailedCoreAsync(OfficeInstallRequest request,
        Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string folder = _workRoot;
        string setupPath;
        if (request.DownloadBeforeInstall)
        {
            folder = await PrepareInstallSourceAsync(request, onStatus, cancellationToken)
                ?? throw new InvalidOperationException("Os arquivos offline do Office não foram preparados.");
            request = request with { SourcePath = folder };
            setupPath = Path.Combine(folder, "setup.exe");
        }
        else setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteToFolderAsync(request, folder, cancellationToken);

        onStatus?.Invoke($"Instalando {request.Plan.DisplayName}...");

        try
        {
            var result = await OdtProcessRunner.RunConfigureElevatedAsync(setupPath, configPath, cancellationToken);

            if (result.ElevationCanceled)
            {
                onStatus?.Invoke("Instalação cancelada pelo usuário (UAC).");
                return result;
            }

            bool success = result.Success;

            onStatus?.Invoke(success
                ? "Office instalado com sucesso."
                : $"setup.exe retornou código {result.ExitCode}.");

            if (!success && !string.IsNullOrWhiteSpace(result.Output))
            {
                onStatus?.Invoke($"Saída: {result.Output}");
            }

            return result;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Erro ao executar setup.exe: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Prepara uma fonte ODT reutilizável. O cache oportunista legado pode retornar null;
    /// o modo offline explícito preserva os arquivos e propaga falhas, sem fallback online.
    /// </summary>
    public async Task<string?> PrepareInstallSourceAsync(
        OfficeInstallRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await PrepareInstallSourceCoreAsync(request, onStatus, cancellationToken).ConfigureAwait(false); }
        finally { SourceGate.Release(); }
    }

    private async Task<string?> PrepareInstallSourceCoreAsync(
        OfficeInstallRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string cachePath = GetPayloadCachePath(request);
        string cacheRoot = Path.GetDirectoryName(cachePath)!;
        bool strict = request.DownloadBeforeInstall;
        string? Fail(string reason)
        {
            if (strict) throw new InvalidOperationException(reason);
            onStatus?.Invoke(reason + " A instalação usará a CDN da Microsoft.");
            return null;
        }

        try
        {
            if (strict && File.Exists(Path.Combine(cachePath, OfficeOfflinePayload.IndexFileName))
                && IsCurrentSetup(Path.Combine(cachePath, "setup.exe")))
            {
                onStatus?.Invoke("Verificando os arquivos locais do Office...");
                await OfficeOfflinePayload.VerifyAsync(cachePath, cancellationToken).ConfigureAwait(false);
                Directory.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                onStatus?.Invoke("Arquivos locais verificados; preparando a instalação offline.");
                return cachePath;
            }
            Directory.CreateDirectory(cacheRoot);
            if (!strict) PruneExpiredOfficeCaches(cacheRoot);

            if (!strict && Directory.Exists(cachePath) && GetDirectorySize(cachePath) > OfficeCacheLimitBytes)
            {
                return Fail("O cache do Office excede o limite local de 8 GB.");
            }

            string volumePath = Path.GetPathRoot(cacheRoot) ?? cacheRoot;
            if (new DriveInfo(volumePath).AvailableFreeSpace < 3L * 1024 * 1024 * 1024)
            {
                return Fail("São necessários pelo menos 3 GB livres para preparar os arquivos do Office.");
            }

            // Evita disputar a instalação de pacotes WinGet em paralelo; caso setup.exe
            // ainda não esteja no cache local, usa diretamente o download oficial.
            string setupPath;
            await SetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                setupPath = KnownInstallPaths().FirstOrDefault(IsCurrentSetup)
                    ?? await DownloadOfficialOdtAsync(onStatus, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Não foi possível obter setup.exe para preparar o cache.");
            }
            finally { SetupGate.Release(); }

            Directory.CreateDirectory(cachePath);
            if (strict)
            {
                string localSetup = Path.Combine(cachePath, "setup.exe");
                await using var input = File.OpenRead(setupPath);
                await AtomicFile.WriteAsync(localSetup, (output, token) => input.CopyToAsync(output, token), cancellationToken).ConfigureAwait(false);
                setupPath = localSetup;
            }
            string configPath = Path.Combine(cachePath, "download-configuration.xml");
            var downloadRequest = request with { SourcePath = cachePath };
            var document = OfficeConfigXmlBuilder.Build(downloadRequest);
            await AtomicFile.WriteAsync(configPath, (stream, token) => document.SaveAsync(stream, SaveOptions.None, token), cancellationToken).ConfigureAwait(false);

            onStatus?.Invoke("Baixando os arquivos do Office para o cache local (ODT)…");
            int exitCode = await OdtProcessRunner.RunDownloadAsync(setupPath, configPath,
                onStatus, cancellationToken).ConfigureAwait(false);

            if (exitCode != 0 || !HasOfficePayload(cachePath))
            {
                return Fail($"O download do Office não foi concluído (código {exitCode}).");
            }

            long cacheSize = GetDirectorySize(cachePath);
            long totalCacheSize = strict ? cacheSize : GetDirectorySize(cacheRoot);
            if (!strict && totalCacheSize > OfficeCacheLimitBytes)
            {
                if (!strict) TryDeleteDirectory(cachePath);
                return Fail("O cache do Office atingiu o limite local de 8 GB.");
            }

            if (strict) await OfficeOfflinePayload.WriteIndexAsync(cachePath, cancellationToken).ConfigureAwait(false);

            Directory.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
            onStatus?.Invoke($"Cache do Office pronto ({FormatSize(cacheSize)}); instalação local preparada.");
            return cachePath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (strict) throw;
            onStatus?.Invoke($"Não foi possível preparar o cache do Office: {ex.Message}. A instalação usará a CDN da Microsoft.");
            return null;
        }
    }

    public string GetPayloadCachePath(OfficeInstallRequest request) =>
        Path.Combine(_workRoot, request.DownloadBeforeInstall ? "OfflineSources" : "PayloadCache", GetPayloadCacheKey(request));

    internal static string GetPayloadCacheKey(OfficeInstallRequest request)
    {
        XElement add = OfficeConfigXmlBuilder.Build(request with { SourcePath = null })
            .Root?.Element("Add") ?? throw new InvalidOperationException("Configuração ODT sem elemento Add.");
        // Exclusions affect installation, not the shared Office product streams.
        if (request.DownloadBeforeInstall) add.Descendants("ExcludeApp").Remove();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(add.ToString(SaveOptions.DisableFormatting)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool HasOfficePayload(string cachePath)
    {
        string dataPath = Path.Combine(cachePath, "Office", "Data");
        return Directory.Exists(dataPath) && Directory.EnumerateFiles(dataPath, "*", SearchOption.AllDirectories).Any();
    }

    private static void PruneExpiredOfficeCaches(string cacheRoot)
    {
        DateTime cutoff = DateTime.UtcNow - OfficeCacheRetention;
        foreach (string directory in Directory.EnumerateDirectories(cacheRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception recoveryException)
            {
                WinProvision.Core.Services.CoreLog.Warning(recoveryException, "OfficeDeploymentToolService.PruneExpiredOfficeCaches");

                // Cache ocupado ou sem acesso: preserva e deixa o ODT/CDN seguir.
            }
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total = checked(total + new FileInfo(file).Length); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (OverflowException) { return long.MaxValue; }
        }
        return total;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0} MB";

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { CoreLog.Warning(exception, "OfficeCacheCleanup"); }
    }

    /// <summary>
    /// Remove via ODT /configure; permite recuperação completa somente quando
    /// RemoveAll e UseAggressiveUninstall forem explicitamente solicitados.
    /// </summary>
    public async Task<bool> RunRemoveAsync(OfficeRemoveRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RunRemoveCoreAsync(request, onStatus, cancellationToken).ConfigureAwait(false); }
        finally { ConfigurationGate.Release(); }
    }

    private async Task<bool> RunRemoveCoreAsync(OfficeRemoveRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string removeDescription = request.RemoveAll
            ? "todas as instalações do Office"
            : $"{request.ProductIds?.Count ?? 0} produto(s) do Office";

        onStatus?.Invoke($"Removendo {removeDescription}...");

        string setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteRemoveToFolderAsync(request, _workRoot, cancellationToken);
        var result = await OdtProcessRunner.RunConfigureElevatedAsync(setupPath, configPath, cancellationToken);
        bool success = result.Success;
        if (result.ElevationCanceled) return false;
        onStatus?.Invoke($"ODT: código {result.ExitCode}. {result.Output}");
        // A recuperação completa só é adequada para uma remoção de todos os produtos.
        if (!success && request.RemoveAll && request.UseAggressiveUninstall)
        {
            var outcome = await _uninstallService.UninstallAsync(onStatus, cancellationToken);
            success = outcome is OfficeUninstallOutcome.RemovedByStandardMethod or OfficeUninstallOutcome.RemovedByAggressiveMethod;
        }

        if (success && request.CleanStoreEdition)
        {
            onStatus?.Invoke("Verificando edição da Microsoft Store do Office...");
            success = await RemoveStoreEditionAsync(onStatus, cancellationToken) && success;
        }

        onStatus?.Invoke(success
            ? "Remoção concluída."
            : "Falha na remoção.");

        return success;
    }

    /// <summary>
    /// Remove a edição UWP/AppX do Office (distribuída pela Microsoft Store), que pode
    /// coexistir e conflitar com uma instalação Click-to-Run. Usa apenas o cmdlet
    /// nativo do PowerShell Remove-AppxPackage — nenhuma interação com licenciamento.
    /// Não falha a operação inteira se o pacote simplesmente não estiver presente.
    /// </summary>
    private static async Task<bool> RemoveStoreEditionAsync(Action<string>? onStatus, CancellationToken cancellationToken)
    {
        const string script = "Get-AppxPackage -Name 'Microsoft.Office.Desktop*' | Remove-AppxPackage -ErrorAction SilentlyContinue";

        var result = await RunProcessAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            Environment.CurrentDirectory,
            onLogReceived: line => onStatus?.Invoke(line),
            cancellationToken);

        // ExitCode diferente de 0 aqui normalmente só significa "nenhum pacote encontrado";
        // não tratamos como falha da remoção do Office em si.
        return true;
    }

    /// <summary>
    /// Solicita a execução da tarefa de atualização instalada pelo Office.
    /// Sucesso significa solicitação aceita; a atualização é gerida pelo Office.
    /// </summary>
    public async Task<bool> RunUpdateNowAsync(bool silent = true, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RunUpdateNowCoreAsync(silent, onStatus, cancellationToken).ConfigureAwait(false); }
        finally { ConfigurationGate.Release(); }
    }

    private async Task<bool> RunUpdateNowCoreAsync(bool silent = true, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        string scheduler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        onStatus?.Invoke("Solicitando a tarefa de atualização do Office...");
        var result = await ElevatedProcessRunner.RunElevatedAsync(
            scheduler, "/Run /TN \"\\Microsoft\\Office\\Office Automatic Updates 2.0\"", cancellationToken).ConfigureAwait(false);
        onStatus?.Invoke(result.Success
            ? "Verificação solicitada. O Office respeitará a configuração e as políticas de atualização existentes."
            : $"Não foi possível iniciar a tarefa de atualização (código {result.ExitCode}). {result.Output}");
        return result.Success;
    }

    /// <summary>
    /// Liga/desliga a atualização automática do Office sem reinstalar nada, aplicando
    /// só o elemento &lt;Updates Enabled="TRUE|FALSE"/&gt; via setup.exe /configure —
    /// o mecanismo que o próprio ODT documenta para essa política, usando OdtProcessRunner.
    /// </summary>
    public async Task<bool> RunSetAutoUpdateAsync(bool enabled, Action<string>? onStatus = null, CancellationToken cancellationToken = default)
    {
        await ConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyAutoUpdateAsync(enabled, onStatus, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ConfigurationGate.Release();
        }
    }

    private async Task<bool> ApplyAutoUpdateAsync(bool enabled, Action<string>? onStatus, CancellationToken cancellationToken)
    {
        string setupPath = await EnsureSetupExeAsync(onStatus, cancellationToken);
        string configPath = await OfficeConfigXmlBuilder.WriteUpdatesOnlyToFolderAsync(enabled, _workRoot, cancellationToken);

        onStatus?.Invoke(enabled ? "Ativando atualizações automáticas..." : "Desativando atualizações automáticas...");

        try
        {
            var result = await OdtProcessRunner.RunConfigureElevatedAsync(setupPath, configPath, cancellationToken).ConfigureAwait(false);
            bool success = result.Success;

            onStatus?.Invoke(success
                ? $"Atualizações automáticas {(enabled ? "ativadas" : "desativadas")}."
                : result.ElevationCanceled ? "Elevação cancelada pelo usuário."
                : $"setup.exe retornou código {result.ExitCode}. {result.Output}");

            return success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Erro ao executar setup.exe: {ex.Message}");
            return false;
        }
    }

    private static async Task<(bool Success, int ExitCode, string Output)> RunProcessAsync(
        string fileName, string arguments, string workingDirectory, Action<string>? onLogReceived, CancellationToken cancellationToken)
    {
        var outputBuilder = new StringBuilder();
        var outputGate = new System.Threading.Lock();
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (outputGate)
                outputBuilder.AppendLine(e.Data);
            onLogReceived?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (outputGate)
                outputBuilder.AppendLine(e.Data);
            onLogReceived?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }

        return (process.ExitCode == 0, process.ExitCode, outputBuilder.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CoreLog.Warning(exception, "OfficeDeploymentToolService");
        }
    }
}
