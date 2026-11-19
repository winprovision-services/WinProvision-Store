using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Profile;

[SupportedOSPlatform("windows")]
public sealed class OfflineCollectionExportService(IOfflineCollectionSource source, IOfflineOfficeSource? officeSource = null,
    IOfflineChocolateyPackageSource? chocolateySource = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const long MaximumExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const string ReportFile = "export-report.json";

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
        var elevationPreferences = new ElevationPreferencesService();
        bool allowManualArguments = elevationPreferences.GetBoolean("allowManualSilentArguments", defaultValue: true);
        bool allowExternalArguments = elevationPreferences.GetBoolean("allowImportedSilentArguments", defaultValue: true);
        var packages = new List<OfflineCollectionPackage>();
        var chocolateyPackages = new List<OfflineChocolateyPackage>();
        var skipped = new List<OfflineCollectionSkipped>();
        var reportItems = new List<OfflineCollectionReportItem>(requests.Count);
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
                    if (string.Equals(request.Source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFailure(reportItems, skipped, request, stage, "Pacotes da Microsoft Store não fornecem instaladores exportáveis para ZIP.");
                        continue;
                    }
                    if (IsChocolatey(request.Source))
                    {
                        string? chocolateyReason = UnsupportedChocolateyOptions(request);
                        if (chocolateyReason is not null)
                        { AddFailure(reportItems, skipped, request, stage, chocolateyReason); continue; }
                        if (chocolateySource is null)
                        { AddFailure(reportItems, skipped, request, stage, "O serviço de download do pacote Chocolatey não está disponível."); continue; }
                        string version = request.Options?.Version?.Trim() ?? string.Empty;
                        string packagePath = $"ChocolateyPackages/{packages.Count + chocolateyPackages.Count + 1:D4}.nupkg";
                        string packageDestination = Path.Combine(staging, packagePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(packageDestination)!);
                        stage = "baixar e verificar o NUPKG Chocolatey";
                        await chocolateySource.DownloadPackageAsync(request.Id.Trim(), version, packageDestination,
                            value => progress?.Report(new($"Baixando pacote Chocolatey {request.Name}…", value.Percent)), token).ConfigureAwait(false);
                        (string packageId, string packageVersion) = ValidateChocolateyPackage(packageDestination, request.Id, version);
                        chocolateyPackages.Add(new(packageId, request.Name, packageVersion,
                            packagePath, await HashAsync(packageDestination, token).ConfigureAwait(false),
                            request.Options?.Interactive == true,
                            string.Equals(request.Options?.Architecture, "x86", StringComparison.OrdinalIgnoreCase),
                            request.Options?.IgnoreSecurityHash == true));
                        reportItems.Add(new(request.Id, request.Name, packageVersion, "Incluído", stage));
                        continue;
                    }
                    string? reason = UnsupportedOptions(request);
                    if (reason is not null) { AddFailure(reportItems, skipped, request, stage, reason); continue; }
                    PackageManifest? manifest = await source.GetManifestAsync(request.Id, token).ConfigureAwait(false);
                    if (manifest is null || !string.Equals(manifest.Id, request.Id, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(manifest.Source, "winget", StringComparison.OrdinalIgnoreCase))
                    { AddFailure(reportItems, skipped, request, stage, "Manifesto WinGet não disponível."); continue; }
                    CatalogInstaller? installer = SelectInstaller(manifest, request.Options, out reason);
                    if (reason is not null) { AddFailure(reportItems, skipped, request, stage, reason); continue; }
                    stage = "baixar e verificar o instalador";
                    (OfflineCollectionPackage package, OfflineCollectionReportItem reportItem) =
                        await PrepareAsync(staging, packages.Count + 1, request, manifest, installer!, progress, token,
                            allowManualArguments, allowExternalArguments).ConfigureAwait(false);
                    packages.Add(package);
                    reportItems.Add(reportItem);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException
                    or InvalidDataException or JsonException or TimeoutException or OperationCanceledException
                    or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    CoreLog.Warning(exception, "OfflineCollection.Prepare");
                    token.ThrowIfCancellationRequested();
                    AddFailure(reportItems, skipped, request, stage, DescribeFailure(exception, stage));
                }
            }

            if (officeRequests.Length > 0)
            {
                if (officeSource is null)
                {
                    foreach (OfflineCollectionRequest request in officeRequests)
                        AddFailure(reportItems, skipped, request, "preparar o Office", "O serviço de exportação do Office não está disponível.");
                }
                else
                {
                    try { office = await OfflineOfficeModuleBuilder.PrepareAsync(staging, officeRequests, officeSource, progress, token).ConfigureAwait(false); }
                    catch (OperationCanceledException exception) when (token.IsCancellationRequested || exception is Office.OfficeElevationCanceledException) { throw; }
                    catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException
                        or JsonException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
                    {
                        CoreLog.Warning(exception, "OfflineCollection.Office");
                        string reason = DescribeFailure(exception, "preparar o Office");
                        foreach (OfflineCollectionRequest request in officeRequests)
                            AddFailure(reportItems, skipped, request, "preparar o Office", reason);
                    }
                }
                if (office is not null)
                    foreach (OfflineCollectionRequest request in officeRequests)
                        reportItems.Add(new(request.Id, request.Name, request.Office?.ProductId, "Incluído", "preparar o Office"));
            }
            token.ThrowIfCancellationRequested();
            if (packages.Count == 0 && chocolateyPackages.Count == 0 && office is null)
                return new(null, packages, skipped, office, chocolateyPackages, reportItems);
            var exportReport = new OfflineCollectionReport(1, DateTimeOffset.UtcNow, reportItems);
            var manifestData = new OfflineCollectionManifest(1, name, DateTimeOffset.UtcNow, packages, skipped, office,
                chocolateyPackages.Count == 0 ? null : chocolateyPackages, exportReport);
            string manifestPath = OfflineCollectionLayout.ManifestPath(packages.Count > 0 || chocolateyPackages.Count > 0);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, manifestPath))!);
            await File.WriteAllTextAsync(Path.Combine(staging, manifestPath), WinProvision.Core.Services.AotJson.Serialize(manifestData, JsonOptions), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, OfflineCollectionLayout.ScriptFile), GetInstallationScript(), new UTF8Encoding(true), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, ReportFile), AotJson.Serialize(exportReport, JsonOptions), token).ConfigureAwait(false);
            string report = BuildReadme(manifestData);
            if (chocolateyPackages.Count > 0)
                report += Environment.NewLine + "CHOCOLATEY\n" +
                    "O ZIP inclui os arquivos NUPKG selecionados e instala esses pacotes pela fonte local do choco.exe no computador de destino. Chocolatey deve estar instalado. " +
                    "Dependências ausentes do ZIP e payloads de instaladores externos ainda podem exigir conexão com a internet. Execute o script em um PowerShell elevado.\n";
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
                    .Concat([manifestPath, OfflineCollectionLayout.ScriptFile, OfflineCollectionLayout.ReadmeFile, ReportFile]);
                files = files.Concat(chocolateyPackages.Select(package => package.PackagePath));
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
            return new(destination, packages, skipped, office, chocolateyPackages, reportItems);
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

    public async Task<OfflineCollectionResult> RetryFailedAsync(string name,
        IReadOnlyList<OfflineCollectionRequest> requests, string destination, OfflineCollectionResult previous,
        IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.FilePath is null || !File.Exists(previous.FilePath))
            return await ExportAsync(name, requests, destination, progress, token).ConfigureAwait(false);

        HashSet<string> failedIds = (previous.ReportItems ?? [])
            .Where(item => item.Status == "Falhou")
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        OfflineCollectionRequest[] retryRequests = requests.Where(request => failedIds.Contains(request.Id)).ToArray();
        if (retryRequests.Length == 0)
            return previous;

        string parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".winprovision-offline-retry-" + Guid.NewGuid().ToString("N"));
        string retryZip = Path.Combine(parent, ".winprovision-offline-retry-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractArchiveAsync(previous.FilePath, staging, token).ConfigureAwait(false);
            OfflineCollectionResult retried = await ExportAsync(name, retryRequests, retryZip, progress, token).ConfigureAwait(false);
            if (retried.FilePath is not null)
                await CopyRetriedPayloadsAsync(retryZip, staging, retried, token).ConfigureAwait(false);

            var reportById = (previous.ReportItems ?? [])
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (OfflineCollectionReportItem item in retried.ReportItems ?? [])
                reportById[item.Id] = item;
            OfflineCollectionReportItem[] mergedReportItems = reportById.Values
                .OrderBy(item => requests.ToList().FindIndex(request => request.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            OfflineCollectionSkipped[] mergedSkipped = (previous.Skipped ?? [])
                .Where(item => !retryRequests.Any(request => request.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
                .Concat(retried.Skipped)
                .ToArray();
            OfflineCollectionPackage[] mergedPackages = previous.Packages.Concat(retried.Packages).ToArray();
            OfflineChocolateyPackage[] mergedChocolatey = (previous.ChocolateyPackages ?? [])
                .Concat(retried.ChocolateyPackages ?? []).ToArray();
            OfflineOfficeModule? mergedOffice = retried.Office ?? previous.Office;
            var report = new OfflineCollectionReport(1, DateTimeOffset.UtcNow, mergedReportItems);
            var manifest = new OfflineCollectionManifest(1, name, DateTimeOffset.UtcNow, mergedPackages,
                mergedSkipped, mergedOffice, mergedChocolatey.Length == 0 ? null : mergedChocolatey, report);
            string manifestPath = OfflineCollectionLayout.ManifestPath(mergedPackages.Length > 0 || mergedChocolatey.Length > 0);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, manifestPath))!);
            await File.WriteAllTextAsync(Path.Combine(staging, manifestPath), AotJson.Serialize(manifest, JsonOptions), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, OfflineCollectionLayout.ScriptFile), GetInstallationScript(),
                new UTF8Encoding(true), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, ReportFile), AotJson.Serialize(report, JsonOptions), token).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, OfflineCollectionLayout.ReadmeFile), BuildReadme(manifest),
                new UTF8Encoding(true), token).ConfigureAwait(false);

            progress?.Report(new("Atualizando o ZIP com os itens verificados…"));
            await WriteArchiveAsync(staging, destination, token).ConfigureAwait(false);
            return new(destination, mergedPackages, mergedSkipped, mergedOffice,
                mergedChocolatey.Length == 0 ? null : mergedChocolatey, mergedReportItems);
        }
        finally
        {
            TryDeleteDirectory(staging, "OfflineCollection.RetryCleanup");
            try { File.Delete(retryZip); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { CoreLog.Warning(exception, "OfflineCollection.RetryZipCleanup"); }
        }
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destination, CancellationToken token)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 10_000)
            throw new InvalidDataException("O ZIP anterior contém arquivos demais para uma retentativa.");
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string relative = entry.FullName.TrimEnd('/', '\\');
            if (relative.Length == 0)
                continue;
            string path = SafePath(destination, relative);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using Stream input = entry.Open();
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        }
    }

    private static async Task CopyRetriedPayloadsAsync(string retryZip, string destination,
        OfflineCollectionResult retried, CancellationToken token)
    {
        var files = retried.Packages.SelectMany(package => package.Files.Select(file => file.Path))
            .Concat(retried.ChocolateyPackages?.Select(package => package.PackagePath) ?? [])
            .Concat(retried.Office?.Files.Select(file => file.Path) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using ZipArchive archive = ZipFile.OpenRead(retryZip);
        foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => files.Contains(entry.FullName)))
        {
            token.ThrowIfCancellationRequested();
            string target = SafePath(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using Stream input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, token).ConfigureAwait(false);
            files.Remove(entry.FullName);
        }
        if (files.Count > 0)
            throw new InvalidDataException("A retentativa não contém todos os instaladores informados no relatório.");
    }

    private static async Task WriteArchiveAsync(string root, string destination, CancellationToken token)
    {
        await AtomicFile.WriteAsync(destination, async (output, cancellation) =>
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);
                await using Stream target = entry.Open();
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(target, cancellation).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
    }

    private static void TryDeleteDirectory(string path, string operation)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { CoreLog.Warning(exception, operation); }
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
            || !string.IsNullOrWhiteSpace(options.InstallLocation)
            || !string.IsNullOrWhiteSpace(options.Version)))
            return "As opções personalizadas exigem WinGet; restaure as opções padrão para exportar silenciosamente.";
        return null;
    }

    private static bool IsChocolatey(string? source) =>
        string.Equals(source?.Trim(), "chocolatey", StringComparison.OrdinalIgnoreCase);

    private static string? UnsupportedChocolateyOptions(OfflineCollectionRequest request)
    {
        string id = request.Id.Trim();
        if (id.Length is 0 or > 128 || !char.IsAsciiLetterOrDigit(id[0])
            || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            return "O identificador não é válido para instalação Chocolatey.";
        PackageInstallOptions? options = request.Options;
        if (options is null || string.IsNullOrWhiteSpace(options.Version))
            return "Fixe uma versão do pacote Chocolatey antes de exportar o ZIP.";
        if (!string.IsNullOrWhiteSpace(options.PreInstallScript) || !string.IsNullOrWhiteSpace(options.PostInstallScript)
            || !string.IsNullOrWhiteSpace(options.CustomArguments) || !string.IsNullOrWhiteSpace(options.InstallLocation)
            || !string.IsNullOrWhiteSpace(options.Scope) || options.RequiresElevation)
            return "Esta opção personalizada não pode ser representada com segurança no script Chocolatey do ZIP.";
        if (!string.IsNullOrWhiteSpace(options.Architecture)
            && !options.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase))
            return "Chocolatey aceita somente a opção de arquitetura x86 nesta exportação.";
        if (!string.IsNullOrWhiteSpace(options.Version))
        {
            string version = options.Version.Trim();
            if (version.Length > 128 || !char.IsAsciiLetterOrDigit(version[0])
                || version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not '+'))
                return "A versão informada não é válida para Chocolatey.";
        }
        return null;
    }

    private static (string Id, string Version) ValidateChocolateyPackage(string path, string expectedId, string expectedVersion)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10_000) throw new InvalidDataException("O NUPKG Chocolatey contém entradas demais.");
        ZipArchiveEntry[] nuspecs = archive.Entries.Where(entry => Path.GetExtension(entry.FullName).Equals(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nuspecs.Length != 1) throw new InvalidDataException("O NUPKG Chocolatey precisa conter exatamente um arquivo nuspec.");
        using Stream stream = nuspecs[0].Open();
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
        { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_048_576 });
        System.Xml.Linq.XDocument document = System.Xml.Linq.XDocument.Load(reader);
        System.Xml.Linq.XElement? metadata = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
        string id = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim() ?? string.Empty;
        string version = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "version")?.Value.Trim() ?? string.Empty;
        if (!id.Equals(expectedId, StringComparison.OrdinalIgnoreCase) || !version.Equals(expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O NUPKG baixado não corresponde ao ID e à versão selecionados.");
        return (id, version);
    }

    private static Architecture? ParseArchitecture(string? architecture) => architecture?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "x64" => Architecture.X64, "x86" => Architecture.X86, "arm64" => Architecture.Arm64,
        _ => throw new InvalidDataException("Arquitetura de exportação inválida.")
    };

    internal static CatalogInstaller? SelectInstaller(PackageManifest manifest, PackageInstallOptions? options, out string? reason)
    {
        Architecture? architecture = ParseArchitecture(options?.Architecture);
        var candidates = manifest.Installers.Where(installer => ValidateInstaller(installer, options) is null).ToList();
        var compatible = new PackageManifest { Installers = candidates };
        CatalogInstaller? selected = WinProvisionApiService.PickInstaller(compatible, architecture, options?.Scope);
        if (selected is not null)
        {
            reason = null;
            return selected;
        }
        var original = WinProvisionApiService.PickInstaller(manifest, architecture, options?.Scope);
        reason = ValidateInstaller(original, options) ?? "Não há instalador exportável para a arquitetura e o escopo escolhidos.";
        return null;
    }

    private static string? ValidateInstaller(CatalogInstaller? installer, PackageInstallOptions? options)
    {
        if (installer is null) return "Não há instalador compatível com a arquitetura escolhida.";
        if (installer.DownloadCommandProhibited) return "O fornecedor não permite o download separado deste instalador.";
        if (!string.IsNullOrWhiteSpace(options?.Architecture)
            && !string.Equals(installer.Architecture, options.Architecture, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(installer.Architecture, "neutral", StringComparison.OrdinalIgnoreCase))
            return "Não há instalador para a arquitetura definida nas opções do pacote.";
        if (!string.IsNullOrWhiteSpace(options?.Scope) && !string.Equals(installer.Scope, options.Scope, StringComparison.OrdinalIgnoreCase))
            return "Não há instalador para o escopo definido nas opções do pacote.";
        string? type = installer.IsZipWithNestedInstaller ? installer.NestedType : installer.Type;
        string extension = Uri.TryCreate(installer.Url, UriKind.Absolute, out Uri? installerUri)
            ? Path.GetExtension(installerUri.AbsolutePath).ToLowerInvariant()
            : "";
        if (!installer.IsZipWithNestedInstaller
            && type?.ToLowerInvariant() is "msix" or "appx" or "msixbundle" or "appxbundle" or "portable" or "font" or "zip")
            return "Formato de instalador não suportado para exportação offline.";
        if (installer.IsZipWithNestedInstaller)
        {
            if (extension != ".zip")
                return "O ZIP não declara um instalador interno compatível.";
        }
        else if (extension is not (".exe" or ".msi"))
            return "Formato de instalador não suportado para exportação offline.";
        if (!Uri.TryCreate(installer.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
            || installer.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
            return "O instalador não possui URL HTTPS e SHA-256 válidos.";
        return null;
    }

    private async Task<(OfflineCollectionPackage Package, OfflineCollectionReportItem Report)> PrepareAsync(string staging, int index, OfflineCollectionRequest request,
        PackageManifest manifest, CatalogInstaller installer, IProgress<OfflineCollectionProgress>? progress, CancellationToken token,
        bool allowManualArguments, bool allowExternalArguments)
    {
        // Uma preparação anterior deste índice pode ter falhado; use pasta única por tentativa.
        string folder = $"{OfflineCollectionLayout.InstallersFolder}/{index:D4}-{Guid.NewGuid():N}";
        string directory = Path.Combine(staging, folder);
        Directory.CreateDirectory(directory);
        string type = (installer.IsZipWithNestedInstaller ? installer.NestedType : installer.Type)?.ToLowerInvariant() ?? "exe";
        string extension = Path.GetExtension(new Uri(installer.Url).AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = type is "msi" or "wix" or "wixmsi" ? ".msi" : ".exe";
        string downloaded = Path.Combine(directory, installer.IsZipWithNestedInstaller ? "payload.zip" : "setup" + extension);
        await source.DownloadAsync(installer, downloaded, value => progress?.Report(new($"Baixando {request.Name}…", value.Percent)), token).ConfigureAwait(false);
        long downloadedBytes = new FileInfo(downloaded).Length;
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
        LanInstallerDetection detected = LanInstallerTypeDetector.Detect(runnable);
        string? manifestSilent = installer.InstallerSwitches.GetValueOrDefault("Silent");
        string? manifestSilentWithProgress = installer.InstallerSwitches.GetValueOrDefault("SilentWithProgress");
        if (manifestSilent is null && manifestSilentWithProgress is null
            && !string.Equals(installer.SilentSource, "type-default", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(installer.SilentSource, "detected", StringComparison.OrdinalIgnoreCase))
            manifestSilent = installer.SilentArgs;
        CoreLog.Information("OfflineCollection.ManifestInstaller",
            $"packageId={request.Id}; InstallerType={type}; InstallerSwitches.Silent={manifestSilent ?? "(ausente)"}; " +
            $"InstallerSwitches.SilentWithProgress={manifestSilentWithProgress ?? "(ausente)"}; Scope={installer.Scope ?? "(ausente)"}; " +
            $"Architecture={installer.Architecture ?? "(ausente)"}");
        InstallerSilentArgumentResolution resolution = InstallerSilentArgumentsResolver.Resolve(
            request.Options?.CustomArguments, manifestSilent, manifestSilentWithProgress, type, detected.ModuleType,
            providedSource: InstallerSilentArgumentsResolver.ParseSourceName(installer.SilentSource),
            allowManualArguments: allowManualArguments,
            allowExternalArguments: allowExternalArguments);
        CoreLog.Information("OfflineCollection.SilentArguments",
            $"packageId={request.Id}; resolutionPath=InstallerSilentArgumentsResolver.Resolve; source={resolution.Source}; " +
            $"InstallerTypeDefault={InstallerSilentArgumentsResolver.DefaultInstallArguments(type) ?? "(indisponível)"}; " +
            $"detectedType={(detected.Kind == LanInstallerKind.Unknown ? "(não detectado)" : detected.ModuleType)}; " +
            $"DetectedTypeDefault={InstallerSilentArgumentsResolver.DefaultInstallArguments(detected.ModuleType) ?? "(indisponível)"}; " +
            $"result={(resolution.Arguments is null ? resolution.RejectionReason ?? "sem argumento silencioso compatível" : "resolvido")}");
        if (resolution.RejectionReason is not null)
            throw new InvalidDataException(resolution.RejectionReason);
        bool requiresInternet = InstallerSilentArgumentsResolver.RequiresInternet(
            installer, downloadedBytes, request.ExpectedInstallerSizeBytes);
        string savedType = detected.Kind != LanInstallerKind.Unknown
            ? detected.ModuleType
            : type is "msi" or "wix" or "wixmsi" or "burn" or "nullsoft" or "inno" or "exe"
                ? type
                : Path.GetExtension(runnable).Equals(".msi", StringComparison.OrdinalIgnoreCase) ? "msi" : "exe";
        string? silentArguments = resolution.Arguments;
        var files = new List<OfflineCollectionFile>();
        string sidecarPath = runnable + ".json";
        var sidecar = new LanInstallerSidecar
        {
            PackageId = request.Id,
            Version = manifest.Version,
            InstallerType = savedType,
            SilentArgs = silentArguments,
            SilentSource = InstallerSilentArgumentsResolver.SourceName(resolution.Source)
        };
        await File.WriteAllTextAsync(sidecarPath, AotJson.Serialize(sidecar, JsonOptions), token).ConfigureAwait(false);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            files.Add(new(Path.GetRelativePath(staging, file).Replace('\\', '/'), await HashAsync(file, token).ConfigureAwait(false)));
        string installerPath = Path.GetRelativePath(staging, runnable).Replace('\\', '/');
        var package = new OfflineCollectionPackage(request.Id, request.Name, manifest.Version, installer.Architecture ?? "neutral", installer.Scope ?? "",
            savedType, installerPath, silentArguments ?? "", InstallerSilentArgumentsResolver.SourceName(resolution.Source),
            originalHash, request.Options?.RequiresElevation == true || installer.Scope?.Equals("machine", StringComparison.OrdinalIgnoreCase) == true,
            new[] { 0, 3010, 1641 }.Concat(installer.SuccessCodes).Distinct().ToArray(), files);
        string status = resolution.Arguments is null ? "Incluído com aviso" : "Incluído";
        var reasons = new List<string>();
        if (resolution.Arguments is null)
            reasons.Add("Sem suporte silencioso; informe argumentos antes de instalar.");
        if (requiresInternet)
        {
            status = "Incluído com aviso";
            reasons.Add(InstallerSilentArgumentsResolver.InternetRequiredMessage);
        }
        return (package, new(request.Id, request.Name, manifest.Version, status,
            "baixar e verificar o instalador", reasons.Count == 0 ? null : string.Join(" ", reasons), requiresInternet));
    }

    private static void AddFailure(
        ICollection<OfflineCollectionReportItem> report,
        ICollection<OfflineCollectionSkipped> skipped,
        OfflineCollectionRequest request,
        string stage,
        string reason)
    {
        skipped.Add(new(request.Id, request.Name, reason, stage));
        report.Add(new(request.Id, request.Name, request.Options?.Version, "Falhou", stage, reason));
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
        {(manifest.ChocolateyPackages?.Count > 0 ? "Os instaladores WinGet usam os arquivos locais e não dependem de WinGet, Fido, aria2 ou do WinProvision; os pacotes Chocolatey usam a internet no destino." : "O script não baixa arquivos e não depende de WinGet, Fido, aria2 ou do WinProvision.")}
        Arquitetura, versão, escopo e argumentos estão registrados em {OfflineCollectionLayout.ManifestPath(manifest.Packages.Count > 0 || (manifest.ChocolateyPackages?.Count ?? 0) > 0)}.
        {(manifest.Packages.Count > 0 ? "O SHA-256 dos instaladores WinGet foi comparado com o catálogo durante a exportação." : "")}
        {(manifest.ChocolateyPackages?.Count > 0 ? "O SHA-256 dos NUPKGs Chocolatey foi registrado no manifesto e é verificado pelo script antes da instalação." : "")}
        Alguns instaladores são web installers ou exigem runtimes/dependências, ativação ou outros
        componentes online. A presença do instalador NÃO garante uma instalação totalmente offline.
        {(manifest.ChocolateyPackages?.Count > 0 ? "Dependências Chocolatey são resolvidas online pelo CLI; dependências dos instaladores WinGet não são baixadas automaticamente." : "Dependências não são baixadas automaticamente.")} As opções personalizadas incompatíveis são relatadas.
        Mantenha o ZIP de uma fonte confiável: os hashes detectam corrupção, não uma edição simultânea dos arquivos e do manifesto.

        Relatório por item: {ReportFile}
        Incluídos: {manifest.Report?.Items.Count(item => item.Status == "Incluído") ?? manifest.Packages.Count + (manifest.ChocolateyPackages?.Count ?? 0)}
        Incluídos com aviso: {manifest.Report?.Items.Count(item => item.Status == "Incluído com aviso") ?? 0}
        Falharam: {manifest.Report?.Items.Count(item => item.Status == "Falhou") ?? manifest.Skipped.Count}
        {string.Join(Environment.NewLine, manifest.Report?.Items.Select(item => $"- {item.Status}: {item.Name} ({item.Id}) — etapa: {item.Stage}{(item.Reason is null ? "" : "; " + item.Reason)}") ?? [])}
        """;
}
