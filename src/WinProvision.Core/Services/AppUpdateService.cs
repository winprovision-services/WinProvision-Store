using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Atualização do próprio cliente: consulta releases GitHub, baixa e valida o SHA-256
/// do instalador configurado e agenda sua execução silenciosa após o processo atual encerrar.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }

    private const string DefaultRepoSlug = "winprovision-services/WinProvision-Store";
    private const string DefaultAssetName = "WinProvision.Store-Setup.exe";

    // Releases estáveis têm prioridade; nightly permanece como canal de compatibilidade.
    private const string NightlyTag = "nightly";

    // Publicação da release e timestamp de build do .exe local vêm de relógios diferentes
    // (servidor do GitHub Actions vs. o file system do disco); essa margem evita marcar
    // "atualização disponível" por causa de alguns segundos de diferença entre os dois,
    // já que o próprio build recém-instalado tende a ficar bem perto do published_at.
    private static readonly TimeSpan NightlyFreshnessMargin = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    // Tempo máximo esperando o processo atual encerrar antes do script de atualização
    // desistir e apagar o instalador baixado sem aplicá-lo.
    private static readonly TimeSpan MaxWaitForExit = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _repoSlug;
    private readonly string _installerAssetName;
    private readonly string? _installerAppId;

    public AppUpdateService() : this((HttpClient?)null, DefaultRepoSlug, DefaultAssetName, null) { }

    public AppUpdateService(string repoSlug, string installerAssetName, string? installerAppId = null)
        : this((HttpClient?)null, repoSlug, installerAssetName, installerAppId) { }

    internal AppUpdateService(HttpClient? http) : this(http, DefaultRepoSlug, DefaultAssetName, null) { }

    internal AppUpdateService(
        HttpClient? http,
        string repoSlug,
        string installerAssetName,
        string? installerAppId = null)
    {
        if (string.IsNullOrWhiteSpace(repoSlug) || repoSlug.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Repositório de atualização inválido.", nameof(repoSlug));
        if (string.IsNullOrWhiteSpace(installerAssetName) ||
            !string.Equals(Path.GetFileName(installerAssetName), installerAssetName, StringComparison.Ordinal))
            throw new ArgumentException("Nome do instalador inválido.", nameof(installerAssetName));

        _repoSlug = repoSlug;
        _installerAssetName = installerAssetName;
        if (installerAppId is not null)
        {
            if (!Guid.TryParse(installerAppId, out Guid parsedAppId))
                throw new ArgumentException("Identificador do instalador inválido.", nameof(installerAppId));
            _installerAppId = parsedAppId.ToString("B");
        }

        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient { Timeout = RequestTimeout };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("WinProvision-Store-Updater", "1.0"));
        }
        if (!_http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/vnd.github+json"))
        {
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    /// <summary>Versão do próprio executável em disco (o mesmo número mostrado em Configurações).</summary>
    public static Version GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    public bool IsCurrentProcessInstalled
    {
        get
        {
            string? executablePath = Environment.ProcessPath;
            if (_installerAppId is null || string.IsNullOrWhiteSpace(executablePath))
                return false;

            string? processDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (processDirectory is null)
                return false;

            string uninstallKeyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{_installerAppId}_is1";
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var uninstallKey = baseKey.OpenSubKey(uninstallKeyPath);
                        if (uninstallKey?.GetValue("InstallLocation") is not string installDirectory ||
                            string.IsNullOrWhiteSpace(installDirectory))
                            continue;

                        string normalizedInstallDirectory = Path.TrimEndingDirectorySeparator(
                            Path.GetFullPath(installDirectory));
                        string normalizedProcessDirectory = Path.TrimEndingDirectorySeparator(processDirectory);
                        if (string.Equals(
                                normalizedInstallDirectory,
                                normalizedProcessDirectory,
                                StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException
                        or SecurityException
                        or ArgumentException
                        or NotSupportedException)
                    {
                        CoreLog.Warning(exception, "AppUpdateService.CheckInstallLocation");
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Data de modificação do executável atual — o mesmo dado que a Store já loga como
    /// "buildDate" na abertura (App.xaml.cs). O publish self-contained (ver csproj: single-file
    /// desligado de propósito) e o instalador Inno preservam esse timestamp do build original,
    /// então ele fica bem próximo do published_at da release no GitHub Actions que o gerou —
    /// é o que permite comparar "meu build é mais velho que o nightly publicado?" sem precisar
    /// embutir um número de versão no canal nightly.
    /// </summary>
    private static DateTimeOffset GetCurrentBuildTime()
    {
        string? exePath = Environment.ProcessPath;
        return exePath is not null && File.Exists(exePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(exePath), TimeSpan.Zero)
            : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Consulta a release mais recente no GitHub e compara com o build atual: primeiro o
    /// canal estável ("/releases/latest", por versão semântica da tag); se o repositório
    /// ainda não tiver nenhuma release estável publicada, cai para o canal nightly (tag fixa,
    /// comparado por data de publicação). Nunca lança: qualquer falha em ambos os canais vira
    /// um <see cref="AppUpdateCheckResult.Failed"/> com a mensagem do canal estável.
    /// </summary>
    public async Task<AppUpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var stable = await CheckStableChannelAsync(ct).ConfigureAwait(false);
        if (stable.Success)
            return stable;

        var nightly = await CheckNightlyChannelAsync(ct).ConfigureAwait(false);
        return nightly.Success ? nightly : stable;
    }

    private async Task<HttpResponseMessage> GetMetadataAsync(string url, CancellationToken token)
    {
        Uri uri = new(url);
        await RequestRateLimitHandler.SharedPolicy.WaitAsync(uri, token).ConfigureAwait(false);
        var response = await _http.GetAsync(uri, token).ConfigureAwait(false);
        RequestRateLimitHandler.SharedPolicy.Observe(uri, response);
        return response;
    }
    private async Task<AppUpdateCheckResult> CheckStableChannelAsync(CancellationToken ct)
    {
        try
        {
            using var response = await GetMetadataAsync(
                $"https://api.github.com/repos/{_repoSlug}/releases/latest", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 404 é o caso normal enquanto só existir a release nightly (sem versão
                // estável publicada ainda) — CheckForUpdateAsync cai pro outro canal sozinho.
                return AppUpdateCheckResult.Failed(
                    $"O GitHub respondeu {(int)response.StatusCode} ao consultar a última versão estável.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string versionText = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out var latestVersion))
            {
                return AppUpdateCheckResult.Failed(
                    $"Não reconheci o formato de versão da última release estável (\"{tag}\").");
            }

            var (downloadUrl, sha256, assetError) = FindInstallerAsset(root, latestVersion.ToString(), _installerAssetName);
            if (assetError is not null)
                return AppUpdateCheckResult.Failed(assetError);

            string? releaseUrl = root.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() : null;
            var current = GetCurrentVersion();

            return new AppUpdateCheckResult(
                Success: true,
                UpdateAvailable: latestVersion > current,
                CurrentVersion: current,
                LatestVersion: latestVersion,
                DownloadUrl: downloadUrl,
                Sha256: sha256,
                ReleaseUrl: releaseUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failed($"Falha ao verificar atualizações: {ex.Message}");
        }
    }

    private async Task<AppUpdateCheckResult> CheckNightlyChannelAsync(CancellationToken ct)
    {
        try
        {
            using var response = await GetMetadataAsync(
                $"https://api.github.com/repos/{_repoSlug}/releases/tags/{NightlyTag}", ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return AppUpdateCheckResult.Failed(
                    $"O GitHub respondeu {(int)response.StatusCode} ao consultar a release \"{NightlyTag}\".");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;

            if (!root.TryGetProperty("published_at", out var publishedProp) ||
                !DateTimeOffset.TryParse(publishedProp.GetString(), out var publishedAt))
            {
                return AppUpdateCheckResult.Failed($"A release \"{NightlyTag}\" não informou a data de publicação.");
            }

            var (downloadUrl, sha256, assetError) = FindInstallerAsset(root, NightlyTag, _installerAssetName);
            if (assetError is not null)
                return AppUpdateCheckResult.Failed(assetError);

            string? releaseLabel = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            string? releaseUrl = root.TryGetProperty("html_url", out var htmlUrlProp) ? htmlUrlProp.GetString() : null;

            bool hasUpdate = publishedAt.ToUniversalTime() - GetCurrentBuildTime().ToUniversalTime() > NightlyFreshnessMargin;

            return new AppUpdateCheckResult(
                Success: true,
                UpdateAvailable: hasUpdate,
                CurrentVersion: GetCurrentVersion(),
                LatestVersion: null,
                DownloadUrl: downloadUrl,
                Sha256: sha256,
                ReleaseUrl: releaseUrl,
                ReleaseLabel: releaseLabel,
                PublishedAt: publishedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AppUpdateCheckResult.Failed($"Falha ao verificar a release \"{NightlyTag}\": {ex.Message}");
        }
    }

    /// <summary>Acha o asset do instalador (<see cref="AssetName"/>) dentro de "assets" e extrai link + SHA-256.</summary>
    private static (string? DownloadUrl, string? Sha256, string? Error) FindInstallerAsset(
        JsonElement root,
        string releaseLabel,
        string installerAssetName)
    {
        if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
        {
            return (null, null, $"A release {releaseLabel} não tem arquivos anexados.");
        }

        JsonElement? asset = assetsProp.EnumerateArray()
            .Cast<JsonElement?>()
            .FirstOrDefault(a => string.Equals(
                a!.Value.TryGetProperty("name", out var n) ? n.GetString() : null,
                installerAssetName,
                StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            return (null, null, $"A release {releaseLabel} não tem o instalador ({installerAssetName}) anexado.");
        }

        string? downloadUrl = asset.Value.TryGetProperty("browser_download_url", out var urlProp)
            ? urlProp.GetString()
            : null;
        if (string.IsNullOrEmpty(downloadUrl))
        {
            return (null, null, $"O instalador da release {releaseLabel} não tem link de download.");
        }

        // GitHub calcula e expõe o SHA-256 de todo asset carregado desde jun/2025
        // (formato "sha256:<hex>"); releases mais antigas podem não ter o campo.
        string? sha256 = null;
        if (asset.Value.TryGetProperty("digest", out var digestProp))
        {
            string? digest = digestProp.GetString();
            const string prefix = "sha256:";
            if (digest is not null && digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                sha256 = digest[prefix.Length..];
            }
        }

        return (downloadUrl, sha256, null);
    }

    /// <summary>
    /// Baixa o instalador de <paramref name="update"/> para uma pasta temporária e, se o
    /// GitHub informou o SHA-256 do upload, confere o arquivo baixado contra ele — um
    /// instalador com hash divergente é apagado e a chamada lança em vez de seguir adiante.
    /// Sem SHA-256 válido, o download é bloqueado.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        AppUpdateCheckResult update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (update is not { Success: true, DownloadUrl: not null })
            throw new ArgumentException("Resultado de checagem inválido (sem DownloadUrl).", nameof(update));

        if (!Uri.TryCreate(update.DownloadUrl, UriKind.Absolute, out var downloadUri)
            || downloadUri.Scheme != Uri.UriSchemeHttps || downloadUri.Port != 443 || !string.IsNullOrEmpty(downloadUri.UserInfo)
            || !downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !downloadUri.AbsolutePath.StartsWith($"/{_repoSlug}/releases/download/", StringComparison.Ordinal)
            || !Uri.UnescapeDataString(downloadUri.Segments[^1]).Equals(_installerAssetName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A atualização precisa usar uma URL HTTPS.", nameof(update));
        if (string.IsNullOrWhiteSpace(update.Sha256) || update.Sha256.Length != 64
            || !update.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("A atualização não possui SHA-256 válido; download bloqueado.");
        string tempName = Path.GetFileNameWithoutExtension(_installerAssetName);
        string tempPath = Path.Combine(Path.GetTempPath(), $"{tempName}-{Guid.NewGuid():N}.exe");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DownloadTimeout);

        using (var response = await _http
                   .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { Scheme: "https", Port: 443 } finalUri
                || !string.IsNullOrEmpty(finalUri.UserInfo)
                || !(finalUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || finalUri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                    || finalUri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("O download da atualização foi redirecionado para uma URL insegura.");

            const long maximumBytes = 512L * 1024 * 1024;
            long? totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is <= 0 || totalBytes > maximumBytes)
                throw new InvalidDataException("Tamanho inválido para o instalador da atualização.");
            await using var source = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            await AtomicFile.WriteAsync(tempPath, async (destination, token) =>
            {

                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token).ConfigureAwait(false);
                    readTotal += read;
                    if (readTotal > maximumBytes)
                        throw new InvalidDataException("O instalador da atualização excedeu o limite de tamanho.");
                    if (totalBytes is > 0)
                    {
                        progress?.Report((double)readTotal / totalBytes.Value);
                    }
                }
                if (readTotal == 0 || totalBytes.HasValue && readTotal != totalBytes.Value)
                    throw new InvalidDataException("O download da atualização está incompleto.");
            }, timeoutCts.Token).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(update.Sha256))
        {
            string actual = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempPath);
                throw new InvalidOperationException(
                    "O instalador baixado não bate com o SHA-256 publicado pelo GitHub; descartado por segurança.");
            }
        }

        return tempPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Dispara um comando PowerShell codificado que espera o processo atual terminar,
    /// roda o instalador em modo silencioso, reabre o app no caminho instalado e se
    /// remove o instalador ao terminar. Não encerra o processo atual — quem chama decide
    /// quando (normalmente logo em seguida, com Application.Shutdown ou Environment.Exit),
    /// já que o instalador precisa que o WinProvision.Store.exe em uso já não esteja rodando
    /// pra poder substituir seus arquivos.
    /// </summary>
    public static void ScheduleSilentInstallAndRestart(
        string setupPath,
        string applicationExeName = "WinProvision.Store.exe")
    {
        if (!File.Exists(setupPath))
            throw new FileNotFoundException("Instalador não encontrado.", setupPath);

        if (string.IsNullOrWhiteSpace(applicationExeName) ||
            !string.Equals(Path.GetFileName(applicationExeName), applicationExeName, StringComparison.Ordinal))
            throw new ArgumentException("Nome do executável inválido.", nameof(applicationExeName));

        string exePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, applicationExeName);
        setupPath = Path.GetFullPath(setupPath);
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        int pid = Environment.ProcessId;
        int maxWaitSeconds = (int)MaxWaitForExit.TotalSeconds;

        // EncodedCommand avoids cmd expansion of %, !, & and Unicode paths.
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $setup = {{Literal(setupPath)}}
            $application = {{Literal(exePath)}}
            try {
                $current = Get-Process -Id {{pid}} -ErrorAction SilentlyContinue
                if ($current -and -not $current.WaitForExit({{maxWaitSeconds}} * 1000)) { exit 1 }
                $installer = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-' -WindowStyle Hidden -Wait -PassThru
                if ($installer.ExitCode -notin @(0, 3010, 1641)) { exit $installer.ExitCode }
                Start-Process -FilePath $application
            } finally {
                Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
            }
            """;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Não foi possível iniciar a atualização.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { CoreLog.Warning(exception, "UpdateTemporaryFileCleanup"); }
    }
}
