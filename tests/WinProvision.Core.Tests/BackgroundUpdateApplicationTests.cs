using WinProvision.Cli;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

[CollectionDefinition("BackgroundUpdates", DisableParallelization = true)]
public class BackgroundUpdatesCollection;

[Collection("BackgroundUpdates")]
public sealed class BackgroundUpdateApplicationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly ApplicationPreferencesService _preferences;
    private readonly IgnoredUpdatesService _ignored;
    private readonly Backend _backend = new();

    public BackgroundUpdateApplicationTests()
    {
        Directory.CreateDirectory(_folder);
        AotJson.Register(BackgroundJsonContext.Default);
        _preferences = new(Path.Combine(_folder, "preferences.json"), persistStartup: false);
        _ignored = new(Path.Combine(_folder, "ignored.json"));
        WinProvisionElevationState.ResetForTests();
    }

    [Fact]
    public void IntervalPreferencesRoundTripAndOldFilesKeepDefaults()
    {
        _preferences.SetUpdateAutomationOptions(BackgroundUpdateMode.NotifyOnly, BackgroundUpdateScope.SelectedPackagesOnly,
            BackgroundUpdateSchedule.Interval, "10:30", DayOfWeek.Friday, 120, false);
        var reloaded = new ApplicationPreferencesService(Path.Combine(_folder, "preferences.json"), false);
        Assert.Equal(BackgroundUpdateSchedule.Interval, reloaded.UpdateSchedule);
        Assert.Equal(120, reloaded.UpdateIntervalMinutes);
        Assert.False(reloaded.UpdateNotificationsEnabled);
        Assert.NotNull(reloaded.UpdateScheduleAnchorUtc);
        Assert.Throws<ArgumentOutOfRangeException>(() => _preferences.SetUpdateAutomationOptions(BackgroundUpdateMode.NotifyOnly,
            BackgroundUpdateScope.AllSupportedPackages, BackgroundUpdateSchedule.Interval, "09:00", DayOfWeek.Monday, 0));
        Assert.Equal(120, _preferences.UpdateIntervalMinutes);
        File.WriteAllText(Path.Combine(_folder, "legacy.json"), "{\"Theme\":0,\"UpdateSchedule\":2}");
        var legacy = new ApplicationPreferencesService(Path.Combine(_folder, "legacy.json"), false);
        Assert.Equal(BackgroundUpdateSchedule.Weekly, legacy.UpdateSchedule);
        Assert.Equal(60, legacy.UpdateIntervalMinutes);
        Assert.True(legacy.UpdateNotificationsEnabled);
    }

    [Fact]
    public async Task ExplicitRunWorksWhenScheduleIsDisabledAndFingerprintTracksVersions()
    {
        string? first = null, second = null;
        _preferences.SetUpdateAutomationOptions(BackgroundUpdateMode.NotifyOnly, BackgroundUpdateScope.AllSupportedPackages,
            BackgroundUpdateSchedule.Interval, "09:00", DayOfWeek.Monday);
        await BackgroundUpdateApplication.RunAsync(_backend, _preferences, _ignored, _ => { }, force: true,
            discovered: (_, fingerprint) => first = fingerprint);
        Assert.NotNull(first);
        Assert.Empty(_backend.Executed);
        _backend.Packages[0] = new() { Id = "One", Name = "One", Source = "winget", AvailableVersion = "3" };
        await BackgroundUpdateApplication.RunAsync(_backend, _preferences, _ignored, _ => { }, force: true,
            discovered: (_, fingerprint) => second = fingerprint);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RunStateRoundTripsAndCorruptStateDoesNotBreakTheApp()
    {
        string path = Path.Combine(_folder, "state.json");
        var state = new BackgroundUpdateRunState(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, 2, "abc");
        BackgroundUpdateRunStore.Write(state, path);
        Assert.Equal(state, BackgroundUpdateRunStore.Read(path));
        File.WriteAllText(path, "incompleto");
        Assert.Null(BackgroundUpdateRunStore.Read(path));
    }

    [Fact]
    public async Task StartupEnablesUpdatesWithoutOpeningUiAndSurvivesReload()
    {
        _preferences.SetLaunchAtStartup(true);
        var reloaded = new ApplicationPreferencesService(Path.Combine(_folder, "preferences.json"), false);
        Assert.True(reloaded.AutoUpdateAtStartup);
        Assert.Equal(0, await BackgroundUpdateApplication.RunAsync(_backend, reloaded, _ignored, _ => { }));
        Assert.Equal(new[] { "One", "Two" }, _backend.Executed);
        Assert.Equal("\"C:\\Program Files\\WinProvision\\cli\\winprovision.exe\" --background-update",
            BackgroundUpdateCommand.ForDirectory(@"C:\Program Files\WinProvision"));
        _preferences.SetLaunchAtStartup(false);
        Assert.False(_preferences.AutoUpdateAtStartup);
    }

    [Fact]
    public async Task SelectedPackagesIgnoredVersionsAndDuplicatesAreRespected()
    {
        _preferences.SetLaunchAtStartup(true);
        _preferences.SetUpdateAutomationOptions(BackgroundUpdateMode.InstallAutomatically,
            BackgroundUpdateScope.SelectedPackagesOnly, BackgroundUpdateSchedule.AtLogon, "09:00", DayOfWeek.Monday);
        _preferences.ToggleAutoUpdatePackage("Two", "winget");
        _ignored.Ignore("One", "2");
        _backend.Packages.Add(_backend.Packages[1]);
        Assert.Equal(0, await BackgroundUpdateApplication.RunAsync(_backend, _preferences, _ignored, _ => { }));
        Assert.Equal(new[] { "Two" }, _backend.Executed);
    }

    [Fact]
    public async Task NotifyOnlyNeverInstallsAndReportsAvailableCount()
    {
        _preferences.SetLaunchAtStartup(true);
        _preferences.SetUpdateAutomationOptions(BackgroundUpdateMode.NotifyOnly,
            BackgroundUpdateScope.AllSupportedPackages, BackgroundUpdateSchedule.AtLogon, "09:00", DayOfWeek.Monday);
        int count = -1;
        Assert.Equal(0, await BackgroundUpdateApplication.RunAsync(_backend, _preferences, _ignored, _ => { }, n => count = n));
        Assert.Equal(2, count);
        Assert.Empty(_backend.Executed);
    }

    [Fact]
    public async Task RefusedElevationStopsTheBatchWithoutAnotherAttempt()
    {
        _preferences.SetLaunchAtStartup(true);
        _backend.RefuseElevation = true;
        Assert.Equal(1, await BackgroundUpdateApplication.RunAsync(_backend, _preferences, _ignored, _ => { }));
        Assert.Equal(new[] { "One" }, _backend.Executed);
    }

    [Fact]
    public async Task CanceledBatchDoesNotStartAnotherPackage()
    {
        _preferences.SetLaunchAtStartup(true);
        using var cancellation = new CancellationTokenSource();
        _backend.AfterExecution = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BackgroundUpdateApplication.RunAsync(_backend, _preferences, _ignored, _ => { }, token: cancellation.Token));
        Assert.Single(_backend.Executed);
    }

    public void Dispose()
    {
        WinProvisionElevationState.ResetForTests();
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class Backend : ICliPackageBackend
    {
        public List<UpgradablePackage> Packages { get; } =
            [new() { Id = "One", Name = "One", Source = "winget", AvailableVersion = "2" },
             new() { Id = "Two", Name = "Two", Source = "winget", AvailableVersion = "2" }];
        public List<string> Executed { get; } = [];
        public bool RefuseElevation { get; set; }
        public Action? AfterExecution { get; set; }
        public Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token) => Task.FromResult(Packages);
        public Task<WingetExecutionResult> ExecuteAsync(bool install, UpgradablePackage package, Action<string> log, CancellationToken token)
        {
            Assert.False(install);
            Executed.Add(package.Id);
            if (RefuseElevation) WinProvisionElevationState.MarkFailed();
            AfterExecution?.Invoke();
            return Task.FromResult(new WingetExecutionResult { Success = !RefuseElevation });
        }
        public Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token) => throw new NotSupportedException();
        public Task<string> DetailsAsync(string id, string source, CancellationToken token) => throw new NotSupportedException();
    }
}
