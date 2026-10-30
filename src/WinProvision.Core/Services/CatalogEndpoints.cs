namespace WinProvision.Core.Services;

/// <summary>Public read-only endpoints shared by the client, CLI and exported scripts.</summary>
public static class CatalogEndpoints
{
    public const string PublicBaseUrl = NetworkEndpoints.CatalogPublicBaseUrl;
    public const string CatalogBaseUrl = NetworkEndpoints.CatalogBaseUrl;
    public const string FeaturedUrl = NetworkEndpoints.FeaturedCatalogUrl;
    public const string OfficeIconBaseUrl = NetworkEndpoints.OfficeIconBaseUrl;

    public static string? ResolvePublicUrl(string? value) => NetworkEndpoints.ResolveCatalogUrl(value);

    internal static string? ResolvePublicUrl(string? value, string publicBaseUrl)
        => NetworkEndpoints.ResolveCatalogUrl(value, publicBaseUrl);
}
