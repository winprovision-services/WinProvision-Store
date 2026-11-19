using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record CacheSettingsSnapshot(CacheUsage Images, CacheUsage Catalog, CacheUsage Installers, int MemoryImages, long MemoryBytes, long MemoryBudget);
public interface ICacheSettingsService
{
    ImageCacheOptions Options { get; }
    string GetPreferenceValue(string storageKey);
    Task<CacheSettingsSnapshot> MeasureAsync(CancellationToken token);
    Task SaveOptionsAsync(ImageCacheOptions options);
    Task SetPreferenceValueAsync(string storageKey, string value);
    Task ClearImagesAsync(CancellationToken token);
    Task ReleaseMemoryAsync(CancellationToken token);
    Task ClearCatalogAsync(CancellationToken token);
    Task ClearInstallersAsync(CancellationToken token);
}

public sealed class CacheSettingsService(CacheService cache) : ICacheSettingsService
{
    public ImageCacheOptions Options => new ImageCachePreferencesService().Options;
    public string GetPreferenceValue(string storageKey) => storageKey switch
    {
        "MaxDiskMegabytes" => Options.MaxDiskMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "RevalidateAfterDays" => Options.RevalidateAfterDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(storageKey), storageKey, "Chave de preferência de cache desconhecida.")
    };

    public Task SetPreferenceValueAsync(string storageKey, string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            throw new ArgumentException("Valor de preferência de cache inválido.", nameof(value));

        ImageCacheOptions current = Options;
        ImageCacheOptions updated = storageKey switch
        {
            "MaxDiskMegabytes" => current with { MaxDiskMegabytes = parsed },
            "RevalidateAfterDays" => current with { RevalidateAfterDays = parsed },
            _ => throw new ArgumentOutOfRangeException(nameof(storageKey), storageKey, "Chave de preferência de cache desconhecida.")
        };
        return SaveOptionsAsync(updated);
    }

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
