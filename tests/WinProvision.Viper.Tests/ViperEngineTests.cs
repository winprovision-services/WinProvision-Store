using System.Collections.Concurrent;
using System.Security.Cryptography;
using WinProvision.Viper;
using Xunit;

namespace WinProvision.Viper.Tests;

public sealed class ViperEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "winprovision-viper-tests-" + Guid.NewGuid().ToString("N"));
    private string Target(string name = "download.bin") => Path.Combine(_root, name);
    private static ViperOptions Options => new() { AllowHttp = true, PieceSize = 65536, ConnectionsPerFile = 4, MaxAttempts = 2 };
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public ViperEngineTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task HostnameWithMultipleAddressFamiliesCanReachIpv4Server()
    {
        await using var server = new ViperTestServer();
        await using var engine = new ViperEngine(Options);
        var source = new UriBuilder(server.Url) { Host = "localhost" }.Uri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await engine.DownloadAsync(new(source, Target()) { Sha256 = Hash(server.Payload) }, token: timeout.Token);
        Assert.Equal(Hash(server.Payload), result.Sha256);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(Target()));
    }

    [Fact]
    public async Task SegmentedProgressReportsBytesBeforeFirstPieceCompletes()
    {
        await using var server = new ViperTestServer();
        await using var engine = new ViperEngine(Options with { PieceSize = 262144, ConnectionsPerFile = 2 });
        var reports = new ConcurrentQueue<ViperProgress>();
        await engine.DownloadAsync(new(server.Url, Target()) { Sha256 = Hash(server.Payload) }, new InlineProgress(reports.Enqueue));
        Assert.Contains(reports, value => value.Phase == ViperPhase.Downloading
            && value.CompletedPieces == 0 && value.DownloadedBytes > 0 && value.DownloadedBytes < server.Payload.Length);
        Assert.All(reports, value => Assert.InRange(value.DownloadedBytes, 0, server.Payload.Length));
        Assert.Equal(ViperPhase.Completed, reports.Last().Phase);
    }

    [Fact]
    public async Task LargeFileHashIsCorrectAndHonorsCancellation()
    {
        byte[] bytes = Enumerable.Range(0, 3 * 1024 * 1024 + 17).Select(index => (byte)(index * 31)).ToArray();
        await File.WriteAllBytesAsync(Target(), bytes);
        Assert.Equal(Hash(bytes), await ViperFileIntegrity.ComputeSha256Async(Target()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ViperFileIntegrity.ComputeSha256Async(Target(), cancellation.Token));
    }

    [Fact]
    public async Task RealHttp_SegmentsConcurrentlyAndVerifiesBeforePublishing()
    {
        await using var server = new ViperTestServer();
        await using var engine = new ViperEngine(Options);
        var result = await engine.DownloadAsync(new(server.Url, Target()) { Sha256 = Hash(server.Payload) });
        Assert.True(result.Segmented); Assert.False(result.Resumed);
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(Target()));
        Assert.Equal(Hash(server.Payload), result.Sha256);
        Assert.InRange(server.Peak, 2, 4);
        Assert.Contains(server.Requests, request => request.Headers.ContainsKey("If-Range"));
        Assert.False(File.Exists(Target() + ".viper.json"));
        Assert.False(File.Exists(Target() + ".viper.lock"));
    }

    [Fact]
    public async Task CancellationResumesVerifiedPartsAndRedownloadsLocallyCorruptedPart()
    {
        await using var server = new ViperTestServer();
        // Segmentation needs >1 worker; the test uses two so checkpoints exist before cancellation.
        await using var segmented = new ViperEngine(Options with { ConnectionsPerFile = 2 });
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.CompletedPieces >= 3) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => segmented.DownloadAsync(new(server.Url, Target()), progress, cancellation.Token));
        Assert.False(File.Exists(Target())); Assert.True(File.Exists(Target() + ".viper.json"));
        using var checkpoint = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Target() + ".viper.json"));
        int corruptedIndex = checkpoint.RootElement.GetProperty("Completed")[0].GetProperty("Index").GetInt32();
        await using (var corrupt = new FileStream(Target() + ".viper.part", FileMode.Open, FileAccess.Write))
        { corrupt.Position = corruptedIndex * 65536L; await corrupt.WriteAsync(new byte[] { 99, 99, 99 }); }
        var result = await segmented.DownloadAsync(new(server.Url, Target()) { Sha256 = Hash(server.Payload) });
        Assert.True(result.Resumed); Assert.Equal(server.Payload, await File.ReadAllBytesAsync(Target()));
        Assert.True(server.Requests.Count(r => r.Headers.TryGetValue("Range", out string? range)
            && range == $"bytes={corruptedIndex * 65536}-{(corruptedIndex + 1) * 65536 - 1}") >= 2);
    }

    [Fact]
    public async Task ServerIgnoringRangesFallsBackToSingleStream()
    {
        await using var server = new ViperTestServer { IgnoreRanges = true };
        await using var engine = new ViperEngine(Options);
        var result = await engine.DownloadAsync(new(server.Url, Target()));
        Assert.False(result.Segmented); Assert.Equal(Hash(server.Payload), result.Sha256);
    }

    [Fact]
    public async Task ServerStopsSupportingRangesAfterProbe_RestartsWithoutMixingBytes()
    {
        await using var server = new ViperTestServer();
        server.Override = request => request.Headers.ContainsKey("If-Range")
            ? server.Normal(request with { Headers = [] }) : null;
        await using var engine = new ViperEngine(Options);
        var result = await engine.DownloadAsync(new(server.Url, Target()));
        Assert.False(result.Segmented); Assert.Equal(Hash(server.Payload), result.Sha256);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongRangeOrChangedETagNeverPublishes(bool wrongRange)
    {
        await using var server = new ViperTestServer();
        server.Override = request =>
        {
            if (!request.Headers.ContainsKey("If-Range")) return null;
            var normal = server.Normal(request);
            normal.Headers![wrongRange ? "Content-Range" : "ETag"] = wrongRange ? "bytes 5-9/1048576" : "\"changed\"";
            return normal;
        };
        await using var engine = new ViperEngine(Options);
        await Assert.ThrowsAsync<ViperProtocolException>(() => engine.DownloadAsync(new(server.Url, Target())));
        Assert.False(File.Exists(Target()));
    }

    [Fact]
    public async Task ChangedRemoteVersionDiscardsEarlierCheckpoint()
    {
        await using var server = new ViperTestServer();
        await using var engine = new ViperEngine(Options with { ConnectionsPerFile = 2 });
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DownloadAsync(new(server.Url, Target()),
            new InlineProgress(p => { if (p.CompletedPieces >= 2) cancellation.Cancel(); }), cancellation.Token));
        server.ETag = "\"v2\"";
        var result = await engine.DownloadAsync(new(server.Url, Target()));
        Assert.False(result.Resumed); Assert.Equal(Hash(server.Payload), result.Sha256);
    }

    [Fact]
    public async Task HashMismatchDiscardsBadPayloadAndPreservesExistingDestination()
    {
        await using var server = new ViperTestServer();
        await using var engine = new ViperEngine(Options);
        await Assert.ThrowsAsync<ViperIntegrityException>(() => engine.DownloadAsync(new(server.Url, Target()) { Sha256 = new string('0', 64) }));
        Assert.False(File.Exists(Target())); Assert.False(File.Exists(Target() + ".viper.part"));
        await File.WriteAllTextAsync(Target(), "existing");
        await Assert.ThrowsAsync<IOException>(() => engine.DownloadAsync(new(server.Url, Target())));
        Assert.Equal("existing", await File.ReadAllTextAsync(Target()));
    }

    [Fact]
    public async Task VerifiedExistingDestinationIsReusableWithoutNetworkOrOverwrite()
    {
        await using var server = new ViperTestServer();
        string expectedHash = Hash(server.Payload);
        await using var engine = new ViperEngine(Options);
        await engine.DownloadAsync(new(server.Url, Target()) { Sha256 = expectedHash });
        int requestsBeforeReuse = server.Requests.Count;
        server.Override = _ => new(503, []);

        ViperResult reused = await engine.DownloadAsync(new(server.Url, Target())
        {
            Sha256 = expectedHash,
            ExistingFilePolicy = ViperExistingFilePolicy.ReuseIfVerified
        });

        Assert.True(reused.ReusedExisting);
        Assert.True(reused.Resumed);
        Assert.Equal(expectedHash, reused.Sha256);
        Assert.Equal(requestsBeforeReuse, server.Requests.Count);
        await Assert.ThrowsAsync<ViperIntegrityException>(() => engine.DownloadAsync(new(server.Url, Target())
        {
            Sha256 = new string('0', 64),
            ExistingFilePolicy = ViperExistingFilePolicy.ReuseIfVerified
        }));
        Assert.Equal(server.Payload, await File.ReadAllBytesAsync(Target()));
    }

    [Fact]
    public async Task TruncatedNetworkBodyIsRetriedAndNeverPublishedIncomplete()
    {
        await using var server = new ViperTestServer();
        int truncate = 1;
        server.Override = request => request.Headers.ContainsKey("If-Range") && Interlocked.Exchange(ref truncate, 0) == 1
            ? server.Normal(request) with { Truncate = true } : null;
        await using var engine = new ViperEngine(Options);
        var result = await engine.DownloadAsync(new(server.Url, Target()));
        Assert.Equal(Hash(server.Payload), result.Sha256);
    }

    [Fact]
    public async Task TooManyRequestsHonorsRetryAfterAndExcessiveWaitFailsPromptly()
    {
        await using var server = new ViperTestServer();
        int first = 1;
        server.Override = _ => Interlocked.Exchange(ref first, 0) == 1
            ? new(429, [], new() { ["Retry-After"] = "1" }) : null;
        await using var engine = new ViperEngine(Options);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await engine.DownloadAsync(new(server.Url, Target()));
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1));
        server.Override = _ => new(429, [], new() { ["Retry-After"] = "300" });
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => engine.DownloadAsync(new(server.Url, Target("blocked.bin"))).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.False(File.Exists(Target("blocked.bin")));
    }

    [Fact]
    public async Task RealBatchEnforcesGlobalAndHostConcurrencyLimits()
    {
        await using var server = new ViperTestServer();
        await using var engine = new ViperEngine(Options with { MaxConcurrentDownloads = 2, MaxConnectionsPerHost = 3, MaxConnections = 3 });
        var requests = Enumerable.Range(0, 5).Select(i => new ViperRequest(new Uri(server.Url + "?id=" + i), Target($"file-{i}.bin")));
        var results = await engine.DownloadManyAsync(requests);
        Assert.Equal(5, results.Count); Assert.InRange(server.Peak, 2, 3);
        Assert.All(results, result => Assert.Equal(Hash(server.Payload), result.Sha256));
    }

    [Fact]
    public async Task BatchFailureCancelsOtherActiveTransfersAndPreservesTheirDestinations()
    {
        await using var failed = new ViperTestServer { Override = _ => new(503, []) };
        await using var slow = new ViperTestServer { BodyDelayMilliseconds = 5000 };
        await using var engine = new ViperEngine(Options with
        {
            MaxConcurrentDownloads = 2,
            ConnectionsPerFile = 1,
            PieceSize = 2 * 1024 * 1024,
            MaxAttempts = 1
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => engine.DownloadManyAsync([
            new(failed.Url, Target("failed.bin")),
            new(slow.Url, Target("cancelled.bin"))
        ]));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3));
        Assert.False(File.Exists(Target("failed.bin")));
        Assert.False(File.Exists(Target("cancelled.bin")));
    }

    [Fact]
    public async Task BatchDeduplicatesEquivalentDestinationsAndRejectsConflictsBeforeStartingTransfers()
    {
        await using var server = new ViperTestServer();
        await using var engine = new ViperEngine(Options);
        var same = new ViperRequest(server.Url, Target());
        var results = await engine.DownloadManyAsync([same, same]);
        Assert.Equal(2, results.Count);
        Assert.Equal(results[0], results[1]);
        int requestsAfterSharedTransfer = server.Requests.Count;
        Assert.True(requestsAfterSharedTransfer > 0);

        File.Delete(Target());
        int requestsBeforeConflict = server.Requests.Count;
        var conflicting = new ViperRequest(new Uri(server.Url + "?other"), Target());
        await Assert.ThrowsAsync<ArgumentException>(async () => await engine.DownloadManyAsync([same, conflicting]));
        Assert.Equal(requestsBeforeConflict, server.Requests.Count);
    }

    [Fact]
    public async Task CrossOriginRedirectStripsCredentialsAndRejectsRedirectLoops()
    {
        await using var destination = new ViperTestServer();
        await using var source = new ViperTestServer();
        source.Override = _ => new(302, [], new() { ["Location"] = destination.Url.ToString() });
        await using var engine = new ViperEngine(Options);
        await engine.DownloadAsync(new(source.Url, Target()) { Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer test-credential", ["X-Private"] = "private" } });
        Assert.All(destination.Requests, request => { Assert.False(request.Headers.ContainsKey("Authorization")); Assert.False(request.Headers.ContainsKey("X-Private")); });
        source.Override = _ => new(302, [], new() { ["Location"] = source.Url.ToString() });
        await Assert.ThrowsAsync<ViperProtocolException>(() => engine.DownloadAsync(new(source.Url, Target("loop.bin"))));
    }

    [Fact]
    public async Task RedirectToSignedUrlThatChangesPerRequestStillDownloads()
    {
        await using var destination = new ViperTestServer();
        await using var source = new ViperTestServer();
        int counter = 0;
        // Como GitHub Releases/Azure Blob: cada requisição recebe uma URL assinada diferente para o mesmo arquivo.
        source.Override = _ => new(302, [], new() { ["Location"] = destination.Url + "?sig=" + Interlocked.Increment(ref counter) });
        await using var engine = new ViperEngine(Options);
        var segmented = await engine.DownloadAsync(new(source.Url, Target("signed-segmented.bin")) { Sha256 = Hash(destination.Payload) });
        Assert.True(segmented.Segmented);
        Assert.Equal(destination.Payload, await File.ReadAllBytesAsync(Target("signed-segmented.bin")));
        destination.IgnoreRanges = true;
        var single = await engine.DownloadAsync(new(source.Url, Target("signed-single.bin")) { Sha256 = Hash(destination.Payload) });
        Assert.False(single.Segmented);
        Assert.Equal(destination.Payload, await File.ReadAllBytesAsync(Target("signed-single.bin")));
    }

    [Fact]
    public async Task ShutdownCancelsAndObservesActiveDownloads()
    {
        await using var server = new ViperTestServer { BodyDelayMilliseconds = 5000 };
        var engine = new ViperEngine(Options);
        var download = engine.DownloadAsync(new(server.Url, Target()));
        while (server.Requests.IsEmpty) await Task.Delay(10);
        await engine.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.DownloadAsync(new(server.Url, Target("later.bin"))));
    }

    [Fact]
    public async Task DefaultPolicyRejectsHttpAndOversizePayload()
    {
        await using var server = new ViperTestServer();
        await using var secure = new ViperEngine();
        await Assert.ThrowsAsync<ArgumentException>(() => secure.DownloadAsync(new(server.Url, Target())));
        await using var limited = new ViperEngine(Options with { MaxFileSize = 100 });
        await Assert.ThrowsAsync<ViperProtocolException>(() => limited.DownloadAsync(new(server.Url, Target())));
        Assert.False(File.Exists(Target()));
        Assert.Throws<ArgumentException>(() => new ViperEngine(Options with { ConnectionsPerFile = 17 }));
    }

    [Fact]
    public async Task EmptyAndUnknownLengthFilesAreHandledWithoutUnboundedGrowth()
    {
        await using var server = new ViperTestServer();
        server.Override = request => request.Headers.ContainsKey("Range")
            ? new(416, [], new() { ["Content-Range"] = "bytes */0" }) : new(200, []);
        await using var engine = new ViperEngine(Options);
        var empty = await engine.DownloadAsync(new(server.Url, Target()));
        Assert.Equal(0, empty.Bytes); Assert.Equal(Hash([]), empty.Sha256);
        server.Override = _ => new(200, server.Payload, WithoutLength: true);
        var unknown = await engine.DownloadAsync(new(server.Url, Target("unknown.bin")));
        Assert.False(unknown.Segmented); Assert.Equal(Hash(server.Payload), unknown.Sha256);
        await using var limited = new ViperEngine(Options with { MaxFileSize = 10 });
        await Assert.ThrowsAsync<ViperProtocolException>(() => limited.DownloadAsync(new(server.Url, Target("large.bin"))));
        Assert.False(File.Exists(Target("large.bin") + ".viper.part"));
    }

    [Fact]
    public async Task ConcurrentWritersCannotShareDestinationAndStalledReadsTimeOut()
    {
        await using var server = new ViperTestServer { BodyDelayMilliseconds = 1000 };
        await using var first = new ViperEngine(Options);
        await using var second = new ViperEngine(Options with { ReadTimeout = TimeSpan.FromMilliseconds(50), MaxAttempts = 1 });
        using var stop = new CancellationTokenSource();
        var active = first.DownloadAsync(new(server.Url, Target()), token: stop.Token);
        while (server.Requests.IsEmpty) await Task.Delay(10);
        await Assert.ThrowsAsync<IOException>(() => second.DownloadAsync(new(server.Url, Target())));
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAsync<TimeoutException>(() => second.DownloadAsync(new(server.Url, Target("timeout.bin"))));
        Assert.False(File.Exists(Target("timeout.bin")));
    }

    [Fact]
    public async Task AuthenticationFailuresAreNotRetriedAndUnsafeHeadersAreRejected()
    {
        await using var server = new ViperTestServer(); server.Override = _ => new(403, []);
        await using var engine = new ViperEngine(Options);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => engine.DownloadAsync(new(server.Url, Target())));
        Assert.Single(server.Requests);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.DownloadAsync(new(server.Url, Target())
            { Headers = new Dictionary<string, string> { ["Range"] = "bytes=0-1" } }));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.DownloadAsync(new(server.Url, Target())
            { Headers = new Dictionary<string, string> { ["X-Test"] = "value\r\nInjected: value" } }));
    }

    private sealed class InlineProgress(Action<ViperProgress> update) : IProgress<ViperProgress>
    { public void Report(ViperProgress value) => update(value); }
}
