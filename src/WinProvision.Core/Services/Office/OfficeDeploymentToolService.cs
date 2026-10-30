using System.Runtime.Versioning;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WixToolset.Dtf.Compression.Cab;
using WinProvision.Viper;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Obtém o Office Deployment Tool pelo fluxo de instalação do gerenciador de pacotes (incluindo a API
/// própria); se esse fluxo falhar, baixa o instalador atual do Centro de Download plataforma do sistema.
///
/// O configuration.xml não precisa ficar do lado do setup.exe: passamos o caminho
/// completo de cada um como argumento, então tanto faz onde o setup.exe está localizado.
/// </summary>
[SupportedOSPlatform("windows")]
public class OfficeDeploymentToolService : IDisposable
{
    internal const string OdtWingetPackageId = "Microsoft.OfficeDeploymentTool";
    private const string OdtOfficialDownloadUrl = NetworkEndpoints.OfficeDeploymentToolUrl;
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
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15), ConnectCallback = ViperHttpConnection.ConnectAsync
        })
        { Timeout = TimeSpan.FromMinutes(10) };
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
    /// 1. Pasta local do WinProvision (extração do instalador oficial plataforma do sistema)
    /// 2. Program Files (instalação via gerenciador de pacotes)
    /// 3. Program Files (x86) (instalação via gerenciador de pacotes em sistemas 64-bit)
    /// </summary>
    private static IEnumerable<string> KnownInstallPaths()
    {
        // Local da extração do instalador oficial plataforma do sistema
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "OfficeDeploymentTool", "setup.exe");

        // Locais de instalação via gerenciador de pacotes
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
    /// do gerenciador de pacotes/API e usando o Centro de Download plataforma do sistema como alternativa.
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
    public async Task<OdtProcessResult> RunConfigureDetailedAsync(OfficeInstallRequest request, Action<string>? onStatus = null, CancellationToken cancellationToken = default,
        Action<InstallProgressUpdate>? onProgress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ConfigurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RunConfigureDetailedCoreAsync(request, onStatus, cancellationToken, onProgress).ConfigureAwait(false); }
        finally { ConfigurationGate.Release(); }
    }

    private async Task<OdtProcessResult> RunConfigureDetailedCoreAsync(OfficeInstallRequest request,
        Action<string>? onStatus = null, CancellationToken cancellationToken = default, Action<InstallProgressUpdate>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? validationError = OfficeConfigXmlBuilder.ValidateRequest(request);
        if (validationError is not null)
            throw new ArgumentException(validationError, nameof(request));
        if (OfficeInstallIdempotence.IsAlreadyInstalled(request, new OfficeInstalledProductsDetector().GetInstalledProducts()))
        {
            onStatus?.Invoke("Os produtos do Office selecionados já estão instalados.");
            return new OdtProcessResult { Success = true, AlreadyInstalled = true, ProcessStarted = false };
        }

        var prepared = await PrepareConfigurationAsync(request, onStatus, cancellationToken, onProgress).ConfigureAwait(false);

        onStatus?.Invoke($"Instalando {request.Plan.DisplayName}...");
        onProgress?.Invoke(new(InstallProgressPhase.Installing));
        var installWatch = Stopwatch.StartNew();

        try
        {
            var result = await OdtProcessRunner.RunConfigureElevatedAsync(prepared.SetupPath, prepared.ConfigurationPath, cancellationToken);

            if (result.ElevationCanceled)
            {
                onStatus?.Invoke("Instalação cancelada pelo usuário (UAC).");
                return result;
            }

            bool success = result.Success;

            onStatus?.Invoke(success
                ? $"Office instalado com sucesso ({installWatch.Elapsed.TotalSeconds:0,0} s no instalador)."
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

    internal async Task<(string SetupPath, string ConfigurationPath)> PrepareConfigurationAsync(OfficeInstallRequest request,
        Action<string>? onStatus, CancellationToken token, Action<InstallProgressUpdate>? onProgress = null)
    {
        var watch = Stopwatch.StartNew();
        onProgress?.Invoke(new(InstallProgressPhase.Preparing));
        string folder = _workRoot;
        string? setup = null;
        if (!string.IsNullOrWhiteSpace(request.SourcePath))
        {
            string source = Path.GetFullPath(request.SourcePath);
            onStatus?.Invoke("Verificando a fonte local do Office...");
            await OfficeOfflinePayload.VerifyAsync(source, token).ConfigureAwait(false);
            string localSetup = Path.Combine(source, "setup.exe");
            setup = IsCurrentSetup(localSetup) ? localSetup : await EnsureDownloadSetupAsync(onStatus, token).ConfigureAwait(false);
            request = request with { SourcePath = source, DownloadBeforeInstall = true };
        }
        else if (!request.DownloadBeforeInstall)
        {
            // DownloadBeforeInstall controls preparation of missing media. A matching,
            // verified offline source can still serve an otherwise online request.
            var offline = request with { DownloadBeforeInstall = true };
            string cached = GetPayloadCachePath(offline);
            string localSetup = Path.Combine(cached, "setup.exe");
            if (File.Exists(Path.Combine(cached, OfficeOfflinePayload.IndexFileName)))
            {
                try
                {
                    string version = OfficeOfflinePayload.GetVersion(cached, request.Architecture,
                        new[] { request.LanguageId }.Concat(request.AdditionalLanguageIds ?? []));
                    var installed = new OfficeInstalledProductsDetector().GetInstalledProducts();
                    if (CanReuseLocalVersion(version, installed.Select(product => product.VersionToReport)))
                    {
                        onStatus?.Invoke("Verificando a mídia já disponível do Office...");
                        await OfficeOfflinePayload.VerifyAsync(cached, token).ConfigureAwait(false);
                        setup = IsCurrentSetup(localSetup) ? localSetup : await EnsureDownloadSetupAsync(onStatus, token).ConfigureAwait(false);
                        request = offline with { SourcePath = cached };
                    }
                    else
                    {
                        // Reuse the signed ODT, but let it select the online version;
                        // an opportunistic cache must not downgrade installed Office.
                        setup = IsCurrentSetup(localSetup) ? localSetup : await EnsureDownloadSetupAsync(onStatus, token).ConfigureAwait(false);
                        onStatus?.Invoke("A versão instalada exige uma fonte mais recente; preparando a configuração online.");
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    CoreLog.Warning(exception, "Office.ReuseLocalSource");
                }
            }
        }
        if (setup is null)
        {
            if (request.DownloadBeforeInstall)
            {
                folder = await PrepareInstallSourceAsync(request, onStatus, token, onProgress).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Os arquivos offline do Office não foram preparados.");
                request = request with { SourcePath = folder };
                setup = Path.Combine(folder, "setup.exe");
            }
            else setup = await EnsureSetupExeAsync(onStatus, token).ConfigureAwait(false);
        }
        string config = await OfficeConfigXmlBuilder.WriteToFolderAsync(request, folder, token).ConfigureAwait(false);
        onStatus?.Invoke(request.SourcePath is not null
            ? $"Fonte local verificada e XML preparado em {watch.Elapsed.TotalSeconds:0.0} s; instalação sem novo download da mídia."
            : $"Configuração online preparada em {watch.Elapsed.TotalSeconds:0.0} s.");
        return (setup, config);
    }

    internal static bool CanReuseLocalVersion(string localVersion, IEnumerable<string?> installedVersions) =>
        Version.TryParse(localVersion, out Version? local)
        && installedVersions.All(value => Version.TryParse(value, out Version? installed) && installed <= local);

    private async Task<string> EnsureDownloadSetupAsync(Action<string>? onStatus, CancellationToken token)
    {
        await SetupGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return KnownInstallPaths().FirstOrDefault(IsCurrentSetup)
                ?? await DownloadOfficialOdtAsync(onStatus, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Não foi possível obter setup.exe para preparar o cache.");
        }
        finally { SetupGate.Release(); }
    }

    /// <summary>
    /// Prepara uma fonte ODT reutilizável. O cache oportunista legado pode retornar null;
    /// o modo offline explícito preserva os arquivos e propaga falhas, sem fallback online.
    /// </summary>
    public async Task<string?> PrepareInstallSourceAsync(
        OfficeInstallRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default, Action<InstallProgressUpdate>? onProgress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await SourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await PrepareInstallSourceCoreAsync(request, onStatus, cancellationToken, onProgress).ConfigureAwait(false); }
        finally { SourceGate.Release(); }
    }

    private async Task<string?> PrepareInstallSourceCoreAsync(
        OfficeInstallRequest request, Action<string>? onStatus = null,
        CancellationToken cancellationToken = default, Action<InstallProgressUpdate>? onProgress = null)
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
            bool verifiedMedia = false;
            if (strict && File.Exists(Path.Combine(cachePath, OfficeOfflinePayload.IndexFileName)))
            {
                onStatus?.Invoke("Verificando os arquivos locais do Office...");
                try
                {
                    await OfficeOfflinePayload.VerifyAsync(cachePath, cancellationToken).ConfigureAwait(false);
                    verifiedMedia = true;
                    if (IsCurrentSetup(Path.Combine(cachePath, "setup.exe")))
                    {
                        Directory.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
                        onStatus?.Invoke("Arquivos locais verificados; preparando a instalação offline.");
                        return cachePath;
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    CoreLog.Warning(exception, "Office.RepairLocalSource");
                    onStatus?.Invoke("A fonte local precisa de reparo; preservando os arquivos concluídos.");
                }
            }
            Directory.CreateDirectory(cacheRoot);
            if (!strict) PruneExpiredOfficeCaches(cacheRoot);

            if (!strict && Directory.Exists(cachePath) && GetDirectorySize(cachePath) > OfficeCacheLimitBytes)
            {
                return Fail("O cache do Office excede o limite local de 8 GB.");
            }

            string volumePath = Path.GetPathRoot(cacheRoot) ?? cacheRoot;
            if (!verifiedMedia && new DriveInfo(volumePath).AvailableFreeSpace < 3L * 1024 * 1024 * 1024)
            {
                return Fail("São necessários pelo menos 3 GB livres para preparar os arquivos do Office.");
            }

            // Evita disputar a instalação de pacotes do gerenciador de pacotes em paralelo; caso setup.exe
            // ainda não esteja no cache local, usa diretamente o download oficial.
            string setupPath = await EnsureDownloadSetupAsync(onStatus, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(cachePath);
            if (strict)
            {
                string localSetup = Path.Combine(cachePath, "setup.exe");
                await using var input = File.OpenRead(setupPath);
                await AtomicFile.WriteAsync(localSetup, (output, token) => input.CopyToAsync(output, token), cancellationToken).ConfigureAwait(false);
                setupPath = localSetup;
            }
            if (verifiedMedia)
            {
                onStatus?.Invoke("Ferramenta atualizada; reutilizando a mídia local já verificada.");
                return cachePath;
            }
            string configPath = Path.Combine(cachePath, "download-configuration.xml");
            var downloadRequest = request with { SourcePath = cachePath };
            var document = OfficeConfigXmlBuilder.Build(downloadRequest);
            await AtomicFile.WriteAsync(configPath, (stream, token) => document.SaveAsync(stream, SaveOptions.None, token), cancellationToken).ConfigureAwait(false);

            if (request.UseViperDownload)
            {
                await DownloadOfficePayloadWithFallbackAsync(request, setupPath, configPath, cachePath,
                    onStatus, cancellationToken, onProgress).ConfigureAwait(false);
            }
            else if (!await DownloadOfficePayloadWithOdtAsync(setupPath, configPath, cachePath,
                onStatus, cancellationToken).ConfigureAwait(false))
                return Fail("O download do Office pelo ODT não foi concluído.");

            if (!HasOfficePayload(cachePath)) return Fail("O download do Office não produziu mídia utilizável.");

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

    private async Task DownloadOfficePayloadWithFallbackAsync(OfficeInstallRequest request, string setupPath,
        string configPath, string cachePath, Action<string>? onStatus, CancellationToken token, Action<InstallProgressUpdate>? onProgress = null)
    {
        try
        {
            if ((request.AdditionalLanguageIds?.Count ?? 0) > 0 || (request.AdditionalProducts?.Count ?? 0) > 0)
                throw new NotSupportedException("O download Viper ainda não aceita idiomas ou produtos adicionais.");

            await DownloadOfficePayloadWithViperAsync(request, cachePath, onStatus, token, onProgress).ConfigureAwait(false);
            if (!HasOfficePayload(cachePath))
                throw new InvalidDataException("O download Viper terminou sem produzir mídia utilizável.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception viperFailure)
        {
            CoreLog.Warning(viperFailure, "Office.Viper.Download");
            onProgress?.Invoke(new(InstallProgressPhase.Preparing));
            onStatus?.Invoke($"O download Viper falhou ({viperFailure.Message}). Preservando arquivos concluídos e tentando pelo ODT oficial…");
            ClearOfficePayloadForRetry(cachePath);

            try
            {
                if (await DownloadOfficePayloadWithOdtAsync(setupPath, configPath, cachePath, onStatus, token).ConfigureAwait(false))
                    return;

                throw new InvalidOperationException("O ODT não produziu uma mídia utilizável.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception odtFailure)
            {
                throw new InvalidOperationException(
                    $"O download Viper falhou ({viperFailure.Message}) e a nova tentativa pelo ODT também falhou ({odtFailure.Message}).",
                    new AggregateException(viperFailure, odtFailure));
            }
        }
    }

    private static async Task<bool> DownloadOfficePayloadWithOdtAsync(string setupPath, string configPath,
        string cachePath, Action<string>? onStatus, CancellationToken token)
    {
        onStatus?.Invoke("Baixando os arquivos do Office para o cache local (ODT)…");
        int exitCode = await OdtProcessRunner.RunDownloadAsync(setupPath, configPath, onStatus, token).ConfigureAwait(false);
        return exitCode == 0 && HasOfficePayload(cachePath);
    }

    internal static void ClearOfficePayloadForRetry(string cachePath)
    {
        string root = Path.GetFullPath(cachePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, "Office"));
        string boundary = root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A pasta Office do cache está fora do diretório permitido.");

        EnsureNoReparsePoints(Path.GetPathRoot(root)!, root);
        FileAttributes targetAttributes;
        try { targetAttributes = File.GetAttributes(target); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((targetAttributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pasta Office é um ponto de redirecionamento; limpeza e fallback cancelados por segurança.");
        if ((targetAttributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException("O destino Office do cache existe e não é uma pasta; fallback cancelado.");
        EnsureTreeHasNoReparsePoints(target);
        // ODT can reuse/repair final CAB and DAT files. Only Viper's unpublished work
        // must be removed before handing this source to ODT and writing its inventory.
        foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".viper.part", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".viper.json", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".viper.lock", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".viper-repair", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        }
    }

    private static void EnsureNoReparsePoints(string volumeRoot, string path)
    {
        string current = volumeRoot;
        foreach (string segment in Path.GetRelativePath(volumeRoot, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("O cache contém um ponto de redirecionamento; limpeza e fallback cancelados por segurança.");
        }
    }

    private static void EnsureTreeHasNoReparsePoints(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pasta Office contém um ponto de redirecionamento; limpeza e fallback cancelados por segurança.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A pasta Office contém um ponto de redirecionamento; limpeza e fallback cancelados por segurança.");
            if (Directory.Exists(entry)) EnsureTreeHasNoReparsePoints(entry);
        }
    }

    private async Task DownloadOfficePayloadWithViperAsync(OfficeInstallRequest request, string destination,
        Action<string>? onStatus, CancellationToken token, Action<InstallProgressUpdate>? onProgress = null)
    {
        string channel = request.ChannelOverride ?? request.Plan.Channel ?? "Current";
        string language = System.Globalization.CultureInfo.GetCultureInfo(request.LanguageId).Name.ToLowerInvariant();
        string architecture = request.Architecture == 32 ? "x86" : "x64";
        string api = $"{NetworkEndpoints.OfficeFileListUrl}?Channel={Uri.EscapeDataString(channel)}&Arch={architecture}&Lid={Uri.EscapeDataString(language)}";
        using JsonDocument release = JsonDocument.Parse(await ReadOfficeResponseAsync(new Uri(api), 4 * 1024 * 1024, token).ConfigureAwait(false));
        JsonElement root = release.RootElement;
        string version = root.GetProperty("version").GetString() ?? throw new InvalidDataException("A lista oficial do Office não informou uma versão.");
        if (!Version.TryParse(version, out Version? parsedVersion) || parsedVersion.Revision < 0 || parsedVersion.ToString() != version)
            throw new InvalidDataException("A lista oficial do Office informou uma versão inválida.");
        string baseUrl = root.GetProperty("baseUrl").GetString() ?? throw new InvalidDataException("A lista oficial do Office não informou a origem CDN.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri) || baseUri.Scheme is not ("http" or "https")
            || !baseUri.IsDefaultPort || !baseUri.Host.Equals("officecdn.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidDataException("A lista oficial do Office informou uma origem CDN inesperada.");
        string[] baseSegments = baseUri.AbsolutePath.Split('/');
        if (baseSegments.Length != 3 || !baseSegments[1].Equals("pr", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(baseSegments[2], out _))
            throw new InvalidDataException("A lista oficial do Office informou um identificador de CDN inválido.");
        JsonElement fileList = root.GetProperty("files");
        if (fileList.ValueKind != JsonValueKind.Array || fileList.GetArrayLength() is < 1 or > 256)
            throw new InvalidDataException("A lista oficial de arquivos do Office está vazia ou excede o limite.");

        string destinationRoot = Path.GetFullPath(destination);
        string boundary = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var entries = new List<(Uri Source, string Target, string RelativePath, string HashLocation)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in fileList.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            string fileName = item.GetProperty("name").GetString() ?? throw new InvalidDataException("Nome de arquivo ausente na lista do Office.");
            string relativePath = item.GetProperty("relativePath").GetString() ?? throw new InvalidDataException("Caminho relativo ausente na lista do Office.");
            string sourceText = item.GetProperty("url").GetString() ?? throw new InvalidDataException("URL ausente na lista do Office.");
            if (!IsSafeOfficeFileName(fileName)
                || relativePath.Contains('\\') || !relativePath.StartsWith("/office/data/", StringComparison.OrdinalIgnoreCase)
                || relativePath.Split('/').Skip(1).SkipLast(1).Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
                throw new InvalidDataException("A lista oficial do Office contém um caminho de arquivo inválido.");
            Uri source = RequireOfficeCdnUri(sourceText, baseUri);
            string relative = GetOfficePayloadRelativePath(relativePath, fileName);
            string target = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !targets.Add(target))
                throw new InvalidDataException("A lista oficial do Office contém um destino duplicado ou fora da pasta de cache.");

            string hashLocation = item.TryGetProperty("hashLocation", out JsonElement hashElement) ? hashElement.GetString() ?? "" : "";
            if (Path.GetExtension(fileName).Equals(".dat", StringComparison.OrdinalIgnoreCase))
            {
                string algorithm = item.TryGetProperty("hashAlgorithm", out JsonElement algorithmElement) ? algorithmElement.GetString() ?? "" : "";
                if (!algorithm.Equals("Sha256", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(hashLocation))
                    throw new InvalidDataException($"A CDN não forneceu SHA-256 para {fileName}.");
                if (hashLocation.Contains('\\') || hashLocation.Split('/') is not [var cabinet, var hashFile]
                    || cabinet is "" or "." or ".." || hashFile is "" or "." or ".."
                    || cabinet.Contains(':') || hashFile.Contains(':'))
                    throw new InvalidDataException("Localização de hash inválida na lista do Office.");
            }
            entries.Add((source, target, relativePath, hashLocation));
        }

        foreach (string folder in entries.Select(entry => Path.GetDirectoryName(entry.Target)!).Distinct(StringComparer.OrdinalIgnoreCase))
            EnsureSafeOfficeDirectory(destinationRoot, folder);
        onStatus?.Invoke($"Lista oficial do Office: versão {version}, {entries.Count} arquivos. Viper baixa os componentes e os pacotes de integridade em paralelo…");
        await using var viper = new ViperEngine(new ViperOptions
        {
            MaxConcurrentDownloads = 3, ConnectionsPerFile = 4, MaxConnectionsPerHost = 8,
            MaxConnections = 12, MaxAttempts = 5, Resume = true,
            PieceSize = 16 * 1024 * 1024
        });
        string canonicalCab = Path.Combine(destinationRoot, "Office", "Data", $"v{(request.Architecture == 32 ? "32" : "64")}.cab");
        var metadata = entries.Where(entry => !Path.GetExtension(entry.Target).Equals(".dat", StringComparison.OrdinalIgnoreCase)
            && !entry.Target.Equals(canonicalCab, StringComparison.OrdinalIgnoreCase)).ToArray();
        string hashScratch = Path.Combine(destinationRoot, ".viper-office-hashes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hashScratch);
        try
        {
            // Metadata has no expected SHA-256 and v64.cab is mutable. Fetch it afresh
            // outside the final layout instead of trusting mere local existence.
            var metadataDownloads = metadata.Select((entry, index) =>
                new ViperRequest(entry.Source, Path.Combine(hashScratch, $"metadata-{index}.cab"))).ToArray();
            await viper.DownloadManyAsync(metadataDownloads, token).ConfigureAwait(false);
            for (int index = 0; index < metadata.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                EnsureNoReparsePoints(destinationRoot, metadata[index].Target);
                File.Move(metadataDownloads[index].Destination, metadata[index].Target, overwrite: true);
            }
            var streams = new List<ViperRequest>();
            foreach (var entry in entries.Where(entry => Path.GetExtension(entry.Target).Equals(".dat", StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested();
                string expectedHash = await ReadOfficeCabHashAsync(destinationRoot, entry.RelativePath, entry.HashLocation, hashScratch, token).ConfigureAwait(false);
                streams.Add(new ViperRequest(entry.Source, entry.Target)
                {
                    Sha256 = expectedHash,
                    ExistingFilePolicy = ViperExistingFilePolicy.ReuseIfVerified
                });
            }
            onStatus?.Invoke($"Pacotes de integridade verificados; Viper baixa {streams.Count} fluxos DAT em paralelo com SHA-256 oficial…");
            OfficeViperProgress? progress = null;
            if (onProgress is not null)
            {
                var lengths = new System.Collections.Concurrent.ConcurrentDictionary<string, long?>();
                await Parallel.ForEachAsync(streams,
                    new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token }, async (stream, ct) =>
                    {
                        lengths[stream.Destination] = await GetOfficeStreamLengthAsync(stream.Source, ct).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                progress = new OfficeViperProgress(lengths, onProgress);
            }
            await Parallel.ForEachAsync(streams,
                new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = token },
                async (stream, ct) => await DownloadOfficeStreamAsync(viper, stream, ct, progress?.ForFile(stream.Destination)).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(hashScratch)) Directory.Delete(hashScratch, recursive: true);
        }
        string versionCab = entries.First(entry => Path.GetFileName(entry.Target).Equals($"v{(request.Architecture == 32 ? "32" : "64")}_{version}.cab", StringComparison.OrdinalIgnoreCase)).Target;
        // Pin the canonical CAB to the resolved version, even when the mutable CDN
        // v64.cab has already advanced to another release.
        {
            await using var input = File.OpenRead(versionCab);
            await AtomicFile.WriteAsync(canonicalCab, (output, ct) => input.CopyToAsync(output, ct), token).ConfigureAwait(false);
        }
        ClearOfficePayloadForRetry(destinationRoot);
        onStatus?.Invoke("Viper concluiu os downloads e validou o SHA-256 oficial dos fluxos DAT.");
    }

    private async Task<long?> GetOfficeStreamLengthAsync(Uri source, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, source);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode && response.Content.Headers.ContentLength is > 0
                ? response.Content.Headers.ContentLength : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
    }

    internal static async Task DownloadOfficeStreamAsync(ViperEngine viper, ViperRequest request, CancellationToken token, IProgress<ViperProgress>? progress = null)
    {
        try
        {
            await viper.DownloadAsync(request, progress, token).ConfigureAwait(false);
        }
        catch (ViperIntegrityException) when (File.Exists(request.Destination))
        {
            // Preserve a mismatching final file until its replacement is fully verified.
            // A stable repair destination also allows cancellation/resume of that download.
            string repair = request.Destination + ".viper-repair";
            await viper.DownloadAsync(request with { Destination = repair }, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(repair, request.Destination, overwrite: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new IOException($"Falha ao baixar {Path.GetFileName(request.Destination)}: {exception.Message}", exception);
        }
    }

    internal static Uri RequireOfficeCdnUri(string value, Uri baseUri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort
            || !uri.Host.Equals("officecdn.microsoft.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("A lista oficial do Office apontou para uma URL fora da CDN esperada.");
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }

    internal static bool IsSafeOfficeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')))
            return false;
        string stem = name.Split('.')[0];
        return !stem.Equals("CON", StringComparison.OrdinalIgnoreCase) && !stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            && !stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) && !stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Traduz o caminho virtual da File List, iniciado em <c>/office/data</c>,
    /// para o layout que o C2R procura no cache local: <c>Office\\Data</c>.
    /// A pasta <c>office</c> da URL já é o diretório local <c>Office</c>; não é
    /// uma pasta adicional dentro dele.
    /// </summary>
    internal static string GetOfficePayloadRelativePath(string relativePath, string fileName)
    {
        const string CdnDataPrefix = "/office/data/";
        if (!IsSafeOfficeFileName(fileName) || relativePath.Contains('\\')
            || !relativePath.StartsWith(CdnDataPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A lista oficial do Office contém um caminho de arquivo inválido.");

        string localDirectory = relativePath["/office/".Length..].Trim('/');
        if (localDirectory.Length == 0
            || localDirectory.Split('/').Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
            throw new InvalidDataException("A lista oficial do Office contém um caminho de arquivo inválido.");

        return Path.Combine("Office", localDirectory.Replace('/', Path.DirectorySeparatorChar), fileName);
    }

    private static void EnsureSafeOfficeDirectory(string root, string directory)
    {
        string rootPath = Path.GetFullPath(root);
        if (!Directory.Exists(rootPath) || (File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pasta de cache Office está ausente ou contém um link de sistema.");
        string relative = Path.GetRelativePath(rootPath, Path.GetFullPath(directory));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Pasta Office fora do cache permitido.");
        string current = rootPath;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("O conteúdo Office não pode atravessar links de sistema de arquivos.");
        }
    }

    private static async Task<string> ReadOfficeCabHashAsync(string root, string relativePath, string hashLocation, string scratch, CancellationToken token)
    {
        string[] location = hashLocation.Split('/');
        string cabinetPath = Path.GetFullPath(Path.Combine(root, GetOfficePayloadRelativePath(relativePath, location[0])));
        string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!cabinetPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(cabinetPath))
            throw new InvalidDataException("O CAB com hashes de integridade do Office está ausente ou fora da pasta de cache.");
        token.ThrowIfCancellationRequested();
        string extracted = Path.Combine(scratch, Guid.NewGuid().ToString("N") + ".hash");
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            CabInfo cabinet = new(cabinetPath);
            string name = cabinet.GetFiles().Single(file => file.Name.Equals(location[1], StringComparison.OrdinalIgnoreCase)).Name;
            cabinet.UnpackFile(name, extracted);
        }, token).ConfigureAwait(false);
        long hashLength = new FileInfo(extracted).Length;
        if (hashLength is < 64 or > 4096) throw new InvalidDataException("O hash oficial de um arquivo Office possui tamanho inválido.");
        byte[] content = await File.ReadAllBytesAsync(extracted, token).ConfigureAwait(false);
        string text = Encoding.Unicode.GetString(content);
        string firstLine = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        string hash = firstLine.Length >= 64 ? firstLine[^64..] : firstLine;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("A CDN do Office retornou um SHA-256 inválido.");
        return hash;
    }

    private async Task<byte[]> ReadOfficeResponseAsync(Uri uri, int maximumBytes, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        token = timeout.Token;
        using HttpResponseMessage response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && (length < 1 || length > maximumBytes))
            throw new InvalidDataException("A resposta da CDN do Office excede o limite esperado.");
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("A resposta da CDN do Office excede o limite esperado.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
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
    /// Remove a edição UWP/AppX do Office (distribuída pela catálogo visual), que pode
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
