using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;
namespace WinProvision.Cli;
public sealed class ResilientCliBackend(Action<string> diagnostic, Func<PackageInstallMethod>? preferredMethod = null, Func<string, string?, PackageInstallOptions?>? updateOptions = null) : ICliPackageBackend, IAsyncDisposable
{
    private readonly ApplicationEngine _engine = new(diagnostic, preferredMethod);
    public Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token) => _engine.SearchAsync(request, token);
    public Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token) => _engine.ListAsync(source, token);
    public Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token) => _engine.UpdatesAsync(log, token);
    public Task<string> DetailsAsync(string id, string source, CancellationToken token) => _engine.DetailsAsync(id, source, token);
    public Task<WingetExecutionResult> ExecuteAsync(bool install, UpgradablePackage package, Action<string> log, CancellationToken token) => !install && updateOptions?.Invoke(package.Id, package.Source) is { } options
        ? _engine.ExecuteUpdateAsync(package, options, log, token) : _engine.ExecuteAsync(install, package, log, token);
    public Task<WingetExecutionResult> DownloadAsync(UpgradablePackage package, string? directory, Action<string> log, CancellationToken token) => _engine.DownloadAsync(package, directory, log, token);
    public Task<WingetExecutionResult> ExportAsync(string path, Action<string> log, CancellationToken token) => _engine.ExportAsync(path, log, token);
    public Task<WingetExecutionResult> UninstallAsync(string id, string? source, Action<string> log, CancellationToken token) => _engine.UninstallAsync(id, source, log, token);
    public Task<string> OfficeAsync(bool offline, Action<string> log, CancellationToken token) => _engine.OfficeAsync(offline, log, token);
    public Task<WingetExecutionResult> InstallOfficeAsync(Action<string> log, CancellationToken token) => _engine.InstallOfficeAsync(log, token);
    public Task<WingetExecutionResult> ExecuteProfileAsync(ProfileAppRef app, Action<string> log, CancellationToken token) => _engine.ExecuteProfileAsync(app, log, token);
    public Task<WingetExecutionResult> DownloadProfileAsync(ProfileAppRef app, string? directory, Action<string> log, CancellationToken token) => _engine.DownloadProfileAsync(app, directory, log, token);
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
