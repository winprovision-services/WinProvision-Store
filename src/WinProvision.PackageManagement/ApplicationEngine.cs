using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

public sealed class ApplicationEngine : IAsyncDisposable
{
    private readonly WingetBootstrapper _bootstrapper;
    private readonly WingetExecutor _executor;
    private readonly WinProvisionApiService _api;
    private readonly IStoreCatalog _catalog;
    private readonly WinGetService _packages;
    private readonly PackageUninstallService _uninstall;
    private readonly Action<string> _diagnostic;
    private readonly OperationPreferencesService _operationPreferences;
    private readonly bool _ownsServices;
    private readonly object _provisionGate = new();
    private Task<WingetBootstrapResult>? _provisionTask;

    public ApplicationEngine(Action<string> diagnostic, Func<PackageInstallMethod>? preferredMethod = null,
        Func<PackageUpdateMethod>? preferredUpdateMethod = null, OperationPreferencesService? operationPreferences = null)
    {
        _diagnostic = diagnostic;
        _operationPreferences = operationPreferences ?? new OperationPreferencesService();
        _bootstrapper = new();
        _executor = new(_bootstrapper, _operationPreferences);
        _api = new(operationPreferences: _operationPreferences);
        _catalog = new StoreService();
        _uninstall = new(_executor,
            new UninstallerEngineService(), _api, new InstalledAppsService());
        _ownsServices = true;
        _packages = new(_executor, _api, _bootstrapper, preferredMethod,
            provision: ProvisionAsync, diagnostic: diagnostic, preferredUpdateMethod: preferredUpdateMethod);
    }

    public ApplicationEngine(WinGetService packages, WingetExecutor executor, WinProvisionApiService api,
        WingetBootstrapper bootstrapper, IStoreCatalog catalog, Action<string> diagnostic,
    OperationPreferencesService? operationPreferences = null,
    PackageUninstallService? uninstall = null)
    {
    _packages = packages; _executor = executor; _api = api; _bootstrapper = bootstrapper; _catalog = catalog; _diagnostic = diagnostic;
    _operationPreferences = operationPreferences ?? new OperationPreferencesService();
    _uninstall = uninstall ?? new PackageUninstallService(
        executor, new UninstallerEngineService(), api, new InstalledAppsService());
    }

    public Task<WingetExecutionResult> InstallPreferredAsync(string id, Action<string>? log = null, CancellationToken token = default,
        PackageInstallOptions? options = null, Action<InstallProgressUpdate>? onProgress = null, string source = "winget")
        => _packages.InstallPreferredAsync(id, log, token, options, onProgress, source);
    public Task<WingetExecutionResult> UpdateAsync(string id, Action<string>? log = null, CancellationToken token = default,
        string source = "winget", Action<InstallProgressUpdate>? onProgress = null,
        UpgradablePackage? offeredPackage = null, PackageInstallOptions? updateOptions = null)
        => _packages.UpdateAsync(id, log, token, source, onProgress, offeredPackage, updateOptions);
    public Task<WingetExecutionResult> VerifySuccessfulUpgradeAsync(UpgradablePackage package,
        WingetExecutionResult result, CancellationToken token)
        => _packages.VerifySuccessfulUpgradeAsync(package, result, token);
    public Task PrepareAsync(CancellationToken token, bool includeStore, Action<string>? log)
        => _packages.PrepareAsync(token, includeStore, log);

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
        var result = await PackageQueryFallback.RunAsync<List<UpgradablePackage>>(
        [
            ct => SearchWinProvisionCatalogAsync(request, ct),
            async ct => (await _packages.SearchComOnlyAsync(request.Id ?? request.Query, ct))
                .Where(p => Matches(p.PackageId, p.Name, p.Source, request))
                .Select(p => new UpgradablePackage { Id = p.PackageId, Name = p.Name, AvailableVersion = p.Version, Source = p.Source }).ToList(),
            ct => _executor.SearchPackagesAsync(request.Id ?? request.Query, request.Source ?? "winget",
                request.Exact || request.Id is not null, request.Id is not null, ct)
        ], items => items.Count > 0, _diagnostic, token).ConfigureAwait(false);
        return result;
    }

    private async Task<List<UpgradablePackage>> SearchWinProvisionCatalogAsync(
        InteractiveCliRequest request, CancellationToken token)
    {
        await _catalog.LoadCatalogAsync(cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        string query = request.Id ?? request.Query;
        return _catalog.Search(query)
            .Where(app => MatchesWinProvisionCatalogEntry(app, request))
            .Select(app => new UpgradablePackage
            {
                Id = app.Id,
                Name = app.Name,
                AvailableVersion = app.Version,
                Source = app.Source
            })
            .ToList();
    }

    internal static bool MatchesWinProvisionCatalogEntry(AppEntry app, InteractiveCliRequest request)
    {
        if (request.Source is not null && !app.Source.Equals(request.Source, StringComparison.OrdinalIgnoreCase))
            return false;
        if (request.Id is not null)
            return app.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase);
        return !request.Exact
            || app.Id.Equals(request.Query, StringComparison.Ordinal)
            || app.Name.Equals(request.Query, StringComparison.Ordinal);
    }

    private static bool Matches(string id, string name, string source, InteractiveCliRequest request)
    {
        if (request.Source is not null && !source.Equals(request.Source, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Id is not null) return id.Equals(request.Id, StringComparison.OrdinalIgnoreCase);
        return request.Exact ? id.Equals(request.Query, StringComparison.Ordinal) || name.Equals(request.Query, StringComparison.Ordinal)
            : id.Contains(request.Query, StringComparison.OrdinalIgnoreCase) || name.Contains(request.Query, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token)
    {
        await EnsureAsync(token);
        return await PackageQueryFallback.RunAsync<List<UpgradablePackage>>(
            [ct => _packages.ListComOnlyAsync(source, ct), ct => _executor.ListInstalledPackagesAsync(ct, source)],
            _ => true, _diagnostic, token);
    }
    public async Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token)
    {
        return await _packages.GetUpgradablePackagesAsync(log, token);
    }
    public Task<List<UpgradablePackage>> ScanUpdatesAsync(Action<string>? log, CancellationToken token) =>
        _packages.GetUpgradablePackagesAsync(log, token);

    public Task<WingetExecutionResult> DownloadInstallerAsync(
        string id,
        string? source,
        string directory,
        Action<string>? log,
        CancellationToken token) =>
        _executor.DownloadInstallerAsync(id, directory, source ?? "winget", onLogReceived: log, cancellationToken: token);

    public async Task<string> DetailsAsync(string id, string source, CancellationToken token)
    {
        await EnsureAsync(token);
        return await PackageQueryFallback.RunAsync<string>([
            async ct => {
                var match = (await _packages.SearchComOnlyAsync(id, ct)).FirstOrDefault(p => p.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase) && p.Source == source)
                    ?? throw new NotSupportedException("Pacote não encontrado pela COM.");
                return $"{match.Name}\nID: {match.PackageId}\nVersão: {match.Version}\nEditor: {match.Publisher}\nOrigem: {match.Source}";
            },
            ct => _executor.GetPackageDetailsAsync(id, source, ct),
            async ct => {
                var manifest = await _api.GetPackageAsync(id, ct, source) ?? throw new NotSupportedException("Pacote ausente do catálogo próprio.");
                return $"ID: {manifest.Id}\nVersão: {manifest.Version}\nOrigem: {manifest.Source}\nInstaladores: {manifest.Installers.Count}";
            }
        ], _ => true, _diagnostic, token);
    }
    public async Task<WingetExecutionResult> ExecuteUpdateAsync(UpgradablePackage package, PackageInstallOptions options,
        Action<string> log, CancellationToken token)
    {
        return await _packages.UpdateAsync(package.Id, log, token, source: package.Source,
            offeredPackage: package, updateOptions: options);
    }

    public async Task<WingetExecutionResult> ExecuteAsync(bool install, UpgradablePackage package, Action<string> log, CancellationToken token)
    {
        // This shared service owns fallback and the installer-started guard. Never retry its result here.
        return install ? await _packages.InstallPreferredAsync(package.Id, log, token, source: package.Source)
            : await _packages.UpdateAsync(package.Id, log, token, source: package.Source,
                offeredPackage: package);
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
            string folder = InstallerDownloadPolicy.ResolveDirectory(directory, _operationPreferences);
            Directory.CreateDirectory(folder);
            string extension = Path.GetExtension(new Uri(installer.Url).AbsolutePath);
            if (extension.Length > 12 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
            string original = Path.GetFileName(new Uri(installer.Url).AbsolutePath);
            string name = InstallerDownloadPolicy.FileName(_operationPreferences.Load().InstallerNames, package.Id, manifest.Version, installer.Architecture, original);
            string staging = Path.Combine(folder, ".winprovision-" + Guid.NewGuid().ToString("N") + extension);
            string file;
            try
            {
                await _api.DownloadVerifiedInstallerAsync(installer, staging, ct: token);
                token.ThrowIfCancellationRequested();
                file = InstallerDownloadPolicy.Publish(staging, folder, name);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            log($"Arquivo verificado: {file}");
            return new() { Success = true, ExitCode = 0, Output = file };
        }
        return await _executor.DownloadPackageAsync(
            package.Id,
            package.Source,
            InstallerDownloadPolicy.ResolveDirectory(directory, _operationPreferences),
            log,
            token);
    }
    public async Task<WingetExecutionResult> ExportAsync(string path, Action<string> log, CancellationToken token)
    {
        await EnsureAsync(token);
        var packages = await ListAsync(null, token);
        var profile = new ProfileManifest { Name = "Aplicativos instalados", Apps = packages.Select(p => new ProfileAppRef { Id = p.Id, Name = p.Name, Source = p.Source }).ToList() };
        await File.WriteAllTextAsync(path, ProfileDocumentService.Serialize(profile), token);
        return new() { Success = true, ExitCode = 0, Output = path };
    }
    public async Task<WingetExecutionResult> UninstallAsync(
        string id,
        string? source,
        Action<string> log,
        CancellationToken token,
        bool interactive = false,
        bool requiresElevation = false)
    {
        await EnsureAsync(token);
        var comResult = await _packages.UninstallComOnlyAsync(id, source, token);
        if (comResult is { } com)
        {
            if (!CanFallbackAfterComUninstall(com)) return com;
            log("A COM falhou antes de iniciar o desinstalador; tentando a rota verificada do WinProvision.");
        }
        var match = await _uninstall.FindInstalledAsync(id, null, token);
        if (match is not null && CanUseWinProvisionUninstall(source))
            return await _uninstall.UninstallAsync(match, source, log, token, interactive, requiresElevation);
        return await _executor.UninstallExactPackageAsync(id, source, log, token);
    }

    internal static bool CanFallbackAfterComUninstall(WingetExecutionResult result) =>
        !result.Success
        && !result.InstallerStarted
        && result.FailureReason is not (WingetFailureReason.ElevationCanceled
            or WingetFailureReason.BlockedByPolicy
            or WingetFailureReason.OperationCanceled);

    internal static bool CanUseWinProvisionUninstall(string? source) =>
        !string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase);
    public Task<string> OfficeAsync(bool offline, Action<string> log, CancellationToken token)
    {
        var service = new WinProvision.Core.Services.Office.OfficeCliDownloadService(new WinProvision.Core.Services.Profile.OfflineOfficeSource(_api));
        return service.DownloadAsync(offline, WinProvision.Core.Services.Office.OfficeCliDownloadService.GetDownloadsFolder(), new OfficeProgress(log), token);
    }
    private sealed class OfficeProgress(Action<string> log) : IProgress<OfflineCollectionProgress>
    {
        public void Report(OfflineCollectionProgress value) => log(value.Message);
    }
    public Task<WingetExecutionResult> InstallOfficeAsync(Action<string> log, CancellationToken token)
        => ExecuteProfileAsync(new() { Id = "office.default", Source = "office", OfficeOptions = WinProvision.Core.Services.Office.OfficeCliDownloadService.DefaultRequest(false).Office }, log, token);
    public async Task<WingetExecutionResult> ExecuteProfileAsync(ProfileAppRef app, Action<string> log, CancellationToken token)
    {
        _ = ProfileDocumentService.CreateExecutionPlan(new() { Tabs = [new() { Apps = [app] }] });
        if (app.OfficeOptions is { } office)
        {
            using var service = new WinProvision.Core.Services.Office.OfficeDeploymentToolService(_executor, installWithPreferredFlowAsync: (id, output, ct) => _packages.InstallPreferredAsync(id, output, ct));
            var result = await service.RunConfigureDetailedAsync(WinProvision.Core.Services.Office.OfficeProfileConfiguration.Build(office), log, token);
            return new() { Success = result.Success, ExitCode = result.ExitCode, Output = result.Output, InstallerStarted = result.ProcessStarted };
        }
        await EnsureAsync(token);
        return app.PinnedVersion is { } version
            ? await _executor.InstallAppAsync(app.Id, log, token, source: app.Source!, version: version)
            : await _packages.InstallPreferredAsync(app.Id, log, token, source: app.Source!);
    }
    public async Task<WingetExecutionResult> DownloadProfileAsync(ProfileAppRef app, string? directory, Action<string> log, CancellationToken token)
    {
        _ = ProfileDocumentService.CreateExecutionPlan(new() { Tabs = [new() { Apps = [app] }] });
        if (app.OfficeOptions is { } office)
        {
            var service = new WinProvision.Core.Services.Office.OfficeCliDownloadService(new WinProvision.Core.Services.Profile.OfflineOfficeSource(_api));
            string folder = await service.DownloadAsync(new OfflineCollectionRequest(app.Id, app.Name ?? app.Id, "office", IsOffice: true, Office: office), directory ?? WinProvision.Core.Services.Office.OfficeCliDownloadService.GetDownloadsFolder(), null, token);
            return new() { Success = true, ExitCode = 0, Output = folder };
        }
        if (app.PinnedVersion is { } version)
        {
            await EnsureAsync(token);
            return await _executor.DownloadPackageAsync(app.Id, app.Source!, InstallerDownloadPolicy.ResolveDirectory(directory, _operationPreferences), log, token, version);
        }
        return await DownloadAsync(new() { Id = app.Id, Name = app.Name ?? app.Id, Source = app.Source! }, directory, log, token);
    }
    public async ValueTask DisposeAsync()
    {
        if (_ownsServices)
        {
            await _api.DisposeAsync();
            await _catalog.DisposeAsync();
        }
    }
}
