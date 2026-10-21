using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public interface IPackageDetailsProvider
{
    Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token);
    Task<WingetPackageDetailsService.ExtendedPackageInfo> LoadExtendedAsync(AppEntry app, CancellationToken token);
    Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token);
    Task<IReadOnlyList<CatalogInstaller>> LoadVersionInstallersAsync(AppEntry app, string? version, CancellationToken token);
}

public sealed partial class PackageDetailsProvider(StoreService store, WingetPackageDetailsService metadata, InstalledAppsService installed) : IPackageDetailsProvider
{
    public async Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token)
    {
        var details = await store.LoadDetailsAsync(app, token);
        await installed.EnsureLoadedAsync(token);
        token.ThrowIfCancellationRequested();
        details.IsInstalled = details.IsSelf || installed.IsInstalled(details.Name, details.Id);
        return details;
    }
    public Task<WingetPackageDetailsService.ExtendedPackageInfo> LoadExtendedAsync(AppEntry app, CancellationToken token) => metadata.GetAsync(app, token);
    public Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token) => store.GetAvailableVersionsAsync(app, token);
    public Task<IReadOnlyList<CatalogInstaller>> LoadVersionInstallersAsync(AppEntry app, string? version, CancellationToken token)
        => store.GetVersionInstallersAsync(app, version, token);
}
