using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ApplicationPackagePolicyTests
{
    [Theory]
    [InlineData("WinProvision.Store", "Aplicativo", true)]
    [InlineData("winprovision.store", null, true)]
    [InlineData("{71B03A9D-2C64-4E81-BF6D-3A90257F1C49}_is1", "WinProvision Store", true)]
    [InlineData("Google.Chrome", "Google Chrome", false)]
    [InlineData("Other.App", "WinProvision Store Helper", false)]
    public void IdentifiesOnlyOwnPackage(string id, string? name, bool expected)
        => Assert.Equal(expected, ApplicationPackagePolicy.IsSelf(id, name));

    [Theory]
    [InlineData("winget", "curated", "Catálogo WinProvision")]
    [InlineData("winget", null, "WinGet")]
    [InlineData("msstore", null, "Microsoft Store")]
    public void PresentationOriginDoesNotChangeInstallationSource(string source, string? origin, string label)
    {
        var app = new WinProvision.Core.Models.AppEntry { Source = source, CatalogOrigin = origin };
        Assert.Equal(label, app.CatalogSourceLabel);
        Assert.Equal(source, app.Source);
    }
}
