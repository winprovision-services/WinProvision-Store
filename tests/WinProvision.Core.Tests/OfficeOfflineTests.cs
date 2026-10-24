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
using WinProvision.Viper;
using WinProvision.Viper.Tests;

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
        var add = OfflineOfficeModuleBuilder.BuildConfiguration((OfflineCollectionRequest[])[Request(offline)]).Root!.Element("Add")!;
        Assert.Equal(offline ? "." : null, (string?)add.Attribute("SourcePath"));
        Assert.Equal(offline ? "FALSE" : null, (string?)add.Attribute("AllowCdnFallback"));
    }

    [Fact]
    public async Task LocalConfigurationPinsSourceVersionEvenWhenXmlIsWrittenElsewhere()
    {
        await FixtureOfficeSource.CreatePayloadAsync(_root, default);
        var request = new OfficeInstallRequest(OfficePlanCatalog.ByProductId("O365HomePremRetail")!, 64, "pt-br", [], SourcePath: _root);
        string configuration = await OfficeConfigXmlBuilder.WriteToFolderAsync(request, Path.Combine(_root, "config"));
        var add = XDocument.Load(configuration).Root!.Element("Add")!;
        Assert.Equal(_root, (string?)add.Attribute("SourcePath"));
        Assert.Equal("16.0.1.2", (string?)add.Attribute("Version"));
        Assert.Equal("FALSE", (string?)add.Attribute("AllowCdnFallback"));
    }

    [Fact]
    public async Task LocalInventoryAcceptsEquivalentWindowsPathSeparators()
    {
        await FixtureOfficeSource.CreatePayloadAsync(_root, default);
        await OfficeOfflinePayload.WriteIndexAsync(_root, default);
        await OfficeOfflinePayload.VerifyAsync(_root.Replace('\\', '/'), default);
    }

    [Fact]
    public async Task LocalSourceSelectionIgnoresNewerIncompleteVersion()
    {
        await FixtureOfficeSource.CreatePayloadAsync(_root, default);
        string newer = Path.Combine(_root, "Office", "Data", "16.0.9.9");
        Directory.CreateDirectory(newer);
        await File.WriteAllTextAsync(Path.Combine(_root, "Office", "Data", "v64_16.0.9.9.cab"), "Version CAB without language");
        await File.WriteAllTextAsync(Path.Combine(newer, "stream.x64.x-none.dat"), "main stream without language");
        Assert.Equal("16.0.1.2", OfficeOfflinePayload.GetVersion(_root, 64, (string[])["pt-br"]));
        Assert.Throws<InvalidDataException>(() => OfficeOfflinePayload.GetVersion(_root, 64, (string[])["en-us"]));
    }

    [Fact]
    public void AutomaticMediaReuseDoesNotDowngradeInstalledOffice()
    {
        Assert.True(OfficeDeploymentToolService.CanReuseLocalVersion("16.0.2.0", []));
        Assert.True(OfficeDeploymentToolService.CanReuseLocalVersion("16.0.2.0", (string[])["16.0.2.0"]));
        Assert.True(OfficeDeploymentToolService.CanReuseLocalVersion("16.0.2.0", (string[])["16.0.1.0"]));
        Assert.False(OfficeDeploymentToolService.CanReuseLocalVersion("16.0.2.0", (string[])["16.0.3.0"]));
        Assert.False(OfficeDeploymentToolService.CanReuseLocalVersion("16.0.2.0", (string?[])[null]));
    }

    [Fact]
    public void MixedOfficeModesCannotCreateAmbiguousZip() =>
        Assert.Throws<InvalidDataException>(() => OfflineOfficeModuleBuilder.BuildConfiguration((OfflineCollectionRequest[])[Request(false), Request(true)]));

    [Fact]
    public void OfflineSourcesShareStreamsAcrossAppExclusions()
    {
        var request = new OfficeInstallRequest(OfficePlanCatalog.ByProductId("O365HomePremRetail")!, 64, "pt-br", [], DownloadBeforeInstall: true);
        Assert.Equal(OfficeDeploymentToolService.GetPayloadCacheKey(request), OfficeDeploymentToolService.GetPayloadCacheKey(request with { ExcludedApps = (string[])["Word"] }));
        Assert.NotEqual(OfficeDeploymentToolService.GetPayloadCacheKey(request), OfficeDeploymentToolService.GetPayloadCacheKey(request with { LanguageId = "en-us" }));
    }

    [Fact]
    public void LegacyProfileDefaultsToOnline()
    {
        var options = AotJson.Deserialize<OfficeInstallOptions>("{\"productId\":\"O365HomePremRetail\"}", WinProvisionJsonOptions.Profile)!;
        Assert.False(options.DownloadBeforeInstall);
        Assert.False(options.UseViperDownload);
    }

    [Fact]
    public void ViperChoiceRoundTripsIntoInstallRequest()
    {
        var options = AotJson.Deserialize<OfficeInstallOptions>(
            "{\"productId\":\"O365HomePremRetail\",\"downloadBeforeInstall\":true,\"useViperDownload\":true}",
            WinProvisionJsonOptions.Profile)!;
        OfficeInstallRequest request = OfficeProfileConfiguration.Build(options);
        Assert.True(request.DownloadBeforeInstall);
        Assert.True(request.UseViperDownload);
    }

    [Fact]
    public void OfficeFileListUrlsArePinnedToMicrosoftCdnAndUpgradedToHttps()
    {
        var baseUri = new Uri("http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60");
        Uri result = OfficeDeploymentToolService.RequireOfficeCdnUri(
            "http://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60/office/data/16.0.1.2/stream.x64.x-none.dat", baseUri);
        Assert.Equal("https", result.Scheme);
        Assert.Equal("officecdn.microsoft.com", result.Host);
        Assert.Throws<InvalidDataException>(() => OfficeDeploymentToolService.RequireOfficeCdnUri(
            "https://evil.example/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60/stream.dat", baseUri));
        Assert.Throws<InvalidDataException>(() => OfficeDeploymentToolService.RequireOfficeCdnUri(
            "https://officecdn.microsoft.com/pr/other-guid/stream.dat", baseUri));
    }

    [Theory]
    [InlineData("stream.x64.x-none.dat", true)]
    [InlineData("v64_16.0.1.2.cab", true)]
    [InlineData("../escape.dat", false)]
    [InlineData("C:\\escape.dat", false)]
    [InlineData("CON.txt", false)]
    public void OfficeFileNamesRejectPathTraversalAndWindowsDevices(string name, bool expected) =>
        Assert.Equal(expected, OfficeDeploymentToolService.IsSafeOfficeFileName(name));

    [Fact]
    public void OfficeFileListPathsUseC2rLocalPayloadLayout()
    {
        string result = OfficeDeploymentToolService.GetOfficePayloadRelativePath(
            "/office/data/16.0.20430.20092/", "stream.x64.pt-br.dat");

        Assert.Equal(Path.Combine("Office", "data", "16.0.20430.20092", "stream.x64.pt-br.dat"), result);
        Assert.DoesNotContain(Path.Combine("Office", "office"), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViperFallbackPreservesCompletedMediaAndRemovesOnlyUnpublishedWork()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Office", "Data"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Office", "Data", "partial.dat"), "partial");
        await File.WriteAllTextAsync(Path.Combine(_root, "Office", "Data", "pending.dat.viper.part"), "unpublished");
        await File.WriteAllTextAsync(Path.Combine(_root, "Office", "Data", "pending.dat.viper.json"), "checkpoint");
        await File.WriteAllTextAsync(Path.Combine(_root, "setup.exe"), "setup");
        await File.WriteAllTextAsync(Path.Combine(_root, "download-configuration.xml"), "config");

        OfficeDeploymentToolService.ClearOfficePayloadForRetry(_root);

        Assert.Equal("partial", await File.ReadAllTextAsync(Path.Combine(_root, "Office", "Data", "partial.dat")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Office", "Data")));
        Assert.Equal("setup", await File.ReadAllTextAsync(Path.Combine(_root, "setup.exe")));
        Assert.Equal("config", await File.ReadAllTextAsync(Path.Combine(_root, "download-configuration.xml")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficeStreamReusesVerifiedFileAndRepairsCorruptFile(bool corrupt)
    {
        Directory.CreateDirectory(_root);
        await using var server = new ViperTestServer();
        string target = Path.Combine(_root, "stream.dat");
        await File.WriteAllBytesAsync(target, corrupt ? [1, 2, 3] : server.Payload);
        await using var viper = new ViperEngine(new() { AllowHttp = true, ConnectionsPerFile = 1 });
        var request = new ViperRequest(server.Url, target)
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(server.Payload)),
            ExistingFilePolicy = ViperExistingFilePolicy.ReuseIfVerified
        };

        await OfficeDeploymentToolService.DownloadOfficeStreamAsync(viper, request, default);

        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(target));
        Assert.Equal(corrupt, !server.Requests.IsEmpty);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task FailedOfficeStreamRepairPreservesExistingFile()
    {
        Directory.CreateDirectory(_root);
        await using var server = new ViperTestServer();
        string target = Path.Combine(_root, "stream.dat");
        await File.WriteAllTextAsync(target, "previous");
        await using var viper = new ViperEngine(new() { AllowHttp = true, ConnectionsPerFile = 1 });
        await Assert.ThrowsAsync<ViperIntegrityException>(() => OfficeDeploymentToolService.DownloadOfficeStreamAsync(viper,
            new(server.Url, target) { Sha256 = new string('0', 64), ExistingFilePolicy = ViperExistingFilePolicy.ReuseIfVerified }, default));
        Assert.Equal("previous", await File.ReadAllTextAsync(target));
        Assert.Single(Directory.GetFiles(_root));
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
        new OfflineCollectionExportService(new NoPackagesSource(), source).ExportAsync("Office", (OfflineCollectionRequest[])[Request(offline)], Path.Combine(_root, "collection.zip"), null, token);

    private static async Task<int> VerifyScriptAsync(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetTempPath() };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-VerifyOnly", "-LogPath", Path.Combine(Path.GetDirectoryName(script)!, "verification-test.log") }) start.ArgumentList.Add(argument);
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
            await File.WriteAllTextAsync(Path.Combine(data, "v64_16.0.1.2.cab"), "Version CAB fixture", token);
            await File.WriteAllTextAsync(Path.Combine(data, "16.0.1.2", "stream.x64.x-none.dat"), "DAT fixture", token);
            await File.WriteAllTextAsync(Path.Combine(data, "16.0.1.2", "stream.x64.pt-br.dat"), "Language DAT fixture", token);
        }
    }
}
