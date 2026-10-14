namespace WinProvision.Core.Services;

/// <summary>Public read-only endpoints shared by the client, CLI and exported scripts.</summary>
public static class CatalogEndpoints
{
    public const string LegacyPublicBaseUrl = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev";
    public const string PublicBaseUrl = "https://pub-e8bcd26106de4f4e82c3188c4a4ef887.r2.dev";
    public const string CatalogBaseUrl = PublicBaseUrl + "/Store/Catalog/";
    public const string FeaturedUrl = CatalogBaseUrl + "manifest/featured.json";
    public const string OfficeIconBaseUrl = CatalogBaseUrl + "office/media/icons";
    private static readonly string LegacyHost = new Uri(LegacyPublicBaseUrl).Host;

    public static string? ResolvePublicUrl(string? value) => ResolvePublicUrl(value, PublicBaseUrl);

    internal static string? ResolvePublicUrl(string? value, string publicBaseUrl)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !uri.Host.Equals(LegacyHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Store/", StringComparison.Ordinal)) return value;
        return publicBaseUrl.TrimEnd('/') + uri.PathAndQuery + uri.Fragment;
    }
}
