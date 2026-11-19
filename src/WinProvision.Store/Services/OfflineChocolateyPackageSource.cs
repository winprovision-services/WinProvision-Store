using WinProvision.Core.Models;
using WinProvision.Core.Services.Profile;

namespace WinProvision.Store.Services;

public sealed class OfflineChocolateyPackageSource(ChocolateyService chocolatey) : IOfflineChocolateyPackageSource
{
    public Task DownloadPackageAsync(string id, string version, string destination,
        Action<InstallProgressUpdate>? progress, CancellationToken token) =>
        chocolatey.DownloadPackageAsync(id, version, destination, progress, token);
}
