using System.Text.Json;
using WinProvision.Cli;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class CliApplicationTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("prune")]
    [InlineData("clear")]
    public async Task CacheCommandsOperateLocallyWithoutPackageOperations(string action)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-cli-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = new Fixture("");
            var cache = new CatalogCacheService(root, new());
            var app = new CliApplication(fixture.Backend, TextReader.Null, fixture.Output, fixture.Error, "test",
                inputRedirected: true, catalogCache: cache);
            Assert.Equal(0, await app.RunAsync(["cache", action], default));
            Assert.Contains(root, fixture.Output.ToString());
            Assert.Empty(fixture.Backend.Executed);
            Assert.Empty(fixture.Error.ToString());
            Assert.Throws<ArgumentException>(() => InteractiveCliInput.ParseRequest(["cache", "invalid"]));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("search")]
    [InlineData("upgrade")]
    [InlineData("list")]
    public async Task JsonIsPureAndNeverInstalls(string command)
    {
        var fixture = new Fixture("1\n1\n");
        Assert.Equal(0, await fixture.App.RunAsync([command, "Chrome", "--json"], default));
        using var json = JsonDocument.Parse(fixture.Output.ToString());
        var package = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("Google.Chrome", package.GetProperty("id").GetString());
        Assert.Equal("Chrome", package.GetProperty("name").GetString());
        Assert.Empty(fixture.Backend.Executed);
        Assert.DoesNotContain("Opção:", fixture.Output.ToString());
    }

    [Fact]
    public async Task EmptyJsonResultRemainsValidAndReportsNoPrompts()
    {
        var fixture = new Fixture("1\n");
        fixture.Backend.Packages = [];
        await fixture.App.RunAsync(["list", "--json"], default);
        Assert.Equal("[]", fixture.Output.ToString().Trim());
        Assert.Empty(fixture.Backend.Executed);
    }

    [Fact]
    public async Task UpdatesAllStillRequiresConfirmationAndContinuesAfterFailure()
    {
        var exit = new Fixture("");
        Assert.Equal(0, await exit.App.RunAsync(["upgrade", "--all"], default));
        Assert.Empty(exit.Backend.Executed);
        var fixture = new Fixture("invalid\n1\n");
        fixture.Backend.FailId = "Google.Chrome";
        Assert.Equal(1, await fixture.App.RunAsync(["upgrade", "--all"], default));
        Assert.Equal(new[] { "Google.Chrome", "Valve.Steam" }, fixture.Backend.Executed);
        Assert.Contains("2/2", fixture.Output.ToString());
        Assert.Contains("1 concluído(s), 1 falha(s)", fixture.Output.ToString());
        Assert.Contains("Opção:", fixture.Output.ToString());
        Assert.DoesNotContain("Usuário:", fixture.Output.ToString());
    }

    [Fact]
    public async Task ExplicitInstallUsesOnlyExactIdAndCannotRunAtEndOfInput()
    {
        var fixture = new Fixture("");
        Assert.Equal(0, await fixture.App.RunAsync(["install", "--id", "Google.Chrome"], default));
        Assert.Empty(fixture.Backend.Executed);
        Assert.Contains("1 pacote(s)", fixture.Output.ToString());
        var confirmed = new Fixture("1\n");
        Assert.Equal(0, await confirmed.App.RunAsync(["install", "--id", "Google.Chrome"], default));
        Assert.Equal("Google.Chrome", Assert.Single(confirmed.Backend.Executed));
    }

    [Fact]
    public async Task PageNavigationKeepsIdentifiersAlignedWithNumbers()
    {
        var fixture = new Fixture("n\np\nn\n16\n1\n");
        fixture.Backend.Packages = Enumerable.Range(1, 20)
            .Select(index => new UpgradablePackage { Id = $"App.{index}", Name = $"App {index}", Source = "winget" }).ToList();
        Assert.Equal(0, await fixture.App.RunAsync(["upgrade", "App"], default));
        Assert.Equal("App.16", Assert.Single(fixture.Backend.Executed));
        Assert.Contains("Resultados 16–20 de 20", fixture.Output.ToString());
    }

    [Fact]
    public async Task NoInputAndCancellationCannotStartOperations()
    {
        var fixture = new Fixture("A\n1\n");
        Assert.Equal(0, await fixture.App.RunAsync(["upgrade", "--no-input"], default));
        Assert.Empty(fixture.Backend.Executed);
        Assert.DoesNotContain("Opção:", fixture.Output.ToString());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.App.RunAsync(["upgrade"], new(true)));
    }

    [Fact]
    public async Task DetailsAndHelpAreReadOnly()
    {
        var fixture = new Fixture("1\n");
        await fixture.App.RunAsync(["show", "--id", "Google.Chrome"], default);
        Assert.Equal("Google.Chrome", fixture.Backend.DetailsId);
        Assert.Empty(fixture.Backend.Executed);
        await fixture.App.RunAsync(["help", "install"], default);
        Assert.Contains("Uso: winprovision install", fixture.Output.ToString());
    }

    [Fact]
    public async Task InvalidIdentifierInBatchPreventsEveryOperation()
    {
        var fixture = new Fixture("1\n");
        fixture.Backend.Packages.Add(new() { Id = "Truncated…", Name = "Inválido", Source = "winget" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.App.RunAsync(["upgrade", "--all"], default));
        Assert.Empty(fixture.Backend.Executed);
    }

    [Fact]
    public async Task SearchIsReadOnlyEvenWithConfirmationInput()
    {
        var fixture = new Fixture("1\n1\n");
        Assert.Equal(0, await fixture.App.RunAsync(["--search", "Chrome"], default));
        Assert.Empty(fixture.Backend.Executed);
        Assert.DoesNotContain("Opção:", fixture.Output.ToString());
    }

    [Fact]
    public async Task ImportValidatesEntireListBeforeAnyOperationAndRequiresConfirmation()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[{\"id\":\"Google.Chrome\"},{\"id\":\"Truncated…\"}]");
            var invalid = new Fixture("1\n");
            await Assert.ThrowsAsync<ArgumentException>(() => invalid.App.RunAsync(["import", path], default));
            Assert.Empty(invalid.Backend.Executed);
            await File.WriteAllTextAsync(path, "[{\"id\":\"Google.Chrome\"},{\"id\":\"Valve.Steam\",\"source\":\"winget\"}]");
            var exit = new Fixture("");
            Assert.Equal(0, await exit.App.RunAsync(["import", "-i", path], default));
            Assert.Empty(exit.Backend.Executed);
            var confirmed = new Fixture("1\n");
            Assert.Equal(0, await confirmed.App.RunAsync(["import", path], default));
            Assert.Equal(new[] { "Google.Chrome", "Valve.Steam" }, confirmed.Backend.Executed);
            var download = new Fixture("1\n");
            Assert.Equal(0, await download.App.RunAsync(["import", path, "--download"], default));
            Assert.Empty(download.Backend.Executed);
            Assert.Equal(2, download.Backend.Downloaded.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WingetManifestIsSupportedAndUnknownSourcesAreRejected()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "{\"Sources\":[{\"SourceDetails\":{\"Name\":\"winget\"},\"Packages\":[{\"PackageIdentifier\":\"Google.Chrome\"}]}]}");
            var fixture = new Fixture("1\n");
            Assert.Equal(0, await fixture.App.RunAsync(["import", path], default));
            Assert.Single(fixture.Backend.Executed);
            await File.WriteAllTextAsync(path, "[{\"id\":\"Google.Chrome\",\"source\":\"untrusted\"}]");
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.App.RunAsync(["import", path], default));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DownloadNeverInstallsAndRemovalRequiresConfirmation()
    {
        var fixture = new Fixture("");
        Assert.Equal(0, await fixture.App.RunAsync(["download", "--id", "Google.Chrome"], default));
        Assert.Single(fixture.Backend.Downloaded);
        Assert.Empty(fixture.Backend.Executed);
        Assert.Equal(0, await fixture.App.RunAsync(["uninstall", "--id", "Google.Chrome"], default));
        Assert.Empty(fixture.Backend.Removed);
        var confirmed = new Fixture("1\n");
        Assert.Equal(0, await confirmed.App.RunAsync(["--uninstall", "-d", "Google.Chrome"], default));
        Assert.Equal("Google.Chrome", Assert.Single(confirmed.Backend.Removed));
    }

    [Fact]
    public async Task ExportPublishesValidManifestAndPreservesExistingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cli-test-{Guid.NewGuid():N}.json");
        try
        {
            var fixture = new Fixture("");
            Assert.Equal(0, await fixture.App.RunAsync(["export", "--output", path], default));
            string original = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(original);
            Assert.True(document.RootElement.TryGetProperty("Sources", out _));
            await Assert.ThrowsAsync<IOException>(() => fixture.App.RunAsync(["export", "--output", path], default));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficeCommandDownloadsWithoutInstalling(bool offline)
    {
        var fixture = new Fixture("1\n");
        string[] arguments = offline ? ["--office", "--offline"] : ["office"];
        Assert.Equal(0, await fixture.App.RunAsync(arguments, default));
        Assert.Equal(offline, Assert.Single(fixture.Backend.OfficeDownloads));
        Assert.Empty(fixture.Backend.Executed);
        Assert.Empty(fixture.Backend.Removed);
        Assert.DoesNotContain("Opção:", fixture.Output.ToString());
    }

    [Fact]
    public async Task OfficeInstallationRequiresExplicitCommandAndConfirmation()
    {
        var exited = new Fixture("");
        Assert.Equal(0, await exited.App.RunAsync(["--install", "--office"], default));
        Assert.Equal(0, exited.Backend.OfficeInstallations);
        var confirmed = new Fixture("1\n");
        Assert.Equal(0, await confirmed.App.RunAsync(["install", "--office"], default));
        Assert.Equal(1, confirmed.Backend.OfficeInstallations);
        Assert.Empty(confirmed.Backend.OfficeDownloads);
    }

    [Theory]
    [InlineData("install", "--id", "Google.Chrome")]
    [InlineData("upgrade", "--id", "Google.Chrome")]
    [InlineData("upgrade", "--all", "--no-input")]
    public async Task YesExecutesWithoutReadingAnyInput(string command, string option, string value)
    {
        var fixture = new Fixture("");
        Assert.Equal(0, await fixture.App.RunAsync([command, option, value, "-y"], default));
        Assert.NotEmpty(fixture.Backend.Executed);
        Assert.DoesNotContain("[0]", fixture.Output.ToString());
        Assert.DoesNotContain("Opção:", fixture.Output.ToString());
    }

    [Fact]
    public async Task YesSupportsUninstallOfficeAndImportWithoutMenus()
    {
        var fixture = new Fixture("");
        Assert.Equal(0, await fixture.App.RunAsync(["uninstall", "--id", "Google.Chrome", "--yes"], default));
        Assert.Single(fixture.Backend.Removed);
        Assert.Equal(0, await fixture.App.RunAsync(["install", "--office", "--yes"], default));
        Assert.Equal(1, fixture.Backend.OfficeInstallations);
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "[{\"id\":\"Google.Chrome\"}]");
            Assert.Equal(0, await fixture.App.RunAsync(["import", path, "--yes", "--no-input"], default));
            Assert.Single(fixture.Backend.Executed);
            Assert.Equal(0, await fixture.App.RunAsync(["import", path, "--download", "--yes"], default));
            Assert.Single(fixture.Backend.Downloaded);
        }
        finally { File.Delete(path); }
        Assert.DoesNotContain("Opção:", fixture.Output.ToString());
        Assert.DoesNotContain("[0]", fixture.Output.ToString());
    }

    [Fact]
    public async Task RedirectedMutationWithoutYesFailsBeforeQueryingOrExecuting()
    {
        var fixture = new Fixture("");
        var app = new CliApplication(fixture.Backend, TextReader.Null, fixture.Output, fixture.Error, "test", inputRedirected: true);
        await Assert.ThrowsAsync<ArgumentException>(() => app.RunAsync(["install", "--id", "Google.Chrome"], default));
        Assert.Empty(fixture.Backend.Executed);
        Assert.Equal(0, await app.RunAsync(["install", "--id", "Google.Chrome", "--yes"], default));
        Assert.Single(fixture.Backend.Executed);
    }

    [Fact]
    public async Task UnattendedBatchReturnsFailureAndContinues()
    {
        var fixture = new Fixture("");
        fixture.Backend.FailId = "Google.Chrome";
        Assert.Equal(1, await fixture.App.RunAsync(["upgrade", "--all", "--yes"], default));
        Assert.Equal(2, fixture.Backend.Executed.Count);
    }

    private sealed class Fixture
    {
        public FakeBackend Backend { get; } = new();
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();
        public CliApplication App { get; }
        public Fixture(string input) => App = new(Backend, new StringReader(input), Output, Error, "0.1.0");
    }

    private sealed class FakeBackend : ICliPackageBackend
    {
        public List<UpgradablePackage> Packages { get; set; } =
        [new() { Id = "Google.Chrome", Name = "Chrome", Source = "winget", CurrentVersion = "1", AvailableVersion = "2" },
         new() { Id = "Valve.Steam", Name = "Steam", Source = "winget", CurrentVersion = "1", AvailableVersion = "2" }];
        public List<string> Executed { get; } = [];
        public List<string> Downloaded { get; } = [];
        public List<string> Removed { get; } = [];
        public List<bool> OfficeDownloads { get; } = [];
        public int OfficeInstallations { get; private set; }
        public Task<WingetExecutionResult> InstallOfficeAsync(Action<string> log, CancellationToken token)
        { OfficeInstallations++; return Task.FromResult(new WingetExecutionResult { Success = true }); }
        public Task<string> OfficeAsync(bool offline, Action<string> log, CancellationToken token)
        { OfficeDownloads.Add(offline); return Task.FromResult("Downloads/Office"); }
        public async Task<WingetExecutionResult> ExportAsync(string path, Action<string> log, CancellationToken token)
        {
            await File.WriteAllTextAsync(path, "{\"Sources\":[{\"SourceDetails\":{\"Name\":\"winget\"},\"Packages\":[{\"PackageIdentifier\":\"Google.Chrome\"}]}]}", token);
            return new() { Success = true };
        }
        public Task<WingetExecutionResult> DownloadAsync(UpgradablePackage package, string? directory, Action<string> log, CancellationToken token)
        { Downloaded.Add(package.Id); return Task.FromResult(new WingetExecutionResult { Success = true }); }
        public Task<WingetExecutionResult> UninstallAsync(string id, string? source, Action<string> log, CancellationToken token)
        { Removed.Add(id); return Task.FromResult(new WingetExecutionResult { Success = true }); }
        public string? DetailsId { get; private set; }
        public string? FailId { get; set; }
        public Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token)
            => Task.FromResult(Packages.Where(package => request.Id is not null || package.Name.Contains(request.Query)).ToList());
        public Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token) => Task.FromResult(Packages);
        public Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token)
        { log("Diagnóstico do WinGet"); return Task.FromResult(Packages); }
        public Task<string> DetailsAsync(string id, string source, CancellationToken token)
        { DetailsId = id; return Task.FromResult("Detalhes do pacote"); }
        public Task<WingetExecutionResult> ExecuteAsync(bool install, UpgradablePackage package, Action<string> log, CancellationToken token)
        { Executed.Add(package.Id); return Task.FromResult(new WingetExecutionResult { Success = package.Id != FailId, ExitCode = package.Id == FailId ? 1 : 0 }); }
    }
}
