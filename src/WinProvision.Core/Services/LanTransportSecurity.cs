using System.Globalization;
using System.Net;

namespace WinProvision.Core.Services;

public interface ILanTransportSecurityPolicy
{
    string GetListenerPrefix(int port);
    HttpClient CreateClient();
    Uri ValidateServerAddress(string serverAddress);
}

public interface ILanManifestSignatureProvider
{
    byte[] Sign(ReadOnlySpan<byte> manifest);
    bool Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature);
}

public sealed class LanHttpTransportSecurityPolicy : ILanTransportSecurityPolicy
{
    public string GetListenerPrefix(int port) => $"http://+:{port.ToString(CultureInfo.InvariantCulture)}/";

    public HttpClient CreateClient() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    public Uri ValidateServerAddress(string serverAddress)
    {
        if (!Uri.TryCreate(serverAddress.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo)
            || !LanTokenAuthenticator.IsPrivateAddress(IPAddress.TryParse(uri.Host, out IPAddress? address)
                ? address : IPAddress.None)
            || uri.Port is < 1 or > 65535)
            throw new ArgumentException("Informe um endereço HTTP com IP privado e porta, por exemplo http://192.168.1.20:8080.");

        return new UriBuilder(uri) { Path = "/", Query = "", Fragment = "" }.Uri;
    }
}
