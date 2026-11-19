using WinProvision.Cli;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class CliNativeProfileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NativeAppExportsReachEngineWithoutLosingOptions(bool backup, bool download)
    {
        var profile = new ProfileManifest { Apps = [
            new() { Id = "7zip.7zip", Source = "winget", PinnedVersion = "25.01" },
            new() { Id = "office.family", Source = "office", OfficeOptions = OfficeCliDownloadService.DefaultRequest(true).Office }] };
        string json = backup ? AotJson.Serialize(new ProfileBackupSet { Tabs = [profile] }, WinProvisionJsonOptions.Profile)
            : ProfileDocumentService.Serialize(profile);
        await WithFile(json, async path =>
        {
            var engine = new Backend();
            var app = new CliApplication(engine, new StringReader("1\n"), new StringWriter(), new StringWriter(), "test");
            string[] args = download ? ["import", path, "--download"] : ["import", path];
            Assert.Equal(0, await app.RunAsync(args, default));
            Assert.Equal(2, engine.Profiles.Count);
            Assert.Equal("25.01", engine.Profiles[0].PinnedVersion);
            Assert.True(engine.Profiles[1].OfficeOptions!.DownloadBeforeInstall);
            Assert.Equal(download ? 2 : 0, engine.Downloads);
        });
    }

    [Fact]
    public async Task InvalidLastEntryPreventsEntireBatch()
    {
        await WithFile("{\"apps\":[{\"id\":\"7zip.7zip\"},{\"id\":\"--invalid\"}]}", async path =>
        {
            var engine = new Backend();
            var app = new CliApplication(engine, new StringReader("1\n"), new StringWriter(), new StringWriter(), "test");
            await Assert.ThrowsAsync<ArgumentException>(() => app.RunAsync(["import", path], default));
            Assert.Empty(engine.Profiles);
        });
    }

    private static async Task WithFile(string json, Func<string, Task> test)
    {
        string path = Path.Combine(Path.GetTempPath(), $"winprovision-profile-{Guid.NewGuid():N}.json");
        try { await File.WriteAllTextAsync(path, json); await test(path); }
        finally { File.Delete(path); }
    }
    private sealed class Backend : ICliPackageBackend
    {
        public List<ProfileAppRef> Profiles { get; } = [];
        public int Downloads { get; private set; }
        public Task<WingetExecutionResult> ExecuteProfileAsync(ProfileAppRef app, Action<string> log, CancellationToken token)
        { Profiles.Add(app); return Task.FromResult(new WingetExecutionResult { Success = true }); }
        public Task<WingetExecutionResult> DownloadProfileAsync(ProfileAppRef app, string? directory, Action<string> log, CancellationToken token)
        { Downloads++; return ExecuteProfileAsync(app, log, token); }
        public Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token) => throw new NotSupportedException();
        public Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token) => throw new NotSupportedException();
        public Task<string> DetailsAsync(string id, string source, CancellationToken token) => throw new NotSupportedException();
        public Task<WingetExecutionResult> ExecuteAsync(bool install, UpgradablePackage package, Action<string> log, CancellationToken token)
            => throw new InvalidOperationException("O perfil deve chegar inteiro ao motor.");
    }
}
