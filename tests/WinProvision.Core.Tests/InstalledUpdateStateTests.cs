using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstalledUpdateStateTests
{
    [Theory]
    [InlineData("26.9.3", "26.09.3", true)]
    [InlineData("26.10", "26.09.3", true)]
    [InlineData("26.08.1", "26.09.3", false)]
    [InlineData("unknown", "26.09.3", false)]
    public void CurrentOrNewerVersionsSatisfyTheTargetWithoutReinstallation(string installed, string target, bool expected) =>
        Assert.Equal(expected, WinGetService.IsUpdateSatisfied(installed, target));

    [Theory]
    [InlineData(PackageUpdateMethod.Automatic)]
    [InlineData(PackageUpdateMethod.ComApi)]
    [InlineData(PackageUpdateMethod.WinProvisionApi)]
    [InlineData(PackageUpdateMethod.WingetCli)]
    public async Task StaleOfferForAlreadyUpdatedAppNeverCallsWingetOrDownloads(PackageUpdateMethod method)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-already-current-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new RejectNetworkHandler());
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"));
        int provisionCalls = 0;
        var backend = new WinGetService(new WingetExecutor(), api, new WingetBootstrapper(),
            preferredUpdateMethod: () => method,
            provision: (_, _) => { provisionCalls++; throw new InvalidOperationException("Must not call WinGet."); },
            stuckUpgradeStatePath: Path.Combine(root, "stuck.json"),
            apiInventory: _ => Task.FromResult(new List<UpgradablePackage> { new()
            {
                Id = "new-msi-code", InstalledRegistryId = "machine:x64:new-msi-code", Name = "App",
                Publisher = "MSI Publisher", CurrentVersion = "26.9.3", InstalledScope = "machine", InstalledArchitecture = "x64"
            } }));
        var stale = new UpgradablePackage
        {
            Id = "Vendor.App", Name = "App", Source = "winget", CurrentVersion = "23.10", AvailableVersion = "26.09.3",
            InstalledRegistryId = "old-key", InstalledPublisher = "", InstalledScope = "machine", InstalledArchitecture = "x64"
        };
        try
        {
            var result = await backend.UpdateAsync(stale.Id, offeredPackage: stale);
            Assert.True(result.Success);
            Assert.True(result.UpdateAlreadySatisfied);
            Assert.False(result.InstallerStarted);
            Assert.Equal(0, provisionCalls);
            Assert.False(File.Exists(Path.Combine(root, "stuck.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void UpToDateX86CannotSatisfyAnOlderX64Installation()
    {
        var offer = new UpgradablePackage { Id = "Vendor.App", Name = "App", InstalledScope = "machine", InstalledArchitecture = "x64" };
        var x86 = new UpgradablePackage { Id = "x86", Name = "App", CurrentVersion = "7.6.8", InstalledScope = "machine", InstalledArchitecture = "x86" };
        var x64 = new UpgradablePackage { Id = "x64", Name = "App 7-x64", CurrentVersion = "7.6.7", InstalledScope = "machine", InstalledArchitecture = "x64" };
        var current = WinGetService.ResolveCurrentUpdateInstallation(offer, new[] { x86, x64 });
        Assert.Same(x64, current);
        Assert.False(WinGetService.IsUpdateSatisfied(current!.CurrentVersion, "7.6.8.0"));
        Assert.Null(WinGetService.ResolveCurrentUpdateInstallation(offer, new[] { x86 }));
    }

    [Fact]
    public void UpdatedNativeProductDoesNotHideTheOnlyOutdatedParallelArchitecture()
    {
        var native = new UpgradablePackage { Id = "native", Name = "App", CurrentVersion = "2.0", InstalledScope = "machine", InstalledArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() };
        var other = new UpgradablePackage { Id = "other", Name = "App", CurrentVersion = "1.0", InstalledScope = "machine", InstalledArchitecture = native.InstalledArchitecture == "x86" ? "x64" : "x86" };
        Assert.Same(other, WinProvisionApiService.SelectUpdateInstallation(new[] { native, other }, "2.0"));
    }

    [Fact]
    public void BurnBundleAndItsMsiChildDoNotBecomeTwoUpdateTargets()
    {
        var msi = new InstalledAppDetail { Id = "msi", DisplayName = "App 7-x64", DisplayVersion = "7.6.7", Publisher = "Vendor", Scope = "machine", IsWindowsInstaller = true, IsSystemComponent = true, Architecture = "x64" };
        var bundle = new InstalledAppDetail { Id = "bundle", DisplayName = "App 7.6.7-x64", DisplayVersion = "7.6.7", Publisher = "Vendor", Scope = "machine", IsBundle = true };
        Assert.Equal("msi", Assert.Single(InstalledAppsService.BuildRegistryInventory(new[] { msi, bundle })).Id);
        var otherScope = new InstalledAppDetail { Id = "user-bundle", DisplayName = bundle.DisplayName, DisplayVersion = bundle.DisplayVersion, Publisher = bundle.Publisher, Scope = "user", IsBundle = true };
        Assert.Equal("user-bundle", Assert.Single(InstalledAppsService.BuildRegistryInventory(new[] { msi, otherScope })).Id);
        Assert.Empty(InstalledAppsService.BuildRegistryInventory(new[] { msi }));
    }

    [Theory]
    [InlineData("DisplayVersion", "VersionMajor", "26.09.3")]
    [InlineData("VersionMajor", "VersionMinor", "7.6")]
    [InlineData("MajorVersion", "MinorVersion", "7.6")]
    [InlineData("VersionMajor", "missing", "7.0")]
    [InlineData("missing", "VersionMinor", "0.6")]
    [InlineData("Version", "missing", "7.6.1234")]
    [InlineData("missing", "missing", "")]
    public void RegistryVersionUsesWingetArpPrecedenceAndPackedVersion(string first, string second, string expected)
    {
        var values = new Dictionary<string, object>
        {
            [first] = first == "DisplayVersion" ? "26.09.3" : first == "Version" ? (7 << 24) | (6 << 16) | 1234 : 7,
            [second] = 6
        };
        Assert.Equal(expected, InstalledAppsService.DetermineRegistryVersion(name => values.GetValueOrDefault(name)));
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A satisfied update must not download metadata or installers.");
    }
}
