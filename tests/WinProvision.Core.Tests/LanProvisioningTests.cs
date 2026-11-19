using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class LanProvisioningTests
{
    [Theory]
    [InlineData(unchecked((int)0x80008083), "hostfxr", ".NET 10")]
    [InlineData(unchecked((int)0x80008096), "hostpolicy", "reinstale")]
    [InlineData(unchecked((int)0x80008081), "runtime .NET", ".NET 10")]
    [InlineData(unchecked((int)0x80008082), "compatível", "worker")]
    [InlineData(unchecked((int)0x800700C1), "arquitetura", "correta")]
    [InlineData(193, "arquitetura", "correta")]
    [InlineData(23, "encerrou", "worker")]
    public void WorkerHostExitCodesHaveActionableStartupMessages(int exitCode, string expectedCause, string expectedAction)
    {
        string workerPath = @"C:\Program Files\WinProvision Store\cli\winprovision.exe";
        LanInstallBatchResult result = LanWorkerStartupFailurePolicy.CreateResult(
            new[] { CreateWorkerRequest("setup.exe") }, exitCode, workerPath, "stdout info", "stderr error");
        LanInstallItemResult item = Assert.Single(result.Items);

        Assert.Equal($"0x{unchecked((uint)exitCode):X8}", $"0x{unchecked((uint)item.ExitCode):X8}");
        Assert.Contains(expectedCause, item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedAction, item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Etapa: início do worker", item.Message);
        Assert.Contains(workerPath, item.Message);
        Assert.Equal(item.Message, result.Message);
        Assert.Contains("stdout info", item.ExceptionDetails);
        Assert.Contains("stderr error", item.ExceptionDetails);
    }

    [Fact]
    public async Task WorkerStartupFailureBecomesOneSectionMessageAndAnErrorForEveryItem()
    {
        LanInstallBatchRequest request = new(
        new[]
        {
            CreateWorkerRequest("steam.exe"),
            CreateWorkerRequest("hydra.exe")
        });
        int probeCalls = 0;
        var service = new LanInstallService(
            new LanHttpTransportSecurityPolicy(),
            Array.Empty<IClientModuleHandler>(),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            workerSelfTest: (_, _) =>
            {
                probeCalls++;
                return Task.FromResult(new LanWorkerSelfTestResult(
                    false, unchecked((int)0x80008083), "", "hostfxr missing"));
            });
        LanInstallBatchResult result = await service.InstallAsync(request.Installers);

        Assert.Equal(1, probeCalls);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains("0x80008083", result.Message);
        Assert.Contains("Reinstale o WinProvision Store", result.Message);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(LanInstallerOutcome.Failed, item.Outcome);
            Assert.Equal(LanInstallerExecutionStage.StartingWorker, item.Stage);
            Assert.Contains("0x80008083", item.Message);
            Assert.Contains("hostfxr", item.Message);
        });
        Assert.Equal(new[] { "steam.exe", "hydra.exe" }, result.Items.Select(item => item.File));
    }

    private static LanInstallerInstallRequest CreateWorkerRequest(string file) =>
        new(@"C:\cache\" + file, file, "Vendor.Test", "1.0", new string('A', 64), "exe", "user", "/S", false, false);

    [Fact]
    public void TokenAcceptsValidBearerAndRejectsInvalidBearer()
    {
        string token = LanTokenAuthenticator.GenerateToken();
        using var authenticator = new LanTokenAuthenticator(token);
        IPAddress client = IPAddress.Parse("192.168.1.24");

        Assert.Equal(LanAuthenticationResult.Authorized, authenticator.Authorize(client, $"Bearer {token}"));
        Assert.Equal(LanAuthenticationResult.Unauthorized, authenticator.Authorize(client, "Bearer invalid"));
        Assert.Equal(LanAuthenticationResult.PrivateNetworkRequired,
            authenticator.Authorize(IPAddress.Parse("8.8.8.8"), $"Bearer {token}"));
    }

    [Fact]
    public void RepeatedInvalidTokensTemporarilyBlockThatClientAddress()
    {
        string token = LanTokenAuthenticator.GenerateToken();
        using var authenticator = new LanTokenAuthenticator(token);
        IPAddress client = IPAddress.Parse("10.2.3.4");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int attempt = 0; attempt < 4; attempt++)
            Assert.Equal(LanAuthenticationResult.Unauthorized,
                authenticator.Authorize(client, "Bearer invalid", now.AddSeconds(attempt)));
        Assert.Equal(LanAuthenticationResult.TemporarilyBlocked,
            authenticator.Authorize(client, "Bearer invalid", now.AddSeconds(4)));
        Assert.Equal(LanAuthenticationResult.TemporarilyBlocked,
            authenticator.Authorize(client, $"Bearer {token}", now.AddSeconds(5)));
        Assert.Equal(LanAuthenticationResult.Authorized,
            authenticator.Authorize(client, $"Bearer {token}", now.AddMinutes(10)));
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("..\\outside.exe")]
    [InlineData("folder\\installer.exe")]
    [InlineData("C:\\installer.exe")]
    [InlineData("installer.exe/../outside.exe")]
    public void ServerFileResolutionRejectsTraversal(string fileName)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-lan-" + Guid.NewGuid().ToString("N"));
        Assert.Null(LanInstallerHttpServer.ResolveSafeFilePath(root, fileName));
    }

    [Fact]
    public void ServerFileResolutionAcceptsOnlyAPlainFileNameInsideRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-lan-" + Guid.NewGuid().ToString("N"));
        string? path = LanInstallerHttpServer.ResolveSafeFilePath(root, "setup.exe");

        Assert.Equal(Path.Combine(Path.GetFullPath(root), "setup.exe"), path);
    }

    [Theory]
    [InlineData("/S", true)]
    [InlineData("/quiet /norestart", true)]
    [InlineData("/S & whoami", false)]
    [InlineData("/S | whoami", false)]
    [InlineData("/S;whoami", false)]
    [InlineData("`whoami`", false)]
    [InlineData("$env:TEMP", false)]
    [InlineData("/S > output.txt", false)]
    [InlineData("/S\r\nnext", false)]
    public void SilentArgumentsRejectShellMetacharacters(string arguments, bool expected) =>
        Assert.Equal(expected, LanInstallerValidation.IsSafeSilentArguments(arguments));

    [Theory]
    [InlineData("setup.exe", true)]
    [InlineData("installer.msi", true)]
    [InlineData("sub\\setup.exe", false)]
    [InlineData("sub/setup.exe", false)]
    [InlineData("..", false)]
    [InlineData("C:\\setup.exe", false)]
    public void InstallerFileMustBeAPlainFileName(string file, bool expected) =>
        Assert.Equal(expected, LanInstallerValidation.IsSafeFileName(file));

    [Fact]
    public void LanClientRejectsCacheEqualToOrNestedUnderServerRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-lan-root-" + Guid.NewGuid().ToString("N"));
        Assert.True(LanInstallerClient.PathsOverlap(root, root));
        Assert.True(LanInstallerClient.PathsOverlap(Path.Combine(root, "cache"), root));
        Assert.True(LanInstallerClient.PathsOverlap(root, Path.Combine(root, "cache")));
        Assert.False(LanInstallerClient.PathsOverlap(root, root + "-other"));
    }

    [Fact]
    public async Task LanClientRefusesOverlappingServerAndCacheFoldersBeforeConnecting()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-lan-root-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new IndexOnlyHandler());
        var client = new LanInstallerClient(http, Array.Empty<IClientModuleHandler>(),
            Path.Combine(root, "cache"), serverRoot: root);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SynchronizeAsync("http://192.168.1.23:8080", "test-token-at-least-20-chars"));

        Assert.Contains(root, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(".msi", null, LanInstallerKind.Msi)]
    [InlineData(".exe", ".wixburn", LanInstallerKind.Burn)]
    [InlineData(".exe", "Inno Setup Setup Data", LanInstallerKind.InnoSetup)]
    [InlineData(".exe", "NullsoftInst", LanInstallerKind.Nsis)]
    [InlineData(".exe", "InstallShield", LanInstallerKind.InstallShield)]
    [InlineData(".exe", null, LanInstallerKind.Unknown)]
    public void DetectorReadsInstallerSignaturesWithoutExecutingFiles(string extension, string? marker, LanInstallerKind expected)
    {
        string path = CreateInstallerFixture(extension, marker);
        try
        {
            Assert.Equal(expected, LanInstallerTypeDetector.Detect(path).Kind);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(LanInstallerKind.Msi, "/qn /norestart")]
    [InlineData(LanInstallerKind.InnoSetup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")]
    [InlineData(LanInstallerKind.Nsis, "/S")]
    [InlineData(LanInstallerKind.Burn, "/quiet /norestart")]
    [InlineData(LanInstallerKind.InstallShield, null)]
    [InlineData(LanInstallerKind.Unknown, null)]
    public void InstallerTypeArgumentDefaultsAreExplicit(LanInstallerKind kind, string? expected) =>
        Assert.Equal(expected, LanInstallerArgumentsPolicy.DefaultInstallArguments(kind));

    [Fact]
    public void SilentArgumentSourcesFollowManualThenServerThenTypeDefaultPriority()
    {
        Assert.Equal(new LanInstallerArguments("/custom", LanInstallerArgumentSource.Manual),
            LanInstallerArgumentsPolicy.Resolve(LanInstallerKind.Nsis, "/server", "/custom"));
        Assert.Equal(new LanInstallerArguments("/server", LanInstallerArgumentSource.Server),
            LanInstallerArgumentsPolicy.Resolve(LanInstallerKind.Nsis, "/server", null));
        Assert.Equal(new LanInstallerArguments("/S", LanInstallerArgumentSource.Detected),
            LanInstallerArgumentsPolicy.Resolve(LanInstallerKind.Nsis, null, null));
        Assert.Equal(LanInstallerArgumentSource.Unsupported,
            LanInstallerArgumentsPolicy.Resolve(LanInstallerKind.Unknown, null, null).Source);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("server")]
    [InlineData("zip")]
    [InlineData("collection")]
    public void ExternalArgumentsUseTheSamePreferencePolicyForEveryRecordedOrigin(string source)
    {
        LanInstallerArguments allowed = LanInstallerArgumentsPolicy.Resolve(
            LanInstallerKind.Nsis, "/external", null, source, allowExternalArguments: true);
        LanInstallerArguments blocked = LanInstallerArgumentsPolicy.Resolve(
            LanInstallerKind.Nsis, "/external", null, source, allowExternalArguments: false);

        Assert.Equal("/external", allowed.Value);
        Assert.Equal("/S", blocked.Value);
        Assert.Equal(LanInstallerArgumentSource.Detected, blocked.Source);
    }

    [Fact]
    public void DisabledManualArgumentsAreIgnoredAndCannotOverrideExternalOrTypeDefaults()
    {
        LanInstallerArguments withExternal = LanInstallerArgumentsPolicy.Resolve(
            LanInstallerKind.Nsis, "/external", "/manual", "manifest",
            allowManualArguments: false, allowExternalArguments: true);
        LanInstallerArguments withoutExternal = LanInstallerArgumentsPolicy.Resolve(
            LanInstallerKind.Nsis, null, "/manual", "manifest",
            allowManualArguments: false, allowExternalArguments: false);

        Assert.Equal(new LanInstallerArguments("/external", LanInstallerArgumentSource.Manifest), withExternal);
        Assert.Equal(new LanInstallerArguments("/S", LanInstallerArgumentSource.Detected), withoutExternal);
    }

    [Fact]
    public void InstallerSelectionDefaultsToVerifiedSupportedItemsOnly()
    {
        Assert.True(LanInstallerSelectionPolicy.SelectByDefault(true, true, true, true));
        Assert.False(LanInstallerSelectionPolicy.SelectByDefault(false, true, true, true));
        Assert.False(LanInstallerSelectionPolicy.SelectByDefault(true, false, true, true));
        Assert.False(LanInstallerSelectionPolicy.SelectByDefault(true, true, false, true));
        Assert.False(LanInstallerSelectionPolicy.SelectByDefault(true, true, true, false));
    }

    [Fact]
    public void ConnectionCodeRoundTripsAndRejectsMalformedOrNonPrivateAddresses()
    {
        const string address = "http://192.168.1.23:8080";
        const string token = "connection-token-secret";

        string code = LanConnectionCode.Encode(address, token);

        Assert.True(LanConnectionCode.TryDecode(code, out LanClientConnection? decoded));
        Assert.Equal(new LanClientConnection(address, token), decoded);
        Assert.False(LanConnectionCode.TryDecode("WPSLAN1:%%%", out _));
        Assert.False(LanConnectionCode.TryDecode(
            "WPSLAN1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("1\nhttp://8.8.8.8:8080\ntoken")), out _));
    }

    [Fact]
    public void ExitCodePolicyClassifiesSuccessRebootCancellationAndFailure()
    {
        Assert.Equal(LanInstallerOutcome.Installed, LanInstallerExitCodePolicy.Classify("setup.msi", 0));
        Assert.Equal(LanInstallerOutcome.RestartPending, LanInstallerExitCodePolicy.Classify("setup.msi", 3010));
        Assert.Equal(LanInstallerOutcome.RestartPending, LanInstallerExitCodePolicy.Classify("setup.msi", 1641));
        Assert.Equal(LanInstallerOutcome.Canceled, LanInstallerExitCodePolicy.Classify("setup.msi", 1602));
        Assert.Equal(LanInstallerOutcome.Canceled, LanInstallerExitCodePolicy.Classify("setup.exe", 1223));
        Assert.Equal(LanInstallerOutcome.Failed, LanInstallerExitCodePolicy.Classify("setup.exe", 3010));
    }

    [Fact]
    public void MetadataReaderUsesMsiPropertyTableAndCleansExeFallbackName()
    {
        string msi = CreateMsiFixtureWithMetadata("Chrome Fixture", "157.1.2");
        string exe = Path.Combine(Path.GetTempPath(), "Installer_x64_machine_en-US.exe");
        try
        {
            LanInstallerMetadata msiMetadata = LanInstallerMetadataReader.Read(msi);
            Assert.Equal("Chrome Fixture", msiMetadata.Name);
            Assert.Equal("157.1.2", msiMetadata.Version);

            File.WriteAllBytes(exe, CreateFixtureBytes());
            LanInstallerMetadata exeMetadata = LanInstallerMetadataReader.Read(exe);
            Assert.Equal("Installer", exeMetadata.Name);
            Assert.Null(exeMetadata.Version);
        }
        finally
        {
            File.Delete(msi);
            if (File.Exists(exe))
                File.Delete(exe);
        }
    }

    [Fact]
    public async Task ProtectedStagingCopiesAndRevalidatesHashBeforeUse()
    {
        string cacheRoot = CreateTemporaryDirectory("lan-stage-cache");
        string runRoot = CreateTemporaryDirectory("lan-stage-run");
        byte[] bytes = CreateFixtureBytes();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string source = Path.Combine(cacheRoot, hash, "setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, bytes);
        var request = new LanInstallerInstallRequest(source, "setup.exe", "Vendor.Setup", "1.0.0",
            hash, "exe", "user", "/S", false, false);
        try
        {
            string staged = await LanProtectedInstallerStager.StageAsync(request, cacheRoot, runRoot);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(staged));

            File.Delete(staged);
            byte[] modifiedBytes = bytes.ToArray();
            modifiedBytes[^1] ^= 1;
            await File.WriteAllBytesAsync(source, modifiedBytes);
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => LanProtectedInstallerStager.StageAsync(request, cacheRoot, runRoot));
            Assert.Contains("SHA-256 divergente", error.Message);
            Assert.False(File.Exists(Path.Combine(runRoot, "setup.exe")));
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(23, false)]
    public async Task LanInstallerExecutionRunsWithoutUiOnAWorkerThreadAndReportsProcessResult(int exitCode, bool expectedSuccess)
    {
        string path = CreateInstallerFixture(".msi", null);
        try
        {
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            var request = new LanInstallerInstallRequest(
                path,
                Path.GetFileName(path),
                "Vendor.Test",
                "1.0.0",
                hash,
                "msi",
                "user",
                "/qn /norestart",
                HasZoneIdentifier: false,
                RemoveZoneIdentifier: false);
            var progress = new ConcurrentQueue<LanInstallerExecutionProgress>();
            var service = new LanInstallerExecutionService(new FakeLanInstallerProcessRunner(exitCode));

            LanInstallerExecutionResult result = await Task.Run(
                () => service.InstallAsync(request, new RecordingProgress<LanInstallerExecutionProgress>(progress.Enqueue)));

            Assert.Equal(expectedSuccess, result.Success);
            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal(expectedSuccess ? LanInstallerExecutionStage.Completed : LanInstallerExecutionStage.Executing, result.Stage);
            Assert.Contains(progress, item => item.Stage == LanInstallerExecutionStage.VerifyingIntegrity);
            Assert.Contains(progress, item => item.Message == "saída simulada do processo");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(LanInstallerKind.InnoSetup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(LanInstallerKind.Nsis, "/S")]
    [InlineData(LanInstallerKind.Burn, "/quiet /norestart")]
    [InlineData(LanInstallerKind.InstallShield, null)]
    [InlineData(LanInstallerKind.Unknown, null)]
    public void UninstallArgumentsHaveOnlyKnownSafeDefaults(LanInstallerKind kind, string? expected) =>
        Assert.Equal(expected, LanInstallerArgumentsPolicy.DefaultUninstallArguments(kind));

    [Fact]
    public void MsiUninstallDefaultRequiresAProductCode()
    {
        Assert.Null(LanInstallerArgumentsPolicy.DefaultUninstallArguments(LanInstallerKind.Msi));
        Assert.Equal("/x {12345678-1234-1234-1234-123456789abc} /qn /norestart",
            LanInstallerArgumentsPolicy.DefaultUninstallArguments(LanInstallerKind.Msi, "12345678-1234-1234-1234-123456789ABC"));
    }

    [Fact]
    public async Task ServerIndexesInstallersWithDetectedTypeHashAndSidecarOverrides()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-lan-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string fixture = CreateInstallerFixture(".exe", null);
        string installerPath = Path.Combine(root, "Setup.exe");
        try
        {
            File.Copy(fixture, installerPath);
            await File.WriteAllTextAsync(installerPath + ".json", """
                {
                  "packageId":"Vendor.Product",
                  "version":"2.5.0",
                  "installerType":"exe",
                  "silentArgs":"/custom",
                  "silentSource":"zip"
                }
                """);
            var server = new LanInstallersServerModule();
            LanModulePayload payload = await server.BuildAsync(root, CancellationToken.None);
            LanInstallersModuleDocument module = LanInstallerValidation.DeserializeModule(payload.Content);
            LanInstallerEntry installer = Assert.Single(module.Installers);

            Assert.Equal("Vendor.Product", installer.PackageId);
            Assert.Equal("2.5.0", installer.Version);
            Assert.Equal("exe", installer.InstallerType);
            Assert.Equal("/custom", installer.SilentArgs);
            Assert.Equal("zip", installer.SilentSource);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(installerPath))), installer.Sha256);
        }
        finally
        {
            File.Delete(fixture);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LanClientRetriesPublicationAfterPartialFileSharingLockIsReleased()
    {
        byte[] bytes = CreateFixtureBytes();
        var handler = new InstallerServerHandler(new Dictionary<string, byte[]> { ["setup.exe"] = bytes });
        using var http = new HttpClient(handler);
        string cache = CreateTemporaryDirectory("lan-cache-retry");
        Task? releaseLock = null;
        int retries = 0;
        var client = new LanInstallerClient(http, new IClientModuleHandler[] { new LanInstallersClientModuleHandler() }, cache, null, null,
            (stage, path, _) =>
            {
                if (stage == "antes da publicação")
                {
                    var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    releaseLock = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        await held.DisposeAsync();
                    });
                }
                return Task.CompletedTask;
            },
            async (milliseconds, token) =>
            {
                Interlocked.Increment(ref retries);
                await Task.Delay(milliseconds, token);
            });
        try
        {
            LanSyncResult result = await client.SynchronizeAsync("http://192.168.1.23:8080", "test-token-at-least-20-chars");
            if (releaseLock is not null)
                await releaseLock;
            Assert.Single(result.Installers);
            Assert.True(retries > 0);
            Assert.True(File.Exists(result.Installers[0].LocalPath));
        }
        finally
        {
            if (releaseLock is not null)
                await releaseLock;
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task LanClientReportsPublicationStageWhenSharingLockOutlastsAllRetries()
    {
        byte[] bytes = CreateFixtureBytes();
        var handler = new InstallerServerHandler(new Dictionary<string, byte[]> { ["setup.exe"] = bytes });
        using var http = new HttpClient(handler);
        string cache = CreateTemporaryDirectory("lan-cache-lock-fail");
        FileStream? held = null;
        var client = new LanInstallerClient(http, new IClientModuleHandler[] { new LanInstallersClientModuleHandler() }, cache, null, null,
            (stage, path, _) =>
            {
                if (stage == "antes da publicação")
                    held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return Task.CompletedTask;
            },
            static (_, _) => Task.CompletedTask);
        try
        {
            LanSyncResult result = await client.SynchronizeAsync("http://192.168.1.23:8080", "test-token-at-least-20-chars");
            LanInstallerSyncItem item = Assert.Single(result.Items!);
            Assert.Null(item.CachedInstaller);
            Assert.Contains("publicar com File.Move", item.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0x80070020", item.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("antivírus ou indexador", item.Status, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (held is not null)
                await held.DisposeAsync();
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task LanClientUsesVerifiedCacheHitWithoutDownloadingAgain()
    {
        byte[] bytes = CreateFixtureBytes();
        var handler = new InstallerServerHandler(new Dictionary<string, byte[]> { ["setup.exe"] = bytes });
        using var http = new HttpClient(handler);
        string cache = CreateTemporaryDirectory("lan-cache-hit");
        string orphan = Path.Combine(cache, "stale.partial");
        await File.WriteAllTextAsync(orphan, "orphan");
        var client = new LanInstallerClient(http, new IClientModuleHandler[] { new LanInstallersClientModuleHandler() }, cache);
        try
        {
            LanSyncResult first = await client.SynchronizeAsync("http://192.168.1.23:8080", "test-token-at-least-20-chars");
            LanSyncResult second = await client.SynchronizeAsync("http://192.168.1.23:8080", "test-token-at-least-20-chars");

            Assert.Single(first.Installers);
            Assert.Single(second.Installers);
            Assert.Equal(1, handler.FileDownloadCount);
            Assert.False(File.Exists(orphan));
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    [Fact]
    public async Task LanClientKeepsOtherInstallersWhenOneFileDownloadFails()
    {
        byte[] bytes = CreateFixtureBytes();
        var handler = new InstallerServerHandler(
            new Dictionary<string, byte[]>
            {
                ["first.exe"] = bytes,
                ["broken.exe"] = bytes,
                ["third.exe"] = bytes
            },
            failingFile: "broken.exe");
        using var http = new HttpClient(handler);
        string cache = CreateTemporaryDirectory("lan-cache-isolated-failure");
        var client = new LanInstallerClient(http, new IClientModuleHandler[] { new LanInstallersClientModuleHandler() }, cache);
        try
        {
            LanSyncResult result = await client.SynchronizeAsync("http://192.168.1.23:8080", "test-token-at-least-20-chars");

            Assert.Equal(2, result.Installers.Count);
            Assert.Equal(3, result.Items!.Count);
            Assert.Equal(2, result.Items.Count(item => item.CachedInstaller is not null));
            LanInstallerSyncItem failed = Assert.Single(result.Items, item => item.CachedInstaller is null);
            Assert.Contains("broken.exe", failed.Installer.File);
            Assert.Contains("receber resposta HTTP", failed.Status);
        }
        finally { Directory.Delete(cache, recursive: true); }
    }

    [Fact]
    public void InstallerWithoutSilentMetadataIsMarkedUnsupported()
    {
        var installer = new LanInstallerEntry
        {
            File = "setup.exe",
            Sha256 = new string('A', 64),
            PackageId = "Vendor.Product",
            Version = "2.5.0"
        };

        LanInstallerAvailability result = LanInstallerValidation.ValidateInstaller(installer);

        Assert.False(result.SilentSupported);
        Assert.NotNull(result.UnsupportedReason);
    }

    [Fact]
    public void InstallerTypeMustBeInTheSupportedSet()
    {
        var installer = new LanInstallerEntry
        {
            File = "setup.exe",
            Sha256 = new string('A', 64),
            PackageId = "Vendor.Product",
            Version = "2.5.0",
            InstallerType = "script",
            SilentArgs = "/S"
        };

        Assert.Throws<InvalidDataException>(() => LanInstallerValidation.ValidateInstaller(installer));
    }

    [Fact]
    public void ShellMetacharactersInSilentArgumentsRejectTheInstaller()
    {
        var installer = new LanInstallerEntry
        {
            File = "setup.exe",
            Sha256 = new string('A', 64),
            PackageId = "Vendor.Product",
            Version = "2.5.0",
            InstallerType = "nullsoft",
            SilentArgs = "/S & whoami"
        };

        Assert.Throws<InvalidDataException>(() => LanInstallerValidation.ValidateInstaller(installer));
    }

    [Fact]
    public async Task UnknownModuleFieldsArePreservedAndUnknownModulesCanBeSkipped()
    {
        const string moduleJson = """
            {
              "schema":"winprovision.lan.installers",
              "schemaVersion":1,
              "futureModuleField":{"keep":true},
              "installers":[{
                "file":"setup.exe",
                "sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "packageId":"Vendor.Product",
                "version":"2.5.0",
                "futureInstallerField":"preserve"
              }]
            }
            """;
        LanInstallersModuleDocument parsed = LanInstallerValidation.DeserializeModule(Encoding.UTF8.GetBytes(moduleJson));

        Assert.Contains("futureModuleField", parsed.AdditionalData!.Keys);
        Assert.Contains("futureInstallerField", Assert.Single(parsed.Installers).AdditionalData!.Keys);

        using var http = new HttpClient(new IndexOnlyHandler());
        var client = new LanInstallerClient(http, Array.Empty<IClientModuleHandler>(),
            Path.Combine(Path.GetTempPath(), "WinProvision-lan-cache-" + Guid.NewGuid().ToString("N")));
        LanSyncResult result = await client.SynchronizeAsync("http://192.168.1.23:8080", "test-token-at-least-20-chars");

        Assert.Empty(result.Installers);
        Assert.Contains(result.Warnings, warning => warning.Contains("future-module", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateInstallerFixture(string extension, string? marker)
    {
        byte[] bytes;
        if (extension == ".msi")
            bytes = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        else
        {
            const int peOffset = 0x80;
            const int sectionRawOffset = 0x200;
            const int sectionRawSize = 0x80;
            byte[] overlay = Encoding.ASCII.GetBytes(marker ?? string.Empty);
            bytes = new byte[sectionRawOffset + sectionRawSize + overlay.Length];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3c);
            bytes[peOffset] = (byte)'P';
            bytes[peOffset + 1] = (byte)'E';
            BitConverter.GetBytes((ushort)1).CopyTo(bytes, peOffset + 6);
            BitConverter.GetBytes((ushort)0xE0).CopyTo(bytes, peOffset + 20);
            int sectionOffset = peOffset + 24 + 0xE0;
            Encoding.ASCII.GetBytes(marker == ".wixburn" ? ".wixburn" : ".text").CopyTo(bytes, sectionOffset);
            BitConverter.GetBytes((uint)sectionRawSize).CopyTo(bytes, sectionOffset + 16);
            BitConverter.GetBytes((uint)sectionRawOffset).CopyTo(bytes, sectionOffset + 20);
            overlay.CopyTo(bytes, sectionRawOffset + sectionRawSize);
        }
        string path = Path.Combine(Path.GetTempPath(), "WinProvision-lan-fixture-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] CreateFixtureBytes()
    {
        string path = CreateInstallerFixture(".exe", null);
        try { return File.ReadAllBytes(path); }
        finally { File.Delete(path); }
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"WinProvision-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateMsiFixtureWithMetadata(string productName, string productVersion)
    {
        string path = Path.Combine(Path.GetTempPath(), "WinProvision-lan-metadata-" + Guid.NewGuid().ToString("N") + ".msi");
        const uint createDatabaseMode = 3;
        if (MsiOpenDatabase(path, (IntPtr)createDatabaseMode, out uint database) != 0)
            throw new InvalidOperationException("Não foi possível criar o fixture MSI.");
        try
        {
            ExecuteMsiSql(database,
                "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`)");
            ExecuteMsiSql(database,
                $"INSERT INTO `Property` (`Property`,`Value`) VALUES ('ProductName','{productName}')");
            ExecuteMsiSql(database,
                $"INSERT INTO `Property` (`Property`,`Value`) VALUES ('ProductVersion','{productVersion}')");
            if (MsiDatabaseCommit(database) != 0)
                throw new InvalidOperationException("Não foi possível salvar o fixture MSI.");
        }
        finally { MsiCloseHandle(database); }
        return path;
    }

    private static void ExecuteMsiSql(uint database, string sql)
    {
        if (MsiDatabaseOpenView(database, sql, out uint view) != 0)
            throw new InvalidOperationException("Não foi possível preparar a consulta do fixture MSI.");
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Não foi possível executar a consulta do fixture MSI.");
        }
        finally { MsiCloseHandle(view); }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll", EntryPoint = "MsiViewExecute")]
    private static extern uint MsiViewExecute(uint view, IntPtr record);
    [DllImport("msi.dll", EntryPoint = "MsiDatabaseCommit")]
    private static extern uint MsiDatabaseCommit(uint database);
    [DllImport("msi.dll", EntryPoint = "MsiCloseHandle")]
    private static extern uint MsiCloseHandle(uint handle);

    private sealed class FakeLanInstallerProcessRunner(int exitCode) : ILanInstallerProcessRunner
    {
        public async Task<LanInstallerProcessResult> RunAsync(
            string executable,
            string arguments,
            string workingDirectory,
            bool elevated,
            TimeSpan timeout,
            Action<string>? onOutput,
            CancellationToken token)
        {
            await Task.Run(() => onOutput?.Invoke("saída simulada do processo"), token);
            return new(exitCode == 0, exitCode, "saída simulada do processo", true, elevated);
        }
    }

    private sealed class RecordingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class IndexOnlyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string index = """
                {
                  "schema":"winprovision.lan",
                  "schemaVersion":1,
                  "generatedAt":"2026-11-09T00:00:00Z",
                  "modules":[{"name":"future-module","version":1,"path":"/v1/future.json","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(index, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class InstallerServerHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly string? _failingFile;
        private readonly byte[] _moduleBytes;
        private readonly string _moduleHash;
        private int _fileDownloadCount;

        public int FileDownloadCount => Volatile.Read(ref _fileDownloadCount);

        public InstallerServerHandler(IReadOnlyDictionary<string, byte[]> files, string? failingFile = null)
        {
            _files = files;
            _failingFile = failingFile;
            var module = new LanInstallersModuleDocument();
            foreach ((string name, byte[] bytes) in files)
                module.Installers.Add(new LanInstallerEntry
                {
                    File = name,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    PackageId = "Vendor." + Path.GetFileNameWithoutExtension(name),
                    Version = "1.0.0",
                    InstallerType = "exe",
                    SilentArgs = "/S"
                });
            _moduleBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                module, CoreJsonContext.Default.LanInstallersModuleDocument);
            _moduleHash = Convert.ToHexString(SHA256.HashData(_moduleBytes));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/v1/index.json")
            {
                var index = new LanIndexDocument();
                index.Modules.Add(new LanModuleDescriptor
                {
                    Name = "installers",
                    Version = 1,
                    Path = "/v1/installers.json",
                    Sha256 = _moduleHash
                });
                byte[] bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(index, CoreJsonContext.Default.LanIndexDocument);
                return Task.FromResult(JsonResponse(bytes));
            }
            if (path == "/v1/installers.json")
                return Task.FromResult(JsonResponse(_moduleBytes));
            if (path.StartsWith("/v1/files/", StringComparison.Ordinal))
            {
                string file = Uri.UnescapeDataString(path["/v1/files/".Length..]);
                Interlocked.Increment(ref _fileDownloadCount);
                if (file == _failingFile)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("falha simulada")
                    });
                return Task.FromResult(JsonResponse(_files[file], "application/octet-stream"));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage JsonResponse(byte[] bytes, string contentType = "application/json") =>
            new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) }
                }
            };
    }
}
