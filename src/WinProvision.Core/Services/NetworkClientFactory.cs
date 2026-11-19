using System.Net.Http;

namespace WinProvision.Core.Services;

public static class NetworkClientFactory
{
    public static HttpClient Create(
        TimeSpan timeout,
        bool allowAutoRedirect = false,
        TimeSpan? pooledConnectionLifetime = null)
    {
        var transport = new SocketsHttpHandler { AllowAutoRedirect = allowAutoRedirect };
        if (pooledConnectionLifetime is { } lifetime)
            transport.PooledConnectionLifetime = lifetime;
        return new HttpClient(new RequestRateLimitHandler(transport)) { Timeout = timeout };
    }
}
