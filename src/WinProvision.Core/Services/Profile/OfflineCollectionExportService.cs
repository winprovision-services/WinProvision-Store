using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Profile;

[SupportedOSPlatform("windows")]
public sealed class OfflineCollectionExportService(IOfflineCollectionSource source, IOfflineOfficeSource? officeSource = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;

    public async Task<OfflineCollectionResult> ExportAsync(string name, IReadOnlyList<OfflineCollectionRequest> requests,
        string destination, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException("Selecione ao menos um aplicativo.", nameof(requests));
        destination = Path.GetFullPath(destination);
        if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Escolha um arquivo ZIP.", nameof(destination));
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".winprovision-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var packages = new List<OfflineCollectionPackage>();
        var skipped = new List<OfflineCollectionSkipped>();
        try
        {
            OfflineCollectionRequest[] officeRequests = requests.Where(OfflineOfficeModuleBuilder.IsOffice).ToArray();
            OfflineOfficeModule? office = null;
            foreach (OfflineCollectionRequest request in requests)
            {
                if (OfflineOfficeModuleBuilder.IsOffice(request)) continue;
                string stage = "consultar o catálogo";
                token.ThrowIfCancellationRequested();
                progress?.Report(new($"Preparando {packages.Count + skipped.Count + 1} de {requests.Count}: {request.Name}…"));
                try
                {
                    string? reason = UnsupportedOptions(request);
                    if (reason is not null) { skipped.Add(new(request.Id, request.Name, reason)); continue; }
                    PackageManifest? manifest = await source.GetManifestAsync(request.Id, token).ConfigureAwait(false);
                    if (manifest is null || manifest.Id != request.Id || !manifest.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                    { skipped.Add(new(request.Id, request.Name, "Manifesto WinGet não disponível.")); continue; }
                    Architecture architecture = ParseArchitecture(request.Options?.Architecture);
                    CatalogInstaller? installer = WinProvisionApiService.PickInstaller(manifest, architecture, request.Options?.Scope);
                    reason = ValidateInstaller(installer, request.Options);
                    if (reason is not null) { skipped.Add(new(request.Id, request.Name, reason)); continue; }
                    stage = "baixar e verificar o instalador";
                    packages.Add(await PrepareAsync(staging, packages.Count + 1, request, manifest, installer!, progress, token).ConfigureAwait(false));
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException)
                {
                    CoreLog.Warning(exception, "OfflineCollection.Prepare");
                    token.ThrowIfCancellationRequested();
                    skipped.Add(new(request.Id, request.Name, DescribeFailure(exception, stage)));
                }
            }
            if (officeRequests.Length > 0)
            {
                if (officeSource is null)
                    skipped.AddRange(officeRequests.Select(request => new OfflineCollectionSkipped(request.Id, request.Name, "O serviço de exportação do Office não está disponível.")));
                else
                    office = await OfflineOfficeModuleBuilder.PrepareAsync(staging, officeRequests, officeSource, progress, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (packages.Count == 0 && office is null)
                throw new InvalidOperationException("Nenhum aplicativo pode ser exportado:\n" + string.Join("\n", skipped.Select(item => $"{item.Name}: {item.Reason}")));
            var manifestData = new OfflineCollectionManifest(1, name, DateTimeOffset.UtcNow, packages, skipped, office);
            string manifestPath = OfflineCollectionLayout.ManifestPath(packages.Count > 0);
            await File.WriteAllTextAsync(Path.Combine(staging, manifestPath), WinProvision.Core.Services.AotJson.Serialize(manifestData, JsonOptions), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, OfflineCollectionLayout.ScriptFile), GetInstallationScript(), new UTF8Encoding(true), token).ConfigureAwait(false);
            string report = BuildReadme(manifestData);
            if (office is not null)
                report += Environment.NewLine + "OFFICE\n" + OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.ScriptFile) + " também pode ser executado sozinho e solicita elevação pelo UAC quando necessário.\n" +
                    (office.IncludesPayload ? "O ZIP inclui os arquivos do Office, com versão fixa e SHA256 obrigatório. A instalação usa a fonte local; a ativação segue as regras Microsoft.\n" : "O Office deste ZIP está no modo online e exige internet no destino.\n") +
                    "O bootstrap executa setup.exe /configure configuration.xml e aguarda o ODT finalizar.\n" +
                    "Logs: %TEMP%\\WinProvision-Log.log e resultado-office.json.\n" +
                    "No SetupComplete, a instalação é silenciosa e roda como SYSTEM.\n";
            await File.WriteAllTextAsync(Path.Combine(staging, OfflineCollectionLayout.ReadmeFile), report, new UTF8Encoding(true), token).ConfigureAwait(false);
            progress?.Report(new("Criando o ZIP com os instaladores verificados…"));
            await AtomicFile.WriteAsync(destination, async (output, cancellation) =>
            {
                using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                // Somente arquivos aprovados; um pacote que falhou não deixa conteúdo no ZIP.
                IEnumerable<string> files = packages.SelectMany(package => package.Files.Select(file => file.Path))
                    .Concat([manifestPath, OfflineCollectionLayout.ScriptFile, OfflineCollectionLayout.ReadmeFile]);
                if (office is not null) files = files.Concat(office.Files.Select(file => file.Path));
                foreach (string relative in files)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);
                    await using Stream target = entry.Open();
                    await using var input = new FileStream(Path.Combine(staging, relative), FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(target, cancellation).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
            return new(destination, packages, skipped, office);
        }
        finally
        {
            // Caminho absoluto criado por esta operação, diretamente no destino escolhido.
            if (Path.GetDirectoryName(staging) != parent || !Path.GetFileName(staging).StartsWith(".winprovision-offline-", StringComparison.Ordinal))
                throw new IOException("Diretório temporário inválido.");
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { CoreLog.Warning(exception, "OfflineCollection.Cleanup"); }
        }
    }

    internal static string DescribeFailure(Exception exception, string stage)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvalidDataException)
                return $"Falha ao {stage}: {current.Message}";
            if (current is HttpRequestException { StatusCode: { } status })
                return $"Falha ao {stage}: o servidor respondeu HTTP {(int)status} ({status}).";
            if (current is OperationCanceledException or TimeoutException)
                return $"Falha ao {stage}: o serviço excedeu o tempo limite.";
        }
        return exception is IOException
            ? $"Falha ao {stage}: {exception.Message}"
            : $"Falha ao {stage}: não foi possível conectar ao serviço.";
    }

    private static string? UnsupportedOptions(OfflineCollectionRequest request)
    {
        if (!string.IsNullOrEmpty(request.Source) && !request.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            return "Esta origem não fornece um instalador exportável pelo catálogo WinGet.";
        if (request.Options is { } hookOptions
            && (!string.IsNullOrWhiteSpace(hookOptions.PreInstallScript)
                || !string.IsNullOrWhiteSpace(hookOptions.PostInstallScript)))
            return "Scripts pré/pós-instalação são locais e não podem ser incluídos no pacote offline.";
        if (request.Options is { } options && (options.Interactive || options.IgnoreSecurityHash
            || !string.IsNullOrWhiteSpace(options.CustomArguments) || !string.IsNullOrWhiteSpace(options.InstallLocation)
            || !string.IsNullOrWhiteSpace(options.Version)))
            return "As opções personalizadas exigem WinGet; restaure as opções padrão para exportar silenciosamente.";
        return null;
    }

    private static Architecture ParseArchitecture(string? architecture) => architecture?.ToLowerInvariant() switch
    {
        null or "" => RuntimeInformation.OSArchitecture,
        "x64" => Architecture.X64, "x86" => Architecture.X86, "arm64" => Architecture.Arm64,
        _ => throw new InvalidDataException("Arquitetura de exportação inválida.")
    };

    private static string? ValidateInstaller(CatalogInstaller? installer, PackageInstallOptions? options)
    {
        if (installer is null) return "Não há instalador compatível com a arquitetura escolhida.";
        if (!string.IsNullOrWhiteSpace(options?.Architecture)
            && !string.Equals(installer.Architecture, options.Architecture, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(installer.Architecture, "neutral", StringComparison.OrdinalIgnoreCase))
            return "Não há instalador para a arquitetura definida nas opções do pacote.";
        if (!string.IsNullOrWhiteSpace(options?.Scope) && !string.Equals(installer.Scope, options.Scope, StringComparison.OrdinalIgnoreCase))
            return "Não há instalador para o escopo definido nas opções do pacote.";
        string? type = installer.IsZipWithNestedInstaller ? installer.NestedType : installer.Type;
        if (type?.ToLowerInvariant() is not ("msi" or "wix" or "wixmsi" or "burn" or "nullsoft" or "inno" or "exe"))
            return "Formato de instalador não suportado para exportação silenciosa.";
        if (!installer.SilentSupported || string.IsNullOrWhiteSpace(installer.SilentArgs)
            || installer.SilentArgs.Any(char.IsControl) || installer.SilentArgs.Contains('<') || installer.SilentArgs.Contains('>'))
            return "O manifesto não fornece parâmetros silenciosos resolvidos para este instalador.";
        if (!Uri.TryCreate(installer.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || installer.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            return "O instalador não possui URL HTTPS e SHA-256 válidos.";
        return null;
    }

    private async Task<OfflineCollectionPackage> PrepareAsync(string staging, int index, OfflineCollectionRequest request,
        PackageManifest manifest, CatalogInstaller installer, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
    {
        // Uma preparação anterior deste índice pode ter falhado; use pasta única por tentativa.
        string folder = $"{OfflineCollectionLayout.InstallersFolder}/{index:D4}-{Guid.NewGuid():N}";
        string directory = Path.Combine(staging, folder);
        Directory.CreateDirectory(directory);
        string type = (installer.IsZipWithNestedInstaller ? installer.NestedType : installer.Type)!.ToLowerInvariant();
        string extension = type is "msi" or "wix" or "wixmsi" ? ".msi" : ".exe";
        string downloaded = Path.Combine(directory, installer.IsZipWithNestedInstaller ? "payload.zip" : "setup" + extension);
        await source.DownloadAsync(installer, downloaded, value => progress?.Report(new($"Baixando {request.Name}…", value.Percent)), token).ConfigureAwait(false);
        string originalHash = await HashAsync(downloaded, token).ConfigureAwait(false);
        if (!originalHash.Equals(installer.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 diferente do manifesto.");
        string runnable = downloaded;
        if (installer.IsZipWithNestedInstaller)
        {
            string content = Path.Combine(directory, "conteudo");
            Directory.CreateDirectory(content);
            await ExtractAsync(downloaded, content, token).ConfigureAwait(false);
            runnable = SafePath(content, installer.NestedInstallerFile!);
            if (!File.Exists(runnable)) throw new InvalidDataException("O instalador declarado não está dentro do ZIP.");
            File.Delete(downloaded);
        }
        var files = new List<OfflineCollectionFile>();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            files.Add(new(Path.GetRelativePath(staging, file).Replace('\\', '/'), await HashAsync(file, token).ConfigureAwait(false)));
        return new(request.Id, request.Name, manifest.Version, installer.Architecture ?? "neutral", installer.Scope ?? "",
            type, Path.GetRelativePath(staging, runnable).Replace('\\', '/'), installer.SilentArgs!, installer.SilentSource,
            originalHash, request.Options?.RequiresElevation == true || installer.Scope?.Equals("machine", StringComparison.OrdinalIgnoreCase) == true,
            new[] { 0, 3010, 1641 }.Concat(installer.SuccessCodes).Distinct().ToArray(), files);
    }

    private static async Task ExtractAsync(string zip, string destination, CancellationToken token)
    {
        using ZipArchive archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > 10_000) throw new InvalidDataException("O ZIP contém arquivos demais.");
        long bytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("O ZIP contém um link de arquivo.");
            string path = SafePath(destination, entry.FullName.TrimEnd('/', '\\'));
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(path); continue; }
            bytes = checked(bytes + entry.Length);
            if (bytes > MaximumExpandedBytes) throw new InvalidDataException("O conteúdo extraído excedeu 4 GB.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using Stream input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("O arquivo extraído excedeu o tamanho declarado.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (output.Length != entry.Length) throw new InvalidDataException("Tamanho de arquivo ZIP divergente.");
        }
    }

    internal static string SafePath(string root, string relative)
    {
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') || IsReservedWindowsName(part)))
            throw new InvalidDataException("O ZIP contém um caminho inválido.");
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O arquivo sai do diretório do ZIP.");
        return path;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        string stem = segment.Split('.')[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)) return true;
        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3';
        return false;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
    }

    public static string GetInstallationScript()
    {
        using Stream stream = typeof(OfflineCollectionExportService).Assembly.GetManifestResourceStream("WinProvision.Core.OfflineInstall.ps1")
            ?? throw new InvalidOperationException("Script de instalação não disponível.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd()
            .Replace("__INSTALLERS_MANIFEST__", OfflineCollectionLayout.ManifestPath(true), StringComparison.Ordinal)
            .Replace("__OFFICE_MANIFEST__", OfflineCollectionLayout.ManifestPath(false), StringComparison.Ordinal)
            .Replace("__OFFICE_SETUP__", OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.SetupFile), StringComparison.Ordinal)
            .Replace("__OFFICE_CONFIGURATION__", OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.ConfigurationFile), StringComparison.Ordinal)
            .Replace("__OFFICE_SCRIPT__", OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.ScriptFile), StringComparison.Ordinal)
            .Replace("__OFFICE_PAYLOAD_INDEX__", OfflineOfficeConfiguration.Relative(Office.OfficeOfflinePayload.IndexFileName), StringComparison.Ordinal)
            .Replace("__OFFICE_PAYLOAD_PREFIX__", OfflineOfficeConfiguration.Relative("Office/Data/"), StringComparison.Ordinal);
    }

    private static string BuildReadme(OfflineCollectionManifest manifest) => $"""
        {manifest.Name} — Instaladores para uso offline

        Extraia TODO o ZIP para uma pasta local. Abra o PowerShell nessa pasta:
          .\Install.ps1 -VerifyOnly
          .\Install.ps1
        Abra o PowerShell como administrador quando o script indicar que é necessário.
        O script verifica todos os hashes antes de iniciar qualquer instalador, aguarda cada instalação
        e registra os resultados em resultado-instalacao.json. Falhas interrompem a sequência.
        Código de sucesso 3010 solicita reinício; 1641 interrompe a sequência para reiniciar.
        O script não baixa arquivos e não depende de WinGet, Fido, aria2 ou do WinProvision.
        Arquitetura, versão, escopo e argumentos estão registrados em {OfflineCollectionLayout.ManifestPath(manifest.Packages.Count > 0)}.
        O SHA-256 dos instaladores foi comparado com o catálogo durante a exportação.
        Alguns instaladores são web installers ou exigem runtimes/dependências, ativação ou outros
        componentes online. A presença do instalador NÃO garante uma instalação totalmente offline.
        Dependências não são baixadas automaticamente. As opções personalizadas incompatíveis são relatadas.
        Mantenha o ZIP de uma fonte confiável: os hashes detectam corrupção, não uma edição simultânea dos arquivos e do manifesto.

        Incluídos: {manifest.Packages.Count}
        Não incluídos: {manifest.Skipped.Count}
        {string.Join(Environment.NewLine, manifest.Skipped.Select(item => $"- {item.Name} ({item.Id}): {item.Reason}"))}
        """;
}
