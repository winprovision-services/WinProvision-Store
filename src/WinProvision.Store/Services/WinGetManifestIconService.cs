using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Providers.WinGet.Cli.Parsing;

namespace WinProvision.Store.Services;

/// <summary>Resolves icons for WinGet packages from the public CDN or community manifests.</summary>
public sealed class WinGetManifestIconService
{
    private readonly HttpClient client;
    private readonly WingetExecutor _wingetExecutor;
    public WinGetManifestIconService(HttpClient client, WingetExecutor? wingetExecutor = null)
    { this.client = client; _wingetExecutor = wingetExecutor ?? new WingetExecutor(); }
    private const string CdnHost = "cdn.winget.microsoft.com";
    private const int MaximumManifestBytes = 256 * 1024;
    private const int MaximumIconBytes = 8 * 1024 * 1024;
    private static readonly Regex FieldPattern = new(
        """(?m)^\s*(PackageIdentifier|IconUrl|IconSha256):\s*['"]?([^\r\n'"]+)['"]?\s*$""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly ConcurrentDictionary<string, string> _iconUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _missingIcons = new(StringComparer.OrdinalIgnoreCase);

    internal static string? GetPublishedIconUrl(string id, string source) =>
        source.Equals("winget", StringComparison.OrdinalIgnoreCase)
        && GetPackageMediaPath(id, usePublishedKey: true) is { } packagePath
            ? packagePath + "media/icon.png"
            : null;

    private static string? GetPackageMediaPath(string id, bool usePublishedKey)
    {
        string[] segments = id.Split('.');
        if (segments.Length < 2 || segments.Any(segment => segment.Length == 0
            || segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '+')))
            return null;

        char first = char.ToLowerInvariant(segments[0][0]);
        char partition = char.IsAsciiLetterOrDigit(first) ? first : '_';
        // R2 stores the escaped segment as part of the object key. Escape it again for HTTP.
        var path = segments.Select(segment => usePublishedKey
            ? Uri.EscapeDataString(Uri.EscapeDataString(segment.ToLowerInvariant()))
            : Uri.EscapeDataString(segment));
        return $"{CatalogEndpoints.CatalogPackageMediaBaseUrl}/{partition}/{string.Join('/', path)}/";
    }

    internal async Task<string?> ResolvePublishedIconAsync(AppEntry package, CancellationToken token)
    {
        if (GetPublishedIconUrl(package.Id, package.Source) is not { } url) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using HttpResponseMessage response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                && string.Equals(response.Content.Headers.ContentType?.MediaType, "image/png", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength is null or (> 0 and <= MaximumIconBytes)
                ? url
                : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (HttpRequestException exception)
        {
            CoreLog.Debug($"R2 icon unavailable for {package.Id}: {exception.Message}");
            return null;
        }
    }

    public async Task<string?> ResolveAsync(AppEntry package, CancellationToken token = default)
    {
        if (!string.Equals(package.Source, "winget", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(package.Id))
            return null;
        string cacheKey = $"{package.Id}\u001f{package.Version}";
        if (_iconUrls.TryGetValue(cacheKey, out string? cached)) return cached;
        if (_missingIcons.TryGetValue(cacheKey, out DateTimeOffset retryAfter)
            && retryAfter > DateTimeOffset.UtcNow) return null;

        try
        {
            string? catalogIcon = await ResolveCatalogIconAsync(package.Id, token).ConfigureAwait(false);
            if (catalogIcon is not null) return _iconUrls.GetOrAdd(cacheKey, catalogIcon);

            IReadOnlyList<string> manifestPaths = await GetManifestPathsAsync(package, token).ConfigureAwait(false);
            foreach (string path in manifestPaths.Take(4))
            {
                token.ThrowIfCancellationRequested();
                string manifestUrl = $"https://{CdnHost}/cache/{path}";
                using HttpResponseMessage response = await client.GetAsync(manifestUrl,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                byte[] manifestBytes = await ReadBoundedAsync(response, MaximumManifestBytes, token).ConfigureAwait(false);
                string manifest = Encoding.UTF8.GetString(manifestBytes);
                Dictionary<string, string> fields = FieldPattern.Matches(manifest)
                    .GroupBy(match => match.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Groups[2].Value.Trim(), StringComparer.OrdinalIgnoreCase);
                if (!fields.TryGetValue("PackageIdentifier", out string? manifestId)
                    || !manifestId.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                    || !fields.TryGetValue("IconUrl", out string? iconUrl)
                    || !fields.TryGetValue("IconSha256", out string? iconSha256)
                    || !IsOfficialIconUrl(iconUrl, iconSha256))
                    continue;

                using HttpResponseMessage iconResponse = await client.GetAsync(iconUrl,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!iconResponse.IsSuccessStatusCode) continue;
                byte[] iconBytes = await ReadBoundedAsync(iconResponse, MaximumIconBytes, token).ConfigureAwait(false);
                string actualHash = Convert.ToHexStringLower(SHA256.HashData(iconBytes));
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash), Convert.FromHexString(iconSha256)))
                    continue;

                _missingIcons.TryRemove(cacheKey, out _);
                return _iconUrls.GetOrAdd(cacheKey, iconUrl);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception)
        {
            CoreLog.Debug($"WinGet icon request timed out for {package.Id}: {exception.Message}");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or TimeoutException or FormatException or InvalidDataException)
        {
            CoreLog.Debug($"WinGet icon metadata unavailable for {package.Id}: {exception.Message}");
        }

        _missingIcons[cacheKey] = DateTimeOffset.UtcNow.AddMinutes(15);
        return null;
    }

    private async Task<string?> ResolveCatalogIconAsync(string id, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { return await ReadCatalogIconAsync(id, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or InvalidDataException)
        {
            CoreLog.Debug($"R2 icon metadata unavailable for {id}: {exception.Message}");
            return null;
        }
    }

    private async Task<string?> ReadCatalogIconAsync(string id, CancellationToken token)
    {
        if (GetPackageMediaPath(id, usePublishedKey: false) is not { } packagePath) return null;
        using var response = await client.GetAsync(packagePath + "app.json",
            HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        byte[] bytes = await ReadBoundedAsync(response, 1_048_576, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("id", out JsonElement idElement)
            || !string.Equals(idElement.GetString(), id, StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("media", out JsonElement media)) return null;
        JsonElement icon = default;
        if (media.TryGetProperty("icons", out JsonElement icons) && icons.ValueKind == JsonValueKind.Array)
            icon = icons.EnumerateArray().FirstOrDefault();
        else if (media.TryGetProperty("icon", out JsonElement single)) icon = single;
        if (icon.ValueKind != JsonValueKind.Object || !icon.TryGetProperty("path", out JsonElement pathElement)) return null;
        string path = pathElement.GetString() ?? string.Empty;
        if (!path.StartsWith("media/", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)
            || path.Contains('\\') || path.StartsWith('/') || !Uri.TryCreate(packagePath + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("cdn.winprovisionstore.com.br", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsoluteUri;
    }

    internal static bool IsOfficialIconUrl(string? value, string? sha256)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(CdnHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/icons/", StringComparison.OrdinalIgnoreCase)
            || !(uri.AbsolutePath.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64)
            return false;

        try
        {
            if (Convert.FromHexString(sha256).Length != 32) return false;
            string expectedName = $"/icons/{sha256}{Path.GetExtension(uri.AbsolutePath)}";
            return uri.AbsolutePath.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException) { return false; }
    }

    internal static IReadOnlyList<string> ParseManifestPaths(string log) => WinGetCliTableParser.ParseManifestPaths(log);

    private async Task<IReadOnlyList<string>> GetManifestPathsAsync(AppEntry package, CancellationToken token)
    {
        IReadOnlyList<string> cachedPaths = FindCachedManifestPaths(package);
        if (cachedPaths.Count > 0) return cachedPaths;

        string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "DiagOutputDir");
        Directory.CreateDirectory(logDirectory);
        DateTime startTime = DateTime.UtcNow.AddSeconds(-2);
        var arguments = new List<string> { "show", "--id", package.Id, "--exact", "--source", "winget",
            "--accept-source-agreements", "--disable-interactivity", "--verbose-logs" };
        if (!string.IsNullOrWhiteSpace(package.Version) && package.Version != "-")
        {
            arguments.Add("--version");
            arguments.Add(package.Version);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        WingetExecutionResult query;
        try { query = await _wingetExecutor.ExecuteCliAsync(arguments, null, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("A consulta do manifesto do WinGet excedeu 20 segundos."); }
        if (!query.Success) return [];

        var paths = new List<string>();
        foreach (string file in Directory.EnumerateFiles(logDirectory, "WinGet-*.log")
                     .Select(path => new FileInfo(path)).Where(info => info.LastWriteTimeUtc >= startTime)
                     .OrderByDescending(info => info.LastWriteTimeUtc).Take(3).Select(info => info.FullName))
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > 4 * 1024 * 1024) continue;
            string text = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
            paths.AddRange(ParseManifestPaths(text));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static IReadOnlyList<string> FindCachedManifestPaths(AppEntry package)
    {
        string sourceCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "WinGet", "cache", "V2_M", "Microsoft.Winget.Source_8wekyb3d8bbwe", "manifests");
        string[] identifierParts = package.Id.Split('.');
        if (identifierParts.Any(part => part.Length == 0 || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            return [];

        string packageFolder = Path.Combine([sourceCache, identifierParts[0][..1].ToLowerInvariant(), .. identifierParts]);
        if (!Directory.Exists(packageFolder)) return [];

        IEnumerable<DirectoryInfo> versions = new DirectoryInfo(packageFolder).EnumerateDirectories()
            .OrderByDescending(directory => directory.LastWriteTimeUtc);
        if (!string.IsNullOrWhiteSpace(package.Version) && package.Version != "-")
            versions = versions.OrderByDescending(directory => directory.Name.Equals(package.Version, StringComparison.OrdinalIgnoreCase));

        var paths = new List<string>();
        foreach (DirectoryInfo version in versions.Take(4))
        {
            foreach (FileInfo manifest in version.EnumerateFiles("16cd", SearchOption.TopDirectoryOnly))
            {
                string relative = Path.GetRelativePath(sourceCache, manifest.FullName).Replace(Path.DirectorySeparatorChar, '/');
                paths.Add($"manifests/{relative}");
            }
        }
        return paths;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximumBytes))
            throw new InvalidDataException("O arquivo do CDN excede o tamanho aceito.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("O arquivo do CDN excede o tamanho aceito.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
