using WinProvision.PackageManagement;
using WinProvision.Core.Services;
using WinProvision.Core.Models;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class UpdateVersionVerificationTests
{
    [Theory]
    [InlineData(true, "26.9.3", false, true)]
    [InlineData(true, "26.10", false, true)]
    [InlineData(false, "26.9.3", false, false)]
    [InlineData(true, "25.09", false, false)]
    [InlineData(true, "unknown", false, false)]
    [InlineData(true, "26.9.3", true, false)]
    public void ScopeMigrationRequiresActualMsiIdentityAndTargetVersionWithoutAnOldInstallation(
        bool identityFromInstaller, string version, bool oldRemains, bool expected)
    {
        var offer = new UpgradablePackage
        {
            Id = "Vendor.App", Name = "App", CurrentVersion = "24.06", AvailableVersion = "26.09.3",
            InstalledRegistryId = "old-user-key", InstalledScope = "user", InstalledArchitecture = "x64"
        };
        var inventory = new List<UpgradablePackage> { new()
        {
            Id = "actual-msi-code", Name = "App", CurrentVersion = version, InstalledScope = "machine", InstalledArchitecture = "x64"
        } };
        if (oldRemains)
            inventory.Add(new UpgradablePackage { Id = "old-user-key", Name = "App", CurrentVersion = "24.06", InstalledScope = "user", InstalledArchitecture = "x64" });
        var result = WinGetService.ConfirmApiUpgradeResult(offer, new WingetExecutionResult
        {
            Success = true, ApiUpdateProductCode = "actual-msi-code", ApiUpdateProductCodeFromInstaller = identityFromInstaller
        }, inventory);
        Assert.Equal(expected, result.Success);
        Assert.Equal(expected, result.UpdateVersionVerified);
    }

    [Fact]
    public async Task RegistryVerificationObservesDelayedRegistrationAndIsNotRepeatedAfterConfirmation()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-post-install-" + Guid.NewGuid().ToString("N"));
        await using var api = new WinProvisionApiService(cacheDir: Path.Combine(root, "cache"));
        int reads = 0;
        var backend = new WinGetService(new WingetExecutor(), api, new WingetBootstrapper(),
            preferredUpdateMethod: () => PackageUpdateMethod.WinProvisionApi,
            stuckUpgradeStatePath: Path.Combine(root, "stuck.json"),
            provision: (_, _) => throw new InvalidOperationException("Must not use WinGet."),
            apiInventory: _ => Task.FromResult(++reads == 1 ? new List<UpgradablePackage>() : new List<UpgradablePackage> { new()
            {
                Id = "new-code", Name = "App", CurrentVersion = "26.9.3", InstalledScope = "machine", InstalledArchitecture = "x64"
            } }));
        var offered = new UpgradablePackage
        {
            Id = "Vendor.App", Name = "App", Source = "winget", IsWinProvisionApiUpdate = true,
            CurrentVersion = "24.06", AvailableVersion = "26.09.3", InstalledRegistryId = "old-key",
            InstalledScope = "user", InstalledArchitecture = "x64"
        };
        try
        {
            var result = new WingetExecutionResult { Success = true, ApiUpdateProductCode = "new-code", ApiUpdateProductCodeFromInstaller = true };
            Assert.True((await backend.VerifySuccessfulUpgradeAsync(offered, result, CancellationToken.None)).Success);
            Assert.True(result.UpdateVersionVerified);
            Assert.Equal(2, reads);
            Assert.True((await backend.VerifySuccessfulUpgradeAsync(offered, result, CancellationToken.None)).Success);
            Assert.Equal(2, reads);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ApiMigrationCanConfirmNormalizedVersionWhenCatalogAndMsiPublishersDiffer()
    {
        var offer = new UpgradablePackage
        {
            Id = "Vendor.App", Name = "App", Publisher = "Catalog Brand", InstalledPublisher = "",
            CurrentVersion = "23.10", AvailableVersion = "26.09.3", InstalledRegistryId = "old-exe-key",
            InstalledScope = "machine", InstalledArchitecture = "x64"
        };
        var installed = new UpgradablePackage
        {
            Id = "actual-new-msi-key", Name = "App", Publisher = "MSI Author", CurrentVersion = "26.9.3",
            InstalledScope = "machine", InstalledArchitecture = "x64"
        };
        Assert.True(WinGetService.ConfirmApiUpgradeResult(offer,
            new WingetExecutionResult { Success = true, ApiUpdateProductCode = "catalog-msi-key" }, new[] { installed }).Success);
    }

    [Theory]
    [InlineData("new-code", "user", "26.09.3", false, true)]
    [InlineData("other-code", "user", "26.09.3", false, false)]
    [InlineData("new-code", "machine", "26.09.3", false, false)]
    [InlineData("new-code", "user", "25.09", false, false)]
    [InlineData("new-code", "user", "26.09.3", true, false)]
    public void ApiMigrationUsesVerifiedProductCodeWithoutAcceptingAnUnchangedOldInstall(
        string id, string scope, string version, bool oldRemains, bool expected)
    {
        var offer = new UpgradablePackage
        {
            Id = "Acme.App", Name = "App", Publisher = "Catalog Publisher", AvailableVersion = "26.09.3",
            InstalledRegistryId = "old-key", InstalledScope = "user"
        };
        var inventory = new List<UpgradablePackage> { new()
        {
            Id = id, Name = "App", Publisher = "Different MSI Publisher", CurrentVersion = version, InstalledScope = scope
        } };
        if (oldRemains)
            inventory.Add(new UpgradablePackage { Id = "old-key", Name = "App", CurrentVersion = "23.10", InstalledScope = "user" });
        var result = WinGetService.ConfirmApiUpgradeResult(offer,
            new WingetExecutionResult { Success = true, ApiUpdateProductCode = "new-code" }, inventory);
        Assert.Equal(expected, result.Success);
    }

    [Theory]
    [InlineData("user", true)]
    [InlineData("machine", false)]
    public void ApiVerificationRecognizesReplacedMsiKeyOnlyInTheSameScope(string scope, bool expected)
    {
        var offer = new UpgradablePackage
        {
            Id = "Acme.App", Name = "Acme App", Publisher = "Acme", AvailableVersion = "2.0",
            InstalledRegistryId = "old-product-code", InstalledScope = "user"
        };
        var installed = new UpgradablePackage
        {
            Id = "new-product-code", Name = "Acme App", Publisher = "Acme", CurrentVersion = "2.0", InstalledScope = scope
        };
        var result = WinGetService.ConfirmApiUpgradeResult(offer, new WingetExecutionResult { Success = true },
            new[] { installed });
        Assert.Equal(expected, result.Success);
    }

    [Fact]
    public void CompletedInstallerDoesNotConfirmUnchangedOrAmbiguousInventory()
    {
        var offer = new UpgradablePackage
        {
            Id = "Microsoft.PowerShell", Name = "PowerShell", Source = "winget",
            CurrentVersion = "7.6.7.0", AvailableVersion = "7.6.8.0"
        };
        var unchanged = WinGetService.ConfirmUpgradeResult(offer,
            new WingetExecutionResult { Success = true },
            new[] { new UpgradablePackage { Id = offer.Id, Name = offer.Name, CurrentVersion = "7.6.7.0" } });
        Assert.False(unchanged.Success);
        Assert.True(unchanged.InstallerStarted);
        Assert.Contains("7.6.7.0", unchanged.Output);

        var ambiguous = WinGetService.ConfirmUpgradeResult(offer,
            new WingetExecutionResult { Success = true },
            new[] { new UpgradablePackage { Id = offer.Id, Name = offer.Name, CurrentVersion = "7.6.8.0" },
                    new UpgradablePackage { Id = offer.Id, Name = offer.Name, CurrentVersion = "7.6.7.0" } });
        Assert.False(ambiguous.Success);
    }

    [Fact]
    public void CompletedInstallerConfirmsSingleTargetVersion()
    {
        var offer = new UpgradablePackage { Id = "Microsoft.PowerShell", Name = "PowerShell", AvailableVersion = "7.6.8.0" };
        var result = WinGetService.ConfirmUpgradeResult(offer,
            new WingetExecutionResult { Success = true },
            new[] { new UpgradablePackage { Id = offer.Id, Name = offer.Name, CurrentVersion = "7.6.8" } });
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("7.6.8.0", "7.6.8.0", true)]
    [InlineData("7.6.8", "7.6.8.0", true)]
    [InlineData("v7.6.8.0", "7.6.8", true)]
    [InlineData("7.6.7.0", "7.6.8.0", false)]
    [InlineData("7.6.9.0", "7.6.8.0", false)]
    [InlineData("7.6.8.0", "7.6.8.1", false)]
    [InlineData("unknown", "7.6.8.0", false)]
    [InlineData("Unknown", "Unknown", false)]
    [InlineData("Desconhecido", "Desconhecido", false)]
    [InlineData("7.6.8.0", "Unknown", false)]
    [InlineData("7.6.8.0", "", false)]
    public void VersionCheckRequiresExactTargetWithFlexibleNumericFormatting(
        string? installed, string? target, bool expected) =>
        Assert.Equal(expected, WinGetService.VersionsMatch(installed, target));

    [Theory]
    [InlineData("7.6.7", "7.6.8.0", false)]
    [InlineData("7.6.8", "7.6.8.0", true)]
    [InlineData("7.6.9", "7.6.8.0", false)]
    public void ExecutableVersionMustMatchPowerShellTarget(
        string executableVersion, string targetVersion, bool expected) =>
        Assert.Equal(expected, WinGetService.RuntimeVersionMatches(executableVersion, targetVersion));
}
