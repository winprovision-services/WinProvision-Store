using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record CacheSettingsSnapshot(CacheUsage Images, CacheUsage Catalog, CacheUsage Installers, int MemoryImages, long MemoryBytes, long MemoryBudget);
public interface ICacheSettingsService
{
    ImageCacheOptions Options { get; }
    Task<CacheSettingsSnapshot> MeasureAsync(CancellationToken token);
    Task SaveOptionsAsync(ImageCacheOptions options);
    Task ClearImagesAsync(CancellationToken token);
    Task ReleaseMemoryAsync(CancellationToken token);
    Task ClearCatalogAsync(CancellationToken token);
    Task ClearInstallersAsync(CancellationToken token);
}

public sealed class CacheSettingsService(CacheService cache) : ICacheSettingsService
{
    public ImageCacheOptions Options => new ImageCachePreferencesService().Options;
    public Task<CacheSettingsSnapshot> MeasureAsync(CancellationToken token)
    {
        var memory = AsyncImage.GetMemoryStatistics();
        return Task.Run(() =>
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = System.IO.Path.Combine(local, "WinProvisionStore");
            CacheUsage images = CacheStorageUsage.Measure(AsyncImage.ImageCacheDirectory, token: token)
                + CacheStorageUsage.Measure(System.IO.Path.Combine(root, "Cache", "Icons"), token: token);
            CacheUsage catalog = CacheStorageUsage.Measure(System.IO.Path.Combine(root, "ApiCache"), token: token)
                + CacheStorageUsage.Measure(System.IO.Path.Combine(root, "catalog-v2-details"), token: token)
                + CacheStorageUsage.Measure(System.IO.Path.Combine(root, "Cache"), "package-metrics.json", false, token);
            foreach (string pattern in new[] { "catalog-v2-*.json*", "msstore-catalog-*.json*", "screenshot-index.json*", "apps.json*" })
                catalog += CacheStorageUsage.Measure(root, pattern, false, token);
            CacheUsage installers = CacheStorageUsage.Measure(System.IO.Path.Combine(local, "WinProvision", "InstallerCache"), token: token);
            return new CacheSettingsSnapshot(images, catalog, installers, memory.Count, memory.Bytes, memory.BudgetBytes);
        }, token);
    }
    public Task SaveOptionsAsync(ImageCacheOptions options)
    {
        new ImageCachePreferencesService().Save(options);
        AsyncImage.ApplyDiskOptions(options);
        return Task.CompletedTask;
    }
    public async Task ClearImagesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await Task.Run(AsyncImage.ClearCache, token);
        await cache.ClearImagesAsync();
    }
    public Task ReleaseMemoryAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); AsyncImage.ClearMemoryCache(); return Task.CompletedTask; }
    public Task ClearCatalogAsync(CancellationToken token) => cache.ClearCatalogAsync(token);
    public Task ClearInstallersAsync(CancellationToken token) => cache.ClearInstallersAsync(token);
}
