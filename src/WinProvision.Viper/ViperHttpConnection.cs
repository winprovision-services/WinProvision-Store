using System.Net;
using System.Net.Sockets;

namespace WinProvision.Viper;

/// <summary>Conexão TCP com tentativas alternadas de IPv6/IPv4; o handler continua responsável pelo TLS.</summary>
public static class ViperHttpConnection
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        // Alternate families so an unreachable IPv6 route does not hold IPv4 behind
        // the operating system's TCP timeout (or behind several other IPv6 addresses).
        var primary = addresses.Where(address => address.AddressFamily == addresses[0].AddressFamily).ToArray();
        var secondary = addresses.Where(address => address.AddressFamily != addresses[0].AddressFamily).ToArray();
        var ordered = new List<IPAddress>(addresses.Length);
        for (int index = 0; index < Math.Max(primary.Length, secondary.Length); index++)
        {
            if (index < primary.Length) ordered.Add(primary[index]);
            if (index < secondary.Length) ordered.Add(secondary[index]);
        }
        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<Socket>[] connections = ordered.Select((address, index) =>
            ConnectAddressAsync(address, context.DnsEndPoint.Port, index, attempts.Token)).ToArray();
        var pending = connections.ToList();
        Socket? winner = null;
        Exception? failure = null;
        try
        {
            while (pending.Count > 0)
            {
                Task<Socket> completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                try
                {
                    winner = await completed.ConfigureAwait(false);
                    return new NetworkStream(winner, ownsSocket: true);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException)
                {
                    token.ThrowIfCancellationRequested();
                    failure = exception;
                }
            }
            throw failure ?? new SocketException((int)SocketError.NotConnected);
        }
        finally
        {
            await attempts.CancelAsync().ConfigureAwait(false);
            // Observe every attempt and dispose any socket that connected concurrently.
            foreach (Task<Socket> connection in connections)
            {
                try
                {
                    Socket socket = await connection.ConfigureAwait(false);
                    if (!ReferenceEquals(socket, winner)) socket.Dispose();
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException) { }
            }
        }
    }

    private static async Task<Socket> ConnectAddressAsync(IPAddress address, int port, int index, CancellationToken token)
    {
        if (index > 0) await Task.Delay(TimeSpan.FromMilliseconds(250L * index), token).ConfigureAwait(false);
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }
}
