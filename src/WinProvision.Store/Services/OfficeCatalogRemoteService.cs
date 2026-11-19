using System.Security.Cryptography;
using System.Text.Json;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>Carrega o catálogo Office remoto somente quando o índice fixa seu SHA-256; qualquer falha usa o JSON embutido.</summary>
public sealed class OfficeCatalogRemoteService(HttpClient http)
{
    private const string IndexUrl = NetworkEndpoints.CatalogPublicBaseUrl + "/office/catalog/index.json";
    private const int MaximumBytes = 2 * 1024 * 1024;

    public async Task<OfficeCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            byte[] indexBytes = await ReadLimitedAsync(IndexUrl, 16 * 1024, cancellationToken).ConfigureAwait(false);
            using JsonDocument index = JsonDocument.Parse(indexBytes);
            JsonElement root = index.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Versão do índice Office não suportada.");
            string path = root.GetProperty("catalogPath").GetString() ?? string.Empty;
            string expectedHash = root.GetProperty("catalogSha256").GetString() ?? string.Empty;
            if (!IsSafePath(path) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Referência/hash do catálogo Office inválido.");
            string url = NetworkEndpoints.CatalogPublicBaseUrl + "/" + path;
            byte[] bytes = await ReadLimitedAsync(url, MaximumBytes, cancellationToken).ConfigureAwait(false);
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(expectedHash)))
                throw new InvalidDataException("Hash ou tamanho do catálogo Office divergente.");
            OfficeCatalogDocument catalog = OfficeCatalogDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            OfficeCatalogRuntime.UseValidated(catalog);
            return catalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            WinProvisionLog.Warning(exception, "OfficeCatalog.Remote");
            return OfficeCatalogRuntime.Current;
        }
    }

    private async Task<byte[]> ReadLimitedAsync(string url, int maximumBytes, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidDataException("Catálogo Office excede o limite de tamanho.");
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Catálogo Office excede o limite de tamanho.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool IsSafePath(string path) => !string.IsNullOrWhiteSpace(path)
        && path.StartsWith("office/catalog/", StringComparison.Ordinal)
        && path.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/')
        && !path.Split('/').Any(part => part is "" or "." or "..");
}
