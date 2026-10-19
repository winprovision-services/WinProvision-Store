using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal sealed class SmokeCacheSettings : ICacheSettingsService
{
    public ImageCacheOptions Options { get; private set; } = new();
    public CacheUsage Images { get; private set; } = new(32 * 1048576, 120);
    public CacheUsage Catalog { get; private set; } = new(12 * 1048576, 60);
    public CacheUsage Installers { get; private set; } = new(256 * 1048576, 3);
    public Task<CacheSettingsSnapshot> MeasureAsync(CancellationToken token) => Task.FromResult(new CacheSettingsSnapshot(Images, Catalog, Installers, 24, 3 * 1048576, 24 * 1048576));
    public Task SaveOptionsAsync(ImageCacheOptions options) { options.Validate(); Options = options; return Task.CompletedTask; }
    public Task ClearImagesAsync(CancellationToken token) { Images = new(); return Task.CompletedTask; }
    public Task ClearCatalogAsync(CancellationToken token) { Catalog = new(); return Task.CompletedTask; }
    public Task ClearInstallersAsync(CancellationToken token) { Installers = new(); return Task.CompletedTask; }
    public Task ReleaseMemoryAsync(CancellationToken token) => Task.CompletedTask;
    public void Reset() { Images = new(32 * 1048576, 120); Catalog = new(12 * 1048576, 60); Installers = new(256 * 1048576, 3); }
}
