using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;

namespace WinProvision.Cli;

public sealed class ResilientCliBackend : ICliPackageBackend, IAsyncDisposable
{
    private readonly WingetBootstrapper _bootstrapper = new();
    private readonly WingetExecutor _executor = new();
    private readonly WingetCliBackend _cli = new();
    private readonly WinProvisionApiService _api = new();
    private readonly StoreService _catalog = new();
    private readonly WinGetService _packages;
    private readonly Action<string> _diagnostic;
    private readonly object _provisionGate = new();
    private Task<WingetBootstrapResult>? _provisionTask;

    public ResilientCliBackend(Action<string> diagnostic)
    {
        _diagnostic = diagnostic;
        _packages = new(_executor, _api, _bootstrapper, provision: ProvisionAsync);
    }

    private Task<WingetBootstrapResult> ProvisionAsync(Action<string>? log, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_provisionGate)
            return _provisionTask ??= _bootstrapper.EnsureWingetAsync(_diagnostic, token);
    }

    private async Task EnsureAsync(CancellationToken token)
    {
        var result = await ProvisionAsync(_diagnostic, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsUsable) _diagnostic("WinGet indisponível; tentando os recursos do catálogo próprio.");
        if (result.Status == WingetBootstrapStatus.Bootstrapped && Interlocked.Exchange(ref _provisionReset, 1) == 0)
            WinGetFactoryHelper.ResetAfterProvisioning("provisionamento da CLI");
    }
    private int _provisionReset;

    public async Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token)
    {
        await EnsureAsync(token);
        var result = await CliQueryFallback.RunAsync<List<UpgradablePackage>>(
        [
            async ct => (await _packages.SearchComOnlyAsync(request.Id ?? request.Query, ct))
                .Where(p => Matches(p.PackageId, p.Name, p.Source, request))
                .Select(p => new UpgradablePackage { Id = p.PackageId, Name = p.Name, AvailableVersion = p.Version, Source = p.Source }).ToList(),
            async ct => (await _catalog.LoadCatalogAsync(cancellationToken: ct))
                .Where(p => Matches(p.Id, p.Name, p.Source, request))
                .Select(p => new UpgradablePackage { Id = p.Id, Name = p.Name, AvailableVersion = p.Version, Source = p.Source }).ToList(),
            ct => _cli.SearchAsync(request, ct)
        ], items => items.Count > 0, _diagnostic, token).ConfigureAwait(false);
        return result;
    }

    private static bool Matches(string id, string name, string source, InteractiveCliRequest request)
    {
        if (request.Source is not null && !source.Equals(request.Source, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Id is not null) return id.Equals(request.Id, StringComparison.OrdinalIgnoreCase);
        return request.Exact ? id.Equals(request.Query, StringComparison.OrdinalIgnoreCase) || name.Equals(request.Query, StringComparison.OrdinalIgnoreCase)
            : id.Contains(request.Query, StringComparison.OrdinalIgnoreCase) || name.Contains(request.Query, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token)
    {
        await EnsureAsync(token);
        return await CliQueryFallback.RunAsync<List<UpgradablePackage>>(
            [ct => _packages.ListComOnlyAsync(source, ct), ct => _cli.ListAsync(source, ct)], _ => true, _diagnostic, token);
    }
    public async Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token)
    {
        await EnsureAsync(token);
        return await _packages.GetUpgradablePackagesAsync(log, token);
    }
    public async Task<string> DetailsAsync(string id, string source, CancellationToken token)
    {
        await EnsureAsync(token);
        return await CliQueryFallback.RunAsync<string>([
            async ct => {
                var match = (await _packages.SearchComOnlyAsync(id, ct)).FirstOrDefault(p => p.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase) && p.Source == source)
                    ?? throw new NotSupportedException("Pacote não encontrado pela COM.");
                return $"{match.Name}\nID: {match.PackageId}\nVersão: {match.Version}\nEditor: {match.Publisher}\nOrigem: {match.Source}";
            },
            async ct => {
                var manifest = await _api.GetPackageAsync(id, ct, source) ?? throw new NotSupportedException("Pacote ausente do catálogo próprio.");
                return $"ID: {manifest.Id}\nVersão: {manifest.Version}\nOrigem: {manifest.Source}\nInstaladores: {manifest.Installers.Count}";
            },
            ct => _cli.DetailsAsync(id, source, ct)
        ], _ => true, _diagnostic, token);
    }
    public async Task<WingetExecutionResult> ExecuteAsync(bool install, UpgradablePackage package, Action<string> log, CancellationToken token)
    {
        await EnsureAsync(token);
        // This shared service owns fallback and the installer-started guard. Never retry its result here.
        return install ? await _packages.InstallAsync(package.Id, log, token, source: package.Source)
            : await _packages.UpdateAsync(package.Id, log, token, source: package.Source);
    }
    public async Task<WingetExecutionResult> DownloadAsync(UpgradablePackage package, string? directory, Action<string> log, CancellationToken token)
    {
        await EnsureAsync(token);
        PackageManifest? manifest;
        try { manifest = await _api.GetPackageAsync(package.Id, token, package.Source); }
        catch (Exception exception) when (exception is HttpRequestException or IOException && exception is not InvalidDataException)
        { log("Catálogo próprio indisponível; tentando download pelo WinGet."); manifest = null; }
        if (manifest is not null && package.Source != "msstore" && WinProvisionApiService.PickInstaller(manifest) is { } installer)
        {
            string folder = Path.GetFullPath(directory ?? WinProvision.Core.Services.Office.OfficeCliDownloadService.GetDownloadsFolder());
            Directory.CreateDirectory(folder);
            string extension = Path.GetExtension(new Uri(installer.Url).AbsolutePath);
            if (extension.Length > 12 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
            string file = Path.Combine(folder, $"{SafeFileName(package.Id)}-{SafeFileName(manifest.Version)}{extension}");
            await _api.DownloadVerifiedInstallerAsync(installer, file, ct: token);
            log($"Arquivo verificado: {file}");
            return new() { Success = true, ExitCode = 0, Output = file };
        }
        return await _cli.DownloadAsync(package, directory, log, token);
    }
    private static string SafeFileName(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
    public async Task<WingetExecutionResult> ExportAsync(string path, Action<string> log, CancellationToken token)
    {
        await EnsureAsync(token);
        return await _cli.ExportAsync(path, log, token);
    }
    public async Task<WingetExecutionResult> UninstallAsync(string id, string? source, Action<string> log, CancellationToken token)
    {
        await EnsureAsync(token);
        var comResult = await _packages.UninstallComOnlyAsync(id, source, token);
        if (comResult is not null) return comResult;
        var installed = new InstalledAppsService();
        await installed.EnsureLoadedAsync(token);
        var match = installed.GetAllInstalledApps().FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (match is not null && source is not "msstore")
        {
            var ownResult = await _api.UninstallAsync(match, new UninstallerEngineService(), log, token);
            if (ownResult is bool success) return new() { Success = success, ExitCode = success ? 0 : 1, InstallerStarted = true };
        }
        return await _cli.UninstallAsync(id, source, log, token);
    }
    public async Task<string> OfficeAsync(bool offline, Action<string> log, CancellationToken token)
    {
        // Office downloads use the verified catalog and do not require WinGet.
        return await _cli.OfficeAsync(offline, log, token);
    }
    public async Task<WingetExecutionResult> InstallOfficeAsync(Action<string> log, CancellationToken token)
    {
        await EnsureAsync(token);
        return await _cli.InstallOfficeAsync(log, token);
    }
    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _catalog.DisposeAsync();
    }
}
