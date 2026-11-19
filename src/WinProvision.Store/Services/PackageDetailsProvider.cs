using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public interface IPackageDetailsProvider
{
    Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token);
    PackageManagerDescriptor GetManager(AppEntry app);
    PackageDetailsSnapshot CreateSnapshot(AppEntry app);
    Task<PackageDetailsSnapshot> LoadDetailsAsync(AppEntry app, CancellationToken token);
    Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token);
    Task<IReadOnlyList<CatalogInstaller>> LoadVersionInstallersAsync(AppEntry app, string? version, CancellationToken token);
    string BuildCommandPreview(AppEntry app, PackageInstallOptions options, string operation);
}

public sealed class PackageDetailsProvider(IEnumerable<IPackageDetailsAdapter> adapters, InstalledAppsService installed) : IPackageDetailsProvider
{
    private readonly IPackageDetailsAdapter[] _adapters = adapters.ToArray();

    public async Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        AppEntry details = app;
        await installed.EnsureLoadedAsync(token);
        token.ThrowIfCancellationRequested();
        installed.ApplyInstallationStatus(details);
        return details;
    }

    public async Task<PackageDetailsSnapshot> LoadDetailsAsync(AppEntry app, CancellationToken token)
    {
        IPackageDetailsAdapter adapter = GetAdapter(app);
        PackageDetailsRecord package = await adapter.LoadAsync(app, token).ConfigureAwait(false);
        return new PackageDetailsSnapshot(package, adapter.Manager);
    }

    public PackageDetailsSnapshot CreateSnapshot(AppEntry app)
    {
        IPackageDetailsAdapter adapter = GetAdapter(app);
        return new PackageDetailsSnapshot(adapter.CreateRecord(app), adapter.Manager);
    }

    public PackageManagerDescriptor GetManager(AppEntry app) => GetAdapter(app).Manager;

    public Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token) => GetAdapter(app).LoadVersionsAsync(app, token);

    public Task<IReadOnlyList<CatalogInstaller>> LoadVersionInstallersAsync(AppEntry app, string? version, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<CatalogInstaller>>([]);

    public string BuildCommandPreview(AppEntry app, PackageInstallOptions options, string operation) =>
        GetAdapter(app).BuildCommandPreview(app, options, operation);

    private IPackageDetailsAdapter GetAdapter(AppEntry app) => _adapters.FirstOrDefault(adapter => adapter.CanHandle(app))
        ?? throw new InvalidOperationException("Nenhum adaptador de detalhes está registrado para este pacote.");
}
