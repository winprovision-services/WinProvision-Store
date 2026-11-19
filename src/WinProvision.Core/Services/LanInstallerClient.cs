using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed record LanDownloadProgress(string File, long BytesReceived, long? TotalBytes);
public sealed record LanCachedInstaller(
    LanInstallerAvailability Availability,
    string LocalPath,
    LanInstallerDetection Detection,
    bool HashVerified = true,
    bool TypeMatches = true,
    LanInstallerSecurityInfo? SecurityInfo = null,
    LanInstallerMetadata? Metadata = null);
public sealed record LanInstallerSyncItem(
    LanInstallerEntry Installer,
    LanCachedInstaller? CachedInstaller,
    string Status,
    string? Failure = null);
public sealed record LanSyncResult(
    IReadOnlyList<LanCachedInstaller> Installers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<LanInstallerSyncItem>? Items = null);

public sealed class LanInstallerClient
{
    private const int MaximumIndexBytes = 1024 * 1024;
    private const int MaximumModuleBytes = 16 * 1024 * 1024;
    private const int MaximumSharingRetries = 8;
    private const int InitialRetryDelayMilliseconds = 100;
    private const int MaximumRetryDelayMilliseconds = 2000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SyncGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;
    private readonly IReadOnlyDictionary<string, IClientModuleHandler> _handlers;
    private readonly string _cacheRoot;
    private readonly string? _serverRoot;
    private readonly ILanTransportSecurityPolicy _transport;
    private readonly Func<string, string, CancellationToken, Task>? _stageHook;
    private readonly Func<int, CancellationToken, Task> _retryDelay;

    public LanInstallerClient(
        HttpClient http,
        IEnumerable<IClientModuleHandler> handlers,
        string? cacheRoot = null,
        ILanTransportSecurityPolicy? transport = null,
        string? serverRoot = null)
        : this(http, handlers, cacheRoot, transport, serverRoot, null, DelayBeforeRetryAsync)
    {
    }

    internal LanInstallerClient(
        HttpClient http,
        IEnumerable<IClientModuleHandler> handlers,
        string? cacheRoot,
        ILanTransportSecurityPolicy? transport,
        string? serverRoot,
        Func<string, string, CancellationToken, Task>? stageHook,
        Func<int, CancellationToken, Task> retryDelay)
    {
        _http = http;
        _handlers = handlers.ToDictionary(handler => handler.Name, StringComparer.OrdinalIgnoreCase);
        _transport = transport ?? new LanHttpTransportSecurityPolicy();
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "lan-cache");
        _serverRoot = serverRoot is null ? null : Path.GetFullPath(serverRoot);
        _stageHook = stageHook;
        _retryDelay = retryDelay;
    }

    public async Task<LanSyncResult> SynchronizeAsync(
        string serverAddress,
        string token,
        IProgress<LanDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string cacheRoot = NormalizeDirectoryPath(_cacheRoot);
        if (_serverRoot is not null && PathsOverlap(cacheRoot, _serverRoot))
            throw new InvalidOperationException($"A pasta de cache do cliente '{cacheRoot}' não pode ser igual, pai ou filha da pasta compartilhada pelo servidor '{_serverRoot}'.");
        SemaphoreSlim gate = SyncGates.GetOrAdd(cacheRoot, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CleanupOrphanPartials(cacheRoot);
            return await SynchronizeCoreAsync(serverAddress, token, progress, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public static bool PathsOverlap(string firstPath, string secondPath)
    {
        string first = NormalizeDirectoryPath(firstPath);
        string second = NormalizeDirectoryPath(secondPath);
        return IsSameOrChild(first, second) || IsSameOrChild(second, first);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length > root.Length ? Path.TrimEndingDirectorySeparator(fullPath) : fullPath;
    }

    private static bool IsSameOrChild(string path, string parent)
    {
        if (path.Equals(parent, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LanSyncResult> SynchronizeCoreAsync(
        string serverAddress,
        string token,
        IProgress<LanDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Uri baseUri = _transport.ValidateServerAddress(serverAddress);
        using var request = CreateRequest(HttpMethod.Get, new Uri(baseUri, "/v1/index.json"), token);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        byte[] indexBytes = await ReadLimitedAsync(response, MaximumIndexBytes, cancellationToken).ConfigureAwait(false);
        LanIndexDocument? index;
        try { index = JsonSerializer.Deserialize(indexBytes, CoreJsonContext.Default.LanIndexDocument); }
        catch (JsonException exception) { throw new InvalidDataException("O manifesto LAN está inválido.", exception); }
        if (index is null || index.Schema != "winprovision.lan" || index.SchemaVersion != 1
            || index.Modules is null || index.Modules.Any(module => module is null))
            throw new InvalidDataException("O manifesto LAN usa uma versão incompatível.");

        var installers = new List<LanCachedInstaller>();
        var items = new List<LanInstallerSyncItem>();
        var warnings = new List<string>();
        var seenModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LanModuleDescriptor descriptor in index.Modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptor is null)
            {
                warnings.Add("Descritor de módulo nulo ignorado.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(descriptor.Name))
            {
                warnings.Add("Descritor de módulo sem nome ignorado.");
                continue;
            }
            if (!seenModules.Add(descriptor.Name))
            {
                warnings.Add($"Descritor duplicado ignorado: {descriptor.Name}.");
                continue;
            }
            if (!_handlers.TryGetValue(descriptor.Name, out IClientModuleHandler? handler))
            {
                warnings.Add($"Módulo desconhecido ignorado: {descriptor.Name}.");
                continue;
            }
            if (descriptor.Version != handler.SupportedMajorVersion)
            {
                warnings.Add($"O módulo {descriptor.Name} usa uma versão incompatível e foi ignorado.");
                continue;
            }
            if (!LanInstallerValidation.IsValidSha256(descriptor.Sha256)
                || !string.Equals(descriptor.Path, handler.ExpectedPath, StringComparison.Ordinal))
            {
                warnings.Add($"O descritor do módulo {descriptor.Name} é inválido e foi ignorado.");
                continue;
            }

            try
            {
                byte[] moduleBytes = await DownloadBytesAsync(new Uri(baseUri, descriptor.Path), token,
                    MaximumModuleBytes, cancellationToken).ConfigureAwait(false);
                if (!HashesEqual(SHA256.HashData(moduleBytes), Convert.FromHexString(descriptor.Sha256)))
                {
                    warnings.Add($"A validação SHA-256 do módulo {descriptor.Name} falhou; módulo ignorado.");
                    continue;
                }

                LanInstallersModuleDocument module = handler.DeserializeAndValidate(moduleBytes);
                foreach (LanInstallerEntry installer in module.Installers)
                {
                    try
                    {
                        _ = LanInstallerValidation.ValidateInstaller(installer);
                        string localPath = await DownloadInstallerAsync(baseUri, token, installer, progress, cancellationToken).ConfigureAwait(false);
                        LanInstallerDetection detection = LanInstallerTypeDetector.Detect(localPath);
                        bool typeMatches = string.Equals(detection.ModuleType, installer.InstallerType, StringComparison.OrdinalIgnoreCase);
                        LanInstallerArguments effectiveArguments = LanInstallerArgumentsPolicy.Resolve(
                            detection.Kind, installer.SilentArgs, manualArguments: null,
                            externalSource: installer.SilentSource);
                        LanInstallerAvailability availability = !typeMatches
                            ? new(installer, false, $"Tipo divergente: servidor '{installer.InstallerType ?? "não informado"}', arquivo detectado '{detection.DisplayName}'.")
                            : effectiveArguments.Source == LanInstallerArgumentSource.Unsupported
                                ? new(installer, false, $"Sem suporte silencioso: {detection.DisplayName} não tem argumentos padrão; informe argumentos manuais.")
                                : new(installer, true, null);

                        var cached = new LanCachedInstaller(availability, localPath, detection, HashVerified: true,
                            TypeMatches: typeMatches, Metadata: LanInstallerMetadataReader.Read(localPath));
                        LanInstallerSecurityInfo security = await new LanInstallerExecutionService()
                            .InspectAsync(cached, cancellationToken).ConfigureAwait(false);
                        cached = cached with { SecurityInfo = security };
                        installers.Add(cached);
                        items.Add(new(installer, cached, "Sincronizado"));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        string failure = exception is LanInstallerSyncException syncException
                            ? syncException.Message
                            : $"Falhou: verificação pós-publicação — {FormatException(exception)}";
                        string message = $"Cliente LAN: {installer.File}: {failure}";
                        warnings.Add(message);
                        items.Add(new(installer, null, failure, message));
                        CoreLog.Warning(exception, $"LanClient.Sync.{installer.File}");
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException)
            {
                warnings.Add($"Não foi possível sincronizar o módulo {descriptor.Name}: {exception.Message}");
            }
        }

        return new(installers, warnings, items);
    }

    public static Uri ValidateServerAddress(string serverAddress) =>
        new LanHttpTransportSecurityPolicy().ValidateServerAddress(serverAddress);

    private async Task<string> DownloadInstallerAsync(
        Uri baseUri,
        string token,
        LanInstallerEntry installer,
        IProgress<LanDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetFullPath(_cacheRoot), installer.Sha256.ToUpperInvariant());
        string targetPath = Path.Combine(directory, installer.File);
        string stage = "preparar cache";
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(targetPath))
            {
                stage = "cache hit (calcular SHA-256)";
                string existingHash = await HashFileAsync(targetPath, cancellationToken).ConfigureAwait(false);
                if (existingHash.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                            LogStage(installer.File, stage, $"cache válido; esperado={installer.Sha256}, gravado={new FileInfo(targetPath).Length} bytes");
                    return targetPath;
                }
                LogStage(installer.File, stage, "hash local divergente; removendo o arquivo e baixando novamente");
                stage = "remover cache inválido";
                File.Delete(targetPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateStageException(installer.File, stage, targetPath, null, installer.Sha256, 0, exception);
        }

        var uri = new Uri(baseUri, "/v1/files/" + Uri.EscapeDataString(installer.File));
        string partialPath = targetPath + $".{Guid.NewGuid():N}.partial";
        long received = 0;
        long? expectedLength = null;
        stage = "receber resposta HTTP";
        try
        {
            using var request = CreateRequest(HttpMethod.Get, uri, token);
            using HttpResponseMessage response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            expectedLength = response.Content.Headers.ContentLength;
            LogStage(installer.File, "receber resposta HTTP", $"resposta recebida; esperado={FormatLength(expectedLength)}");
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                stage = "criar arquivo .partial";
                LogStage(installer.File, stage, $"início; temporário='{partialPath}'");
                await using var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                LogStage(installer.File, stage, $"concluído; temporário='{partialPath}'");
                stage = "gravar .partial";
                LogStage(installer.File, stage, $"início; esperado={FormatLength(expectedLength)}");
                byte[] buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new(installer.File, received, expectedLength));
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                stage = "fechar .partial";
                LogStage(installer.File, stage, $"fechando; esperado={FormatLength(expectedLength)}, gravado={received} bytes");
                await output.DisposeAsync().ConfigureAwait(false);
            }

            LogStage(installer.File, stage, $"fechado; esperado={FormatLength(expectedLength)}, gravado={received} bytes");
            stage = "calcular SHA-256 do .partial";
            LogStage(installer.File, stage, $"início; esperado={FormatLength(expectedLength)}, gravado={received} bytes");
            if (_stageHook is not null)
                await _stageHook("antes da leitura SHA-256", partialPath, cancellationToken).ConfigureAwait(false);
            string actualHash = await RetrySharingViolationAsync(installer.File, stage, partialPath, targetPath,
                installer.Sha256, received, expectedLength,
                () => HashFileAsync(partialPath, cancellationToken), cancellationToken).ConfigureAwait(false);
            LogStage(installer.File, stage, $"concluído; SHA-256={actualHash}");
            if (!actualHash.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase))
                throw CreateStageException(installer.File, "validar SHA-256", partialPath, targetPath,
                    installer.Sha256, received, new InvalidDataException($"hash calculado={actualHash}"), expectedLength);

            stage = "publicar com File.Move";
            LogStage(installer.File, stage, $"início; temporário='{partialPath}', destino='{targetPath}'");
            if (_stageHook is not null)
                await _stageHook("antes da publicação", partialPath, cancellationToken).ConfigureAwait(false);
            await RetrySharingViolationAsync(installer.File, stage, partialPath, targetPath, installer.Sha256, received,
                expectedLength,
                () =>
                {
                    File.Move(partialPath, targetPath, overwrite: true);
                    return Task.FromResult(true);
                }, cancellationToken).ConfigureAwait(false);
            LogStage(installer.File, stage,
                $"concluído; esperado={FormatLength(expectedLength)}, gravado={received} bytes, hash={actualHash}");
            return targetPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LanInstallerSyncException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or HttpRequestException or InvalidDataException or CryptographicException or ArgumentException)
        {
            throw CreateStageException(installer.File, stage, partialPath, targetPath,
                installer.Sha256, received, exception, expectedLength);
        }
        finally
        {
            TryDeletePartial(partialPath, installer.File);
        }
    }

    private async Task<T> RetrySharingViolationAsync<T>(
        string file,
        string stage,
        string partialPath,
        string targetPath,
        string expectedHash,
        long bytesWritten,
        long? expectedLength,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (IOException exception) when (IsSharingViolation(exception) && attempt < MaximumSharingRetries)
            {
                int delay = Math.Min(InitialRetryDelayMilliseconds << Math.Min(attempt, 20), MaximumRetryDelayMilliseconds);
                LogStage(file, stage,
                    $"sharing violation HResult=0x{exception.HResult:X8}; tentativa {attempt + 1}/{MaximumSharingRetries}, nova tentativa em {delay} ms");
                await _retryDelay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                throw CreateStageException(file, stage, partialPath, targetPath, expectedHash, bytesWritten, exception, expectedLength);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        exception.HResult == unchecked((int)0x80070020)
        || exception.HResult == unchecked((int)0x80070021);

    private static LanInstallerSyncException CreateStageException(
        string file,
        string stage,
        string? partialPath,
        string? targetPath,
        string expectedHash,
        long bytesWritten,
        Exception exception,
        long? expectedLength = null)
    {
        string source = partialPath is null ? "(sem .partial)" : partialPath;
        string target = targetPath is null ? "(sem destino)" : targetPath;
        string suggestion = IsSharingViolationInChain(exception)
            ? " Um antivírus ou indexador pode estar segurando o arquivo; aguarde e tente novamente."
            : string.Empty;
        string inner = exception.InnerException is null
            ? string.Empty
            : $" Inner: {exception.InnerException.GetType().Name}: {exception.InnerException.Message} (HResult=0x{exception.InnerException.HResult:X8}).";
        string message = $"Falhou na etapa '{stage}' para '{file}'. HResult=0x{exception.HResult:X8}; esperado={FormatLength(expectedLength)}; gravado={bytesWritten} bytes; SHA-256 esperado={expectedHash}; temporário='{source}'; destino='{target}'. Motivo: {exception.Message}.{inner}{suggestion}";
        return new LanInstallerSyncException(message, stage, exception);
    }

    private static bool IsSharingViolationInChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is IOException io && IsSharingViolation(io))
                return true;
        return false;
    }

    private static string FormatLength(long? length) => length is null ? "desconhecido" : $"{length.Value} bytes";

    private static string FormatException(Exception exception)
    {
        string inner = exception.InnerException is null ? string.Empty
            : $" Inner: {exception.InnerException.GetType().Name}: {exception.InnerException.Message} (HResult=0x{exception.InnerException.HResult:X8}).";
        return $"{exception.Message} (HResult=0x{exception.HResult:X8}).{inner}";
    }

    private static void LogStage(string file, string stage, string detail) =>
        CoreLog.Information("LanClient.SyncStage", $"arquivo={file}; etapa={stage}; {detail}");

    private static void CleanupOrphanPartials(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
            return;
        foreach (string partial in Directory.EnumerateFiles(cacheRoot, "*.partial", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(partial);
                CoreLog.Information("LanClient.PartialCleanup", $"parcial órfão removido '{partial}'.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CoreLog.Warning(exception, $"LanClient.PartialCleanup.{partial}");
            }
        }
    }

    private static void TryDeletePartial(string partialPath, string file)
    {
        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, $"LanClient.PartialCleanup.{file}.{partialPath}");
        }
    }

    private static Task DelayBeforeRetryAsync(int milliseconds, CancellationToken cancellationToken) =>
        Task.Delay(milliseconds, cancellationToken);

    private async Task<byte[]> DownloadBytesAsync(Uri uri, string token, int maxBytes, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri, token);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadLimitedAsync(response, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        string detail = string.Empty;
        if (response.StatusCode == HttpStatusCode.InternalServerError)
        {
            byte[] body = await ReadLimitedAsync(response, 8 * 1024, cancellationToken).ConfigureAwait(false);
            detail = Encoding.UTF8.GetString(body);
            if (!string.IsNullOrWhiteSpace(detail))
                detail = $" Detalhe do servidor: {detail}";
        }
        throw new HttpRequestException($"O servidor LAN respondeu HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).{detail}");
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("A resposta excede o limite permitido.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("A resposta excede o limite permitido.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string token)
    {
        if (token.Length is < 20 or > 128 || token.Any(char.IsControl))
            throw new ArgumentException("Informe um token válido.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static bool HashesEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}

public sealed class LanInstallerSyncException(string message, string stage, Exception innerException)
    : IOException(message, innerException)
{
    public string Stage { get; } = stage;
}
