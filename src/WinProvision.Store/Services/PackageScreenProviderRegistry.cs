using WinProvision.Core.Models;
using WinProvision.Store.Controls;

namespace WinProvision.Store.Services;

/// <summary>Resolves the data provider declared by a screen without constructing unrelated providers.</summary>
public sealed class PackageScreenProviderRegistry(IEnumerable<PackageScreenProviderRegistration> registrations)
{
    private readonly IReadOnlyDictionary<string, Func<IPackageScreenProvider>> _factories = registrations
        .ToDictionary(registration => registration.Id, registration => registration.Create, StringComparer.OrdinalIgnoreCase);

    public IPackageScreenProvider Resolve(PackageScreenDefinition definition)
    {
        if (!_factories.TryGetValue(definition.Data.Provider, out var create))
            throw new InvalidOperationException($"Provedor da tela '{definition.Id}' não registrado: '{definition.Data.Provider}'.");
        var provider = create();
        if (!string.Equals(provider.Definition.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"O provedor '{definition.Data.Provider}' não atende a definição '{definition.Id}'.");
        return provider;
    }

    public PackageScreen CreateScreen(PackageScreenDefinition definition) => new() { Provider = Resolve(definition) };
}

public sealed record PackageScreenProviderRegistration(string Id, Func<IPackageScreenProvider> Create);
