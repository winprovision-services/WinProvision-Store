using System.Net;

namespace WinProvision.Core.Services;

/// <summary>Spaces application requests, configures dual-stack connections and observes server backoff without retrying writes.</summary>
public sealed class RequestRateLimitHandler : DelegatingHandler
{
    internal static readonly RequestRateLimitPolicy SharedPolicy = new(TimeSpan.FromMilliseconds(250), 64);
    private readonly RequestRateLimitPolicy _policy;

    public RequestRateLimitHandler(HttpMessageHandler transport) : this(transport, SharedPolicy) { }
    internal RequestRateLimitHandler(HttpMessageHandler transport, RequestRateLimitPolicy policy) : base(ConfigureTransport(transport)) => _policy = policy;

    private static HttpMessageHandler ConfigureTransport(HttpMessageHandler transport)
    {
        if (transport is SocketsHttpHandler sockets)
        {
            // Startup clients (catalogs, featured apps and images) must not wait for
            // an unreachable IPv6 route before trying an available IPv4 address.
            sockets.ConnectCallback ??= WinProvision.Viper.ViperHttpConnection.ConnectAsync;
            if (sockets.ConnectTimeout == Timeout.InfiniteTimeSpan)
                sockets.ConnectTimeout = TimeSpan.FromSeconds(15);
        }
        return transport;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new HttpRequestException("A requisição não possui destino.");
        await _policy.WaitAsync(uri, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _policy.Observe(uri, response);
        return response;
    }
}

internal sealed class RequestRateLimitPolicy(TimeSpan interval, int maximumQueued)
{
    // Fixed buckets bound RAM even when an untrusted catalogue supplies many hosts.
    // Collisions share a more conservative quota; they never weaken a host's limit.
    private readonly Bucket[] _buckets = Enumerable.Range(0, 256).Select(_ => new Bucket()).ToArray();

    private Bucket GetBucket(Uri uri) => _buckets[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(uri.IdnHost) % 256];

    internal async Task WaitAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bucket = GetBucket(uri);
        if (Interlocked.Increment(ref bucket.Waiters) > maximumQueued)
        {
            Interlocked.Decrement(ref bucket.Waiters);
            throw new HttpRequestException("Muitas requisições pendentes para este servidor. Tente novamente em instantes.");
        }
        bool entered = false;
        try
        {
            await bucket.Gate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            while (true)
            {
                TimeSpan delay;
                lock (bucket)
                {
                    var now = DateTimeOffset.UtcNow;
                    delay = bucket.NextRequest - now;
                    if (delay <= TimeSpan.Zero)
                    {
                        bucket.NextRequest = now + interval;
                        return;
                    }
                }
                // Recheck after each bounded delay, including any new server cooldown.
                await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (entered) bucket.Gate.Release();
            Interlocked.Decrement(ref bucket.Waiters);
        }
    }

    internal void Observe(Uri uri, HttpResponseMessage response)
    {
        DateTimeOffset? until = null;
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            if (response.Headers.RetryAfter?.Date is { } date) until = date;
            else if (response.Headers.RetryAfter?.Delta is { } delta)
                until = DateTimeOffset.UtcNow + (delta > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delta);
            else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        }
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")
            && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out long seconds))
        {
            try
            {
                var resetTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (until is null || resetTime > until) until = resetTime;
            }
            catch (ArgumentOutOfRangeException) { /* Invalid server metadata does not break the response. */ }
        }
        if (until is null) return;
        var bucket = GetBucket(uri);
        lock (bucket)
            if (until.Value > bucket.NextRequest) bucket.NextRequest = until.Value;
    }

    private sealed class Bucket
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Waiters;
        internal DateTimeOffset NextRequest;
    }
}
