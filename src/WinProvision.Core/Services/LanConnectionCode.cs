using System.Text;

namespace WinProvision.Core.Services;

public sealed record LanClientConnection(string Address, string Token);

public static class LanConnectionCode
{
    private const string Prefix = "WPSLAN1:";
    private const int MaximumEncodedLength = 8192;

    public static string Encode(string address, string token)
    {
        Validate(address, token);
        string payload = $"1\n{address.TrimEnd('/')}\n{token}";
        return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? code, out LanClientConnection? connection)
    {
        connection = null;
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaximumEncodedLength
            || !code.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            string value = code[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + ((4 - value.Length % 4) % 4), '=');
            string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('\n');
            if (parts.Length != 3 || parts[0] != "1")
                return false;
            Validate(parts[1], parts[2]);
            connection = new(parts[1], parts[2]);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    private static void Validate(string address, string token)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out System.Net.IPAddress? ipAddress)
            || !LanTokenAuthenticator.IsPrivateAddress(ipAddress)
            || uri.Port is < 1 or > 65535)
            throw new ArgumentException("O endereço precisa ser HTTP e apontar para uma rede privada.", nameof(address));
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
            throw new ArgumentException("O token do servidor é inválido.", nameof(token));
    }
}
