using WinProvision.Core.Models;
using System.Runtime.Versioning;

namespace WinProvision.Core.Services.Profile;

public interface IOfflineCollectionSource
{
    Task<PackageManifest?> GetManifestAsync(string id, CancellationToken token);
    Task DownloadAsync(CatalogInstaller installer, string destination,
        Action<InstallProgressUpdate>? progress, CancellationToken token);
}

[SupportedOSPlatform("windows")]
public sealed class OfflineCollectionSource(WinProvisionApiService api) : IOfflineCollectionSource
{
    public Task<PackageManifest?> GetManifestAsync(string id, CancellationToken token) => api.GetPackageAsync(id, token, "winget");
    public Task DownloadAsync(CatalogInstaller installer, string destination,
        Action<InstallProgressUpdate>? progress, CancellationToken token) =>
        api.DownloadVerifiedInstallerAsync(installer, destination, progress, token);
}
