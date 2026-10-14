using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OfficeOfflineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinProvision-OfficeTests-" + Guid.NewGuid().ToString("N"));
    private static OfflineCollectionRequest Request(bool offline) => new("office.test", "Office de teste", "office",
        IsOffice: true, Office: new OfficeInstallOptions { ProductId = "O365HomePremRetail", DownloadBeforeInstall = offline });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfigurationUsesSelectedSourceMode(bool offline)
    {
        var add = OfflineOfficeModuleBuilder.BuildConfiguration([Request(offline)]).Root!.Element("Add")!;
        Assert.Equal(offline ? "." : null, (string?)add.Attribute("SourcePath"));
        Assert.Equal(offline ? "FALSE" : null, (string?)add.Attribute("AllowCdnFallback"));
    }

    [Fact]
    public void MixedOfficeModesCannotCreateAmbiguousZip() =>
        Assert.Throws<InvalidDataException>(() => OfflineOfficeModuleBuilder.BuildConfiguration([Request(false), Request(true)]));

    [Fact]
    public void OfflineSourcesShareStreamsAcrossAppExclusions()
    {
        var request = new OfficeInstallRequest(OfficePlanCatalog.ByProductId("O365HomePremRetail")!, 64, "pt-br", [], DownloadBeforeInstall: true);
        Assert.Equal(OfficeDeploymentToolService.GetPayloadCacheKey(request), OfficeDeploymentToolService.GetPayloadCacheKey(request with { ExcludedApps = ["Word"] }));
        Assert.NotEqual(OfficeDeploymentToolService.GetPayloadCacheKey(request), OfficeDeploymentToolService.GetPayloadCacheKey(request with { LanguageId = "en-us" }));
    }

    [Fact]
    public void LegacyProfileDefaultsToOnline()
    {
        var options = AotJson.Deserialize<OfficeInstallOptions>("{\"productId\":\"O365HomePremRetail\"}", WinProvisionJsonOptions.Profile)!;
        Assert.False(options.DownloadBeforeInstall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliDownloadUsesDefaultXmlAndOnlyDownloadsMediaWhenOffline(bool offline)
    {
        var source = new FixtureOfficeSource();
        string folder = await new OfficeCliDownloadService(source).DownloadAsync(offline, _root, null, default);
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, folder);
        var configuration = XDocument.Load(Path.Combine(folder, "configuration.xml"));
        var add = configuration.Root!.Element("Add")!;
        Assert.Equal("64", (string?)add.Attribute("OfficeClientEdition"));
        Assert.Equal("Current", (string?)add.Attribute("Channel"));
        Assert.Equal(OfficePlan.Family.ProductId, (string?)add.Element("Product")!.Attribute("ID"));
        Assert.Equal("pt-br", (string?)add.Element("Product")!.Element("Language")!.Attribute("ID"));
        Assert.Equal(new[] { "Access", "Publisher" }, add.Element("Product")!.Elements("ExcludeApp").Select(item => (string)item.Attribute("ID")!));
        Assert.Equal(offline ? 1 : 0, source.DownloadCalls);
        Assert.Equal(0, await VerifyScriptAsync(Path.Combine(folder, "Install-Office.ps1")));
        Assert.Empty(Directory.GetDirectories(_root, "*.partial"));
        if (offline) await OfficeOfflinePayload.VerifyAsync(folder, default);
    }

    [Fact]
    public async Task CliFailedDownloadPreservesExistingDirectoryAndCleansStaging()
    {
        Directory.CreateDirectory(_root);
        string previous = Path.Combine(_root, "previous.txt");
        await File.WriteAllTextAsync(previous, "preserve");
        await Assert.ThrowsAsync<IOException>(() => new OfficeCliDownloadService(new FixtureOfficeSource { FailDownload = true })
            .DownloadAsync(true, _root, null, default));
        Assert.Equal("preserve", await File.ReadAllTextAsync(previous));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task CliCancellationDoesNotPublishPartialMedia()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FixtureOfficeSource { BeforeDownload = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfficeCliDownloadService(source)
            .DownloadAsync(true, _root, null, cancellation.Token));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task OnlineZipKeepsExistingThreeFileModule()
    {
        var source = new FixtureOfficeSource();
        var result = await ExportAsync(false, source);
        Assert.False(result.Office!.IncludesPayload);
        Assert.Equal(3, result.Office.Files.Count);
        Assert.Equal(0, source.DownloadCalls);
    }

    [Fact]
    public async Task OfflineZipIsPortableAndDetectsPayloadTampering()
    {
        var source = new FixtureOfficeSource();
        var result = await ExportAsync(true, source);
        Assert.True(result.Office!.IncludesPayload);
        Assert.Equal(1, source.DownloadCalls);
        string relocated = Path.Combine(_root, "outro local");
        ZipFile.ExtractToDirectory(result.FilePath, relocated);
        string office = Path.Combine(relocated, "Office");
        var xml = XDocument.Load(Path.Combine(office, "configuration.xml"));
        Assert.Equal(".", (string?)xml.Root!.Element("Add")!.Attribute("SourcePath"));
        Assert.Equal("16.0.1.2", (string?)xml.Root.Element("Add")!.Attribute("Version"));
        foreach (var file in result.Office.Files)
        {
            await using var stream = File.OpenRead(Path.Combine(relocated, file.Path));
            Assert.Equal(file.Sha256, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
        }
        string script = Path.Combine(office, "Install-Office.ps1");
        Assert.Equal(0, await VerifyScriptAsync(script));
        await File.AppendAllTextAsync(Path.Combine(office, "Office", "Data", "16.0.1.2", "stream.x64.x-none.dat"), "tampered");
        Assert.Equal(1, await VerifyScriptAsync(script));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("missing")]
    [InlineData("tampered")]
    [InlineData("traversal")]
    public async Task InventoryRejectsUnexpectedOrCorruptContent(string change)
    {
        await FixtureOfficeSource.CreatePayloadAsync(_root, CancellationToken.None);
        await OfficeOfflinePayload.WriteIndexAsync(_root, CancellationToken.None);
        string stream = Path.Combine(_root, "Office", "Data", "16.0.1.2", "stream.x64.x-none.dat");
        if (change == "extra") await File.WriteAllTextAsync(Path.Combine(_root, "Office", "Data", "extra.cab"), "extra");
        if (change == "missing") File.Delete(stream);
        if (change == "tampered") await File.AppendAllTextAsync(stream, "tampered");
        if (change == "traversal") await File.WriteAllTextAsync(Path.Combine(_root, OfficeOfflinePayload.IndexFileName), new string('A', 64) + "\tOffice/Data/../../outside.dat");
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficeOfflinePayload.VerifyAsync(_root, CancellationToken.None));
    }

    [Fact]
    public async Task FailedDownloadPreservesPreviousZip()
    {
        Directory.CreateDirectory(_root);
        string destination = Path.Combine(_root, "collection.zip");
        await File.WriteAllTextAsync(destination, "previous");
        await Assert.ThrowsAsync<IOException>(() => ExportAsync(true, new FixtureOfficeSource { FailDownload = true }));
        Assert.Equal("previous", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task CancellationStopsZipPreparation()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FixtureOfficeSource { BeforeDownload = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportAsync(true, source, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(_root, "collection.zip")));
    }

    private Task<OfflineCollectionResult> ExportAsync(bool offline, FixtureOfficeSource source, CancellationToken token = default) =>
        new OfflineCollectionExportService(new NoPackagesSource(), source).ExportAsync("Office", [Request(offline)], Path.Combine(_root, "collection.zip"), null, token);

    private static async Task<int> VerifyScriptAsync(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-VerifyOnly" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("WinProvision-OfficeTests-", StringComparison.Ordinal)) throw new InvalidOperationException("Destino de limpeza inválido.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed class NoPackagesSource : IOfflineCollectionSource
    {
        public Task<PackageManifest?> GetManifestAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task DownloadAsync(CatalogInstaller installer, string destination, Action<InstallProgressUpdate>? progress, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class FixtureOfficeSource : IOfflineOfficeSource
    {
        public int DownloadCalls { get; private set; }
        public bool FailDownload { get; init; }
        public Action? BeforeDownload { get; init; }
        public async Task<string> CopySetupAsync(string destination, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
        {
            byte[] fixture = "Non-executable ODT fixture"u8.ToArray();
            await File.WriteAllBytesAsync(destination, fixture, token);
            return Convert.ToHexString(SHA256.HashData(fixture));
        }
        public async Task DownloadPayloadAsync(string setup, string configuration, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
        {
            DownloadCalls++;
            BeforeDownload?.Invoke();
            token.ThrowIfCancellationRequested();
            if (FailDownload) throw new IOException("Simulated ODT download failure");
            var xml = XDocument.Load(configuration);
            string destination = (string)xml.Root!.Element("Add")!.Attribute("SourcePath")!;
            Assert.Equal(Path.GetDirectoryName(setup), destination);
            await CreatePayloadAsync(destination, token);
        }
        public static async Task CreatePayloadAsync(string root, CancellationToken token)
        {
            string data = Path.Combine(root, "Office", "Data");
            Directory.CreateDirectory(Path.Combine(data, "16.0.1.2"));
            await File.WriteAllTextAsync(Path.Combine(data, "v64.cab"), "CAB fixture", token);
            await File.WriteAllTextAsync(Path.Combine(data, "16.0.1.2", "stream.x64.x-none.dat"), "DAT fixture", token);
        }
    }
}
