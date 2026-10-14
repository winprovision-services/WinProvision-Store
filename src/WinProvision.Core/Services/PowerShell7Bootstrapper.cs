using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Instalação por usuário do PowerShell 7 e dados para executar pwsh.</summary>
public sealed record PowerShell7Runtime(
    string ExecutablePath,
    Version Version,
    bool IsManagedByApp,
    bool WasProvisioned);

/// <summary>
/// Localiza uma instalação suportada do PowerShell 7 ou provisiona a versão estável
/// oficial em LocalAppData. Não altera o PATH nem a instalação do Windows PowerShell 5.1.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerShell7Bootstrapper
{
    private const string LatestReleaseApi = "https://api.github.com/repos/PowerShell/PowerShell/releases/latest";
    private const string WinGetManifestDirectoryApi = "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/m/Microsoft/PowerShell?ref=master";
    private const string GithubUserAgent = "WinProvision-Store/1.0 (PowerShell runtime bootstrap)";
    private const long MaximumArchiveBytes = 512L * 1024 * 1024;
    private static readonly Version MinimumSupportedVersion = new(7, 4);
    private static readonly TimeSpan ProcessCheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);
    private static readonly HttpClient GithubApiHttp = CreateHttpClient(includeGithubAccept: true);
    private static readonly HttpClient DownloadHttp = CreateHttpClient(includeGithubAccept: false);
    private static readonly Regex VersionPattern = new(@"(?<!\d)(?<version>\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WinProvisionApiService _apiService;
    private PowerShell7Runtime? _runtime;

    private static string RuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "Runtime", "PowerShell", "versions");

    public PowerShell7Bootstrapper(WinProvisionApiService apiService) => _apiService = apiService;

    /// <summary>
    /// Retorna uma instalação de PowerShell 7.4+ existente ou baixa a versão estável mais
    /// recente quando necessária. Falhas na consulta de atualização usam a cópia gerenciada
    /// local se ela ainda funcionar.
    /// </summary>
    public async Task<PowerShell7Runtime> EnsureAvailableAsync(
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is { } cached && File.Exists(cached.ExecutablePath))
                return cached;

            string? managedExecutable = null;
            Version? managedVersion = null;
            foreach ((string executable, Version version) in await FindManagedVersionsAsync(cancellationToken).ConfigureAwait(false))
            {
                managedExecutable = executable;
                managedVersion = version;
                break;
            }

            (string Path, Version Version)? installed = await FindSystemInstallationAsync(cancellationToken).ConfigureAwait(false);
            if (installed is { } systemInstallation)
            {
                (string path, Version version) = systemInstallation;
                _runtime = new PowerShell7Runtime(path, version, IsManagedByApp: false, WasProvisioned: false);
                onLog?.Invoke($"PowerShell {version} já está disponível em {path}.");
                return _runtime;
            }

            ReleaseAsset release;
            try
            {
                release = await GetReleaseWithFallbacksAsync(onLog, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (managedExecutable is not null && !cancellationToken.IsCancellationRequested)
            {
                onLog?.Invoke($"Não foi possível consultar atualizações do PowerShell; usando a versão local {managedVersion}: {ex.Message}");
                _runtime = new PowerShell7Runtime(managedExecutable, managedVersion!, IsManagedByApp: true, WasProvisioned: false);
                return _runtime;
            }

            if (managedExecutable is not null && managedVersion is not null && managedVersion >= release.Version)
            {
                _runtime = new PowerShell7Runtime(managedExecutable, managedVersion, IsManagedByApp: true, WasProvisioned: false);
                onLog?.Invoke($"PowerShell {managedVersion} já está atualizado.");
                return _runtime;
            }

            onLog?.Invoke(managedExecutable is null
                ? $"Provisionando PowerShell {release.Version} no perfil do usuário..."
                : $"Atualizando PowerShell {managedVersion} para {release.Version}...");

            try
            {
                _runtime = await InstallReleaseAsync(release, onLog, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (managedExecutable is not null && !cancellationToken.IsCancellationRequested)
            {
                onLog?.Invoke($"A atualização do PowerShell não foi concluída; mantendo a versão local {managedVersion}: {ex.Message}");
                _runtime = new PowerShell7Runtime(managedExecutable, managedVersion!, IsManagedByApp: true, WasProvisioned: false);
                return _runtime;
            }

            CleanupOldVersions(_runtime.ExecutablePath);
            return _runtime;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static HttpClient CreateHttpClient(bool includeGithubAccept)
    {
        var transport = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        var http = new HttpClient(includeGithubAccept ? new RequestRateLimitHandler(transport) : transport) { Timeout = NetworkTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(GithubUserAgent);
        if (includeGithubAccept)
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static async Task<(string Path, Version Version)?> FindSystemInstallationAsync(CancellationToken ct)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        AddCandidate(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        AddCandidate(Path.Combine(programFilesX86, "PowerShell", "7", "pwsh.exe"));
        AddCandidate(Path.Combine(localAppData, "Programs", "PowerShell", "7", "pwsh.exe"));
        AddCandidate(Path.Combine(localAppData, "Microsoft", "WindowsApps", "pwsh.exe"));

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string cleanDirectory = directory.Trim('"');
            if (!string.IsNullOrWhiteSpace(cleanDirectory))
                AddCandidate(Path.Combine(cleanDirectory, "pwsh.exe"));
        }

        foreach (string candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            Version? version = await ReadVersionAsync(candidate, ct).ConfigureAwait(false);
            if (version is not null && version >= MinimumSupportedVersion)
                return (candidate, version);
        }

        return null;

        void AddCandidate(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                candidates.Add(path);
        }
    }

    private static async Task<List<(string Executable, Version Version)>> FindManagedVersionsAsync(CancellationToken ct)
    {
        var found = new List<(string Executable, Version Version)>();
        if (!Directory.Exists(RuntimeRoot))
            return found;

        foreach (string directory in Directory.EnumerateDirectories(RuntimeRoot))
        {
            string name = Path.GetFileName(directory);
            if (!Version.TryParse(name, out Version? directoryVersion) || directoryVersion < MinimumSupportedVersion)
                continue;

            string executable = Path.Combine(directory, "pwsh.exe");
            if (!File.Exists(executable))
                continue;

            Version? actualVersion = await ReadVersionAsync(executable, ct).ConfigureAwait(false);
            if (actualVersion is not null && actualVersion >= MinimumSupportedVersion)
                found.Add((executable, actualVersion));
        }

        found.Sort((left, right) => right.Version.CompareTo(left.Version));
        return found;
    }

    private static async Task<Version?> ReadVersionAsync(string executable, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };
        process.StartInfo.ArgumentList.Add("--version");

        Task<string[]>? readers = null;
        bool started = false;
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start())
                return null;

            started = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProcessCheckTimeout);
            readers = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;

            string output = string.Join(" ", await readers.ConfigureAwait(false));
            Match match = VersionPattern.Match(output);
            return match.Success && Version.TryParse(match.Groups["version"].Value, out Version? version)
                ? version
                : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            CoreLog.Warning(exception, "ProbePowerShellVersion");
            return null;
        }
        finally
        {
            if (started)
            {
                TryKill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            if (readers is not null) await readers.ConfigureAwait(false);
        }
    }

    private async Task<ReleaseAsset> GetReleaseWithFallbacksAsync(Action<string>? log, CancellationToken ct)
    {
        // A API própria tem cache local e representa a versão já publicada no catálogo WinGet.
        try
        {
            PackageManifest? package = await _apiService.GetPackageAsync("Microsoft.PowerShell", ct, "winget")
                .ConfigureAwait(false);
            if (package is not null && TryNormalizeReleaseVersion(package.Version, out string? apiVersion))
            {
                ReleaseAsset? asset = await TryGetReleaseForVersionAsync(apiVersion!, ct).ConfigureAwait(false);
                if (asset is not null)
                {
                    log?.Invoke($"Versão do PowerShell obtida pelo catálogo da WinProvision API: {asset.Version}.");
                    return asset;
                }
            }
            log?.Invoke("A WinProvision API não forneceu uma versão utilizável do PowerShell; tentando o catálogo WinGet oficial.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"WinProvision API indisponível para provisionar o PowerShell: {ex.Message}");
        }

        // Consulta os diretórios versionados do manifesto comunitário oficial do WinGet.
        try
        {
            string? wingetVersion = await GetLatestWinGetVersionAsync(ct).ConfigureAwait(false);
            if (wingetVersion is not null)
            {
                ReleaseAsset? asset = await TryGetReleaseForVersionAsync(wingetVersion, ct).ConfigureAwait(false);
                if (asset is not null)
                {
                    log?.Invoke($"Versão do PowerShell obtida pelo manifesto WinGet: {asset.Version}.");
                    return asset;
                }
            }
            log?.Invoke("O catálogo WinGet não forneceu uma versão utilizável; tentando o release oficial mais recente.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Catálogo WinGet indisponível para provisionar o PowerShell: {ex.Message}");
        }

        return await GetReleaseFromApiAsync(new Uri(LatestReleaseApi), expectedVersion: null, ct).ConfigureAwait(false);
    }

    private static async Task<string?> GetLatestWinGetVersionAsync(CancellationToken ct)
    {
        using var response = await GithubApiHttp.GetAsync(WinGetManifestDirectoryApi, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        Version? latest = null;
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement type)
                || !string.Equals(type.GetString(), "dir", StringComparison.Ordinal)
                || !item.TryGetProperty("name", out JsonElement name)
                || !Version.TryParse(name.GetString(), out Version? candidate)
                || candidate < MinimumSupportedVersion)
                continue;

            if (latest is null || candidate > latest)
                latest = candidate;
        }

        return latest is null ? null : NormalizeReleaseVersion(latest);
    }

    private static bool TryNormalizeReleaseVersion(string? value, out string? normalized)
    {
        normalized = null;
        if (!Version.TryParse(value, out Version? version) || version < MinimumSupportedVersion)
            return false;
        normalized = NormalizeReleaseVersion(version);
        return true;
    }

    private static string NormalizeReleaseVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";

    private static async Task<ReleaseAsset?> TryGetReleaseForVersionAsync(string version, CancellationToken ct)
    {
        string tag = Uri.EscapeDataString($"v{version}");
        var releaseUri = new Uri($"https://api.github.com/repos/PowerShell/PowerShell/releases/tags/{tag}");
        try
        {
            return await GetReleaseFromApiAsync(releaseUri, version, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<ReleaseAsset> GetReleaseFromApiAsync(Uri releaseUri, string? expectedVersion, CancellationToken ct)
    {
        using var response = await GithubApiHttp.GetAsync(releaseUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("A API do PowerShell retornou uma versão que não é estável.");

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        string versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out Version? version) || version < MinimumSupportedVersion)
            throw new InvalidDataException($"Versão estável do PowerShell inválida: {tag}");
        string releaseVersion = NormalizeReleaseVersion(version);
        if (expectedVersion is not null && !string.Equals(releaseVersion, expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"O release oficial {tag} não corresponde à versão solicitada {expectedVersion}.");

        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException("PowerShell 7 gerenciado requer Windows x64 ou ARM64."),
        };
        string expectedName = $"PowerShell-{releaseVersion}-win-{architecture}.zip";

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), expectedName, StringComparison.Ordinal))
                continue;

            string downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O endereço do pacote do PowerShell não é confiável.");

            string digest = asset.TryGetProperty("digest", out JsonElement digestElement)
                ? digestElement.GetString() ?? string.Empty
                : string.Empty;
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || digest.Length != "sha256:".Length + 64
                || !digest.AsSpan("sha256:".Length).ToString().All(Uri.IsHexDigit))
                throw new InvalidDataException("O GitHub não forneceu um SHA-256 válido para o pacote do PowerShell.");

            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumArchiveBytes)
                throw new InvalidDataException("O tamanho do pacote do PowerShell está fora do limite permitido.");

            return new ReleaseAsset(version, uri, digest["sha256:".Length..], size);
        }

        throw new InvalidDataException($"O pacote oficial {expectedName} não está na versão estável {tag}.");
    }

    private static async Task<PowerShell7Runtime> InstallReleaseAsync(
        ReleaseAsset release,
        Action<string>? log,
        CancellationToken ct)
    {
        Directory.CreateDirectory(RuntimeRoot);
        string versionDirectory = Path.Combine(RuntimeRoot, release.Version.ToString());
        string stagingDirectory = Path.Combine(RuntimeRoot, $".staging-{Guid.NewGuid():N}");
        string archivePath = Path.Combine(RuntimeRoot, $".download-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadAndVerifyAsync(release, archivePath, log, ct).ConfigureAwait(false);
            Directory.CreateDirectory(stagingDirectory);
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory);

            string stagedExecutable = Path.Combine(stagingDirectory, "pwsh.exe");
            Version? stagedVersion = await ReadVersionAsync(stagedExecutable, ct).ConfigureAwait(false);
            if (stagedVersion is null || stagedVersion != release.Version)
                throw new InvalidDataException("A versão extraída do PowerShell não corresponde ao release validado.");

            if (Directory.Exists(versionDirectory))
                Directory.Delete(versionDirectory, recursive: true);
            Directory.Move(stagingDirectory, versionDirectory);

            string installedExecutable = Path.Combine(versionDirectory, "pwsh.exe");
            log?.Invoke($"PowerShell {release.Version} provisionado em {installedExecutable}.");
            return new PowerShell7Runtime(installedExecutable, release.Version, IsManagedByApp: true, WasProvisioned: true);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static async Task DownloadAndVerifyAsync(
        ReleaseAsset release,
        string destination,
        Action<string>? log,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NetworkTimeout);
        using var response = await DownloadHttp.GetAsync(release.DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > MaximumArchiveBytes)
            throw new InvalidDataException("O pacote do PowerShell excede o tamanho máximo permitido.");

        await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int lastReportedPercent = -1;

        while (true)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (total > MaximumArchiveBytes || total > release.Size)
                throw new InvalidDataException("O download do PowerShell excedeu o tamanho informado pelo release.");

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);

            if (contentLength is > 0)
            {
                int percent = (int)Math.Min(100, total * 100 / contentLength.Value);
                if (percent >= lastReportedPercent + 10)
                {
                    lastReportedPercent = percent;
                    log?.Invoke($"Download do PowerShell: {percent}%.");
                }
            }
        }

        await output.FlushAsync(timeout.Token).ConfigureAwait(false);
        if (total != release.Size)
            throw new InvalidDataException($"Download incompleto do PowerShell: recebidos {total} de {release.Size} bytes.");

        string actualDigest = Convert.ToHexString(hash.GetHashAndReset());
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualDigest),
                Convert.FromHexString(release.Sha256)))
            throw new InvalidDataException("A verificação SHA-256 do pacote do PowerShell falhou.");

        log?.Invoke("Integridade do pacote PowerShell confirmada (SHA-256).");
    }

    private static void CleanupOldVersions(string currentExecutable)
    {
        try
        {
            string currentDirectory = Path.GetDirectoryName(currentExecutable)!;
            foreach (string directory in Directory.EnumerateDirectories(RuntimeRoot))
            {
                if (string.Equals(directory, currentDirectory, StringComparison.OrdinalIgnoreCase)
                    || !Version.TryParse(Path.GetFileName(directory), out _))
                    continue;
                TryDeleteDirectory(directory);
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Core.Services.CoreLog.Warning(recoveryException, "PowerShell7Bootstrapper.CleanupOldVersions");

            // Manter uma versão anterior por falha de limpeza não afeta a execução.
        }
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
            CoreLog.Warning(exception, "ProcessCleanup");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "TemporaryFileCleanup");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "TemporaryFileCleanup");
        }
    }

    internal sealed record ReleaseAsset(Version Version, Uri DownloadUri, string Sha256, long Size);
}
