using WinProvision.PackageManagement;
using WinProvision.Core.Services;
using WinProvision.Core.Models;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class UpdateVersionVerificationTests
{
    [Fact]
    public void InstallConfirmationRequiresExactIdentitySourceAndRequestedVersion()
    {
        var result = new WingetExecutionResult { Success = true };
        var installed = new[]
        {
            new UpgradablePackage
            {
                Id = "Opera.OperaGX", Name = "Opera GX", Source = "winget", CurrentVersion = "1.2.3"
            }
        };

        var confirmed = WinGetOperationVerificationService.ConfirmInstallResult(
            "Opera.OperaGX", "winget", "1.2.3.0", result, installed);
        Assert.True(confirmed.Success);
        Assert.True(confirmed.InstallationVerified);
        Assert.Equal(OperationVerificationStatus.Confirmed, confirmed.VerificationStatus);

        var fallbackSourceResult = WinGetOperationVerificationService.ConfirmInstallResult(
            "Opera.OperaGX", "winget", null,
            new WingetExecutionResult { Success = true, EffectiveSource = "msstore" },
            new[] { new UpgradablePackage
            {
                Id = "Opera.OperaGX", Name = "Opera GX", Source = "msstore", CurrentVersion = "1.2.3"
            } });
        Assert.True(fallbackSourceResult.Success);
        Assert.True(fallbackSourceResult.InstallationVerified);

        var wrongSource = WinGetOperationVerificationService.ConfirmInstallResult(
            "Opera.OperaGX", "msstore", "1.2.3.0", new WingetExecutionResult { Success = true }, installed);
        Assert.False(wrongSource.Success);
        Assert.True(wrongSource.IsUnconfirmed);
        Assert.Contains("não encontrado", wrongSource.Output);

        var wrongVersion = WinGetOperationVerificationService.ConfirmInstallResult(
            "Opera.OperaGX", "winget", "2.0", new WingetExecutionResult { Success = true }, installed);
        Assert.False(wrongVersion.Success);
        Assert.True(wrongVersion.IsUnconfirmed);
        Assert.Contains("versão 1.2.3", wrongVersion.Output);
    }

    [Fact]
    public void InstallConfirmationDoesNotConvertUnresolvedInstallerFailureIntoSuccessOrFailureClaim()
    {
        var failure = new WingetExecutionResult { Success = false, InstallerStarted = true, Output = "exit code 1" };
        var result = WinGetOperationVerificationService.ConfirmInstallResult(
            "Opera.OperaGX", "winget", null, failure, Array.Empty<UpgradablePackage>());

        Assert.False(result.Success);
        Assert.True(result.IsUnconfirmed);
        Assert.Contains("exit code 1", result.Output);
    }

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
        var result = WinGetOperationVerificationService.ConfirmApiUpgradeResult(offer, new WingetExecutionResult
        {
            Success = true, ApiUpdateProductCode = "actual-msi-code", ApiUpdateProductCodeFromInstaller = identityFromInstaller
        }, inventory);
        Assert.Equal(expected, result.Success);
        Assert.Equal(expected, result.UpdateVersionVerified);
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
        Assert.True(WinGetOperationVerificationService.ConfirmApiUpgradeResult(offer,
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
        var result = WinGetOperationVerificationService.ConfirmApiUpgradeResult(offer,
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
        var result = WinGetOperationVerificationService.ConfirmApiUpgradeResult(offer, new WingetExecutionResult { Success = true },
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
        var unchanged = WinGetOperationVerificationService.ConfirmUpgradeResult(offer,
            new WingetExecutionResult { Success = true },
            new[] { new UpgradablePackage { Id = offer.Id, Name = offer.Name, CurrentVersion = "7.6.7.0" } });
        Assert.False(unchanged.Success);
        Assert.True(unchanged.InstallerStarted);
        Assert.True(unchanged.IsUnconfirmed);
        Assert.Contains("7.6.7.0", unchanged.Output);

        var ambiguous = WinGetOperationVerificationService.ConfirmUpgradeResult(offer,
            new WingetExecutionResult { Success = true },
            new[] { new UpgradablePackage { Id = offer.Id, Name = offer.Name, CurrentVersion = "7.6.8.0" },
                    new UpgradablePackage { Id = offer.Id, Name = offer.Name, CurrentVersion = "7.6.7.0" } });
        Assert.False(ambiguous.Success);
    }

    [Fact]
    public void CompletedInstallerConfirmsSingleTargetVersion()
    {
        var offer = new UpgradablePackage { Id = "Example.App", Name = "App", Source = "winget", AvailableVersion = "7.6.8.0" };
        var result = WinGetOperationVerificationService.ConfirmUpgradeResult(offer,
            new WingetExecutionResult { Success = true },
            new[] { new UpgradablePackage { Id = offer.Id, Name = offer.Name, Source = "winget", CurrentVersion = "7.6.8" } });
        Assert.True(result.Success);
        Assert.True(result.UpdateVersionVerified);
    }

    [Fact]
    public void MatchingVersionFromAnotherSourceDoesNotConfirmUpgrade()
    {
        var offer = new UpgradablePackage { Id = "Example.App", Name = "App", Source = "winget", AvailableVersion = "2.0" };
        var installed = new UpgradablePackage { Id = offer.Id, Name = offer.Name, Source = "msstore", CurrentVersion = "2.0" };

        var result = WinGetOperationVerificationService.ConfirmUpgradeResult(offer,
            new WingetExecutionResult { Success = true }, new[] { installed });

        Assert.True(result.IsUnconfirmed);
        Assert.False(result.UpdateVersionVerified);
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
        Assert.Equal(expected, WinGetOperationVerificationService.VersionsMatch(installed, target));

    [Theory]
    [InlineData("7.6.7", "7.6.8.0", false)]
    [InlineData("7.6.8", "7.6.8.0", true)]
    [InlineData("7.6.9", "7.6.8.0", false)]
    public void ExecutableVersionMustMatchPowerShellTarget(
        string executableVersion, string targetVersion, bool expected) =>
        Assert.Equal(expected, WinGetOperationVerificationService.RuntimeVersionMatches(executableVersion, targetVersion));
}

