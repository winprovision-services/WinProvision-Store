using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WinProvision.Viper.Tests;

internal sealed record TestRequest(string Path, Dictionary<string, string> Headers);
internal sealed record TestResponse(int Status, byte[] Body, Dictionary<string, string>? Headers = null, int DelayMilliseconds = 0, bool Truncate = false, bool WithoutLength = false);

internal sealed class ViperTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _clients = [];
    private readonly Task _accept;
    private int _active, _peak;
    public int Peak => Volatile.Read(ref _peak);
    public ConcurrentQueue<TestRequest> Requests { get; } = [];
    public byte[] Payload { get; } = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i * 31 + i / 199)).ToArray();
    public string ETag { get; set; } = "\"v1\"";
    public Func<TestRequest, TestResponse?>? Override { get; set; }
    public bool IgnoreRanges { get; set; }
    public int BodyDelayMilliseconds { get; set; } = 20;
    public Uri Url { get; }
    public ViperTestServer()
    {
        _listener.Start(); Url = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/file");
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _clients.Add(HandleAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            int active = Interlocked.Increment(ref _active);
            int peak;
            do { peak = _peak; if (active <= peak) break; } while (Interlocked.CompareExchange(ref _peak, active, peak) != peak);
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                string? first = await reader.ReadLineAsync(_stop.Token);
                if (first is null) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
                var request = new TestRequest(first.Split(' ')[1], headers); Requests.Enqueue(request);
                TestResponse response = Override?.Invoke(request) ?? Normal(request);
                var fields = response.Headers ?? [];
                var text = new StringBuilder($"HTTP/1.1 {response.Status} Test\r\nConnection: close\r\n");
                if (!response.WithoutLength) text.Append($"Content-Length: {response.Body.Length}\r\n");
                foreach (var field in fields) text.Append(field.Key).Append(": ").Append(field.Value).Append("\r\n");
                text.Append("\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()), _stop.Token);
                if (response.DelayMilliseconds > 0) await Task.Delay(response.DelayMilliseconds, _stop.Token);
                int count = response.Truncate ? response.Body.Length / 2 : response.Body.Length;
                await stream.WriteAsync(response.Body.AsMemory(0, count), _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
    public TestResponse Normal(TestRequest request)
    {
        var headers = new Dictionary<string, string> { ["ETag"] = ETag, ["Accept-Ranges"] = "bytes" };
        if (!IgnoreRanges && request.Headers.TryGetValue("Range", out string? range))
        {
            string[] parts = range[6..].Split('-');
            int start = int.Parse(parts[0]), end = int.Parse(parts[1]);
            headers["Content-Range"] = $"bytes {start}-{end}/{Payload.Length}";
            return new(206, Payload[start..(end + 1)], headers, BodyDelayMilliseconds);
        }
        return new(200, Payload, headers, BodyDelayMilliseconds);
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _listener.Stop();
        await _accept;
        await Task.WhenAll(_clients);
        _stop.Dispose();
    }
}
