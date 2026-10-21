namespace WinProvision.Core.Services;

/// <summary>Public read-only endpoints shared by the client, CLI and exported scripts.</summary>
public static class CatalogEndpoints
{
    public const string PublicBaseUrl = "https://cdn.winprovisionstore.com.br";
    public const string CatalogBaseUrl = PublicBaseUrl + "/Store/Catalog/";
    public const string FeaturedUrl = CatalogBaseUrl + "manifest/featured.json";
    public const string OfficeIconBaseUrl = PublicBaseUrl + "/office/media";
    private static readonly string[] LegacyHosts =
    [
        "pub-166b41912a994dbe86583ba10596d673.r2." + "dev",
        "pub-e8bcd26106de4f4e82c3188c4a4ef887.r2." + "dev"
    ];

    public static string? ResolvePublicUrl(string? value) => ResolvePublicUrl(value, PublicBaseUrl);

    internal static string? ResolvePublicUrl(string? value, string publicBaseUrl)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !LegacyHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Store/", StringComparison.Ordinal)) return value;
        return publicBaseUrl.TrimEnd('/') + uri.PathAndQuery + uri.Fragment;
    }
}
