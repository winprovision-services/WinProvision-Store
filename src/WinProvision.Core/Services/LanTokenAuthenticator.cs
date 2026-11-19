using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services;

public enum LanAuthenticationResult
{
    Authorized,
    Unauthorized,
    TemporarilyBlocked,
    PrivateNetworkRequired
}

public sealed class LanTokenAuthenticator : IDisposable
{
    private const int FailureLimit = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);
    private readonly byte[] _expected;
    private readonly object _gate = new();
    private readonly Dictionary<string, FailureState> _failures = new(StringComparer.Ordinal);

    public LanTokenAuthenticator(string token)
    {
        if (!TryDecodeToken(token, out _expected))
            throw new ArgumentException("O token LAN precisa conter 256 bits no formato base64url.", nameof(token));
    }

    public static string GenerateToken()
    {
        byte[] random = RandomNumberGenerator.GetBytes(32);
        try { return Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(random); }
    }

    public LanAuthenticationResult Authorize(IPAddress? remoteAddress, string? authorizationHeader, DateTimeOffset? now = null)
    {
        if (remoteAddress is null || !IsPrivateAddress(remoteAddress))
            return LanAuthenticationResult.PrivateNetworkRequired;

        string key = remoteAddress.ToString();
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_failures.TryGetValue(key, out FailureState? state) && state.BlockedUntil > timestamp)
                return LanAuthenticationResult.TemporarilyBlocked;
            if (state is not null && state.BlockedUntil <= timestamp && timestamp - state.WindowStarted > FailureWindow)
                _failures.Remove(key);
        }

        byte[]? supplied = null;
        bool matches = false;
        try
        {
            matches = authorizationHeader is not null
                && authorizationHeader.StartsWith("Bearer ", StringComparison.Ordinal)
                && TryDecodeToken(authorizationHeader[7..], out supplied)
                && CryptographicOperations.FixedTimeEquals(_expected, supplied);
        }
        finally
        {
            if (supplied is not null)
                CryptographicOperations.ZeroMemory(supplied);
        }

        if (matches)
        {
            lock (_gate) _failures.Remove(key);
            return LanAuthenticationResult.Authorized;
        }

        lock (_gate)
        {
            if (!_failures.TryGetValue(key, out FailureState? state) || timestamp - state.WindowStarted > FailureWindow)
                state = new FailureState(timestamp, 0, DateTimeOffset.MinValue);

            int count = state.Count + 1;
            DateTimeOffset blockedUntil = count >= FailureLimit ? timestamp + BlockDuration : DateTimeOffset.MinValue;
            _failures[key] = new FailureState(state.WindowStarted, count, blockedUntil);
            return blockedUntil > timestamp
                ? LanAuthenticationResult.TemporarilyBlocked
                : LanAuthenticationResult.Unauthorized;
        }
    }

    public static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 169 && bytes[1] == 254;

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && (bytes[0] & 0xFE) == 0xFC;
    }

    private static bool TryDecodeToken(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length != 43 || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        try
        {
            string base64 = value.Replace('-', '+').Replace('_', '/') + "=";
            bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 32)
                return true;
        }
        catch (FormatException) { }
        CryptographicOperations.ZeroMemory(bytes);
        bytes = [];
        return false;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_expected);

    private sealed record FailureState(DateTimeOffset WindowStarted, int Count, DateTimeOffset BlockedUntil);
}

public static class LanTokenStorage
{
    public static string ServerTokenPath => GetPath("server-token.dpapi");
    public static string ClientTokenPath => GetPath("client-token.dpapi");
    private static string ClientConnectionPath => GetPath("client-connection.dpapi");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void SaveServerToken(string token) => Backup.SecureTokenStore.Save(ServerTokenPath, token);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string? LoadServerToken() => Backup.SecureTokenStore.TryLoad(ServerTokenPath);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void SaveClientToken(string token) => Backup.SecureTokenStore.Save(ClientTokenPath, token);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string? LoadClientToken() => Backup.SecureTokenStore.TryLoad(ClientTokenPath);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void SaveClientConnection(string address, string token) =>
        Backup.SecureTokenStore.Save(ClientConnectionPath, address + "\n" + token);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static LanClientConnection? LoadClientConnection()
    {
        string? value = Backup.SecureTokenStore.TryLoad(ClientConnectionPath);
        if (value is null)
            return null;
        int separator = value.IndexOf('\n');
        return separator > 0 && separator < value.Length - 1
            ? new(value[..separator], value[(separator + 1)..])
            : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void DeleteClientConnection() => Backup.SecureTokenStore.Delete(ClientConnectionPath);

    private static string GetPath(string fileName) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore", "lan", fileName);
}
