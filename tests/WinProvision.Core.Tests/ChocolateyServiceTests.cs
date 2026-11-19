using System.Net;
using System.Security.Authentication;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ChocolateyServiceTests
{
    [Fact]
    public async Task ListInstalledUsesV2LocalListCommandAndSkipsConfiguredHeader()
    {
        string[]? arguments = null;
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, args, _, _) =>
        {
            arguments = args;
            return Task.FromResult((0, "Id|Version\r\nChocolatey|2.7.4\r\ndemo.package|1.2.3\r\n", string.Empty));
        }, "C:\\fake\\choco.exe");

        IReadOnlyList<InstalledPackageInfo> packages = await service.ListInstalledAsync();

        Assert.Equal(new[] { "list", "--limit-output" }, arguments);
        var package = Assert.Single(packages);
        Assert.Equal("demo.package", package.Id);
        Assert.Equal("1.2.3", package.Version);
    }

    [Fact]
    public async Task ListInstalledReportsCliFailureWithStandardError()
    {
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, _, _, _) =>
            Task.FromResult((1, string.Empty, "Chocolatey CLI failed")), "C:\\fake\\choco.exe");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListInstalledAsync());

        Assert.Contains("código 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Chocolatey CLI failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOutdatedAcceptsEnhancedExitCodeAndSkipsConfiguredHeader()
    {
        string[]? arguments = null;
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, args, _, _) =>
        {
            arguments = args;
            return Task.FromResult((2, "Id|Version|AvailableVersion|Pinned\r\ndemo.package|1.2.3|2.0.0|false\r\n", string.Empty));
        }, "C:\\fake\\choco.exe");

        IReadOnlyList<UpgradablePackage> packages = await service.GetOutdatedAsync();

        Assert.Equal(new[] { "outdated", "--limit-output" }, arguments);
        var package = Assert.Single(packages);
        Assert.Equal("demo.package", package.Id);
        Assert.Equal("1.2.3", package.CurrentVersion);
        Assert.Equal("2.0.0", package.AvailableVersion);
    }

    [Fact]
    public async Task GetOutdatedParsesOnlyFourFieldsAndExcludesPinnedRows()
    {
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, _, _, _) => Task.FromResult((2,
            "warning: ignored log line\r\nvalid.package|1.0|2.0|false\r\npinned.package|1.0|2.0|true\r\nextra.field|1.0|2.0|false|ignored\r\nmissing.field|1.0|2.0\r\nunknown.pin|1.0|2.0|yes\r\n",
            string.Empty)), "C:\\fake\\choco.exe");

        IReadOnlyList<UpgradablePackage> packages = await service.GetOutdatedAsync();

        var package = Assert.Single(packages);
        Assert.Equal("valid.package", package.Id);
        Assert.Equal("chocolatey", package.Source);
    }

    [Fact]
    public async Task GetOutdatedAcceptsEnhancedExitCodeWhenNoRowsAreReturned()
    {
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, _, _, _) =>
            Task.FromResult((2, "Id|Version|AvailableVersion|Pinned\r\n", string.Empty)), "C:\\fake\\choco.exe");

        IReadOnlyList<UpgradablePackage> packages = await service.GetOutdatedAsync();

        Assert.Empty(packages);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task GetOutdatedParsesRowsRegardlessOfExitCode(int exitCode)
    {
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, _, _, _) => Task.FromResult((exitCode,
            "Id|Version|AvailableVersion|Pinned\r\nchocolatey|2.7.4|2.7.5|false\r\n", "CLI warning")),
            "C:\\fake\\choco.exe");

        UpgradablePackage package = Assert.Single(await service.GetOutdatedAsync());

        Assert.Equal("chocolatey", package.Id);
        Assert.Equal("2.7.4", package.CurrentVersion);
        Assert.Equal("2.7.5", package.AvailableVersion);
    }

    [Fact]
    public async Task InstallCliDownloadsOfficialScriptAndKeepsUacCancellationIsolated()
    {
        string? requestedUrl = null;
        string? executable = null;
        string? arguments = null;
        string? downloadedScript = null;
        using var client = new HttpClient(new FeedHandler(request =>
        {
            requestedUrl = request.RequestUri?.AbsoluteUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("Write-Output 'official chocolatey installer'")
            };
        }));
        var service = new ChocolateyService(client, (file, command, _, onOutput) =>
        {
            executable = file;
            arguments = command;
            string scriptPath = command.Split(" -File ", 2, StringSplitOptions.None)[1].Trim('"');
            downloadedScript = File.ReadAllText(scriptPath);
            onOutput?.Invoke("Instalador aguardando autorização");
            return Task.FromResult(new WingetExecutionResult
            {
                Success = false,
                ExitCode = 1223,
                Output = "Elevação cancelada pelo usuário.",
                FailureReason = WingetFailureReason.ElevationCanceled,
            });
        }, "C:\\fake\\choco.exe");
        var progress = new List<string>();

        OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.InstallCliAsync(onProgress: progress.Add));

        Assert.Equal("https://community.chocolatey.org/install.ps1", requestedUrl);
        Assert.Equal("powershell.exe", executable);
        Assert.Contains("-NoProfile -ExecutionPolicy Bypass -File ", arguments, StringComparison.Ordinal);
        Assert.Contains("official chocolatey installer", downloadedScript, StringComparison.Ordinal);
        Assert.Contains("UAC", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Instalador aguardando autorização", progress);
    }

    [Theory]
    [InlineData("install", "\"install\" \"demo.package\" \"-y\" \"--no-progress\"")]
    [InlineData("upgrade", "\"upgrade\" \"demo.package\" \"-y\" \"--no-progress\"")]
    [InlineData("uninstall", "\"uninstall\" \"demo.package\" \"-y\" \"--no-progress\"")]
    public async Task PackageOperationsUseElevatedRunner(string operation, string expectedArguments)
    {
        string? actualExecutable = null;
        string? actualArguments = null;
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (executable, arguments, _) =>
        {
            actualExecutable = executable;
            actualArguments = arguments;
            return Task.FromResult(new WingetExecutionResult { Success = true, ExitCode = 0, Output = " installed \r\n" });
        }, "C:\\fake\\choco.exe");

        WingetExecutionResult result = operation switch
        {
            "install" => await service.InstallAsync("demo.package", false, CancellationToken.None),
            "upgrade" => await service.UpgradeAsync("demo.package", false, CancellationToken.None),
            "uninstall" => await service.UninstallAsync("demo.package", false, CancellationToken.None),
            _ => throw new InvalidOperationException(),
        };

        Assert.Equal("C:\\fake\\choco.exe", actualExecutable);
        Assert.Equal(expectedArguments, actualArguments);
        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("installed", result.Output);
    }

    [Fact]
    public async Task PackageOperationReturnsElevationFailureWithoutRunningChocolateyDirectly()
    {
        int runnerCalls = 0;
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, _, _) =>
        {
            runnerCalls++;
            return Task.FromResult(new WingetExecutionResult
            {
                Success = false,
                ExitCode = 1223,
                Output = "Elevação cancelada pelo usuário.",
                FailureReason = WingetFailureReason.ElevationCanceled,
            });
        }, "C:\\fake\\choco.exe");

        WingetExecutionResult result = await service.InstallAsync("demo.package", false, CancellationToken.None);

        Assert.Equal(1, runnerCalls);
        Assert.False(result.Success);
        Assert.Equal(1223, result.ExitCode);
        Assert.Equal(WingetFailureReason.ElevationCanceled, result.FailureReason);
    }

    [Theory]
    [InlineData("install", 3010, true)]
    [InlineData("upgrade", 1641, true)]
    [InlineData("uninstall", 1605, false)]
    [InlineData("uninstall", 1614, false)]
    public async Task PackageOperationsTreatDocumentedPackageCodesAsSuccess(string operation, int exitCode, bool rebootRequired)
    {
        using var client = new HttpClient(new FeedHandler(_ => throw new InvalidOperationException("No feed expected.")));
        var service = new ChocolateyService(client, (_, _, _, _) => Task.FromResult(new WingetExecutionResult
        {
            Success = false,
            ExitCode = exitCode,
            Output = "package output"
        }), "C:\\fake\\choco.exe");

        WingetExecutionResult result = operation switch
        {
            "install" => await service.InstallAsync("demo.package", new PackageInstallOptions(RequiresElevation: false), CancellationToken.None),
            "upgrade" => await service.UpgradeAsync("demo.package", new PackageInstallOptions(RequiresElevation: false), CancellationToken.None),
            "uninstall" => await service.UninstallAsync("demo.package", new PackageInstallOptions(RequiresElevation: false), CancellationToken.None),
            _ => throw new InvalidOperationException(),
        };

        Assert.True(result.Success);
        Assert.Equal(rebootRequired, result.RebootRequired);
        if (rebootRequired) Assert.Contains("reinicialização", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchRetriesPrematureResponseEndWithinSingleThreeAttemptBudget()
    {
        int calls = 0;
        using var client = new HttpClient(new FeedHandler(request =>
        {
            Assert.True(request.Options.TryGetValue(RequestRateLimitHandler.DisableTransportRetriesOption, out bool retriesDisabled));
            Assert.True(retriesDisabled);

            if (Interlocked.Increment(ref calls) < 3)
                throw new HttpRequestException("An error occurred while sending the request.", new IOException("The response ended prematurely."));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FeedXml),
            };
        }));
        var service = new ChocolateyService(client);

        var packages = await service.SearchAsync("sample");

        Assert.Equal(3, calls);
        var package = Assert.Single(packages);
        Assert.Equal("sample.package", package.Id);
        Assert.Equal("1.2.3", package.Version);
        Assert.Equal(123456L, package.SourceDownloadCount);
    }

    [Fact]
    public async Task PackageDownloadUsesVersionedCommunityEndpointAndStreamsTheNupkg()
    {
        string? requestedUri = null;
        string? userAgent = null;
        byte[] payload = [0x50, 0x4B, 0x03, 0x04, 1, 2, 3];
        using var client = new HttpClient(new FeedHandler(request =>
        {
            requestedUri = request.RequestUri?.AbsoluteUri;
            userAgent = request.Headers.UserAgent.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(payload) };
        }));
        var service = new ChocolateyService(client);
        string destination = Path.Combine(Path.GetTempPath(), "choco-" + Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            await service.DownloadPackageAsync("demo.package", "1.2.3", destination, null, CancellationToken.None);
            Assert.Equal("https://community.chocolatey.org/api/v2/package/demo.package/1.2.3", requestedUri);
            Assert.Contains("WinProvisionStore/1.0", userAgent);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        }
        finally { if (File.Exists(destination)) File.Delete(destination); }
    }

    [Fact]
    public async Task SearchStopsAfterThreePrematureResponseEnds()
    {
        int calls = 0;
        using var client = new HttpClient(new FeedHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("An error occurred while sending the request.", new IOException("ResponseEnded"));
        }));
        var service = new ChocolateyService(client);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("sample"));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SearchRetriesWhenResponseBodyEndsDuringXmlRead()
    {
        int calls = 0;
        using var client = new HttpClient(new FeedHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(FeedXml);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new TruncatedReadStream(bytes, bytes.Length / 2)),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(FeedXml),
            };
        }));
        var service = new ChocolateyService(client);

        var packages = await service.SearchAsync("sample");

        Assert.Equal(2, calls);
        Assert.Equal("sample.package", Assert.Single(packages).Id);
    }

    [Fact]
    public async Task SearchDoesNotRetryTlsAuthenticationFailure()
    {
        int calls = 0;
        using var client = new HttpClient(new FeedHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("TLS failed", new AuthenticationException("RemoteCertificateNameMismatch"));
        }));
        var service = new ChocolateyService(client);

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => service.SearchAsync("sample"));

        Assert.Contains("community.chocolatey.org", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetVersionsUsesChocolateyCliSearchOutput()
    {
        using var client = new HttpClient(new FeedHandler(request =>
        {
            throw new InvalidOperationException("Version lookup must use choco.exe.");
        }));
        string[]? executedArguments = null;
        var service = new ChocolateyService(client,
            (executable, arguments, _, _) =>
            {
                executedArguments = arguments;
                return Task.FromResult((0, "[Approved] sample.package|2.0.0\nsample.package|1.2.3\nwarning: ignored", string.Empty));
            }, "C:\\fake\\choco.exe");

        string[] versions = await service.GetVersionsAsync("sample.package");

        Assert.Equal(["search", "sample.package", "--exact", "--all-versions", "--limit-output"], executedArguments!);
        Assert.Equal(["2.0.0", "1.2.3"], versions);
    }

    private const string FeedXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
          <entry>
            <id>https://community.chocolatey.org/api/v2/Packages(Id='sample.package',Version='1.2.3')</id>
            <content type="application/xml"><m:properties><d:Description>Sample description</d:Description><d:DownloadCount m:type="Edm.Int64">123456</d:DownloadCount></m:properties></content>
          </entry>
        </feed>
        """;

    private sealed class FeedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TruncatedReadStream(byte[] bytes, int cutoff) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= cutoff) ThrowTruncated();
            int copied = Math.Min(count, cutoff - _position);
            Array.Copy(bytes, _position, buffer, offset, copied);
            _position += copied;
            return copied;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= cutoff) ThrowTruncated();
            int copied = Math.Min(buffer.Length, cutoff - _position);
            bytes.AsMemory(_position, copied).CopyTo(buffer);
            _position += copied;
            return ValueTask.FromResult(copied);
        }

        private void ThrowTruncated()
        {
            throw new IOException("The response ended prematurely.");
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
