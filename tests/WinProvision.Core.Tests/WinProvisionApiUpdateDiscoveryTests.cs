using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinProvisionApiUpdateDiscoveryTests
{
    [Fact]
    public void RegistryArchitectureComesFromExecutableInsteadOfRegistryView()
    {
        string path = Environment.ProcessPath!;
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            InstalledAppsService.ReadExecutableArchitecture($"\"{path}\",0"));
        Assert.Null(InstalledAppsService.ReadExecutableArchitecture(@"C:\missing\unknown.exe"));
        Assert.Null(InstalledAppsService.ReadExecutableArchitecture(null));
    }

    [Theory]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.0.0", "1.2.1", true)]
    [InlineData("1.10.0", "1.9.9", false)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("unknown", "2.0.0", false)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    [InlineData("1.0.0", "1.1.0-preview.1", false)]
    [InlineData("", "2.0.0", false)]
    public void OnlyStrictlyNewerNumericVersionsAreApiUpdateCandidates(
        string installed, string candidate, bool expected) =>
        Assert.Equal(expected,
            WinProvisionApiService.IsStrictlyNewerNumericVersion(installed, candidate));

    [Theory]
    [InlineData("Google Chrome", "google chrome")]
    [InlineData("Notepad++ (64-bit x64)", "notepad++")]
    [InlineData("Git version 2.44.0.windows.1", "git")]
    [InlineData("Mozilla Firefox (x64 pt-BR)", "mozilla firefox")]
    [InlineData("7-Zip 23.01 (x64)", "7-zip")]
    public void NormalizeForMatchingStripsArchitecturesAndVersions(string input, string expected) =>
        Assert.Equal(expected, WinProvisionApiService.NormalizeForMatching(input));

    [Fact]
    public void RegistryInventoryReturnsDesktopAppsWithoutWinget()
    {
        var inventory = InstalledAppsService.GetRegistryInventory();
        Assert.NotNull(inventory);
        // Em um sistema Windows real, o Registro contém aplicativos com Nome e Versão
        Assert.All(inventory, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.Equal("winget", item.Source);
        });
    }

}
