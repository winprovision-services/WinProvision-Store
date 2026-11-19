using System.ComponentModel;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallerElevationExecutionTests
{
    [Theory]
    [InlineData("elevationRequired", null, "required")]
    [InlineData("elevationProhibited", "machine", "unknown")]
    [InlineData("elevationProhibited", "user", "not-required")]
    [InlineData("elevationProhibited", null, "unknown")]
    [InlineData("elevatesSelf", "machine", "required")]
    public void ManifestAndScopePrecedence(string requirement, string? scope, string decision)
        => Assert.Equal(decision, InstallerElevationResolver.Resolve("exe", scope, requirement).Decision);

    [Theory]
    [InlineData("msi", "machine", "elevationProhibited", "not-required")]
    [InlineData("exe", "user", "elevationProhibited", "not-required")]
    [InlineData("exe", null, "elevationProhibited", "not-required")]
    public async Task ProhibitedAlwaysPreventsPreElevationAnd740Retry(string type, string? scope, string requirement, string _)
    {
        var starter = new RecordingStarter { DirectException = new Win32Exception(740) };
        var service = new WinProvisionApiService(installerProcessStarter: starter);
        var state = InstallerElevationResolver.Resolve(type, scope, requirement);
        await Assert.ThrowsAsync<Win32Exception>(() => service.RunInstallerWithElevationRetryAsync("setup.exe", type,
            "/quiet", default, state.RequiresElevation, allow740Retry: false));
        Assert.Equal(1, starter.DirectCalls); Assert.Equal(0, starter.ElevatedCalls);
    }

    [Fact]
    public void TypePolicyAppliesOnlyWhenScopeIsAbsent()
    {
        Assert.Equal("not-required", InstallerElevationResolver.Resolve("portable", null, null).Decision);
        Assert.Equal("unknown", InstallerElevationResolver.Resolve("portable", "machine", null).Decision);
        Assert.Equal("not-required", InstallerElevationResolver.Resolve("portable", "user", null).Decision);
        var portable = InstallerElevationResolver.Resolve("portable", null, null);
        Assert.Equal("not-required", InstallerElevationResolver.ApplyInspection(portable,
            new InstallerElevationInspection("required", "fixture", [])).Decision);
        Assert.Equal("required", InstallerElevationResolver.Resolve("portable", null, "elevationRequired").Decision);
        Assert.Equal("required", InstallerElevationResolver.Resolve("portable", null, "elevatesSelf").Decision);
        foreach (string type in new[] { "portable", "pwa", "msix", "appx" })
        {
            var state = InstallerElevationResolver.Resolve(type, null, null);
            Assert.Equal("not-required", state.Decision);
            Assert.Equal("type", state.Source);
            Assert.Equal("unknown", InstallerElevationResolver.Resolve(type, "machine", null).Decision);
        }
        Assert.Equal("unknown", InstallerElevationResolver.Resolve("zip", null, null).Decision);
        Assert.Equal("not-required", InstallerElevationResolver.Resolve("zip", null, null, "portable").Decision);
        Assert.Equal("required", InstallerElevationResolver.Resolve("zip", "machine", null, "msi").Decision);
    }

    [Theory]
    [InlineData("inno")]
    [InlineData("burn")]
    public async Task ElevatesSelfStartsElevatedThroughElevator(string type)
    {
        var starter = new RecordingStarter { DirectException = new Win32Exception(740) };
        var service = new WinProvisionApiService(installerProcessStarter: starter);
        var installer = new CatalogInstaller { Type = type, ElevationRequirement = "elevatesSelf" };
        int code = await service.RunDownloadedInstallerAsync("id", "1", installer,
            false, "setup.exe", type, "", null, default);
        Assert.Equal(0, code);
        Assert.Equal(0, starter.DirectCalls); Assert.Equal(1, starter.ElevatedCalls);
    }

    [Fact]
    public async Task Error740BeforeStartRetriesOnceThroughElevator()
    {
        var starter = new RecordingStarter { DirectException = new Win32Exception(740) };
        var service = new WinProvisionApiService(installerProcessStarter: starter);
        int code = await service.RunInstallerWithElevationRetryAsync("setup.exe", "exe", "/quiet", default, false);
        Assert.Equal(0, code);
        Assert.Equal(1, starter.DirectCalls);
        Assert.Equal(1, starter.ElevatedCalls);
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(1925)]
    public async Task InstallerExitCodesDoNotRetry(int exitCode)
    {
        var starter = new RecordingStarter { DirectExitCode = exitCode };
        var service = new WinProvisionApiService(installerProcessStarter: starter);
        int code = await service.RunInstallerWithElevationRetryAsync("setup.exe", "exe", "/quiet", default, false);
        Assert.Equal(exitCode, code);
        Assert.Equal(1, starter.DirectCalls);
        Assert.Equal(0, starter.ElevatedCalls);
    }

    [Fact]
    public void Msi1925IsElevationDiagnosticWhileOtherCodesKeepTheirClassification()
    {
        Assert.Equal(WingetFailureReason.ElevationRequired, InstallerExitCodes.Classify(1925));
        Assert.Contains("privilégios administrativos", InstallerExitCodes.Describe(1925), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WingetFailureReason.InstallError, InstallerExitCodes.Classify(1603));
        Assert.Equal(WingetFailureReason.BlockedByPolicy, InstallerExitCodes.Classify(1625));
    }

    [Fact]
    public async Task Error740AfterStartDoesNotRetry()
    {
        var starter = new RecordingStarter { DirectException = new Win32Exception(740), MarkStarted = true };
        var service = new WinProvisionApiService(installerProcessStarter: starter);
        await Assert.ThrowsAsync<Win32Exception>(() => service.RunInstallerWithElevationRetryAsync(
            "setup.exe", "exe", "/quiet", default, false, () => { }, () => starter.Started));
        Assert.Equal(1, starter.DirectCalls);
        Assert.Equal(0, starter.ElevatedCalls);
    }

    [Fact]
    public async Task FailedElevatedRetryDoesNotStartThirdAttempt()
    {
        var starter = new RecordingStarter { DirectException = new Win32Exception(740), ElevatedException = new Win32Exception(1223) };
        var service = new WinProvisionApiService(installerProcessStarter: starter);
        await Assert.ThrowsAsync<Win32Exception>(() => service.RunInstallerWithElevationRetryAsync("setup.exe", "exe", "/quiet", default, false));
        Assert.Equal(1, starter.DirectCalls);
        Assert.Equal(1, starter.ElevatedCalls);
    }

    [Fact]
    public async Task MixedBatchUsesPerInstallerDecision()
    {
        var starter = new RecordingStarter();
        var service = new WinProvisionApiService(installerProcessStarter: starter);
        var machine = InstallerElevationResolver.Resolve("msi", "machine", null);
        var user = InstallerElevationResolver.Resolve("exe", "user", null);
        await service.RunInstallerWithElevationRetryAsync("machine.msi", "msi", "/quiet", default, machine.RequiresElevation);
        await service.RunInstallerWithElevationRetryAsync("user.exe", "exe", "/quiet", default, user.RequiresElevation);
        Assert.Equal(new[] { "elevated:" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"), "direct:user.exe" }, starter.Calls);
    }

    [Fact]
    public async Task UserSelectionNeverFallsBackToMachine()
    {
        var manifest = new PackageManifest { Id = "demo", Installers =
        [
            new CatalogInstaller { Id = "machine", Architecture = "x64", Scope = "machine", Type = "msi" },
            new CatalogInstaller { Id = "user", Architecture = "x64", Scope = "user", Type = "exe" }
        ] };
        Assert.Equal("user", WinProvisionApiService.PickInstaller(manifest, System.Runtime.InteropServices.Architecture.X64, "user")?.Id);
        manifest.Installers.RemoveAt(1);
        Assert.Null(WinProvisionApiService.PickInstaller(manifest, System.Runtime.InteropServices.Architecture.X64, "user"));
        await using var service = new WinProvisionApiService();
        var result = await service.TryInstallAsync("demo", preferredArchitecture: "x64", preferredScope: "user",
            suppliedManifest: manifest, recordInstall: false);
        Assert.Equal(WinProvisionInstallOutcome.NoCompatibleInstaller, result.Outcome);
        Assert.Contains("escopo de usuário", result.Message, StringComparison.Ordinal);
        Assert.Contains("não foi iniciada", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProhibitedElevationLogsRequiredIdentityOnlyWhenElevating()
    {
        var logger = new RecordingAppLog();
        CoreLog.Configure(logger);
        try
        {
            var prohibitedAndRequired = InstallerElevationResolver.Resolve("msi", "machine", "elevationProhibited");
            WinProvisionApiService.LogProhibitedElevationIfNeeded("demo.id", "2.0", "x64-msi", prohibitedAndRequired, true);
            Assert.Contains("id=demo.id", logger.LastMessage);
            Assert.Contains("version=2.0", logger.LastMessage);
            Assert.Contains("installerId=x64-msi", logger.LastMessage);
            Assert.Contains("reason=conflict:elevationProhibited-vs-scope:machine+msi", logger.LastMessage);

            logger.LastMessage = "";
            var prohibitedUser = InstallerElevationResolver.Resolve("exe", "user", "elevationProhibited");
            WinProvisionApiService.LogProhibitedElevationIfNeeded("demo.id", "2.0", "user-exe", prohibitedUser, false);
            Assert.Empty(logger.LastMessage);
        }
        finally { CoreLog.Configure(new RecordingAppLog()); }
    }

    private sealed class RecordingStarter : IInstallerProcessStarter
    {
        public int DirectCalls { get; private set; }
        public int ElevatedCalls { get; private set; }
        public int DirectExitCode { get; init; }
        public Exception? DirectException { get; init; }
        public Exception? ElevatedException { get; init; }
        public bool MarkStarted { get; init; }
        public bool Started { get; private set; }
        public List<string> Calls { get; } = [];
        public Task<int> StartDirectAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token, Action? onStarted = null)
        {
            DirectCalls++; Calls.Add("direct:" + fileName);
            if (DirectException is not null)
            {
                if (MarkStarted) { Started = true; onStarted?.Invoke(); }
                return Task.FromException<int>(DirectException);
            }
            Started = true; onStarted?.Invoke();
            return Task.FromResult(DirectExitCode);
        }
        public Task<int> StartElevatedAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token,
            Action? onStarted = null, Action<string>? onLog = null, string? expectedSha256 = null)
        {
            ElevatedCalls++; Calls.Add("elevated:" + fileName);
            if (ElevatedException is not null) return Task.FromException<int>(ElevatedException);
            onStarted?.Invoke(); return Task.FromResult(0);
        }
    }

    private sealed class RecordingAppLog : IAppLog
    {
        public string SessionId => "test";
        public string LogDirectory => "";
        public AppLogLevel MinimumLevel { get; set; }
        public string CurrentLogFilePath => "";
        public long DroppedEventCount => 0;
        public string LastMessage { get; set; } = "";
        public void Write(AppLogLevel level, string category, string source, string message, Guid? taskId = null,
            string eventName = "Message", JsonElement? data = null, Exception? exception = null, IReadOnlyList<string>? knownSecrets = null)
            => LastMessage = message;
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
