using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed record LanModulePayload(string Name, int Version, string Path, byte[] Content);

public interface IServerModule
{
    string Name { get; }
    int Version { get; }
    string Path { get; }
    Task<LanModulePayload> BuildAsync(string rootDirectory, CancellationToken token);
    Task<IReadOnlyCollection<string>> GetServedFilesAsync(string rootDirectory, CancellationToken token);
}

public interface IClientModuleHandler
{
    string Name { get; }
    int SupportedMajorVersion { get; }
    string ExpectedPath { get; }
    LanInstallersModuleDocument DeserializeAndValidate(ReadOnlySpan<byte> content);
}

public sealed class LanInstallersServerModule : IServerModule
{
    private static readonly HashSet<string> InstallerExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi" };
    private readonly ConcurrentDictionary<string, CachedHash> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _hashGate = new(1, 1);
    private const int MaximumMetadataBytes = 16 * 1024 * 1024;
    private const int MaximumSidecarBytes = 64 * 1024;

    public string Name => "installers";
    public int Version => 1;
    public string Path => "/v1/installers.json";

    public async Task<LanModulePayload> BuildAsync(string rootDirectory, CancellationToken token)
    {
        string root = System.IO.Path.GetFullPath(rootDirectory);
        LanInstallersModuleDocument source = await LoadSourceModuleAsync(root, token).ConfigureAwait(false);
        var sourceEntries = source.Installers.ToDictionary(installer => installer.File, StringComparer.OrdinalIgnoreCase);
        var installers = new List<LanInstallerEntry>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            string file = System.IO.Path.GetFileName(filePath);
            if (!InstallerExtensions.Contains(System.IO.Path.GetExtension(file)))
                continue;
            string installerPath = ResolveSafeFilePath(root, file)
                ?? throw new InvalidDataException($"Caminho inválido do instalador: {filePath}.");
            if (!seenFiles.Add(file))
                throw new InvalidDataException($"O módulo installers contém o arquivo duplicado '{installerPath}'.");
            if ((File.GetAttributes(installerPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"O arquivo '{installerPath}' não pode ser um link ou ponto de redirecionamento.");

            try
            {
                LanInstallerDetection detected = LanInstallerTypeDetector.Detect(installerPath);
                sourceEntries.TryGetValue(file, out LanInstallerEntry? sourceEntry);
                LanInstallerSidecar? sidecar = await LoadSidecarAsync(installerPath, token).ConfigureAwait(false);
                var installer = new LanInstallerEntry
                {
                    File = file,
                    Sha256 = await GetSha256Async(installerPath, token).ConfigureAwait(false),
                    PackageId = sidecar?.PackageId ?? sourceEntry?.PackageId
                        ?? NormalizePackageId(System.IO.Path.GetFileNameWithoutExtension(file)),
                    Version = LanInstallerMetadataReader.NormalizeVersion(sidecar?.Version ?? sourceEntry?.Version
                        ?? LanInstallerMetadataReader.Read(installerPath).Version) ?? "unknown",
                    InstallerType = sidecar?.InstallerType ?? sourceEntry?.InstallerType ?? detected.ModuleType,
                    SilentArgs = sidecar?.SilentArgs ?? sourceEntry?.SilentArgs,
                    SilentSource = sidecar?.SilentSource ?? sourceEntry?.SilentSource ?? "server",
                    Scope = sourceEntry?.Scope,
                    AdditionalData = sourceEntry?.AdditionalData
                };
                _ = LanInstallerValidation.ValidateInstaller(installer);
                installers.Add(installer);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new IOException($"Servidor LAN: falha ao indexar o instalador '{installerPath}': {exception.Message}", exception);
            }
        }

        foreach (LanInstallerEntry sourceEntry in source.Installers)
            if (!seenFiles.Contains(sourceEntry.File))
            {
                string missingPath = System.IO.Path.Combine(root, sourceEntry.File);
                throw new FileNotFoundException($"Servidor LAN: o instalador declarado não existe: '{missingPath}'.", missingPath);
            }

        var module = new LanInstallersModuleDocument { Installers = installers };
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(module, CoreJsonContext.Default.LanInstallersModuleDocument);
        return new(Name, Version, Path, content);
    }

    public async Task<IReadOnlyCollection<string>> GetServedFilesAsync(string rootDirectory, CancellationToken token)
    {
        string root = System.IO.Path.GetFullPath(rootDirectory);
        LanModulePayload payload = await BuildAsync(root, token).ConfigureAwait(false);
        LanInstallersModuleDocument module = LanInstallerValidation.DeserializeModule(payload.Content);
        return module.Installers.Select(installer => installer.File).ToArray();
    }

    private static async Task<LanInstallersModuleDocument> LoadSourceModuleAsync(string root, CancellationToken token)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A pasta compartilhada não pode ser um link ou ponto de redirecionamento.");
        string metadataPath = System.IO.Path.Combine(root, "installers.json");
        LanInstallersModuleDocument module;
        if (File.Exists(metadataPath))
        {
            if ((File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("O arquivo installers.json não pode ser um link ou ponto de redirecionamento.");
            if (new FileInfo(metadataPath).Length > MaximumMetadataBytes)
                throw new InvalidDataException($"O arquivo '{metadataPath}' excede 16 MiB.");
            byte[] metadata;
            try
            {
                await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
                metadata = buffer.ToArray();
            }
            catch (IOException exception)
            {
                throw new IOException($"Servidor LAN: falha ao ler o arquivo de índice '{metadataPath}': {exception.Message}", exception);
            }
            module = LanInstallerValidation.DeserializeModule(metadata, serverSource: true);
        }
        else
        {
            module = new LanInstallersModuleDocument();
        }

        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LanInstallerEntry installer in module.Installers)
        {
            _ = LanInstallerValidation.ValidateInstaller(installer);
            if (!InstallerExtensions.Contains(System.IO.Path.GetExtension(installer.File)))
                throw new InvalidDataException($"Extensão de instalador não suportada: {installer.File}.");
            if (!seenFiles.Add(installer.File))
                throw new InvalidDataException($"O módulo installers contém o arquivo duplicado {installer.File}.");
        }
        return module;
    }

    private static async Task<LanInstallerSidecar?> LoadSidecarAsync(string installerPath, CancellationToken token)
    {
        string sidecarPath = installerPath + ".json";
        if (!File.Exists(sidecarPath))
            return null;
        if ((File.GetAttributes(sidecarPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"O arquivo de metadados '{sidecarPath}' não pode ser um link ou ponto de redirecionamento.");
        if (new FileInfo(sidecarPath).Length > MaximumSidecarBytes)
            throw new InvalidDataException($"O arquivo de metadados '{sidecarPath}' excede 64 KiB.");
        try
        {
            await using var stream = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            LanInstallerSidecar? sidecar = await JsonSerializer.DeserializeAsync(stream, CoreJsonContext.Default.LanInstallerSidecar, token)
                .ConfigureAwait(false);
            return sidecar ?? throw new InvalidDataException($"O arquivo de metadados '{sidecarPath}' está vazio.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"O arquivo de metadados '{sidecarPath}' contém JSON inválido.", exception);
        }
        catch (IOException exception)
        {
            throw new IOException($"Servidor LAN: falha ao ler o arquivo de metadados '{sidecarPath}': {exception.Message}", exception);
        }
    }

    public static string? ResolveSafeFilePath(string rootDirectory, string requestedName)
    {
        if (!LanInstallerValidation.IsSafeFileName(requestedName))
            return null;
        string root = System.IO.Path.GetFullPath(rootDirectory);
        string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, requestedName));
        string prefix = root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? root
            : root + System.IO.Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    public void Invalidate(string? fileName = null)
    {
        if (fileName is null) _hashes.Clear();
        else _hashes.TryRemove(System.IO.Path.GetFullPath(fileName), out _);
    }

    private async Task<string> GetSha256Async(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        string fullPath = info.FullName;
        await _hashGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_hashes.TryGetValue(fullPath, out CachedHash? cached)
                && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
                return cached.Sha256;

            try
            {
                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                _hashes[fullPath] = new CachedHash(info.Length, info.LastWriteTimeUtc, hash);
                return hash;
            }
            catch (IOException exception)
            {
                throw new IOException($"Servidor LAN: falha ao calcular SHA-256 do arquivo '{fullPath}': {exception.Message}", exception);
            }
        }
        finally { _hashGate.Release(); }
    }

    private static string NormalizePackageId(string stem)
    {
        string value = new(stem.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '.').ToArray());
        value = value.Trim('.', '-', '_');
        return value.Length > 0 && char.IsAsciiLetterOrDigit(value[0]) ? value : "Unknown.Package";
    }

    private sealed record CachedHash(long Length, DateTime LastWriteUtc, string Sha256);
}

public sealed class LanInstallersClientModuleHandler : IClientModuleHandler
{
    public string Name => "installers";
    public int SupportedMajorVersion => 1;
    public string ExpectedPath => "/v1/installers.json";

    public LanInstallersModuleDocument DeserializeAndValidate(ReadOnlySpan<byte> content) =>
        LanInstallerValidation.DeserializeModule(content);
}
