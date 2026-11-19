using System.Net;
using System.Security.Authentication;

namespace WinProvision.Core.Services;

/// <summary>Spaces requests, configures dual-stack connections, observes backoff and retries transient GET failures.</summary>
public sealed class RequestRateLimitHandler : DelegatingHandler
{
    internal static readonly RequestRateLimitPolicy SharedPolicy = new(TimeSpan.FromMilliseconds(250), 64);
    public static readonly HttpRequestOptionsKey<bool> DisableTransportRetriesOption =
        new("WinProvision.DisableTransportRetries");
    private const int MaximumGetAttempts = 3;
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
        bool transportRetriesDisabled = request.Options.TryGetValue(DisableTransportRetriesOption, out bool disabled) && disabled;
        bool retryableGet = !transportRetriesDisabled && request.Method == HttpMethod.Get && request.Content is null;
        int maximumAttempts = retryableGet ? MaximumGetAttempts : 1;

        for (int attempt = 0; ; attempt++)
        {
            await _policy.WaitAsync(uri, cancellationToken).ConfigureAwait(false);
            try
            {
                // HttpRequestMessage instances cannot be sent more than once. GETs have no
                // body here, so create a fresh equivalent request for each transport attempt.
                using HttpRequestMessage? clone = retryableGet ? CloneGetRequest(request) : null;
                HttpRequestMessage outbound = clone ?? request;
                HttpResponseMessage response = await base.SendAsync(outbound, cancellationToken).ConfigureAwait(false);
                _policy.Observe(uri, response);
                if (!retryableGet || attempt + 1 >= maximumAttempts || !IsTransient(response.StatusCode))
                    return response;

                TimeSpan delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception) when (retryableGet && exception.StatusCode is null
                && !ContainsAuthenticationFailure(exception) && attempt + 1 < maximumAttempts)
            {
                await Task.Delay(GetRetryDelay(null, attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception) when (retryableGet
                && !ContainsAuthenticationFailure(exception) && attempt + 1 < maximumAttempts)
            {
                await Task.Delay(GetRetryDelay(null, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool ContainsAuthenticationFailure(Exception exception)
    {
        if (exception is AuthenticationException) return true;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(ContainsAuthenticationFailure);
        return exception.InnerException is { } inner && ContainsAuthenticationFailure(inner);
    }

    private static HttpRequestMessage CloneGetRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(HttpMethod.Get, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        return clone;
    }

    private static bool IsTransient(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is { } delta)
            return TimeSpan.FromSeconds(Math.Clamp(delta.TotalSeconds, 0, 30));
        if (response?.Headers.RetryAfter?.Date is { } date)
            return TimeSpan.FromSeconds(Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, 30));
        return TimeSpan.FromMilliseconds(Math.Min(1000, 250 * (attempt + 1)));
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
