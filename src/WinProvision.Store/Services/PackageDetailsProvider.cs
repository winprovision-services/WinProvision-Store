using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public interface IPackageDetailsProvider
{
    Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token);
    Task<WingetPackageDetailsService.ExtendedPackageInfo> LoadExtendedAsync(AppEntry app, CancellationToken token);
}

public sealed partial class PackageDetailsProvider(StoreService store, WingetPackageDetailsService metadata, InstalledAppsService installed) : IPackageDetailsProvider
{
    public async Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token)
    {
        var details = await store.LoadDetailsAsync(app, token);
        await installed.EnsureLoadedAsync(token);
        token.ThrowIfCancellationRequested();
        details.IsInstalled = installed.IsInstalled(details.Name, details.Id);
        return details;
    }
    public Task<WingetPackageDetailsService.ExtendedPackageInfo> LoadExtendedAsync(AppEntry app, CancellationToken token) => metadata.GetAsync(app, token);
}
