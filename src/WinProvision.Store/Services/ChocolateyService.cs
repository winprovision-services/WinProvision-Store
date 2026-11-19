using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Authentication;
using System.Web;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Elevation;

namespace WinProvision.Store.Services;

internal sealed record ChocolateyOutdatedQueryResult(IReadOnlyList<UpgradablePackage> Packages, int ExitCode,
    int LinesRead = 0, string StandardOutput = "", string StandardError = "");

/// <summary>Integração com uma instalação existente do Chocolatey CLI.</summary>
public sealed partial class ChocolateyService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OutdatedTimeout = TimeSpan.FromSeconds(120);
    private static readonly Encoding OutputEncoding = CreateOutputEncoding();
    private const string CommunityFeedUrl = "https://community.chocolatey.org/api/v2/";
    private const string OfficialInstallScriptUrl = "https://community.chocolatey.org/install.ps1";
    private const int MaximumInstallScriptBytes = 4 * 1024 * 1024;
    private const int MaximumFeedAttempts = 3;
    private const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace DataNamespace = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private readonly HttpClient _httpClient;
    private readonly HttpClient _packageDownloadClient;
    private readonly Func<string, string, CancellationToken, Action<string>?, Task<WingetExecutionResult>> _elevatedProcessRunner;
    private readonly Func<string, string[], TimeSpan, CancellationToken, Task<CommandResult>> _commandRunner;
    private readonly AppLogTaskExecutor? _taskExecutor;
    private string? _executablePathOverride;
    private readonly ConcurrentDictionary<string, (string? IconUrl, DateTimeOffset ExpiresUtc)> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public ChocolateyService(HttpClient? httpClient = null, AppLogTaskExecutor? taskExecutor = null)
        : this(httpClient, httpClient, RunElevatedProcessAsync, null, taskExecutor: taskExecutor) { }

    public ChocolateyService(HttpClient? httpClient, HttpClient? packageDownloadClient, AppLogTaskExecutor? taskExecutor = null)
        : this(httpClient, packageDownloadClient, RunElevatedProcessAsync, null, taskExecutor: taskExecutor) { }

    internal ChocolateyService(HttpClient? httpClient,
        Func<string, string, CancellationToken, Task<WingetExecutionResult>> elevatedProcessRunner,
        string? executablePathOverride)
        : this(httpClient, httpClient,
            (executable, arguments, token, _) => elevatedProcessRunner(executable, arguments, token),
            executablePathOverride) { }

    internal ChocolateyService(HttpClient? httpClient,
        Func<string, string, CancellationToken, Action<string>?, Task<WingetExecutionResult>> elevatedProcessRunner,
        string? executablePathOverride)
        : this(httpClient, httpClient, elevatedProcessRunner, executablePathOverride) { }

    internal ChocolateyService(HttpClient? httpClient,
        Func<string, string[], TimeSpan, CancellationToken, Task<(int ExitCode, string Output, string Error)>> commandRunner,
        string? executablePathOverride)
        : this(httpClient, httpClient, RunElevatedProcessAsync, executablePathOverride,
            async (executable, arguments, timeout, token) =>
            {
                var result = await commandRunner(executable, arguments, timeout, token).ConfigureAwait(false);
                return new CommandResult(result.ExitCode, result.Output, result.Error);
            }) { }

    private ChocolateyService(HttpClient? httpClient, HttpClient? packageDownloadClient,
        Func<string, string, CancellationToken, Action<string>?, Task<WingetExecutionResult>> elevatedProcessRunner,
        string? executablePathOverride,
        Func<string, string[], TimeSpan, CancellationToken, Task<CommandResult>>? commandRunner = null,
        AppLogTaskExecutor? taskExecutor = null)
    {
        _httpClient = httpClient ?? NetworkClientFactory.Create(TimeSpan.FromSeconds(30));
        _packageDownloadClient = packageDownloadClient ?? NetworkClientFactory.Create(TimeSpan.FromMinutes(5), allowAutoRedirect: true);
        _elevatedProcessRunner = elevatedProcessRunner;
        _commandRunner = commandRunner ?? RunAsync;
        _executablePathOverride = executablePathOverride;
        _taskExecutor = taskExecutor;
    }

    public string? ExecutablePath => _executablePathOverride ?? ResolveExecutable();
    public string? ResolveExecutablePath() => ResolveExecutable();
    public string InstallCommand => "winget install --exact --id Chocolatey.Chocolatey";
    public void SetExecutablePathOverride(string? path) =>
        _executablePathOverride = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());

    public async Task<string> InstallCliAsync(CancellationToken cancellationToken = default, Action<string>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        onProgress?.Invoke("Baixando o script oficial do Chocolatey…");
        using var request = new HttpRequestMessage(HttpMethod.Get, OfficialInstallScriptUrl);
        using HttpResponseMessage response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Uri? finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps
            || !finalUri.Host.Equals("community.chocolatey.org", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O endereço do instalador oficial do Chocolatey não foi validado.");
        if (response.Content.Headers.ContentLength is > MaximumInstallScriptBytes)
            throw new InvalidDataException("O script oficial do Chocolatey excede o tamanho permitido.");

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var scriptBytes = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int read = await responseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (scriptBytes.Length + read > MaximumInstallScriptBytes)
                throw new InvalidDataException("O script oficial do Chocolatey excede o tamanho permitido.");
            scriptBytes.Write(buffer, 0, read);
        }
        if (scriptBytes.Length == 0) throw new InvalidDataException("O script oficial do Chocolatey veio vazio.");

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"winprovision-chocolatey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string scriptPath = Path.Combine(temporaryDirectory, "install.ps1");
        try
        {
            await File.WriteAllBytesAsync(scriptPath, scriptBytes.ToArray(), cancellationToken).ConfigureAwait(false);
            onProgress?.Invoke("Instalando o Chocolatey CLI com autorização do Windows…");
            WingetExecutionResult result = await _elevatedProcessRunner("powershell.exe",
                $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File {QuoteWindowsArgument(scriptPath)}",
                cancellationToken, onProgress).ConfigureAwait(false);
            if (!result.Success)
            {
                if (result.ExitCode == 1223)
                    throw new OperationCanceledException("A autorização do UAC foi cancelada. O Chocolatey não foi instalado.", cancellationToken);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Output)
                    ? $"O instalador do Chocolatey falhou (código {result.ExitCode})."
                    : $"O instalador do Chocolatey falhou (código {result.ExitCode}). {result.Output.Trim()}");
            }

            SetExecutablePathOverride(null);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ResolveExecutable() is { } executable && File.Exists(executable))
                    return Path.GetFullPath(executable);
                if (attempt < 9) await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            throw new FileNotFoundException("O instalador terminou, mas choco.exe não apareceu em ChocolateyInstall, ProgramData ou PATH.");
        }
        finally
        {
            try { Directory.Delete(temporaryDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Completa os ícones que não vieram na resposta da busca consultando o manifesto V2
    /// do próprio pacote, como faz o resolvedor NuGet usado pelo UniGetUI.
    /// </summary>
    public async Task<string?> ResolveIconUrlAsync(AppEntry package, CancellationToken cancellationToken = default)
    {
        if (!package.Source.Equals("chocolatey", StringComparison.OrdinalIgnoreCase)
            || !IsValidPackageId(package.Id)) return null;
        if (IsPublicImageUrl(package.IconUrl)) return package.IconUrl;

        string version = package.Version.Trim();
        string cacheKey = $"{package.Id}\u001f{version}";
        if (_iconCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            return cached.IconUrl;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            string safeId = package.Id.Replace("'", "''", StringComparison.Ordinal);
            string safeVersion = version.Replace("'", "''", StringComparison.Ordinal);
            var manifestUri = new Uri($"{CommunityFeedUrl}Packages(Id='{Uri.EscapeDataString(safeId)}',Version='{Uri.EscapeDataString(safeVersion)}')");
            XDocument manifest = await GetFeedDocumentAsync(manifestUri, 1_048_576, timeout.Token).ConfigureAwait(false);
            string? icon = manifest.Descendants().FirstOrDefault(element => element.Name.LocalName == "IconUrl")?.Value.Trim();
            string? result = IsPublicImageUrl(icon) ? new Uri(icon!).AbsoluteUri : null;
            _iconCache[cacheKey] = (result, result is null ? DateTimeOffset.UtcNow.AddMinutes(15) : DateTimeOffset.MaxValue);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { _iconCache[cacheKey] = (null, DateTimeOffset.UtcNow.AddMinutes(15)); return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or XmlException or InvalidOperationException or UriFormatException)
        {
            CoreLog.Debug($"Chocolatey icon metadata unavailable for {package.Id}: {exception.Message}");
            _iconCache[cacheKey] = (null, DateTimeOffset.UtcNow.AddMinutes(15));
            return null;
        }
    }

    private static bool IsPublicImageUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "https" or "http"
        && !string.IsNullOrWhiteSpace(uri.Host);

    private static bool ContainsAuthenticationFailure(Exception exception)
    {
        if (exception is AuthenticationException) return true;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(ContainsAuthenticationFailure);
        return exception.InnerException is { } inner && ContainsAuthenticationFailure(inner);
    }

    public async Task<(bool Available, string Version, string Path)> CheckStatusAsync(CancellationToken cancellationToken = default)
    {
        string? path = ExecutablePath;
        if (path is null) return (false, "Não encontrada", string.Empty);
        if (!File.Exists(path)) return (false, "Executável não encontrado", path);
        CommandResult result = await RunManagerCommandAsync(path, ["--version"], TimeSpan.FromSeconds(12), "Outra tarefa", cancellationToken).ConfigureAwait(false);
        string version = FirstNonEmptyLine(result.Output, result.Error);
        return (result.ExitCode == 0, string.IsNullOrWhiteSpace(version) ? "Versão não informada" : version, path);
    }

    public async Task<IReadOnlyList<string>> ListSourcesAsync(CancellationToken token = default)
    {
        string path = RequireExecutable();
        CommandResult result = await RunManagerCommandAsync(path, ["source", "list", "--limit-output"], TimeSpan.FromSeconds(20), "Listar fontes", token).ConfigureAwait(false);
        EnsureSuccess(result, "Listar fontes Chocolatey");
        return Lines(result.Output).Where(line => !line.StartsWith("Chocolatey v", StringComparison.OrdinalIgnoreCase)
            && (line.Contains('|') || line.Contains(" - http", StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public async Task AddSourceAsync(string name, string sourceUrl, CancellationToken token = default)
    {
        string normalizedName = name.Trim();
        string normalizedSource = sourceUrl.Trim();
        if (normalizedName.Length is 0 or > 64 || !char.IsAsciiLetterOrDigit(normalizedName[0])
            || normalizedName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException("Use um nome de fonte com até 64 letras, números, ponto, hífen ou sublinhado.", nameof(name));
        if (!Uri.TryCreate(normalizedSource, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("A fonte deve usar uma URL HTTPS absoluta.", nameof(sourceUrl));
        string path = RequireExecutable();
        string arguments = "source add --name " + QuoteWindowsArgument(normalizedName)
            + " --source " + QuoteWindowsArgument(uri.AbsoluteUri) + " --priority 0 --force";
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        WingetExecutionResult result = await _elevatedProcessRunner(path, arguments, token, null).ConfigureAwait(false);
        await RecordElevatedResultAsync(path, arguments, "Outra tarefa", startedAt, result, token).ConfigureAwait(false);
        EnsureSuccess(new CommandResult(result.ExitCode, result.Output, string.Empty), "Adicionar fonte Chocolatey");
    }

    public async Task RemoveSourceAsync(string name, CancellationToken token = default)
    {
        string normalizedName = name.Trim();
        if (normalizedName.Length is 0 or > 64 || normalizedName.Any(char.IsControl))
            throw new ArgumentException("O nome da fonte Chocolatey é inválido.", nameof(name));
        string path = RequireExecutable();
        string arguments = "source remove --name " + QuoteWindowsArgument(normalizedName);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        WingetExecutionResult result = await _elevatedProcessRunner(path, arguments, token, null).ConfigureAwait(false);
        await RecordElevatedResultAsync(path, arguments, "Outra tarefa", startedAt, result, token).ConfigureAwait(false);
        EnsureSuccess(new CommandResult(result.ExitCode, result.Output, string.Empty), "Remover fonte Chocolatey");
    }

    public Task<IReadOnlyList<AppEntry>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
        _taskExecutor is null
            ? SearchCoreAsync(query, cancellationToken)
            : _taskExecutor.RunNativeAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Chocolatey,
                "Chocolatey", "Search", "Chocolatey"), innerToken => SearchCoreAsync(query, innerToken), cancellationToken);

    private async Task<IReadOnlyList<AppEntry>> SearchCoreAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Any(char.IsControl)) return [];

        // Segue a consulta V2 do BaseNuGet do UniGetUI: busca textual no feed,
        // latest estável, 50 resultados por página e sem filtro local por nome.
        string searchTerm = HttpUtility.UrlEncode(query.Trim().Replace("'", "''", StringComparison.Ordinal));
        Uri? nextPage = new($"{CommunityFeedUrl}Search()?" +
            "$filter=IsLatestVersion&$orderby=Id" +
            $"&searchTerm='{searchTerm}'" +
            "&targetFramework=''&includePrerelease=false&$skip=0&$top=50&semVerLevel=2.0.0");
        var packagesById = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var requestedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (nextPage is not null && requestedPages.Add(nextPage.AbsoluteUri))
        {
            cancellationToken.ThrowIfCancellationRequested();
            XDocument feed;
            try
            {
                feed = await GetFeedDocumentAsync(nextPage, 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception) when (ContainsAuthenticationFailure(exception))
            {
                throw new HttpRequestException(
                    $"A validação TLS do feed Chocolatey em '{nextPage.Host}' falhou. Verifique o DNS, o proxy e a inspeção HTTPS da rede.",
                    exception);
            }
            foreach (XElement entry in feed.Root?.Elements(AtomNamespace + "entry") ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entityUrl = entry.Element(AtomNamespace + "id")?.Value ?? string.Empty;
                Match identity = PackageEntityRegex().Match(entityUrl);
                if (!identity.Success) continue;

                string id = identity.Groups["id"].Value;
                string version = identity.Groups["version"].Value;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version)) continue;

                XElement? properties = entry.Element(AtomNamespace + "content")?.Element(DataNamespace + "Properties")
                    ?? entry.Descendants().FirstOrDefault(element => element.Name.LocalName == "properties");
                string Value(string name) => properties?.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim() ?? string.Empty;
                string iconUrl = Value("IconUrl");
                var package = new AppEntry
                {
                    Id = id,
                    Name = FormatPackageIdAsName(id),
                    Version = version,
                    Publisher = entry.Element(AtomNamespace + "author")?.Element(AtomNamespace + "name")?.Value.Trim() ?? string.Empty,
                    Description = Value("Description"),
                    Homepage = Value("ProjectUrl"),
                    PackageUrl = Value("GalleryDetailsUrl"),
                    LicenseUrl = Value("LicenseUrl"),
                    SourceDownloadCount = long.TryParse(Value("DownloadCount"), out long downloads) && downloads >= 0 ? downloads : null,
                    Tags = Value("Tags").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    Source = "chocolatey",
                    IconUrl = Uri.TryCreate(iconUrl, UriKind.Absolute, out Uri? iconUri) && iconUri.Scheme == Uri.UriSchemeHttps
                        ? iconUri.AbsoluteUri
                        : string.Empty,
                };

                // Search() is filtered to IsLatestVersion; repeated IDs across pages
                // therefore represent the same feed result. Keep its first occurrence.
                packagesById.TryAdd(id, package);
            }

            string? continuation = feed.Root?.Elements(AtomNamespace + "link")
                .FirstOrDefault(link => string.Equals((string?)link.Attribute("rel"), "next", StringComparison.OrdinalIgnoreCase))?
                .Attribute("href")?.Value;
            nextPage = Uri.TryCreate(continuation, UriKind.Absolute, out Uri? continuationUri)
                && continuationUri.Scheme == Uri.UriSchemeHttps
                && continuationUri.Host.Equals("community.chocolatey.org", StringComparison.OrdinalIgnoreCase)
                    ? continuationUri
                    : null;
        }
        return packagesById.Values.ToArray();
    }

    public async Task<string[]> GetVersionsAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidPackageId(id)) throw new ArgumentException("O identificador do pacote Chocolatey é inválido.", nameof(id));
        string executable = RequireExecutable();
        CommandResult result = await RunManagerCommandAsync(executable,
            ["search", id.Trim(), "--exact", "--all-versions", "--limit-output"],
            TimeSpan.FromSeconds(90), "ListPackageVersions", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error)
                ? $"O Chocolatey falhou ao consultar versões (código {result.ExitCode})."
                : result.Error.Trim());
        return ParseVersionsOutput(id, result.Output);
    }

    internal static string[] ParseVersionsOutput(string id, string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.StartsWith("[Approved]", StringComparison.OrdinalIgnoreCase)
            ? line["[Approved]".Length..].Trim() : line)
        .Select(line => line.Split('|', 2, StringSplitOptions.TrimEntries))
        .Where(parts => parts.Length == 2 && parts[0].Equals(id, StringComparison.OrdinalIgnoreCase)
            && IsValidPackageVersion(parts[1]))
        .Select(parts => parts[1])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private async Task<XDocument> GetFeedDocumentAsync(Uri uri, long maxCharacters, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("WinProvisionStore/1.0");
                // This method owns the complete retry budget, including response body reads.
                // The shared handler still rate-limits and observes Retry-After, but must not
                // multiply Chocolatey's three attempts into as many as nine requests.
                request.Options.Set(RequestRateLimitHandler.DisableTransportRetriesOption, true);
                using HttpResponseMessage response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    bool transientStatus = response.StatusCode is System.Net.HttpStatusCode.RequestTimeout
                        or System.Net.HttpStatusCode.TooManyRequests
                        or System.Net.HttpStatusCode.InternalServerError
                        or System.Net.HttpStatusCode.BadGateway
                        or System.Net.HttpStatusCode.ServiceUnavailable
                        or System.Net.HttpStatusCode.GatewayTimeout;
                    if (transientStatus && attempt + 1 < MaximumFeedAttempts)
                    {
                        TimeSpan delay = GetRetryDelay(response, attempt);
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new HttpRequestException($"Consultar o feed do Chocolatey falhou (HTTP {(int)response.StatusCode}).",
                        null, response.StatusCode);
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    Async = true,
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = maxCharacters,
                });
                return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt + 1 < MaximumFeedAttempts
                && !ContainsAuthenticationFailure(exception)
                && exception is (HttpRequestException { StatusCode: null } or IOException or XmlException))
            {
                CoreLog.Debug($"Chocolatey feed request failed transiently ({attempt + 1}/{MaximumFeedAttempts}): {exception.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task DownloadPackageAsync(string id, string version, string destination,
        Action<InstallProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        if (!IsValidPackageId(id) || !IsValidPackageVersion(version))
            throw new InvalidDataException("O ID ou a versão Chocolatey são inválidos para download.");

        var uri = new Uri($"{CommunityFeedUrl}package/{Uri.EscapeDataString(id.Trim())}/{Uri.EscapeDataString(version.Trim())}");
        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("WinProvisionStore/1.0");
                request.Options.Set(RequestRateLimitHandler.DisableTransportRetriesOption, true);
                using HttpResponseMessage response = await _packageDownloadClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    bool transient = response.StatusCode is System.Net.HttpStatusCode.RequestTimeout
                        or System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.InternalServerError
                        or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable
                        or System.Net.HttpStatusCode.GatewayTimeout;
                    if (transient && attempt + 1 < MaximumFeedAttempts)
                    {
                        await Task.Delay(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    throw new HttpRequestException($"Baixar o pacote Chocolatey falhou (HTTP {(int)response.StatusCode}).",
                        null, response.StatusCode);
                }
                if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
                    throw new InvalidDataException("O download do pacote Chocolatey foi redirecionado para uma URL que não usa HTTPS.");
                if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
                    throw new InvalidDataException("O NUPKG Chocolatey excede o limite de 2 GiB.");

                await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                long? contentLength = response.Content.Headers.ContentLength;
                await using var output = new FileStream(fullDestination, FileMode.Create, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = new byte[128 * 1024];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    received = checked(received + read);
                    if (received > MaximumPackageBytes) throw new InvalidDataException("O NUPKG Chocolatey excede o limite de 2 GiB.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    double? percent = contentLength is > 0
                        ? Math.Clamp(received * 100d / contentLength.Value, 0, 100)
                        : null;
                    progress?.Invoke(new(InstallProgressPhase.Downloading, percent));
                }
                if (contentLength is > 0 && received != contentLength.Value)
                    throw new IOException("O download do NUPKG Chocolatey terminou antes do tamanho informado pelo servidor.");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(fullDestination);
                throw;
            }
            catch (Exception exception) when (attempt + 1 < MaximumFeedAttempts
                && exception is (HttpRequestException { StatusCode: null } or IOException))
            {
                TryDelete(fullDestination);
                CoreLog.Debug($"Chocolatey package download failed transiently ({attempt + 1}/{MaximumFeedAttempts}): {exception.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(fullDestination);
                throw;
            }
        }
    }

    private static bool IsValidPackageVersion(string? version) => !string.IsNullOrWhiteSpace(version)
        && version.Length <= 128 && PackageVersionRegex().IsMatch(version.Trim());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return TimeSpan.FromSeconds(Math.Clamp(delta.TotalSeconds, 0, 5));
        if (response.Headers.RetryAfter?.Date is { } date)
            return TimeSpan.FromSeconds(Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, 5));
        return TimeSpan.FromMilliseconds(250 * (attempt + 1));
    }

    public async Task<IReadOnlyList<InstalledPackageInfo>> ListInstalledAsync(CancellationToken cancellationToken = default)
    {
        string path = RequireExecutable();
        // Chocolatey CLI 2.x list is local-only by default. --local-only was removed in v2.
        CommandResult result = await RunManagerCommandAsync(path, ["list", "--limit-output"], DefaultTimeout, "Listar instalados", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "Listar pacotes Chocolatey");
        var packages = new List<InstalledPackageInfo>();
        foreach (string line in Lines(result.Output))
        {
            string[] parts = line.Split('|', 2);
            if (parts.Length != 2 || IsInstalledHeader(parts) || !IsValidPackageId(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) continue;
            string id = parts[0].Trim();
            if (id.Equals("chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
            packages.Add(new InstalledPackageInfo(id, id, parts[1].Trim(), "chocolatey", string.Empty, string.Empty, string.Empty, string.Empty, false));
        }
        return packages;
    }

    public async Task<IReadOnlyList<UpgradablePackage>> GetOutdatedAsync(CancellationToken cancellationToken = default) =>
        (await GetOutdatedWithExitCodeAsync(cancellationToken).ConfigureAwait(false)).Packages;

    internal async Task<ChocolateyOutdatedQueryResult> GetOutdatedWithExitCodeAsync(CancellationToken cancellationToken = default)
    {
        string path = RequireExecutable();
        CommandResult result = await RunManagerCommandAsync(path, ["outdated", "--limit-output"], OutdatedTimeout, "Listar atualizações", cancellationToken).ConfigureAwait(false);
        // `choco outdated` returns rows with code 0 unless enhanced exit codes are enabled.
        // Parsed rows, not the exit code, determine whether updates are available.
        var packages = new List<UpgradablePackage>();
        string[] lines = Lines(result.Output);
        foreach (string line in lines)
        {
            string[] parts = line.Split('|');
            if (parts.Length != 4 || IsOutdatedHeader(parts) || !IsValidPackageId(parts[0])
                || string.IsNullOrWhiteSpace(parts[1]) || string.IsNullOrWhiteSpace(parts[2])
                || !bool.TryParse(parts[3].Trim(), out bool pinned) || pinned
                || parts[1].Trim().Equals(parts[2].Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            string id = parts[0].Trim();
            packages.Add(new UpgradablePackage { Id = id, Name = id, CurrentVersion = parts[1].Trim(), AvailableVersion = parts[2].Trim(), Source = "chocolatey" });
        }
        return new ChocolateyOutdatedQueryResult(packages, result.ExitCode, lines.Length, result.Output, result.Error);
    }

    public Task<WingetExecutionResult> InstallAsync(string id, bool interactive, CancellationToken cancellationToken,
        Action<string>? onOutput = null) =>
        InstallAsync(id, new PackageInstallOptions(Interactive: interactive, RequiresElevation: true), cancellationToken, onOutput);

    public Task<WingetExecutionResult> InstallAsync(string id, PackageInstallOptions? options, CancellationToken cancellationToken,
        Action<string>? onOutput = null) => OperateAsync("install", id, options ?? new PackageInstallOptions(RequiresElevation: true), cancellationToken, onOutput);

    public Task<WingetExecutionResult> UpgradeAsync(string id, bool interactive, CancellationToken cancellationToken,
        Action<string>? onOutput = null) =>
        UpgradeAsync(id, new PackageInstallOptions(Interactive: interactive, RequiresElevation: true), cancellationToken, onOutput);

    public Task<WingetExecutionResult> UpgradeAsync(string id, PackageInstallOptions? options, CancellationToken cancellationToken,
        Action<string>? onOutput = null) => OperateAsync("upgrade", id, options ?? new PackageInstallOptions(RequiresElevation: true), cancellationToken, onOutput);

    public Task<WingetExecutionResult> UninstallAsync(string id, bool interactive, CancellationToken cancellationToken,
        Action<string>? onOutput = null) =>
        UninstallAsync(id, new PackageInstallOptions(Interactive: interactive, RequiresElevation: true), cancellationToken, onOutput);

    public Task<WingetExecutionResult> UninstallAsync(string id, PackageInstallOptions? options, CancellationToken cancellationToken,
        Action<string>? onOutput = null) => OperateAsync("uninstall", id, options ?? new PackageInstallOptions(RequiresElevation: true), cancellationToken, onOutput);

    private async Task<WingetExecutionResult> OperateAsync(string operation, string id, PackageInstallOptions options,
        CancellationToken cancellationToken, Action<string>? onOutput)
    {
        if (!IsValidPackageId(id))
            return new WingetExecutionResult { Success = false, ExitCode = 2, Output = "O identificador do pacote Chocolatey é inválido." };
        string path;
        try { path = RequireExecutable(); }
        catch (Exception exception) { return new WingetExecutionResult { Success = false, ExitCode = 1, Output = exception.Message }; }

        string[] arguments;
        try { arguments = ChocolateyCommandLineBuilder.BuildArguments(operation, id, options); }
        catch (ArgumentException exception)
        {
            return new WingetExecutionResult { Success = false, ExitCode = 2, Output = exception.Message };
        }
        try
        {
            // Chocolatey writes to its system installation root by default. Package
            // mutations always use the isolated UAC runner, even if a stale saved option
            // requested a non-elevated run.
            string commandLine = string.Join(' ', arguments.Select(QuoteWindowsArgument));
            string displayCommandLine = CommandLineDisplayFormatter.FormatPowerShellArguments(arguments);
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            WingetExecutionResult result = await _elevatedProcessRunner(path, commandLine, cancellationToken, onOutput).ConfigureAwait(false);
            await RecordElevatedResultAsync(path, displayCommandLine, operation == "install" ? "Instalar" : operation == "upgrade" ? "Atualizar" : "Desinstalar",
                startedAt, result, cancellationToken).ConfigureAwait(false);
            result.Success = result.Success || result.ExitCode is 0 or 3010 or 1641
                || operation == "uninstall" && result.ExitCode is 1605 or 1614;
            if (result.ExitCode is 1641 or 3010)
                result.Output = JoinOutput(result.Output, "A operação foi concluída, mas o Chocolatey solicitou reinicialização do Windows.");
            result.Output = result.Output.Trim();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            return new WingetExecutionResult { Success = false, ExitCode = 1, Output = exception.Message };
        }
    }

    private static string QuoteWindowsArgument(string value)
    {
        var result = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2);
        return result.Append('"').ToString();
    }

    internal static async Task<CommandResult> RunAsync(string executable, string[] arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = OutputEncoding,
            StandardErrorEncoding = OutputEncoding
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o Chocolatey CLI.");
        process.StandardInput.Close();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try { await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("A operação do Chocolatey excedeu o tempo limite.");
        }
        return new CommandResult(process.ExitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false));
    }

    private Task<CommandResult> RunManagerCommandAsync(string executable, string[] arguments, TimeSpan timeout,
        string taskType, CancellationToken cancellationToken)
    {
        if (_taskExecutor is null) return _commandRunner(executable, arguments, timeout, cancellationToken);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true,
            StandardOutputEncoding = OutputEncoding, StandardErrorEncoding = OutputEncoding
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return RunLoggedAsync(start, timeout, taskType, cancellationToken);
    }

    private async Task<CommandResult> RunLoggedAsync(ProcessStartInfo start, TimeSpan timeout, string taskType, CancellationToken token)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutSource.CancelAfter(timeout);
        AppLogProcessResult result = await _taskExecutor!.RunProcessAsync(new AppLogTaskDescriptor(
            PackageManagerLogCategories.Chocolatey, "Chocolatey", taskType, "Chocolatey"), start, timeoutSource.Token).ConfigureAwait(false);
        return new CommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private Task RecordElevatedResultAsync(string executable, string arguments, string taskType, DateTimeOffset startedAt,
        WingetExecutionResult result, CancellationToken token) => _taskExecutor is null ? Task.CompletedTask
        : _taskExecutor.RecordExternalProcessResultAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Chocolatey,
            "Chocolatey", taskType, "Chocolatey"), executable, arguments, startedAt, DateTimeOffset.UtcNow,
            result.ExitCode, result.StandardOutput.Length > 0 ? result.StandardOutput : result.Output,
            result.StandardError, token);

    private static Task<WingetExecutionResult> RunElevatedProcessAsync(string executable, string arguments,
        CancellationToken token, Action<string>? onOutput) =>
        ElevatedProcessRunner.RunElevatedAsync(executable, WindowsArgumentParser.Parse(arguments), token,
            ElevationCategory.Chocolatey, affectWingetElevationState: false, onLogReceived: onOutput);

    private static Encoding CreateOutputEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            CoreLog.Debug($"Não foi possível obter a página de código OEM do Chocolatey; usando UTF-8. {exception.Message}");
            return Encoding.UTF8;
        }
    }

    private static string? ResolveExecutable()
    {
        string? installRoot = Environment.GetEnvironmentVariable("ChocolateyInstall");
        string[] candidates =
        [
            Path.Combine(installRoot ?? @"C:\ProgramData\chocolatey", "bin", "choco.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "chocolatey", "bin", "choco.exe")
        ];
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim().Trim('"'), "choco.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static bool IsValidPackageId(string? id) => !string.IsNullOrWhiteSpace(id)
        && PackageIdRegex().IsMatch(id.Trim())
        && id.Length <= 128;
    private string RequireExecutable() => ExecutablePath ?? throw new InvalidOperationException($"Chocolatey não está instalado. Instale pelo WinGet com: {InstallCommand}");
    private static void EnsureSuccess(CommandResult result, string operation, int? acceptedExitCode = null)
    {
        if (result.ExitCode != 0 && result.ExitCode != acceptedExitCode)
            throw new InvalidOperationException($"{operation} falhou (código {result.ExitCode}). {FirstNonEmptyLine(result.Error, result.Output)}");
    }
    private static bool IsInstalledHeader(string[] parts) => parts.Length >= 2
        && NormalizeHeader(parts[0]) == "id" && NormalizeHeader(parts[1]) == "version";
    private static bool IsOutdatedHeader(string[] parts) => parts.Length >= 4
        && NormalizeHeader(parts[0]) == "id" && NormalizeHeader(parts[1]) == "version"
        && NormalizeHeader(parts[2]) == "availableversion" && NormalizeHeader(parts[3]) == "pinned";
    private static string NormalizeHeader(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string[] Lines(string value) => value.TrimStart('\uFEFF').Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string FirstNonEmptyLine(params string[] values) => values.SelectMany(Lines).FirstOrDefault() ?? string.Empty;
    private static string JoinOutput(string output, string error) => string.Join(Environment.NewLine, new[] { output.Trim(), error.Trim() }.Where(value => value.Length > 0));
    private static string FormatPackageIdAsName(string id)
    {
        string name = id.Replace(".install", "", StringComparison.Ordinal)
            .Replace(".portable", "", StringComparison.Ordinal)
            .Replace('-', ' ')
            .Replace('_', ' ')
            .Split('/')[^1]
            .Split(':')[0];
        var result = new StringBuilder(name.Length + 4);
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (index == 0 || name[index - 1] == ' ' || name[index - 1] == '[')
                character = char.ToUpperInvariant(character);
            if (character == '[' && index > 0 && name[index - 1] != ' ')
                result.Append(' ');
            result.Append(character);
        }
        return result.ToString().Replace(" [", "[").Replace("[", " [", StringComparison.Ordinal).TrimStart();
    }

    internal readonly record struct CommandResult(int ExitCode, string Output, string Error);
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersionRegex();
    [GeneratedRegex("Packages\\(Id='(?<id>[^']+)',Version='(?<version>[^']+)'\\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PackageEntityRegex();
}
