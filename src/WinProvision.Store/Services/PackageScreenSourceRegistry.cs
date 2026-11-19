using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

/// <summary>Projects the same enabled adapters used by the package-manager settings page.</summary>
public sealed class PackageScreenSourceRegistry(
    PackageManagerSettingsAdapterRegistry registry,
    ApplicationPreferencesService preferences)
{
    public IReadOnlyList<PackageScreenSource> GetSources()
    {
        var sources = new List<PackageScreenSource>();
        foreach (IPackageManagerSettingsAdapter adapter in registry.Adapters.Where(adapter => IsEnabled(adapter.Definition.Id)))
        {
            var definition = adapter.Definition;
            string name = PackageManagerCatalog.Default.GetText(definition.DisplayNameKey, definition.DisplayName);
            sources.Add(new PackageScreenSource(definition.Id, name, definition.IconGlyph ?? string.Empty, definition.IconUri));
            if (definition.Id.Equals("winget", StringComparison.OrdinalIgnoreCase))
            {
                PackageManagerDescriptor store = PackageManagerCatalog.Default.GetDescriptor("msstore");
                sources.Add(new PackageScreenSource("msstore", store.DisplayName,
                    store.IconGlyph ?? string.Empty, store.IconUri));
            }
        }
        return sources;
    }

    public string? GetIconUri(string id) => registry.TryGet(id, out IPackageManagerSettingsAdapter? adapter)
        ? adapter.Definition.IconUri : null;

    public PackageScreenIconSources GetIconSources(string id)
    {
        if (registry.TryGet(id, out IPackageManagerSettingsAdapter? adapter)
            && !string.IsNullOrWhiteSpace(adapter.Definition.IconUri))
            return new(adapter.Definition.IconUri, adapter.Definition.IconUri);
        PackageScreenIconSources? sources = PackageScreenCatalog.Default.ResolveIconSources(id);
        return sources ?? new(null, null);
    }

    public bool IsEnabled(string id)
    {
        if (id.Equals("msstore", StringComparison.OrdinalIgnoreCase)) return preferences.WingetEnabled;
        if (!registry.TryGet(id, out var adapter) || !adapter.Enabled
            || !preferences.GetPackageManagerEnabled(id)) return false;
        return adapter.Definition.ParentId is not { Length: > 0 } parent
            || registry.TryGet(parent, out var parentAdapter) && parentAdapter.Enabled
                && preferences.GetPackageManagerEnabled(parent);
    }
}

public sealed record PackageScreenSource(string Id, string Name, string IconGlyph, string? IconUri = null);
