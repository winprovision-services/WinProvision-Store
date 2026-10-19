using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal static partial class SmokeTest
{
    private static async Task VerifyImageCacheRenewalAsync()
    {
        byte[] png = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "default_app.png"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/{Guid.NewGuid():N}.png";
        string path = Path.Combine(AsyncImage.ImageCacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool conditionalSeen = false;
        async Task ServeAsync()
        {
            for (int responseIndex = 0; responseIndex < 4; responseIndex++)
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var headers = new StringBuilder();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token))) headers.AppendLine(line);
                if (responseIndex == 1) conditionalSeen = headers.ToString().Contains("If-None-Match: \"v1\"", StringComparison.OrdinalIgnoreCase);
                string status = responseIndex switch { 1 => "304 Not Modified", 2 => "503 Service Unavailable", _ => "200 OK" };
                byte[] body = responseIndex switch { 0 => png, 3 => Encoding.UTF8.GetBytes("not an image"), _ => [] };
                byte[] responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nETag: \"v1\"\r\nContent-Type: image/png\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeaders, timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
                await stream.FlushAsync(timeout.Token);
            }
        }
        Task server = ServeAsync();
        try
        {
            Check(await AsyncImage.LoadBitmapAsync(url, 32, timeout.Token) is not null, "Imagens: download local validado e decodificado pelo carregador real");
            Check(File.Exists(path), "Imagens: arquivo publicado após decodificação");
            for (int step = 0; step < 3; step++)
            {
                var metadata = ImageCacheTransfer.ReadMetadata(path + ".meta.json")!;
                await ImageCacheTransfer.WriteMetadataAsync(path + ".meta.json", metadata with { ValidatedUtc = DateTimeOffset.UtcNow.AddDays(-31) }, timeout.Token);
                AsyncImage.ClearMemoryCache();
                Check(await AsyncImage.LoadBitmapAsync(url, 32, timeout.Token) is not null,
                    step switch { 0 => "Imagens: HTTP 304 reutiliza a imagem anterior", 1 => "Imagens: falha de rede preserva a cópia válida", _ => "Imagens: resposta inválida não substitui a cópia válida" });
                Check((await File.ReadAllBytesAsync(path, timeout.Token)).SequenceEqual(png), "Imagens: bytes válidos preservados no disco");
            }
            Check(conditionalSeen, "Imagens: revalidação envia o ETag anterior");
            await server;
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await server; } catch (Exception) when (timeout.IsCancellationRequested) { }
            AsyncImage.ClearMemoryCache();
            // Only this probe's unique URL hash is removed; other cache entries are untouched.
            File.Delete(path);
            File.Delete(path + ".meta.json");
        }
    }
}
