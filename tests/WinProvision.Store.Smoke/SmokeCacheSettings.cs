using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal sealed class SmokeCacheSettings : ICacheSettingsService
{
    public ImageCacheOptions Options { get; private set; } = new();
    public CacheUsage Images { get; private set; } = new(32 * 1048576, 120);
    public CacheUsage Catalog { get; private set; } = new(12 * 1048576, 60);
    public CacheUsage Installers { get; private set; } = new(256 * 1048576, 3);
    public bool FailNextSave { get; set; }
    public int MeasureCallCount { get; private set; }
    public Task<CacheSettingsSnapshot> MeasureAsync(CancellationToken token)
    {
        MeasureCallCount++;
        return Task.FromResult(new CacheSettingsSnapshot(Images, Catalog, Installers, 24, 3 * 1048576, 24 * 1048576));
    }
    public string GetPreferenceValue(string storageKey) => storageKey switch
    {
        "MaxDiskMegabytes" => Options.MaxDiskMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "RevalidateAfterDays" => Options.RevalidateAfterDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(storageKey))
    };
    public Task SaveOptionsAsync(ImageCacheOptions options)
    {
        options.Validate();
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new IOException("Falha de teste ao salvar opções.");
        }
        Options = options;
        return Task.CompletedTask;
    }
    public Task SetPreferenceValueAsync(string storageKey, string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            throw new ArgumentException("Valor inválido.", nameof(value));
        ImageCacheOptions updated = storageKey switch
        {
            "MaxDiskMegabytes" => Options with { MaxDiskMegabytes = parsed },
            "RevalidateAfterDays" => Options with { RevalidateAfterDays = parsed },
            _ => throw new ArgumentOutOfRangeException(nameof(storageKey))
        };
        return SaveOptionsAsync(updated);
    }
    public Task ClearImagesAsync(CancellationToken token) { Images = new(); return Task.CompletedTask; }
    public Task ClearCatalogAsync(CancellationToken token) { Catalog = new(); return Task.CompletedTask; }
    public Task ClearInstallersAsync(CancellationToken token) { Installers = new(); return Task.CompletedTask; }
    public Task ReleaseMemoryAsync(CancellationToken token) => Task.CompletedTask;
    public void Reset() { Options = new(); FailNextSave = false; Images = new(32 * 1048576, 120); Catalog = new(12 * 1048576, 60); Installers = new(256 * 1048576, 3); }
}
