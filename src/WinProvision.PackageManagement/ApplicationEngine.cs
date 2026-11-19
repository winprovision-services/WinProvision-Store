using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

public sealed class ApplicationEngine : IAsyncDisposable
{
    private readonly WingetBootstrapper _bootstrapper;
    private readonly WingetExecutor _executor;
    private readonly WinProvisionApiService _api;
    private readonly WinGetService _packages;
    private readonly Action<string> _diagnostic;
    private readonly OperationPreferencesService _operationPreferences;
    private readonly bool _ownsServices;
    private readonly Func<WinGetEnginePreference> _preferredEngine;

    public ApplicationEngine(Action<string> diagnostic, OperationPreferencesService? operationPreferences = null,
        Func<WinGetEnginePreference>? preferredEngine = null)
    {
        _diagnostic = diagnostic;
        _operationPreferences = operationPreferences ?? new OperationPreferencesService();
        _bootstrapper = new();
        _executor = new(_bootstrapper, _operationPreferences);
        _api = new(operationPreferences: _operationPreferences);
        _ownsServices = true;
        _preferredEngine = preferredEngine ?? (() => WinGetEnginePreference.WingetCli);
        _packages = new(_executor, _bootstrapper, preferredEngine: _preferredEngine, diagnostic: diagnostic);
    }

    public ApplicationEngine(WinGetService packages, WingetExecutor executor, WinProvisionApiService api,
        WingetBootstrapper bootstrapper, Action<string> diagnostic,
    OperationPreferencesService? operationPreferences = null,
    Func<WinGetEnginePreference>? preferredEngine = null)
    {
    _packages = packages; _executor = executor; _api = api; _bootstrapper = bootstrapper; _diagnostic = diagnostic;
    _preferredEngine = preferredEngine ?? (() => WinGetEnginePreference.WingetCli);
    _operationPreferences = operationPreferences ?? new OperationPreferencesService();
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

    private async Task EnsureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await _bootstrapper.EnsureOnceAsync(_diagnostic, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.IsUsable) _diagnostic("WinGet indisponível para a operação solicitada.");
    }

    public async Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token)
    {
        string query = request.Id ?? request.Query;
        return (await _packages.SearchAsync(query, token).ConfigureAwait(false))
            .Where(p => Matches(p.PackageId, p.Name, p.Source, request))
            .Select(p => new UpgradablePackage { Id = p.PackageId, Name = p.Name,
                AvailableVersion = p.Version, Source = p.Source, Publisher = p.Publisher }).ToList();
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
        return await _packages.ListInstalledAsync(source, token).ConfigureAwait(false);
    }
    public async Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token)
    {
        return await _packages.GetUpgradablePackagesAsync(log, token);
    }
    public Task<List<UpgradablePackage>> ScanUpdatesAsync(Action<string>? log, CancellationToken token, bool includeMicrosoftStore = true) =>
        _packages.GetUpgradablePackagesAsync(log, token, includeMicrosoftStore);

    public Task<WingetExecutionResult> DownloadInstallerAsync(
        string id,
        string? source,
        string directory,
        Action<string>? log,
        CancellationToken token) =>
        _executor.DownloadInstallerAsync(id, directory, source ?? "winget", onLogReceived: log, cancellationToken: token);

    public async Task<string> DetailsAsync(string id, string source, CancellationToken token)
    {
        var match = (await _packages.SearchAsync(id, token).ConfigureAwait(false))
            .FirstOrDefault(p => p.PackageId.Equals(id, StringComparison.OrdinalIgnoreCase)
                && p.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new NotSupportedException("Pacote não encontrado pelo WinGet.");
        return $"{match.Name}\nID: {match.PackageId}\nVersão: {match.Version}\nEditor: {match.Publisher}\nOrigem: {match.Source}";
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
        bool requiresElevation = false,
        PackageInstallOptions? options = null)
    {
        return await _packages.UninstallAsync(id, source, log, token, interactive,
            requiresElevation, options).ConfigureAwait(false);
    }

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
            using var service = new WinProvision.Core.Services.Office.OfficeDeploymentToolService();
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
        }
    }
}
