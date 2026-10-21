using System.Diagnostics;
using System.IO;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public partial class IconService
{
    private const string CatalogAppsBaseUrl = CatalogEndpoints.CatalogBaseUrl;
    // Exposto publicamente para compartilhar o fallback quando o app não tem
    // ícone na mídia V2 nem URL fornecida pela Microsoft Store.
    public const string DefaultIconPackUri = "pack://application:,,,/Assets/Icons/default_app.png";

    private readonly string _iconCacheFolder;
    private readonly string _legacyIconManifestCachePath;
    public IconService()
    {
        string appDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore"
        );

        _iconCacheFolder = Path.Combine(appDataFolder, "Cache", "Icons");
        _legacyIconManifestCachePath = Path.Combine(appDataFolder, "icon-manifest.json");
        Directory.CreateDirectory(_iconCacheFolder);
    }

    /// <summary>
    /// Limpa o cache local dos ícones extraídos de aplicativos instalados.
    /// </summary>
    public void ClearCache()
    {
        try
        {
            if (File.Exists(_legacyIconManifestCachePath))
                File.Delete(_legacyIconManifestCachePath);
            if ((File.GetAttributes(_iconCacheFolder) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A pasta de ícones não pode ser um link.");
            foreach (string file in Directory.EnumerateFiles(_iconCacheFolder, "*.png"))
                File.Delete(file);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconService] Não foi possível limpar todos os ícones locais: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Retorna a melhor URL ou caminho local de ícone para o aplicativo.
    /// Para o catálogo WinGet, usa a mídia declarada no próprio registro V2. Para a
    /// Microsoft Store, usa o ícone fornecido por ela. Sem mídia associada, retorna o
    /// ícone genérico local.
    /// </summary>
    public string ResolveIconUrl(AppEntry app)
    {
        if (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveMicrosoftStoreAssetUrl(app.StoreIconUrl) ?? DefaultIconPackUri;
        }

        if (ResolveCatalogMediaUrl(app, app.Media?.Icon, app.Media?.IconSha256) is { } catalogIcon)
            return catalogIcon;

        // Apps "msstore" usam o ícone fornecido pela própria Microsoft Store.
        if (!string.IsNullOrWhiteSpace(app.StoreIconUrl))
        {
            return CatalogEndpoints.ResolvePublicUrl(app.StoreIconUrl)!;
        }

        return DefaultIconPackUri;
    }

    public string? ResolveBannerUrl(AppEntry app) =>
        string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
            ? ResolveMicrosoftStoreAssetUrl(app.StoreBannerUrl)
            : ResolveCatalogMediaUrl(app, app.Media?.Banner, app.Media?.BannerSha256)
                ?? CatalogEndpoints.ResolvePublicUrl(app.StoreBannerUrl);

    public List<string> ResolveScreenshotUrls(AppEntry app)
    {
        if (!string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
            && app.Media?.Screenshots is { Count: > 0 } paths)
        {
            List<string> urls = paths
                .Select(path => ResolveCatalogMediaUrl(
                    app,
                    path,
                    app.Media?.ScreenshotSha256?.GetValueOrDefault(path)))
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Cast<string>()
                .ToList();
            if (urls.Count > 0)
                return urls;
        }

        IEnumerable<string?> screenshotUrls = app.StoreScreenshotUrls ?? app.ScreenshotUrls ?? [];
        return (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
                ? screenshotUrls.Select(ResolveMicrosoftStoreAssetUrl)
                : screenshotUrls.Select(url => CatalogEndpoints.ResolvePublicUrl(url)))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Cast<string>()
            .ToList();
    }

    public static string? ResolveMicrosoftStoreAssetUrl(string? value)
    {
        string? resolved = CatalogEndpoints.ResolvePublicUrl(value);
        if (!Uri.TryCreate(resolved, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host.Equals(new Uri(CatalogEndpoints.PublicBaseUrl).Host, StringComparison.OrdinalIgnoreCase))
            return null;
        return resolved;
    }

    public static string? ResolveCatalogMediaUrl(AppEntry app, string? mediaPath, string? sha256)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)
            || string.IsNullOrWhiteSpace(app.CatalogDetailPath)
            || !mediaPath.StartsWith("media/", StringComparison.Ordinal)
            || Path.IsPathRooted(mediaPath)
            || mediaPath.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or ".."))
            return null;

        string detailPath = app.CatalogDetailPath.Replace('\\', '/');
        int lastSlash = detailPath.LastIndexOf('/');
        if (lastSlash < 0)
            return null;

        string encodedMedia = string.Join('/', mediaPath.Split('/').Select(Uri.EscapeDataString));
        string baseUrl = app.CatalogDetailBaseUrl
            ?? (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
                ? CatalogAppsBaseUrl + "msstore"
                : CatalogAppsBaseUrl.TrimEnd('/'));
        string url = $"{baseUrl}/{detailPath[..(lastSlash + 1)]}{encodedMedia}";
        return !string.IsNullOrWhiteSpace(sha256) && sha256.Length == 64 && sha256.All(Uri.IsHexDigit)
            ? $"{url}?v={sha256[..16]}"
            : url;
    }

}
