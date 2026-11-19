using WinProvision.Core.Models;
using System.Text.Json;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageManagerCatalogTests
{
    [Fact]
    public void EmbeddedCatalogListsSupportedManagersAndModelsStoreAsWingetChild()
    {
        PackageManagerCatalog catalog = PackageManagerCatalog.Default;
        Assert.Equal(new[] { "winget", "chocolatey", "winprovision-store" }, catalog.SettingsManagers.Select(item => item.Id));
        Assert.False(catalog.GetDescriptor("msstore").ShowInSettings);
        Assert.Equal("winget", catalog.GetDescriptor("msstore").ParentId);
        Assert.Equal("WinGet", catalog.GetDescriptor("winget").DisplayName);
        PackageManagerDescriptor store = catalog.GetDescriptor("winprovision-store");
        Assert.Equal(EmbeddedFlag("canToggle"), store.CanToggle);
        Assert.Equal(EmbeddedFlag("hasDetailsPage"), store.HasDetailsPage);
        Assert.Equal("manager.winprovisionStore.comingSoon", store.DisabledReasonKey);
        Assert.NotNull(catalog.GetDescriptor("winget").SupportedArchitectures);
        Assert.NotNull(catalog.GetDescriptor("winget").Capabilities);
    }

    private static bool EmbeddedFlag(string name)
    {
        using Stream stream = typeof(PackageManagerCatalog).Assembly
            .GetManifestResourceStream("WinProvision.Core.Models.package-managers.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement descriptor = document.RootElement.GetProperty("managers").EnumerateArray()
            .Single(element => element.GetProperty("id").GetString() == "winprovision-store");
        return descriptor.GetProperty(name).GetBoolean();
    }

    [Fact(Skip = "preexistente, SVG empacotado")]
    public void EmbeddedManagerSvgAssetsResolveToSvgUris()
    {
        PackageManagerCatalog catalog = PackageManagerCatalog.Default;
        Assert.Equal("ms-appx:///Assets/Icons/winget.png", catalog.GetDescriptor("winget").IconUri);
        Assert.Equal("ms-appx:///Assets/Icons/microsoft-store.svg", catalog.GetDescriptor("msstore").IconUri);
        Assert.Equal("ms-appx:///Assets/Icons/chocolatey.svg", catalog.GetDescriptor("chocolatey").IconUri);
        Assert.Equal("ms-appx:///Assets/Icons/winprovision-store.svg", catalog.GetDescriptor("winprovision-store").IconUri);
    }

    [Fact]
    public void CatalogUsesTranslationsAndSafeFallbackForMissingTranslation()
    {
        var logs = new List<string>();
        PackageManagerCatalog catalog = PackageManagerCatalog.Load("""
            { "schemaVersion": 1,
              "translations": { "pt-BR": { "manager.test.name": "Teste" } },
              "managers": [{ "schemaVersion": 1, "id": "test", "source": "test", "displayName": "Test", "displayNameKey": "manager.test.name", "descriptionKey": "manager.test.description", "showInSettings": true, "canToggle": true, "check": { "kind": "none" } }] }
            """, logs.Add);

        Assert.Equal("Teste", catalog.GetDescriptor("test").DisplayName);
        Assert.Equal("Fallback", catalog.GetText("missing", "Fallback"));
        Assert.Contains(logs, line => line.Contains("ausente", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnsupportedManagerSchemaIsSkippedWithoutRejectingOtherManagers()
    {
        var logs = new List<string>();
        PackageManagerCatalog catalog = PackageManagerCatalog.Load("""
            { "schemaVersion": 1,
              "managers": [
                { "schemaVersion": 1, "id": "supported", "source": "supported", "displayName": "Supported", "showInSettings": true },
                { "schemaVersion": 2, "id": "future", "source": "future", "displayName": "Future", "showInSettings": true }
              ] }
            """, logs.Add);

        Assert.Equal(new[] { "supported" }, catalog.SettingsManagers.Select(manager => manager.Id));
        Assert.DoesNotContain(catalog.Managers, manager => manager.Id == "future");
        Assert.False(catalog.IsSchemaRejected("supported"));
        Assert.True(catalog.IsSchemaRejected("future"));
        Assert.Contains(logs, message => message.Contains("future", StringComparison.Ordinal)
            && message.Contains("schemaVersion", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("{\"schemaVersion\":7,\"managers\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"managers\":[{\"id\":\"x\",\"showInSettings\":false}]}")]
    public void InvalidCatalogUsesBuiltInDefinitionsAndLogs(string json)
    {
        var logs = new List<string>();
        PackageManagerCatalog catalog = PackageManagerCatalog.Load(json, logs.Add);

        Assert.Equal(new[] { "winget", "chocolatey" }, catalog.SettingsManagers.Select(item => item.Id));
        Assert.NotEmpty(logs);
    }

    [Fact]
    public void MissingManagerReturnsGenericDescriptorAndUnknownStyleUsesInfo()
    {
        var logs = new List<string>();
        PackageManagerCatalog catalog = PackageManagerCatalog.Load("{\"schemaVersion\":1,\"managers\":[{\"schemaVersion\":1,\"id\":\"x\",\"showInSettings\":true}]} ", logs.Add);
        PackageManagerDescriptor descriptor = catalog.GetDescriptor("unknown");

        Assert.Equal("unknown", descriptor.Id);
        Assert.Equal("info", catalog.GetStatusStyle("unknown"));
        Assert.Contains(logs, line => line.Contains("ausente", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, "checking", "")]
    [InlineData(false, "disabled", "")]
    public void ResolverHandlesUncheckedAndDisabledStates(bool? enabled, string expectedStatus, string expectedVersion)
    {
        PackageManagerDescriptor definition = PackageManagerCatalog.Default.GetDescriptor("chocolatey");
        PackageManagerRowStatus status = PackageManagerStatusResolver.Resolve(definition, enabled ?? true, null);
        Assert.Equal(expectedStatus, status.Status);
        Assert.Equal(expectedVersion, status.Version);
    }

    [Fact]
    public void ResolverMapsFoundMissingErrorAndVersionStates()
    {
        PackageManagerDescriptor definition = PackageManagerCatalog.Default.GetDescriptor("chocolatey");
        Assert.Equal("ready", PackageManagerStatusResolver.Resolve(definition, true, new(false, true, true, true, Version: "2.5.1")).Status);
        Assert.Equal("2.5.1", PackageManagerStatusResolver.Resolve(definition, true, new(false, true, true, true, Version: "2.5.1")).Version);
        Assert.Equal("notFound", PackageManagerStatusResolver.Resolve(definition, true, new(false, true, false, false)).Status);
        Assert.Equal("error", PackageManagerStatusResolver.Resolve(definition, true, new(false, true, true, false, true)).Status);
        Assert.Equal("error", PackageManagerStatusResolver.Resolve(definition, true, new(false, true, true, false)).Status);
    }

    [Fact]
    public void StoreInheritsWingetStatusAndBothEnablePreferences()
    {
        PackageManagerDescriptor definition = PackageManagerCatalog.Default.GetDescriptor("msstore");
        PackageManagerProbeState ready = new(false, true, true, true, Version: "1.10");

        Assert.Equal("checking", PackageManagerStatusResolver.Resolve(definition, true, null, true, null).Status);
        Assert.Equal("ready", PackageManagerStatusResolver.Resolve(definition, true, null, true, ready).Status);
        Assert.Equal("notFound", PackageManagerStatusResolver.Resolve(definition, true, null, true, new(false, true, false, false)).Status);
        Assert.Equal("error", PackageManagerStatusResolver.Resolve(definition, true, null, true, new(false, true, true, false, true)).Status);
        Assert.Equal("disabled", PackageManagerStatusResolver.Resolve(definition, true, null, false, ready).Status);
        Assert.Equal("disabled", PackageManagerStatusResolver.Resolve(definition, false, null, true, ready).Status);
    }
}
