using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace WinProvision.Viper;

internal sealed class ViperHttpException(HttpStatusCode status, TimeSpan? retryAfter) : HttpRequestException(
    $"O servidor retornou HTTP {(int)status}.", null, status)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class ViperTransport : IDisposable
{
    private readonly ViperOptions _options;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<string, HostGate> _hosts = new(StringComparer.OrdinalIgnoreCase);
    public ViperTransport(ViperOptions options)
    {
        _options = options; _global = new(options.MaxConnections);
        _client = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false,
            MaxConnectionsPerServer = options.MaxConnectionsPerHost, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = options.HeaderTimeout, EnableMultipleHttp2Connections = true,
            ConnectCallback = ViperHttpConnection.ConnectAsync
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" && !(uri.Scheme == "http" && _options.AllowHttp)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Use uma URL HTTPS sem credenciais ou fragmento; HTTP exige autorização explícita.");
    }
    public async Task<ResponseLease> SendAsync(ViperRequest download, long? from, long? to, string? etag, CancellationToken token,
        Uri? resolvedUri = null)
    {
        // Keep the identidade da solicitação inicial for credential isolation even when the
        // sondagem has resolved a mirror or a signed URL for this transfer.
        Uri uri = resolvedUri ?? download.Source;
        for (int redirect = 0; redirect <= 5; redirect++)
        {
            ValidateUri(uri);
            var host = _hosts.GetOrAdd(uri.GetLeftPart(UriPartial.Authority), _ => new HostGate(_options.MaxConnectionsPerHost, _options.MaxRetryDelay));
            using var hostSlot = await host.EnterAsync(token).ConfigureAwait(false);
            await _global.WaitAsync(token).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            bool transferLease = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                request.Headers.UserAgent.ParseAdd("WinProvision-Viper/0.1");
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                if (from is not null) request.Headers.Range = new RangeHeaderValue(from, to);
                if (etag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag));
                if (SameOrigin(uri, download.Source))
                    foreach (var header in download.Headers) request.Headers.Add(header.Key, header.Value);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(_options.HeaderTimeout);
                try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested) { throw new TimeoutException("O servidor demorou a responder.", ex); }
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirect == 5 || response.Headers.Location is not { } location) throw new ViperProtocolException("Redirecionamento inválido ou excessivo.");
                    Uri next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    uri = RequireSecureRedirect(uri, next); continue;
                }
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    TimeSpan? wait = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                    if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                        host.Throttle(wait ?? TimeSpan.FromSeconds(1));
                    throw new ViperHttpException(response.StatusCode, wait);
                }
                if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                    throw new ViperProtocolException("O servidor alterou a codificação do arquivo solicitado.");
                host.Succeeded();
                transferLease = true;
                return new ResponseLease(response, uri, _global, hostSlot.Detach());
            }
            finally
            {
                if (!transferLease) { response?.Dispose(); _global.Release(); }
            }
        }
        throw new ViperProtocolException("Redirecionamento excessivo.");
    }
    public async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                try { return await action().ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("O transporte interrompeu a transferência sem cancelamento do solicitante.", ex);
                }
            }
            catch (Exception ex) when (attempt < _options.MaxAttempts && IsTransient(ex) && !token.IsCancellationRequested)
            {
                TimeSpan delay = ex is ViperHttpException { RetryAfter: { } requested }
                    ? requested : TimeSpan.FromMilliseconds(Math.Min(15000, 500 * Math.Pow(2, attempt - 1)) + Random.Shared.Next(100, 350));
                if (delay > _options.MaxRetryDelay) throw;
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, token).ConfigureAwait(false);
            }
        }
    }
    internal static Uri RequireSecureRedirect(Uri current, Uri next)
    {
        if (current.Scheme != "https" || next.Scheme == "https") return next;
        // Mirror selectors can advertise HTTP even when the mirror supports TLS.
        // Contact only its HTTPS endpoint; never send a request over plain HTTP.
        if (next.Scheme == "http" && next.IsDefaultPort)
            return new UriBuilder(next) { Scheme = "https", Port = -1 }.Uri;
        throw new ViperProtocolException("O redirecionamento não oferece um destino HTTPS seguro.");
    }
    private static bool IsTransient(Exception ex) => ex switch
    {
        ViperProtocolException or ViperIntegrityException => false,
        ViperHttpException http => (int)http.StatusCode! is 408 or 429 or 500 or 502 or 503 or 504,
        // Conexão derrubada no meio do corpo chega como IOException com SocketException interna (não HttpIOException).
        // Erros de disco (sem SocketException interna) continuam não repetidos.
        IOException { InnerException: SocketException or HttpRequestException } => true,
        SocketException => true,
        HttpRequestException or HttpIOException or TimeoutException or EndOfStreamException => true,
        _ => false
    };
    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
    public void Dispose() { _client.Dispose(); _global.Dispose(); }

    internal sealed class ResponseLease(HttpResponseMessage response, Uri uri, SemaphoreSlim global, IDisposable host) : IDisposable
    {
        public HttpResponseMessage Response { get; } = response;
        public Uri Uri { get; } = uri;
        public void Dispose() { Response.Dispose(); host.Dispose(); global.Release(); }
    }
    private sealed class HostGate(int maximum, TimeSpan maximumWait)
    {
        private readonly object _gate = new();
        private readonly int _maximum = maximum;
        private int _active, _successes, _target = maximum;
        private DateTimeOffset _retryAt;
        public async Task<HostLease> EnterAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_retryAt - DateTimeOffset.UtcNow > maximumWait)
                        throw new ViperHttpException(HttpStatusCode.TooManyRequests, _retryAt - DateTimeOffset.UtcNow);
                    if (_active < _target && DateTimeOffset.UtcNow >= _retryAt) { _active++; return new(this); }
                }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
        public void Throttle(TimeSpan wait)
        {
            lock (_gate)
            {
                _target = Math.Max(1, _target / 2); _successes = 0;
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset next = wait >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + (wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
                if (next > _retryAt) _retryAt = next;
            }
        }
        public void Succeeded() { lock (_gate) if (++_successes >= 10) { _target = Math.Min(_maximum, _target + 1); _successes = 0; } }
        public sealed class HostLease(HostGate owner) : IDisposable
        {
            private HostGate? _owner = owner;
            public HostLease Detach() { var lease = new HostLease(_owner!); _owner = null; return lease; }
            public void Dispose() { if (Interlocked.Exchange(ref _owner, null) is { } current) lock (current._gate) current._active--; }
        }
    }
}
