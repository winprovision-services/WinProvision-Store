namespace WinProvision.Core.Services;

public static class NetworkEndpoints
{
    public const string CatalogPublicBaseUrl = "https://cdn.winprovisionstore.com.br";
    public const string CatalogBaseUrl = CatalogPublicBaseUrl + "/Store/Catalog/";
    public const string FeaturedCatalogUrl = CatalogBaseUrl + "manifest/featured.json";
    public const string OfficeIconBaseUrl = CatalogPublicBaseUrl + "/office/media";
    public const string GitHubApiBaseUrl = "https://api.github.com";
    public const string GitHubWebBaseUrl = "https://github.com";
    public const string GitHubRawBaseUrl = "https://gist.githubusercontent.com";
    public const string DisplayCatalogProductsUrl = "https://displaycatalog.mp.microsoft.com/v7.0/products";
    public const string DisplayCatalogProductFamiliesUrl = "https://displaycatalog.mp.microsoft.com/v7.0/productFamilies/Apps/products";
    public const string WingetPackagesRawBaseUrl = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master";
    public const string OfficeFileListUrl = "https://clients.config.office.net/releases/v1.0/filelist";
    public const string OfficeDeploymentToolUrl = "https://download.microsoft.com/download/6c1eeb25-cf8b-41d9-8d0d-cc1dbc032140/officedeploymenttool_20326-20112.exe";
    public const string HelpRecoveryAssistantUrl = "https://aka.ms/SaRA_EnterpriseVersionFiles";
    public const string WingetDependenciesUrl = GitHubWebBaseUrl + "/microsoft/winget-cli/releases/latest/download/DesktopAppInstaller_Dependencies.zip";
    public const string WingetBundleUrl = GitHubWebBaseUrl + "/microsoft/winget-cli/releases/latest/download/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
    public const string StoreSetupUrl = GitHubWebBaseUrl + "/winprovision-services/WinProvision-Store/releases/latest/download/WinProvision.Store-Setup.exe";
    public const string MicrosoftStoreAppBaseUrl = "https://apps.microsoft.com/detail";

    private static readonly string[] LegacyCatalogHosts =
    [
        "pub-166b41912a994dbe86583ba10596d673.r2." + "dev",
        "pub-e8bcd26106de4f4e82c3188c4a4ef887.r2." + "dev"
    ];

    public static string GitHubAvatarUrl(string login) => $"{GitHubWebBaseUrl}/{login}.png";
    public static string GitHubRawFileUrl(string login, string gistId, string fileName) =>
        $"{GitHubRawBaseUrl}/{login}/{gistId}/raw/{fileName}";

    public static string GitHubRepositoryUrl(string repository, string path) =>
        $"{GitHubApiBaseUrl}/repos/{repository.Trim('/')}/{path.TrimStart('/')}";

    public static string? ResolveCatalogUrl(string? value) =>
        ResolveCatalogUrl(value, CatalogPublicBaseUrl);

    internal static string? ResolveCatalogUrl(string? value, string publicBaseUrl)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0
            || !LegacyCatalogHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Store/", StringComparison.Ordinal))
            return value;

        return publicBaseUrl.TrimEnd('/') + uri.PathAndQuery + uri.Fragment;
    }
}
