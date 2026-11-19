using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed class LanInstallerHttpServer : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, IServerModule> _modules;
    private readonly LanTokenAuthenticator _authenticator;
    private readonly ILanTransportSecurityPolicy _transport;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private int _nextRequestId;
    private Task? _acceptLoop;
    private string _rootDirectory = string.Empty;

    public event Action<LanRequestLog>? RequestCompleted;
    public bool IsRunning => _listener.IsListening;

    public LanInstallerHttpServer(IEnumerable<IServerModule> modules, string token, ILanTransportSecurityPolicy? transport = null)
    {
        _modules = modules.ToDictionary(module => module.Name, StringComparer.OrdinalIgnoreCase);
        _authenticator = new LanTokenAuthenticator(token);
        _transport = transport ?? new LanHttpTransportSecurityPolicy();
    }

    public Task StartAsync(string rootDirectory, int port, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (!Directory.Exists(rootDirectory)) throw new DirectoryNotFoundException("A pasta de instaladores não existe.");
        if (IsRunning) throw new InvalidOperationException("O servidor LAN já está em execução.");

        _rootDirectory = Path.GetFullPath(rootDirectory);
        _listener.Prefixes.Add(_transport.GetListenerPrefix(port));
        try
        {
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_stop.Token);
        }
        catch
        {
            _listener.Prefixes.Clear();
            throw;
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_listener.IsListening) return;
        _stop.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
        Task[] running = _requests.Values.ToArray();
        if (running.Length > 0)
            await Task.WhenAll(running).ConfigureAwait(false);
        _listener.Prefixes.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _authenticator.Dispose();
        _stop.Dispose();
    }

    public static string? ResolveSafeFilePath(string rootDirectory, string requestedName) =>
        LanInstallersServerModule.ResolveSafeFilePath(rootDirectory, requestedName);

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (token.IsCancellationRequested || !_listener.IsListening) { break; }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }

            int id = Interlocked.Increment(ref _nextRequestId);
            Task request = HandleRequestAsync(context, token);
            _requests[id] = request;
            _ = request.ContinueWith(completed => { _requests.TryRemove(id, out Task? ignored); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken token)
    {
        string ip = context.Request.RemoteEndPoint?.Address.ToString() ?? "desconhecido";
        string requestedPath = context.Request.Url?.AbsolutePath ?? "/";
        string? localFilePath = null;
        int status = (int)HttpStatusCode.BadRequest;
        try
        {
            if (!HttpMethods.IsGet(context.Request.HttpMethod))
            {
                status = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Headers[HttpResponseHeader.Allow] = "GET";
                return;
            }

            var auth = _authenticator.Authorize(context.Request.RemoteEndPoint?.Address,
                context.Request.Headers["Authorization"]);
            if (auth != LanAuthenticationResult.Authorized)
            {
                status = auth == LanAuthenticationResult.PrivateNetworkRequired
                    ? (int)HttpStatusCode.Forbidden
                    : (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (requestedPath == "/v1/index.json")
            {
                localFilePath = _rootDirectory;
                await WriteIndexAsync(context.Response, token).ConfigureAwait(false);
                status = (int)HttpStatusCode.OK;
                return;
            }

            IServerModule? module = _modules.Values.FirstOrDefault(candidate =>
                candidate.Path.Equals(requestedPath, StringComparison.OrdinalIgnoreCase));
            if (module is not null)
            {
                localFilePath = _rootDirectory;
                LanModulePayload payload = await module.BuildAsync(_rootDirectory, token).ConfigureAwait(false);
                await WriteBytesAsync(context.Response, payload.Content, "application/json; charset=utf-8", token).ConfigureAwait(false);
                status = (int)HttpStatusCode.OK;
                return;
            }

            const string filePrefix = "/v1/files/";
            if (requestedPath.StartsWith(filePrefix, StringComparison.Ordinal))
            {
                string fileName;
                try { fileName = Uri.UnescapeDataString(requestedPath[filePrefix.Length..]); }
                catch (UriFormatException) { fileName = string.Empty; }
                string? filePath = ResolveSafeFilePath(_rootDirectory, fileName);
                localFilePath = filePath;
                bool isListed = false;
                if (filePath is not null)
                    foreach (IServerModule candidate in _modules.Values)
                        if ((await candidate.GetServedFilesAsync(_rootDirectory, token).ConfigureAwait(false))
                            .Contains(fileName, StringComparer.OrdinalIgnoreCase))
                        {
                            isListed = true;
                            break;
                        }
                if (!isListed || filePath is null || !File.Exists(filePath)
                    || (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                {
                    status = (int)HttpStatusCode.NotFound;
                    return;
                }
                try
                {
                    status = await StreamFileAsync(context, filePath, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Servidor LAN: falha ao abrir/servir o arquivo '{filePath}': {exception.Message}", exception);
                }
                return;
            }

            status = (int)HttpStatusCode.NotFound;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = (int)HttpStatusCode.ServiceUnavailable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or HttpListenerException)
        {
            status = (int)HttpStatusCode.InternalServerError;
            CoreLog.Warning(exception, $"LanServer.Request.{localFilePath ?? requestedPath}");
            byte[] error = Encoding.UTF8.GetBytes(exception.Message.Length <= 2048
                ? exception.Message
                : exception.Message[..2048]);
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = error.Length;
            try { await context.Response.OutputStream.WriteAsync(error, token).ConfigureAwait(false); }
            catch (Exception writeException) when (writeException is IOException or HttpListenerException or OperationCanceledException)
            {
                CoreLog.Warning(writeException, $"LanServer.ErrorResponse.{localFilePath ?? requestedPath}");
            }
        }
        finally
        {
            try { if (context.Response.OutputStream.CanWrite) context.Response.StatusCode = status; }
            catch (HttpListenerException) { }
            try { context.Response.Close(); }
            catch (HttpListenerException) { }
            try { RequestCompleted?.Invoke(new(DateTimeOffset.UtcNow, ip, status == (int)HttpStatusCode.InternalServerError
                ? localFilePath ?? requestedPath : requestedPath, status)); }
            catch (Exception exception) { CoreLog.Warning(exception, "LanServer.RequestLog"); }
        }
    }

    private async Task WriteIndexAsync(HttpListenerResponse response, CancellationToken token)
    {
        var index = new LanIndexDocument { GeneratedAt = DateTimeOffset.UtcNow };
        foreach (IServerModule module in _modules.Values)
        {
            try
            {
                LanModulePayload payload = await module.BuildAsync(_rootDirectory, token).ConfigureAwait(false);
                index.Modules.Add(new LanModuleDescriptor
                {
                    Name = payload.Name,
                    Version = payload.Version,
                    Path = payload.Path,
                    Sha256 = Convert.ToHexString(SHA256.HashData(payload.Content))
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(index, CoreJsonContext.Default.LanIndexDocument);
        await WriteBytesAsync(response, bytes, "application/json; charset=utf-8", token).ConfigureAwait(false);
    }

    private static async Task<int> StreamFileAsync(HttpListenerContext context, string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long start = 0;
        long end = stream.Length - 1;
        int status = (int)HttpStatusCode.OK;
        string? range = context.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(range))
        {
            if (!TryParseRange(range, stream.Length, out start, out end))
            {
                context.Response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                context.Response.Headers[HttpResponseHeader.ContentRange] = $"bytes */{stream.Length}";
                return (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            }
            status = (int)HttpStatusCode.PartialContent;
            context.Response.Headers[HttpResponseHeader.ContentRange] = $"bytes {start}-{end}/{stream.Length}";
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength64 = Math.Max(0, end - start + 1);
        context.Response.Headers[HttpResponseHeader.AcceptRanges] = "bytes";
        stream.Position = start;
        byte[] buffer = new byte[128 * 1024];
        long remaining = end - start + 1;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) break;
            await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
        }
        return status;
    }

    private static bool TryParseRange(string value, long length, out long start, out long end)
    {
        start = 0;
        end = length - 1;
        if (length <= 0 || !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)
            || value.Contains(',', StringComparison.Ordinal))
            return false;
        string[] parts = value[6..].Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out start)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)
            || start < 0 || end < start || end >= length)
            return false;
        return true;
    }

    private static async Task WriteBytesAsync(HttpListenerResponse response, byte[] bytes, string contentType, CancellationToken token)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static class HttpMethods
    {
        public static bool IsGet(string method) => method.Equals("GET", StringComparison.OrdinalIgnoreCase);
    }
}
