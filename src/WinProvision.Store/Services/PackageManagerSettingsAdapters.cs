using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

/// <summary>Adapta um gerenciador concreto ao contrato neutro usado pela tela de configurações.</summary>
public interface IPackageManagerSettingsAdapter
{
    PackageManagerDescriptor Definition { get; }
    bool Enabled { get; }
    Task<PackageManagerSettingsDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default);
    Task CheckAsync(CancellationToken cancellationToken = default);
    Task CheckAsync(IProgress<string>? progress, CancellationToken cancellationToken = default) => CheckAsync(cancellationToken);
    void SetEnabled(bool enabled);
    Task SetEnabledAsync(bool enabled, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetEnabled(enabled);
        return Task.CompletedTask;
    }
    void SetField(string fieldId, string value);
    Task ApplyAsync(CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PackageManagerSettingsSource>> ListSourcesAsync(CancellationToken cancellationToken = default);
    Task AddSourceAsync(string name, string url, CancellationToken cancellationToken = default);
    Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default);
    Task ResetSourcesAsync(CancellationToken cancellationToken = default);
    Task<string?> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default);
}

/// <summary>Registro imutável de adaptadores descobertos pelo container de serviços.</summary>
public sealed class PackageManagerSettingsAdapterRegistry
{
    private readonly IReadOnlyDictionary<string, IPackageManagerSettingsAdapter> _adapters;

    public PackageManagerSettingsAdapterRegistry(IEnumerable<IPackageManagerSettingsAdapter> adapters, PackageManagerCatalog? catalog = null)
    {
        catalog ??= PackageManagerCatalog.Default;
        _adapters = adapters.Where(adapter => !string.IsNullOrWhiteSpace(adapter.Definition.Id)
                && !catalog.IsSchemaRejected(adapter.Definition.Id))
            .GroupBy(adapter => adapter.Definition.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToDictionary(adapter => adapter.Definition.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IPackageManagerSettingsAdapter> Adapters => _adapters.Values
        .OrderBy(adapter => adapter.Definition.SortOrder).ThenBy(adapter => adapter.Definition.DisplayName, StringComparer.CurrentCulture).ToArray();

    public bool TryGet(string id, out IPackageManagerSettingsAdapter adapter) => _adapters.TryGetValue(id, out adapter!);
    public IPackageManagerSettingsAdapter Get(string id) => _adapters.TryGetValue(id, out IPackageManagerSettingsAdapter? adapter)
        ? adapter : throw new KeyNotFoundException($"Não há adaptador registrado para '{id}'.");
}
