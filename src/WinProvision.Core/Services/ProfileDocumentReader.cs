using System.Net;
using System.Text;
using System.Text.Json;

namespace WinProvision.Core.Services;

public sealed class ProfileDocumentReader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    public ProfileDocumentReader(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
    }
    public void Dispose() { if (_ownsClient) _http.Dispose(); }

    public async Task<string> ReadAsync(string location, CancellationToken token = default)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            await using var file = File.OpenRead(uri?.IsFile == true ? uri.LocalPath : location);
            return await ReadTextAsync(file, token).ConfigureAwait(false);
        }
        ValidateUri(uri);
        if (uri.Host.Equals("gist.github.com", StringComparison.OrdinalIgnoreCase))
        {
            string id = uri.AbsolutePath.Split('/').FirstOrDefault(segment => segment.Length is >= 20 and <= 40 && segment.All(char.IsAsciiHexDigit))
                ?? throw new ArgumentException("O endereço não contém um identificador de Gist válido.");
            uri = new Uri($"https://api.github.com/gists/{id}");
        }
        string text = await DownloadAsync(uri, token).ConfigureAwait(false);
        // A Gist API envelope is transport metadata, never an installation plan.
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/gists/", StringComparison.Ordinal))
        {
            using var gist = JsonDocument.Parse(text);
            if (!gist.RootElement.TryGetProperty("files", out var files)) throw new InvalidDataException("O Gist não contém arquivos de perfil.");
            var candidates = files.EnumerateObject().Where(file => file.Name.Equals("profile.json", StringComparison.OrdinalIgnoreCase)
                || file.Name.Equals("winprovision-profile.json", StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) candidates = files.EnumerateObject().Where(file => file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToList();
            var selected = candidates.FirstOrDefault(file => file.Name.Equals("profile.json", StringComparison.OrdinalIgnoreCase));
            if (selected.Value.ValueKind == JsonValueKind.Undefined)
            {
                if (candidates.Count != 1) throw new InvalidDataException("O Gist contém vários JSONs. Informe a URL raw do perfil desejado.");
                selected = candidates[0];
            }
            bool truncated = selected.Value.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (!truncated && selected.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? throw new InvalidDataException("Perfil vazio no Gist.");
            if (!selected.Value.TryGetProperty("raw_url", out var raw) || !Uri.TryCreate(raw.GetString(), UriKind.Absolute, out var rawUri)
                || !rawUri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O Gist não forneceu um endereço raw válido.");
            text = await DownloadAsync(rawUri, token).ConfigureAwait(false);
        }
        return text;
    }

    private async Task<string> DownloadAsync(Uri uri, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            ValidateUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("WinProvision-Store");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is not { } target) throw new InvalidDataException("Redirecionamento sem destino.");
                uri = target.IsAbsoluteUri ? target : new Uri(uri, target);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > ProfileDocumentService.MaximumBytes) throw new InvalidDataException("O perfil excede 16 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await ReadTextAsync(stream, token).ConfigureAwait(false);
        }
        throw new InvalidDataException("O endereço excedeu o limite de redirecionamentos.");
    }
    private static void ValidateUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Informe um arquivo local ou URL HTTPS sem credenciais no endereço.");
    }
    private static async Task<string> ReadTextAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > ProfileDocumentService.MaximumBytes) throw new InvalidDataException("O perfil excede 16 MiB.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }
}
