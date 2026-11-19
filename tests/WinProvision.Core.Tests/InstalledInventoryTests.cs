using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class InstalledInventoryTests
{
    [Theory]
    [InlineData("Name          Id                  Version     Source\n---------------------------------------------------\nLocal Tool    Vendor.LocalTool    1.2.3       winget\n")]
    [InlineData("Nome          Id                  Versão\n-------------------------------------------\nLocal Tool    Vendor.LocalTool    1.2.3\n")]
    public void InstalledListPreservesPackagesOutsideCatalog(string output)
    {
        Assert.True(WingetUpgradeListParser.TryParse(output, out var packages,
            requireAvailableVersion: false, allowMissingAvailableColumn: true));
        var package = Assert.Single(packages);
        Assert.Equal("Vendor.LocalTool", package.Id);
        Assert.Equal("1.2.3", package.CurrentVersion);
        Assert.False(WingetUpgradeListParser.TryParse(output, out _));
    }

    [Fact]
    public void UnknownCliOutputIsNotEmptySuccessfulInventory() =>
        Assert.False(WingetUpgradeListParser.TryParse("Unexpected output", out _,
            requireAvailableVersion: false, allowMissingAvailableColumn: true));

    [Fact]
    public void RegistryIdentityWithSpacesIsValidInInstalledInventory()
    {
        string header = $"{"Name",-30}{"ID",-60}{"Version",-20}{"Source"}";
        string row = $"{"Microsoft 365 - pt-br",-30}{@"ARP\Machine\X64\O365HomePremRetail - pt-br",-60}{"16.0",-20}";
        Assert.True(WingetUpgradeListParser.TryParse(header + "\n" + new string('-', 116) + "\n" + row,
            out var packages, requireAvailableVersion: false, allowMissingAvailableColumn: true));
        Assert.Equal(@"ARP\Machine\X64\O365HomePremRetail - pt-br", Assert.Single(packages).Id);
    }

    [Fact]
    public void NativeIconCanBeExtractedWithWindowsApi()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = new IconService();
        string? icon = service.ExtractIconResource(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll"), 0, "test-" + Guid.NewGuid().ToString("N"));
        Assert.NotNull(icon);
        try
        {
            Assert.True(File.Exists(icon));
            Assert.Equal(new byte[] { 137, 80, 78, 71 }, File.ReadAllBytes(icon).Take(4));
        }
        finally { if (File.Exists(icon)) File.Delete(icon); }
    }
}
