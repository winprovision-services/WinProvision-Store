using System.IO.Compression;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Profile;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OfflineCollectionExportTests
{
    private static CatalogInstaller Installer(string arch, string type = "exe") => new()
    {
        Architecture = arch, Type = type, Url = "https://example.test/setup.exe",
        Sha256 = Convert.ToHexString(SHA256.HashData([1, 2, 3])), SilentSupported = true, SilentArgs = "/quiet"
    };

    [Fact]
    public void SelectsExportableAlternativeInsteadOfUnsupportedFirstInstaller()
    {
        var unsupported = Installer("x64", "msix");
        var supported = Installer("x64");
        var manifest = new PackageManifest { Installers = [unsupported, supported] };
        Assert.Same(supported, OfflineCollectionExportService.SelectInstaller(manifest,
            new PackageInstallOptions(Architecture: "x64"), out string? reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("nullsoft", "/S", ".exe", "NullsoftInst")]
    [InlineData("inno", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", ".exe", "Inno Setup Setup Data")]
    [InlineData("msi", "/qn /norestart", ".msi", null)]
    [InlineData("wix", "/qn /norestart", ".msi", null)]
    [InlineData("burn", "/quiet /norestart", ".exe", ".wixburn")]
    public async Task ExportsWithoutSwitchesUsingManifestInstallerTypeDefaults(
        string type, string expectedArguments, string extension, string? marker)
    {
        await using var fixture = new OfflineFixture(type, extension, marker);
        OfflineCollectionResult result = await fixture.ExportAsync("Teste.PacoteA");

        OfflineCollectionPackage package = Assert.Single(result.Packages);
        Assert.Equal(expectedArguments, package.SilentArguments);
        Assert.Equal("type-default", package.SilentSource);
        Assert.Equal("Incluído", Assert.Single(result.ReportItems!).Status);
        Assert.NotNull(result.FilePath);
        using ZipArchive archive = ZipFile.OpenRead(result.FilePath!);
        Assert.Contains(archive.Entries, entry => entry.FullName == package.InstallerPath + ".json");
        Assert.Contains(archive.Entries, entry => entry.FullName == OfflineCollectionExportService.ReportFile);
        LanInstallerSidecar sidecar = await ReadZipEntryJsonAsync<LanInstallerSidecar>(archive, package.InstallerPath + ".json");
        Assert.Equal("Teste.PacoteA", sidecar.PackageId);
        Assert.Equal("1.2.3", sidecar.Version);
        Assert.Equal(package.InstallerType, sidecar.InstallerType);
        Assert.Equal(expectedArguments, sidecar.SilentArgs);
        Assert.Equal("type-default", sidecar.SilentSource);
    }

    [Theory]
    [InlineData("Teste.PacoteA")]
    [InlineData("Teste.PacoteB")]
    [InlineData("Teste.Z9F4c1")]
    public async Task SilentResolutionDoesNotDependOnPackageIdentity(string packageId)
    {
        await using var fixture = new OfflineFixture("nullsoft", ".exe", "NullsoftInst");
        OfflineCollectionResult result = await fixture.ExportAsync(packageId);
        OfflineCollectionPackage package = Assert.Single(result.Packages);

        Assert.Equal("/S", package.SilentArguments);
        Assert.Equal("type-default", package.SilentSource);
        Assert.Equal("Incluído", Assert.Single(result.ReportItems!).Status);
    }

    [Fact]
    public async Task ExeManifestSwitchesAndManualArgumentsFollowSourcePriority()
    {
        await using var fixture = new OfflineFixture("exe", ".exe", "NullsoftInst",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Silent"] = "/manifest",
                ["SilentWithProgress"] = "/progress"
            });
        OfflineCollectionResult fromManifest = await fixture.ExportAsync("Teste.Manifesto");
        Assert.Equal("/manifest", Assert.Single(fromManifest.Packages).SilentArguments);
        Assert.Equal("manifest", Assert.Single(fromManifest.Packages).SilentSource);

        var source = InstallerSilentArgumentsResolver.Resolve("/manual", "/manifest", "/progress", "nullsoft", "inno");
        Assert.Equal(new InstallerSilentArgumentResolution("/manual", InstallerSilentArgumentSource.Manual), source);
        source = InstallerSilentArgumentsResolver.Resolve(null, "/manifest", "/progress", "nullsoft", "inno");
        Assert.Equal(new InstallerSilentArgumentResolution("/manifest", InstallerSilentArgumentSource.Manifest), source);
        source = InstallerSilentArgumentsResolver.Resolve(null, null, "/progress", "nullsoft", "inno");
        Assert.Equal(new InstallerSilentArgumentResolution("/progress", InstallerSilentArgumentSource.Manifest), source);
        source = InstallerSilentArgumentsResolver.Resolve(null, null, null, "nullsoft", "inno");
        Assert.Equal(new InstallerSilentArgumentResolution("/S", InstallerSilentArgumentSource.TypeDefault), source);
        source = InstallerSilentArgumentsResolver.Resolve(null, null, null, "exe", "inno");
        Assert.Equal(new InstallerSilentArgumentResolution("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", InstallerSilentArgumentSource.Detected), source);
    }

    [Fact]
    public async Task UnknownExecutableWithoutRecognizedSignatureIsIncludedWithWarning()
    {
        await using var fixture = new OfflineFixture("unknown", ".exe", null);
        OfflineCollectionResult result = await fixture.ExportAsync("Teste.Desconhecido");
        OfflineCollectionPackage package = Assert.Single(result.Packages);
        OfflineCollectionReportItem report = Assert.Single(result.ReportItems!);

        Assert.Empty(package.SilentArguments);
        Assert.Equal("unsupported", package.SilentSource);
        Assert.Equal("Incluído com aviso", report.Status);
        Assert.Contains("Sem suporte silencioso", report.Reason);
        Assert.False(report.RequiresInternet);
        Assert.DoesNotContain(InstallerSilentArgumentsResolver.InternetRequiredMessage, report.Reason);
        Assert.NotNull(result.FilePath);
    }

    [Fact]
    public async Task FailedItemDoesNotDiscardSuccessfulItemsAndReportRecordsBoth()
    {
        await using var fixture = new OfflineFixture("nullsoft", ".exe", "NullsoftInst");
        fixture.Source.FailOnceIds.Add("Teste.Falha");
        OfflineCollectionResult result = await fixture.ExportAsync((string[])["Teste.Ok", "Teste.Falha"]);

        Assert.Single(result.Packages);
        Assert.Equal(2, result.ReportItems!.Count);
        Assert.Equal("Incluído", result.ReportItems[0].Status);
        Assert.Equal("Falhou", result.ReportItems[1].Status);
        Assert.Equal("consultar o catálogo", result.ReportItems[1].Stage);
        using ZipArchive archive = ZipFile.OpenRead(result.FilePath!);
        OfflineCollectionReport report = await ReadZipEntryJsonAsync<OfflineCollectionReport>(archive, OfflineCollectionExportService.ReportFile);
        Assert.Equal("Falhou", report.Items[1].Status);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("Teste.Falha", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TotalFailureDoesNotPublishPartialZip()
    {
        await using var fixture = new OfflineFixture("nullsoft", ".exe", "NullsoftInst");
        fixture.Source.FailOnceIds.Add("Teste.Falha");
        OfflineCollectionResult result = await fixture.ExportAsync("Teste.Falha");

        Assert.Null(result.FilePath);
        Assert.Equal("Falhou", Assert.Single(result.ReportItems!).Status);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "export.zip")));
    }

    [Fact]
    public async Task RetryOnlyRedownloadsFailedItemsAndPreservesVerifiedZipContent()
    {
        await using var fixture = new OfflineFixture("nullsoft", ".exe", "NullsoftInst");
        fixture.Source.FailOnceIds.Add("Teste.Retry");
        OfflineCollectionRequest[] requests =
        [
            new("Teste.Ok", "OK", "winget"),
            new("Teste.Retry", "Retry", "winget")
        ];
        string destination = Path.Combine(fixture.DirectoryPath, "retry.zip");
        OfflineCollectionResult first = await fixture.Service.ExportAsync("Teste", requests, destination, null, default);
        Assert.Equal("Falhou", first.ReportItems!.Single(item => item.Id == "Teste.Retry").Status);
        Assert.Equal(1, fixture.Source.DownloadCount);

        OfflineCollectionResult retried = await fixture.Service.RetryFailedAsync("Teste", requests, destination, first, null, default);

        Assert.Equal(2, retried.Packages.Count);
        Assert.Equal(2, fixture.Source.DownloadCount);
        Assert.All(retried.ReportItems!, item => Assert.Equal("Incluído", item.Status));
        using ZipArchive archive = ZipFile.OpenRead(destination);
        Assert.Equal(2, archive.Entries.Count(entry => entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(archive.Entries, entry => entry.FullName == OfflineCollectionExportService.ReportFile);
    }

    [Fact]
    public async Task ManifestInternetSignalAddsWarningNotFailure()
    {
        await using var fixture = new OfflineFixture("nullsoft", ".exe", "NullsoftInst");
        fixture.Source.RequiresInternet = true;
        OfflineCollectionResult result = await fixture.ExportAsync("Teste.Internet");
        OfflineCollectionReportItem report = Assert.Single(result.ReportItems!);

        Assert.Equal("Incluído com aviso", report.Status);
        Assert.Equal(InstallerSilentArgumentsResolver.InternetRequiredMessage, report.Reason);
        Assert.NotNull(result.FilePath);
    }

    [Fact]
    public async Task ExpectedSizeHeuristicAddsInternetWarningWithoutFailingItem()
    {
        await using var fixture = new OfflineFixture("nullsoft", ".exe", "NullsoftInst");
        OfflineCollectionResult result = await fixture.ExportAsync("Teste.TamanhoEsperado", 50 * 1024 * 1024);
        OfflineCollectionReportItem report = Assert.Single(result.ReportItems!);

        Assert.Equal("Incluído com aviso", report.Status);
        Assert.True(report.RequiresInternet);
        Assert.Equal(InstallerSilentArgumentsResolver.InternetRequiredMessage, report.Reason);
        Assert.NotNull(result.FilePath);
    }

    [Fact]
    public void DoesNotExportInstallerWhenProviderProhibitsDownload()
    {
        var installer = Installer("x64");
        installer.DownloadCommandProhibited = true;
        var selected = OfflineCollectionExportService.SelectInstaller(new PackageManifest { Installers = [installer] },
            new PackageInstallOptions(Architecture: "x64"), out string? reason);
        Assert.Null(selected);
        Assert.Contains("não permite", reason);
    }

    [Fact]
    public void UnspecifiedArchitecturePermitsCompatibleX86Fallback()
    {
        if (RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64)) return;
        var installer = Installer("x86");
        Assert.Same(installer, OfflineCollectionExportService.SelectInstaller(
            new PackageManifest { Installers = [installer] }, null, out _));
    }

    [Fact]
    public async Task ExportIncludesCaseInsensitiveIdAndReportsTimeoutForOtherItem()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "pacotes.zip");
        try
        {
            var source = new FixtureSource();
            var service = new OfflineCollectionExportService(source);
            OfflineCollectionResult result = await service.ExportAsync("Teste",
                (OfflineCollectionRequest[])[new("example.app", "Exemplo", "winget"), new("example.timeout", "Lento", "winget")], path, null, default);
            Assert.Single(result.Packages);
            Assert.Equal("example.app", result.Packages[0].Id);
            Assert.Contains("tempo limite", Assert.Single(result.Skipped).Reason);
            using var zip = ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, item => item.FullName.EndsWith("Readme.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(zip.Entries, item => item.FullName.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MicrosoftStorePackageIsSkippedWithoutQueryingCatalog()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = new StoreRejectingSource();
            var service = new OfflineCollectionExportService(source, chocolateySource: new FixtureChocolateyPackageSource());
            OfflineCollectionResult result = await service.ExportAsync("Teste",
                (OfflineCollectionRequest[])[new("9WZDNCRFJ3TJ", "Store app", "msstore")],
                Path.Combine(directory, "store.zip"), null, default);
            Assert.Null(result.FilePath);
            Assert.Equal("Falhou", Assert.Single(result.ReportItems!).Status);
            Assert.Contains("Microsoft Store", Assert.Single(result.Skipped).Reason);
            Assert.Equal(0, source.ManifestCalls);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ChocolateyOnlyCollectionExportsZipScriptAndVersionWithoutCatalogRequest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = new StoreRejectingSource();
            var service = new OfflineCollectionExportService(source, chocolateySource: new FixtureChocolateyPackageSource());
            OfflineCollectionResult result = await service.ExportAsync("Chocolatey",
                (OfflineCollectionRequest[])[new("git.install", "Git", "chocolatey", new PackageInstallOptions(Version: "2.45.1"))],
                Path.Combine(directory, "chocolatey.zip"), null, default);
            Assert.Empty(result.Packages);
            OfflineChocolateyPackage chocolateyPackage = Assert.Single(result.ChocolateyPackages!);
            Assert.Equal("2.45.1", chocolateyPackage.Version);
            Assert.Equal(0, source.ManifestCalls);

            using var zip = ZipFile.OpenRead(result.FilePath!);
            string manifest = await ReadZipEntryAsync(zip, "Instaladores/colecao.json");
            Assert.Contains("\"ChocolateyPackages\"", manifest);
            Assert.Contains("\"Id\": \"git.install\"", manifest);
            string readme = await ReadZipEntryAsync(zip, "Readme.txt");
            Assert.Contains("inclui os arquivos NUPKG selecionados", readme);
            Assert.Contains("payloads de instaladores externos ainda podem exigir conexão com a internet", readme);
            Assert.Contains(zip.Entries, item => item.FullName == chocolateyPackage.PackagePath);
            string script = await ReadZipEntryAsync(zip, "Install.ps1");
            Assert.Contains("ChocolateyPackages", script);
            Assert.Contains("choco.exe", script);
            Assert.Contains("--allow-downgrade", script);

            string extracted = Path.Combine(directory, "extraido");
            ZipFile.ExtractToDirectory(result.FilePath!, extracted);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(Path.Combine(extracted, "Install.ps1"));
            start.ArgumentList.Add("-VerifyOnly");
            using Process process = Process.Start(start)!;
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, $"VerifyOnly retornou {process.ExitCode}: {stdout}\n{stderr}");
            Assert.Contains("Todos os arquivos foram verificados", stdout);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CollectionInstallScriptUsesChocolateyForChocolateyEntries()
    {
        var app = new AppEntry { Id = "git.install", Name = "Git", Source = "chocolatey" };
        string script = new CollectionScriptService(null).Build((AppEntry[])[app], _ => "2.45.1");

        Assert.Contains("if ($package.Source -eq 'chocolatey')", script);
        Assert.Contains("Get-Command choco.exe", script);
        Assert.Contains("'--version'", script);
        Assert.DoesNotContain("--source 'chocolatey'", script);
        Assert.Equal(1, script.Split("function Test-WinProvisionElevated", StringSplitOptions.None).Length - 1);
        Assert.Contains("Invoke-WinProvisionSelfElevation -ScriptPath $PSCommandPath", script);
        Assert.DoesNotContain("$start.Verb = 'RunAs'", script);
    }

    [Fact]
    public void CollectionInstallScriptAppliesSharedArgumentPreferencesByRecordedSource()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var preferences = new ElevationPreferencesService(Path.Combine(folder, "elevation.json"));
            preferences.SetBoolean("allowManualSilentArguments", false);
            preferences.SetBoolean("allowImportedSilentArguments", false);
            var app = new AppEntry { Id = "synthetic.package", Name = "Synthetic", Source = "winget" };

            string script = new CollectionScriptService(null, preferences).Build((AppEntry[])[app]);

            Assert.Contains("$allowManualSilentArguments = $false", script);
            Assert.Contains("$allowExternalSilentArguments = $false", script);
            Assert.Contains("$sourceIsExternal = $argumentSource -notin $nonExternalSilentSources", script);
            Assert.Contains("Sem suporte silencioso compatível com as preferências atuais.", script);
            Assert.Contains("'/S'", script);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CollectionInstallScriptRejectsInvalidChocolateyIds()
    {
        var app = new AppEntry { Id = "git; Remove-Item C:\\", Name = "Git", Source = "chocolatey" };
        Assert.Throws<InvalidOperationException>(() => new CollectionScriptService(null).Build((AppEntry[])[app]));
    }

    private static async Task<string> ReadZipEntryAsync(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = Assert.Single(archive.Entries, item => item.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<T> ReadZipEntryJsonAsync<T>(ZipArchive archive, string path)
    {
        string json = await ReadZipEntryAsync(archive, path);
        return AotJson.Deserialize<T>(json)
            ?? throw new InvalidDataException("JSON de teste vazio.");
    }

    private sealed class OfflineFixture : IAsyncDisposable
    {
        private readonly byte[] _installerBytes;

        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "WinProvision-offline-" + Guid.NewGuid().ToString("N"));
        public SyntheticSource Source { get; }
        public OfflineCollectionExportService Service { get; }

        public OfflineFixture(string installerType, string extension, string? marker,
            Dictionary<string, string>? installerSwitches = null)
        {
            System.IO.Directory.CreateDirectory(DirectoryPath);
            _installerBytes = CreateInstallerBytes(extension, marker);
            Source = new SyntheticSource(id => new CatalogInstaller
            {
                Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                Type = installerType,
                Url = $"https://example.test/{Uri.EscapeDataString(id)}{extension}",
                Sha256 = Convert.ToHexString(SHA256.HashData(_installerBytes)),
                SilentSupported = false,
                SilentArgs = null,
                SilentSource = "none",
                InstallerSwitches = installerSwitches is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(installerSwitches, StringComparer.OrdinalIgnoreCase)
            }, _installerBytes);
            Service = new OfflineCollectionExportService(Source);
        }

        public Task<OfflineCollectionResult> ExportAsync(string id, long? expectedSizeBytes = null) =>
            ExportAsync((string[])[id], expectedSizeBytes);

        public Task<OfflineCollectionResult> ExportAsync(IReadOnlyList<string> ids, long? expectedSizeBytes = null)
        {
            OfflineCollectionRequest[] requests = ids.Select(id =>
                new OfflineCollectionRequest(id, id, "winget", ExpectedInstallerSizeBytes: expectedSizeBytes)).ToArray();
            return Service.ExportAsync("Teste", requests, Path.Combine(DirectoryPath, "export.zip"), null, default);
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.Directory.Exists(DirectoryPath))
                System.IO.Directory.Delete(DirectoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static byte[] CreateInstallerBytes(string extension, string? marker)
        {
            if (extension == ".msi")
                return [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

            const int peOffset = 0x80;
            const int sectionRawOffset = 0x200;
            const int sectionRawSize = 0x80;
            byte[] overlay = System.Text.Encoding.ASCII.GetBytes(marker ?? string.Empty);
            byte[] bytes = new byte[sectionRawOffset + sectionRawSize + overlay.Length];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3c);
            bytes[peOffset] = (byte)'P';
            bytes[peOffset + 1] = (byte)'E';
            BitConverter.GetBytes((ushort)1).CopyTo(bytes, peOffset + 6);
            BitConverter.GetBytes((ushort)0xE0).CopyTo(bytes, peOffset + 20);
            int sectionOffset = peOffset + 24 + 0xE0;
            System.Text.Encoding.ASCII.GetBytes(marker == ".wixburn" ? ".wixburn" : ".text").CopyTo(bytes, sectionOffset);
            BitConverter.GetBytes((uint)sectionRawSize).CopyTo(bytes, sectionOffset + 16);
            BitConverter.GetBytes((uint)sectionRawOffset).CopyTo(bytes, sectionOffset + 20);
            overlay.CopyTo(bytes, sectionRawOffset + sectionRawSize);
            return bytes;
        }
    }

    private sealed class SyntheticSource(Func<string, CatalogInstaller> installerFactory, byte[] bytes) : IOfflineCollectionSource
    {
        public HashSet<string> FailOnceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ManifestCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int DownloadCount { get; private set; }
        public bool RequiresInternet { get; set; }

        public Task<PackageManifest?> GetManifestAsync(string id, CancellationToken token)
        {
            ManifestCalls.TryGetValue(id, out int calls);
            ManifestCalls[id] = calls + 1;
            if (FailOnceIds.Remove(id))
                return Task.FromResult<PackageManifest?>(null);
            CatalogInstaller installer = installerFactory(id);
            installer.RequiresInternet = RequiresInternet;
            return Task.FromResult<PackageManifest?>(new PackageManifest
            {
                Id = id,
                Version = "1.2.3",
                Source = "winget",
                Installers = [installer]
            });
        }

        public async Task DownloadAsync(CatalogInstaller installer, string destination,
            Action<InstallProgressUpdate>? progress, CancellationToken token)
        {
            DownloadCount++;
            await File.WriteAllBytesAsync(destination, bytes, token);
            progress?.Invoke(new(InstallProgressPhase.Downloading, 100));
        }
    }

    [Fact]
    public async Task OfficeFailureIsReportedWithoutDiscardingOtherPackages()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new OfflineCollectionExportService(new FixtureSource(), new FailingOfficeSource());
            var office = new OfflineCollectionRequest("office.test", "Office", "office", IsOffice: true,
                Office: new OfficeInstallOptions { ProductId = "O365HomePremRetail" });
            var result = await service.ExportAsync("Teste", (OfflineCollectionRequest[])[new("example.app", "Exemplo", "winget"), office],
                Path.Combine(directory, "pacotes.zip"), null, default);
            Assert.Single(result.Packages);
            Assert.Contains("Office indisponível", Assert.Single(result.Skipped).Reason);
            Assert.Null(result.Office);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FailingOfficeSource : IOfflineOfficeSource
    {
        public Task<string> CopySetupAsync(string destination, IProgress<OfflineCollectionProgress>? progress, CancellationToken token) =>
            Task.FromException<string>(new IOException("Office indisponível"));
        public Task DownloadPayloadAsync(string setup, string configuration, IProgress<OfflineCollectionProgress>? progress, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class StoreRejectingSource : IOfflineCollectionSource
    {
        public int ManifestCalls { get; private set; }
        public Task<PackageManifest?> GetManifestAsync(string id, CancellationToken token)
        { ManifestCalls++; throw new Xunit.Sdk.XunitException("Microsoft Store não deve consultar o catálogo próprio."); }
        public Task DownloadAsync(CatalogInstaller installer, string destination, Action<InstallProgressUpdate>? progress, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class FixtureChocolateyPackageSource : IOfflineChocolateyPackageSource
    {
        public async Task DownloadPackageAsync(string id, string version, string destination,
            Action<InstallProgressUpdate>? progress, CancellationToken token)
        {
            await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            ZipArchiveEntry nuspec = archive.CreateEntry(id + ".nuspec");
            await using Stream output = nuspec.Open();
            await using var writer = new StreamWriter(output);
            await writer.WriteAsync($"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
            progress?.Invoke(new(InstallProgressPhase.Downloading, 100));
        }
    }

    private sealed class FixtureSource : IOfflineCollectionSource
    {
        public Task<PackageManifest?> GetManifestAsync(string id, CancellationToken token) =>
            id.EndsWith("timeout", StringComparison.OrdinalIgnoreCase)
                ? Task.FromException<PackageManifest?>(new TaskCanceledException("timeout"))
                : Task.FromResult<PackageManifest?>(new PackageManifest { Id = "Example.App", Version = "1", Installers = [Installer(RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant())] });
        public Task DownloadAsync(CatalogInstaller installer, string destination, Action<InstallProgressUpdate>? progress, CancellationToken token) =>
            File.WriteAllBytesAsync(destination, [1, 2, 3], token);
    }
}
