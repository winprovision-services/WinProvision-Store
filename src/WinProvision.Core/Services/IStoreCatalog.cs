using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public interface IStoreCatalog : IAsyncDisposable
{
    event Action<IReadOnlyList<AppEntry>>? CatalogUpdated;
    event Action? CacheCleared;
    DateTime? LastCatalogSyncUtc { get; }
    Task<List<AppEntry>> LoadCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    IReadOnlyList<AppEntry> GetAll();
    Task ReleaseCatalogMemoryAsync(CancellationToken cancellationToken = default);
    Task<AppEntry> LoadDetailsAsync(AppEntry app, CancellationToken cancellationToken = default);
    IEnumerable<AppEntry> Search(string query, int maxResults = int.MaxValue);
    Task<string[]> GetAvailableVersionsAsync(AppEntry summary, CancellationToken token = default);
    Task<IReadOnlyList<CatalogInstaller>> GetVersionInstallersAsync(AppEntry summary, string? version, CancellationToken token = default);
}
