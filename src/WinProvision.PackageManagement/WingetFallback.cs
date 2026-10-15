using WinProvision.Core.Models;
using WinProvision.Core.Services;
namespace WinProvision.PackageManagement;
internal sealed class WingetFallback
{
    private readonly WingetExecutor _executor = new();
    public Task<WingetExecutionResult> DownloadAsync(UpgradablePackage package, string? directory, Action<string> log, CancellationToken token)
        => _executor.DownloadPackageAsync(package.Id, package.Source, directory, log, token);
    public Task<WingetExecutionResult> UninstallAsync(string id, string? source, Action<string> log, CancellationToken token)
        => _executor.UninstallExactPackageAsync(id, source, log, token);
    public Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token)
        => _executor.SearchPackagesAsync(request.Id ?? request.Query, request.Source ?? "winget",
            request.Exact || request.Id is not null, request.Id is not null, token);
    public Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token)
        => _executor.ListInstalledPackagesAsync(token, source);
    public Task<string> DetailsAsync(string id, string source, CancellationToken token)
        => _executor.GetPackageDetailsAsync(id, source, token);
}
