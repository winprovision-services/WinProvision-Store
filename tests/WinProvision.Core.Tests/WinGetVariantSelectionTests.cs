using System.ComponentModel;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using WinProvision.Providers.WinGet.Com;
using WinProvision.Providers.WinGet.Cli.Commands;
using WinProvision.Providers.WinGet.Cli.Contracts;
using WinProvision.Providers.WinGet.Com.Contracts;
using WinProvision.WinGet.Contracts;
using ProviderComOperationResult = WinProvision.Providers.WinGet.Com.Contracts.WinGetComOperationResult;
using WinGetComOperationResult = WinProvision.PackageManagement.WinGetComOperationResult;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetVariantSelectionTests
{
    [Fact]
    public async Task ComMetadataAndExecutionReceiveSameSelectionInstanceAndNeverAnyAfterExamination()
    {
        var client = new FakeComClient((_, selection) =>
        {
            selection.Scope = "user";
            selection.Version = "2.0";
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new("User", "X64", "Exe", "Unknown", false, "2.0")));
        });
        var elevator = new FakeElevator();
        var service = new WinGetComOperationService(client: client, elevator: elevator);
        var options = new PackageInstallOptions(Architecture: "x64");

        WinGetComOperationResult outcome = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.App",
            client.Logs.Add, null, CancellationToken.None, null, "winget", false, options);

        Assert.Same(client.QuerySelection, client.InstallSelection);
        Assert.Equal("User", WinGetComClient.ResolvePackageInstallScope(client.InstallSelection, null).ToString());
        Assert.False(outcome.Result.VariantConfirmed);
        Assert.Contains("scope", outcome.Result.VariantFixedFields);
        Assert.Contains("version", outcome.Result.VariantFixedFields);
        Assert.Contains("installerId", outcome.Result.VariantUnfixedFields);
        Assert.Contains("variantConfirmed=false", string.Join("\n", client.Logs));
    }

    [Fact]
    public async Task RequestedScopeMismatchIsRejectedInPortugueseBeforeExecution()
    {
        var client = new FakeComClient((_, selection) =>
        {
            selection.Scope = "machine";
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new("System", "X64", "Exe", "Unknown", false, "2.0")));
        });
        var elevator = new FakeElevator();
        var service = new WinGetComOperationService(client: client, elevator: elevator);

        WinGetComOperationResult result = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.App",
            client.Logs.Add, null, CancellationToken.None, null, "winget", false,
            new PackageInstallOptions(Scope: "user"));

        Assert.False(result.Result.Success);
        Assert.Contains("escopo solicitado não corresponde", result.Result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.InstallCalls);
    }

    [Theory]
    [InlineData("user", "User")]
    [InlineData("machine", "System")]
    public async Task DeclaredInstallerScopeIsUsed(string requested, string declared)
    {
        var client = new FakeComClient((_, selection) =>
        {
            selection.Scope = requested;
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new(declared, "X64", "Exe", "Unknown", false, "1.0")));
        });
        var elevator = new FakeElevator();
        var service = new WinGetComOperationService(client: client, elevator: elevator);

        WinGetComOperationResult result = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.App", null, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions(Scope: requested));

        Assert.True(result.Result.Success);
        Assert.Equal(requested, requested == "machine" ? elevator.LastSelection?.Scope : client.InstallRequests.Single().Scope);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("machine")]
    public async Task UndeclaredInstallerScopePreservesExplicitRequest(string requested)
    {
        var client = new FakeComClient((_, selection) =>
        {
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new("Unknown", "X64", "Exe", "Unknown", false, "1.0")));
        });
        var elevator = new FakeElevator();
        var service = new WinGetComOperationService(client: client, elevator: elevator);

        WinGetComOperationResult result = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.App", client.Logs.Add, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions(Scope: requested));

        Assert.True(result.Result.Success);
        Assert.Equal(requested, requested == "machine" ? elevator.LastSelection?.Scope : client.InstallRequests.Single().Scope);
        Assert.Contains(client.Logs, line => line.Contains("scope=Unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(client.Logs, line => line.Contains("escopo não declarado pelo manifesto", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UndeclaredInstallerWithoutRequestUsesAnyScope()
    {
        var client = new FakeComClient((_, selection) =>
        {
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new("Unknown", "X64", "Exe", "Unknown", false, "1.0")));
        });
        var service = new WinGetComOperationService(client: client, elevator: new FakeElevator());

        WinGetComOperationResult result = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.App", null, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions());

        Assert.True(result.Result.Success);
        Assert.Null(client.InstallRequests.Single().Scope);
        Assert.Equal("Any", WinGetComClient.ResolvePackageInstallScope(client.InstallSelection, null).ToString());
        Assert.Contains("scope", client.InstallSelection!.UnfixedFields);
    }

    [Fact]
    public void ExaminedUndeclaredScopeResolvesToAnyInsteadOfFailing()
    {
        var selection = WinGetVariantSelectionOptions.Create(null, null, null, null, null);
        selection.MarkExamined();

        Assert.Equal("Any", WinGetComClient.ResolvePackageInstallScope(selection, null).ToString());
    }

    [Fact]
    public async Task UpgradePreservesKnownInstalledScopeWhenInstallerDoesNotDeclareOne()
    {
        var client = new FakeComClient((_, selection) =>
        {
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new("Unknown", "X64", "Exe", "Unknown", false, "2.0")));
        })
        {
            InstalledPackages = [new WinGetComPackage("Vendor.App", "App", "1.0", "winget", InstalledScope: "user")]
        };
        var service = new WinGetComOperationService(client: client, elevator: new FakeElevator());

        WinGetComOperationResult result = await service.ExecuteAsync(ComOperationKind.Upgrade, "Vendor.App", null, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions());

        Assert.True(result.Result.Success);
        Assert.Equal("user", client.UpgradeRequests.Single().Selection?.Scope);
    }

    [Fact]
    public async Task FailedMetadataDoesNotElevateUnlessDirectStartReturnsPreStart740()
    {
        var noRetryClient = new FakeComClient((_, _) => Task.FromResult(
            new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.CatalogOrSourceFailed)))
        { InstallResult = new(WinGetComStatus.Success, FurthestPhase: WinGetComPhase.Finished) };
        var noRetryElevator = new FakeElevator();
        var noRetryService = new WinGetComOperationService(client: noRetryClient, elevator: noRetryElevator);
        _ = await noRetryService.ExecuteAsync(ComOperationKind.Install, "Vendor.App", null, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions());
        Assert.Equal(0, noRetryElevator.Calls);

        var retryClient = new FakeComClient((_, _) => Task.FromResult(
            new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.CatalogOrSourceFailed)))
        { InstallException = new Win32Exception(740) };
        var retryElevator = new FakeElevator();
        var retryService = new WinGetComOperationService(client: retryClient, elevator: retryElevator);
        WinGetComOperationResult result = await retryService.ExecuteAsync(ComOperationKind.Install, "Vendor.App",
            null, null, CancellationToken.None, null, "winget", false, new PackageInstallOptions());

        Assert.Equal(1, retryElevator.Calls);
        Assert.True(result.Result.WasElevated);
    }

    [Fact]
    public async Task MixedMachineAndUserOperationsElevateOnlyMachineItem()
    {
        var client = new FakeComClient((id, selection) =>
        {
            string scope = id == "Vendor.Machine" ? "machine" : "user";
            selection.Scope = scope;
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new(scope == "machine" ? "System" : "User", "X64", "Exe", "Unknown", false, "1.0")));
        });
        var elevator = new FakeElevator();
        var service = new WinGetComOperationService(client: client, elevator: elevator);

        _ = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.Machine", null, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions());
        WinGetComOperationResult user = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.User", null, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions());

        Assert.Equal(1, elevator.Calls);
        Assert.True(client.InstallRequests.Single(item => item.PackageId == "Vendor.User").Selection?.Scope == "user");
        Assert.False(user.Result.WasElevated);
    }

    [Fact]
    public async Task WinGetReturned740AfterInstallerStartDoesNotUseElevatorAndKeepsFailureClassification()
    {
        var client = new FakeComClient((_, selection) =>
        {
            selection.Scope = "user";
            selection.MarkExamined();
            return Task.FromResult(new WinGetComQueryResult<WinGetComInstallerMetadata?>(WinGetComStatus.Success,
                new("User", "X64", "Exe", "Unknown", false, "1.0")));
        })
        {
            InstallResult = new(WinGetComStatus.InstallerFailed, InstallerExitCode: 740,
                FurthestPhase: WinGetComPhase.Installing)
        };
        var elevator = new FakeElevator();
        var service = new WinGetComOperationService(client: client, elevator: elevator);

        WinGetComOperationResult result = await service.ExecuteAsync(ComOperationKind.Install, "Vendor.App", null, null,
            CancellationToken.None, null, "winget", false, new PackageInstallOptions());

        Assert.Equal(0, elevator.Calls);
        Assert.Equal(WingetFailureReason.ElevationRequired, result.Result.FailureReason);
        Assert.True(result.Result.InstallerStarted);
    }

    [Fact]
    public void CliBuilderPinsKnownSelectionFieldsAndSkipsUnmappedComTypes()
    {
        IReadOnlyList<string> args = WinGetCliCommandBuilder.BuildMutation(WinGetCliOperation.Install, "Vendor.App", "winget",
            new(Version: "2.0", Scope: "machine", Architecture: "x64", InstallerType: "burn", Locale: "en-US"));

        Assert.Contains("--version", args);
        Assert.Contains("2.0", args);
        Assert.Contains("--scope", args);
        Assert.Contains("--architecture", args);
        Assert.Contains("--installer-type", args);
        Assert.Contains("burn", args);
        Assert.Contains("--locale", args);
        Assert.Equal("burn", WinGetInstallerTypeArgumentMapper.ToCliValue("Burn"));
        Assert.Equal("msstore", WinGetInstallerTypeArgumentMapper.ToCliValue("MSStore"));
        Assert.Null(WinGetInstallerTypeArgumentMapper.ToCliValue("Msix"));
        Assert.Null(WinGetInstallerTypeArgumentMapper.ToCliValue("Unknown"));
    }

    [Fact]
    public void ExaminedCliVariantWithDifferentRequestedScopeIsDetectableBeforeExecution()
    {
        var selection = WinGetVariantSelectionOptions.Create("2.0", "user", null, null, null);
        selection.Scope = "machine";
        selection.MarkExamined();

        Assert.True(selection.HasRequestedInstallScopeMismatch);
    }

    private sealed class FakeElevator : IWinGetComElevator
    {
        public int Calls { get; private set; }
        public WinGetVariantSelectionOptions? LastSelection { get; private set; }
        public Task<WingetExecutionResult> RunAsync(ComOperationKind operation, string packageId, string? installLocation,
            WinGetVariantSelectionOptions selection, Action<string>? log, Action<InstallProgressUpdate>? progress,
            CancellationToken token)
        {
            Calls++;
            LastSelection = selection;
            return Task.FromResult(new WingetExecutionResult { Success = true, ExitCode = 0, WasElevated = true });
        }
    }

    private sealed class FakeComClient(
        Func<string, WinGetVariantSelectionOptions, Task<WinGetComQueryResult<WinGetComInstallerMetadata?>>> metadata)
        : IWinGetComClient
    {
        public List<string> Logs { get; } = [];
        public List<WinGetComInstallRequest> InstallRequests { get; } = [];
        public List<WinGetComUpgradeRequest> UpgradeRequests { get; } = [];
        public WinGetComPackage[] InstalledPackages { get; init; } = [];
        public WinGetVariantSelectionOptions? QuerySelection { get; private set; }
        public WinGetVariantSelectionOptions? InstallSelection { get; private set; }
        public int InstallCalls { get; private set; }
        public Exception? InstallException { get; init; }
        public ProviderComOperationResult InstallResult { get; init; } = new(WinGetComStatus.Success,
            FurthestPhase: WinGetComPhase.Finished);
        public IReadOnlyList<WinGetComOperationDescriptor> Operations => WinGetComOperationCatalog.All;
        public Task<ProviderComOperationResult> ProbeActivationAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<ProviderComOperationResult> ProbeAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task<WinGetComQueryResult<WinGetComPackage[]>> ListInstalledAsync(WinGetComQueryOptions options, CancellationToken token = default) =>
            Task.FromResult(new WinGetComQueryResult<WinGetComPackage[]>(WinGetComStatus.Success, InstalledPackages));
        public Task<WinGetComQueryResult<WinGetComPackageMatch[]>> SearchAsync(string query, WinGetComQueryOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task<WinGetComQueryResult<WinGetComPackageDetails?>> GetDetailsAsync(string packageId, string? source, CancellationToken token = default) => throw new NotSupportedException();
        public Task<WinGetComQueryResult<WinGetComPackage[]>> GetUpdatesAsync(WinGetComQueryOptions options, CancellationToken token = default) => throw new NotSupportedException();
        public Task<WinGetComQueryResult<WinGetComInstallerMetadata?>> GetInstallerMetadataAsync(string packageId, string? source,
            WinGetVariantSelectionOptions selection, CancellationToken token = default)
        { QuerySelection = selection; return metadata(packageId, selection); }
        public Task<ProviderComOperationResult> InstallAsync(WinGetComInstallRequest request, IProgress<WinGetComInstallProgress>? progress = null,
            IProgress<WinGetComEvent>? events = null, CancellationToken token = default)
        {
            InstallCalls++; InstallSelection = request.Selection; InstallRequests.Add(request);
            if (InstallException is not null) return Task.FromException<ProviderComOperationResult>(InstallException);
            return Task.FromResult(InstallResult);
        }
        public Task<ProviderComOperationResult> UpgradeAsync(WinGetComUpgradeRequest request, IProgress<WinGetComInstallProgress>? progress = null,
            IProgress<WinGetComEvent>? events = null, CancellationToken token = default)
        { UpgradeRequests.Add(request); return Task.FromResult(new ProviderComOperationResult(WinGetComStatus.Success, FurthestPhase: WinGetComPhase.Finished)); }
        public Task<ProviderComOperationResult> UninstallAsync(WinGetComUninstallRequest request, IProgress<WinGetComUninstallProgress>? progress = null,
            IProgress<WinGetComEvent>? events = null, CancellationToken token = default) => throw new NotSupportedException();
    }
}
