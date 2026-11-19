using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class RequestRateLimitTests
{
    private static readonly Uri Endpoint = new("https://api.example.test/resource");

    [Fact]
    public async Task StartupTransportConnectsToIpv4ServerThroughLocalhost()
    {
        await using var server = new WinProvision.Viper.Tests.ViperTestServer();
        var transport = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(new RequestRateLimitHandler(transport)) { Timeout = TimeSpan.FromSeconds(10) };
        var endpoint = new UriBuilder(server.Url) { Host = "localhost" }.Uri;
        Assert.NotNull(transport.ConnectCallback);
        Assert.False(transport.AllowAutoRedirect);
        Assert.Equal(TimeSpan.FromSeconds(15), transport.ConnectTimeout);
        Assert.Equal(server.Payload, await client.GetByteArrayAsync(endpoint));
    }

    [Fact]
    public void StartupTransportPreservesExplicitConnectionPolicy()
    {
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> callback =
            (_, _) => throw new NotSupportedException();
        var transport = new SocketsHttpHandler { ConnectCallback = callback, ConnectTimeout = TimeSpan.FromSeconds(3) };
        using var handler = new RequestRateLimitHandler(transport);
        Assert.Same(callback, transport.ConnectCallback);
        Assert.Equal(TimeSpan.FromSeconds(3), transport.ConnectTimeout);
    }

    [Fact]
    public async Task ParallelStartsAreSpacedAndSharedAcrossClients()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(25), 64);
        var timestamps = new List<long>();
        var recording = new RecordingHandler(_ =>
        {
            lock (timestamps) timestamps.Add(Stopwatch.GetTimestamp());
            return new(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        using var first = new HttpClient(new RequestRateLimitHandler(recording, policy));
        using var second = new HttpClient(new RequestRateLimitHandler(new RecordingHandler(recording.Respond), policy));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => (i % 2 == 0 ? first : second).GetAsync(Endpoint)));
        foreach (var response in results) response.Dispose();
        Assert.Equal(4, timestamps.Count);
        Assert.True(Stopwatch.GetElapsedTime(timestamps.Min(), timestamps.Max()) >= TimeSpan.FromMilliseconds(65));
    }

    [Fact]
    public async Task CancellationWhileQueuedDoesNotSendOrBlockNextRequest()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(500), 64);
        int calls = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new RecordingHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return new(HttpStatusCode.OK);
        }), policy));
        using var initial = await client.GetAsync(Endpoint);
        using var cancellation = new CancellationTokenSource();
        var queued = client.GetAsync(Endpoint, cancellation.Token);
        Assert.False(queued.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, calls);
        using var next = await client.GetAsync(Endpoint);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task QueueLimitRejectsExcessAndCancellationReleasesCapacity()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromSeconds(5), 1);
        await policy.WaitAsync(Endpoint, default);
        using var cancellation = new CancellationTokenSource();
        var queued = policy.WaitAsync(Endpoint, cancellation.Token);
        await Assert.ThrowsAsync<HttpRequestException>(() => policy.WaitAsync(Endpoint, default));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        using var secondCancellation = new CancellationTokenSource(10);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.WaitAsync(Endpoint, secondCancellation.Token));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RetryAfterDelaysNextCallWithoutAutomaticallyRepeatingPost(int status)
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(1), 64);
        int calls = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(Interlocked.Increment(ref calls) == 1 ? (HttpStatusCode)status : HttpStatusCode.OK);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(80));
            return response;
        }), policy));
        using var first = await client.PostAsync(Endpoint, new StringContent("do not repeat"));
        Assert.Equal(1, calls);
        var timer = Stopwatch.StartNew();
        using var second = await client.GetAsync(Endpoint);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(55));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetRetriesWhenResponseEndsPrematurely()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(1), 64);
        int calls = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new RecordingHandler(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new HttpRequestException("An error occurred while sending the request.", new IOException("ResponseEnded"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), policy));

        using HttpResponseMessage response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetDoesNotRetryWhenTlsAuthenticationFails()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(1), 64);
        int calls = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new RecordingHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("The SSL connection could not be established.",
                new AuthenticationException("RemoteCertificateNameMismatch"));
        }), policy));

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Endpoint));

        Assert.IsType<AuthenticationException>(exception.InnerException);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetHonorsDisabledTransportRetryOption()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(1), 64);
        int calls = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new RecordingHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("An error occurred while sending the request.",
                new IOException("The response ended prematurely."));
        }), policy));
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Options.Set(RequestRateLimitHandler.DisableTransportRetriesOption, true);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PostIsNotRetriedAfterTransportFailure()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(1), 64);
        int calls = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new RecordingHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("transport failure", new IOException("connection reset"));
        }), policy));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(Endpoint, new StringContent("write")));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GitHubResetPausesEvenAfterSuccessfulLastRequest()
    {
        var policy = new RequestRateLimitPolicy(TimeSpan.FromMilliseconds(1), 64);
        var endpoint = new Uri("https://api.github.com/user");
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeSeconds().ToString());
        policy.Observe(endpoint, response);
        using var cancellation = new CancellationTokenSource(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.WaitAsync(endpoint, cancellation.Token));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, HttpResponseMessage> Respond => respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
