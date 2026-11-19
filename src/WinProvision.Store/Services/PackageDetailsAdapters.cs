using System.Globalization;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record PackageDetailsSnapshot(PackageDetailsRecord Package, PackageManagerDescriptor Manager);

public interface IPackageDetailsAdapter
{
    bool CanHandle(AppEntry app);
    PackageManagerDescriptor Manager { get; }
    PackageDetailsRecord CreateRecord(AppEntry app);
    Task<PackageDetailsRecord> LoadAsync(AppEntry app, CancellationToken token);
    Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token);
    string BuildCommandPreview(AppEntry app, PackageInstallOptions options, string operation);
}

public abstract class PackageDetailsAdapterBase : IPackageDetailsAdapter
{
    protected PackageDetailsAdapterBase(PackageManagerDescriptor manager) => Manager = manager;
    public PackageManagerDescriptor Manager { get; }
    public abstract bool CanHandle(AppEntry app);
    public virtual PackageDetailsRecord CreateRecord(AppEntry app) => FromCatalog(app, Manager.Source);
    public virtual Task<PackageDetailsRecord> LoadAsync(AppEntry app, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(CreateRecord(app));
    }
    public virtual Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token) => Task.FromResult(Array.Empty<string>());
    public virtual string BuildCommandPreview(AppEntry app, PackageInstallOptions options, string operation) =>
        ManualInstallCommandBuilder.BuildInstallCommand(app, options, operation);

    protected static PackageDetailsRecord FromCatalog(AppEntry app, string source, WingetPackageDetailsService.ExtendedPackageInfo? extended = null,
        bool allowWingetManifest = false, string? explicitManifestUrl = null)
    {
        CatalogInstaller? installer = SelectInstaller(app.Installers);
        string? licenseName = extended?.License ?? app.License;
        string? licenseUrl = extended?.LicenseUrl ?? app.LicenseUrl;
        string[] dependencies = !string.IsNullOrWhiteSpace(extended?.Dependencies)
            ? SplitValues(extended.Dependencies)
            : installer?.Dependencies.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() ?? [];
        string? releaseNotesText = extended?.ReleaseNotes ?? app.ReleaseNotes;
        string? releaseNotesUrl = extended?.ReleaseNotesUrl ?? app.ReleaseNotesUrl;
        DateTimeOffset? releaseDate = app.ReleaseDate;
        if (extended?.ReleaseDate is { Length: > 0 } rawDate
            && DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsedDate))
            releaseDate = parsedDate;

        string normalizedSource = source.Trim().ToLowerInvariant();
        string? manifestUrl = explicitManifestUrl;
        if (manifestUrl is null && allowWingetManifest
            && !string.Equals(app.CatalogOrigin, "curated", StringComparison.OrdinalIgnoreCase))
            manifestUrl = BuildWingetManifestUrl(app.Id);

        return new PackageDetailsRecord
        {
            Id = $"{normalizedSource}:{app.Id}",
            Source = normalizedSource,
            PackageId = app.Id,
            Name = app.Name,
            Version = FirstNonEmpty(extended?.Version, app.Version),
            Description = FirstNonEmpty(extended?.Description, app.Description),
            Tags = extended?.Tags.Count > 0 ? extended.Tags.ToArray() : app.Tags.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray(),
            Publisher = FirstNonEmpty(extended?.Publisher, app.Publisher),
            Author = FirstNonEmpty(extended?.Author, app.Author),
            Homepage = FirstNonEmpty(extended?.Homepage, app.Homepage),
            License = IsPresent(licenseName) || IsPresent(licenseUrl) ? new PackageLicenseDetails(NullIfEmpty(licenseName), NullIfEmpty(licenseUrl)) : null,
            IconUrl = NullIfEmpty(app.IconUrl),
            Screenshots = MapScreenshots(app, normalizedSource),
            LastUpdated = releaseDate,
            Dependencies = dependencies,
            ReleaseNotes = IsPresent(releaseNotesText) || IsPresent(releaseNotesUrl)
                ? new PackageReleaseNotes(NullIfEmpty(releaseNotesText), NullIfEmpty(releaseNotesUrl)) : null,
            ManifestUrl = manifestUrl,
            Installer = BuildInstaller(extended, installer, app.InstallerSizeBytes)
        };
    }

    private static PackageInstallerDetails? BuildInstaller(WingetPackageDetailsService.ExtendedPackageInfo? extended,
        CatalogInstaller? installer, long? sizeBytes)
    {
        string? type = FirstNonEmpty(extended?.InstallerType, installer?.Type);
        string? url = FirstNonEmpty(extended?.InstallerUrl, installer?.Url);
        string? sha = FirstNonEmpty(extended?.Sha256, installer?.Sha256);
        return IsPresent(type) || IsPresent(url) || IsPresent(sha) || sizeBytes is > 0
            ? new PackageInstallerDetails(NullIfEmpty(type), NullIfEmpty(url), NullIfEmpty(sha), sizeBytes is > 0 ? sizeBytes : null)
            : null;
    }

    private static CatalogInstaller? SelectInstaller(IReadOnlyList<CatalogInstaller> installers)
    {
        string architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        return installers.FirstOrDefault(item => string.Equals(item.Architecture, architecture, StringComparison.OrdinalIgnoreCase))
            ?? installers.FirstOrDefault();
    }

    private static string? BuildWingetManifestUrl(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string firstChar = Uri.EscapeDataString(char.ToLowerInvariant(id[0]).ToString());
        string[] segments = id.Split('.');
        string[] pathSegments = [segments[0], .. (segments.Length > 1 ? segments[1..] : segments)];
        string parts = string.Join('/', pathSegments.Select(Uri.EscapeDataString));
        return $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{firstChar}/{parts}";
    }

    private static string[] SplitValues(string? value) => string.IsNullOrWhiteSpace(value)
        ? [] : value.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string[] MapScreenshots(AppEntry app, string source)
    {
        if (!string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)
            && app.Media?.Screenshots is { Count: > 0 } mediaScreenshots)
        {
            string[] mediaUrls = mediaScreenshots.Select(path => IconService.ResolveCatalogMediaUrl(
                    app, path, app.Media.ScreenshotSha256.GetValueOrDefault(path)))
                .Where(url => !string.IsNullOrWhiteSpace(url)).Cast<string>().ToArray();
            if (mediaUrls.Length > 0) return mediaUrls;
        }

        IEnumerable<string?> urls = app.StoreScreenshotUrls ?? app.ScreenshotUrls ?? [];
        return (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)
                ? urls.Select(IconService.ResolveMicrosoftStoreAssetUrl)
                : urls.Select(CatalogEndpoints.ResolvePublicUrl))
            .Where(url => !string.IsNullOrWhiteSpace(url)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(IsPresent)?.Trim();
    private static bool IsPresent(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string? NullIfEmpty(string? value) => IsPresent(value) ? value!.Trim() : null;
}

public sealed class ChocolateyPackageDetailsAdapter : PackageDetailsAdapterBase
{
    private readonly ChocolateyService? _chocolatey;

    public ChocolateyPackageDetailsAdapter(ChocolateyService? chocolatey = null)
        : base(PackageManagerCatalog.Default.GetDescriptor("chocolatey")) => _chocolatey = chocolatey;

    public override bool CanHandle(AppEntry app) => string.Equals(app.Source, "chocolatey", StringComparison.OrdinalIgnoreCase);
    public override PackageDetailsRecord CreateRecord(AppEntry app) => FromCatalog(app, "chocolatey") with
    {
        ManifestUrl = null,
        Installer = null
    };

    public override Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token) =>
        _chocolatey is null ? Task.FromResult(Array.Empty<string>()) : _chocolatey.GetVersionsAsync(app.Id, token);
}

public class WingetPackageDetailsAdapter(WingetPackageDetailsService metadata)
    : PackageDetailsAdapterBase(PackageManagerCatalog.Default.GetDescriptor("winget"))
{
    public override bool CanHandle(AppEntry app) => string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(app.CatalogOrigin, "curated", StringComparison.OrdinalIgnoreCase);
    public override PackageDetailsRecord CreateRecord(AppEntry app) => FromCatalog(app, "winget", allowWingetManifest: true);
    public override async Task<PackageDetailsRecord> LoadAsync(AppEntry app, CancellationToken token)
    {
        WingetPackageDetailsService.ExtendedPackageInfo info = await metadata.GetAsync(app, token).ConfigureAwait(false);
        return FromCatalog(app, "winget", info, allowWingetManifest: true);
    }
    public override Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token) => metadata.GetVersionsAsync(app, token);
}

public sealed class MicrosoftStorePackageDetailsAdapter(WingetPackageDetailsService metadata)
    : PackageDetailsAdapterBase(PackageManagerCatalog.Default.GetDescriptor("msstore"))
{
    public override bool CanHandle(AppEntry app) => app.Source.Contains("store", StringComparison.OrdinalIgnoreCase)
        || string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase);
    public override PackageDetailsRecord CreateRecord(AppEntry app) => FromCatalog(app, "msstore",
        explicitManifestUrl: $"https://apps.microsoft.com/detail/{Uri.EscapeDataString(app.Id)}");
    public override async Task<PackageDetailsRecord> LoadAsync(AppEntry app, CancellationToken token)
    {
        WingetPackageDetailsService.ExtendedPackageInfo info = await metadata.GetAsync(app, token).ConfigureAwait(false);
        string productUrl = $"https://apps.microsoft.com/detail/{Uri.EscapeDataString(app.Id)}";
        return FromCatalog(app, "msstore", info, explicitManifestUrl: productUrl);
    }
}

public sealed class CuratedPackageDetailsAdapter : PackageDetailsAdapterBase
{
    public CuratedPackageDetailsAdapter() : base(PackageManagerCatalog.Default.GetDescriptor("curated")) { }
    public override bool CanHandle(AppEntry app) => string.Equals(app.CatalogOrigin, "curated", StringComparison.OrdinalIgnoreCase);
    public override PackageDetailsRecord CreateRecord(AppEntry app)
    {
        string? manifestUrl = app.WingetManifestAvailable == true ? BuildCuratedWingetManifestUrl(app.Id) : null;
        return FromCatalog(app, "winget", explicitManifestUrl: manifestUrl);
    }
    public override Task<PackageDetailsRecord> LoadAsync(AppEntry app, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(CreateRecord(app));
    }
    private static string? BuildCuratedWingetManifestUrl(string id) => string.IsNullOrWhiteSpace(id) ? null
        : $"https://github.com/microsoft/winget-pkgs/tree/master/manifests/{Uri.EscapeDataString(char.ToLowerInvariant(id[0]).ToString())}/{string.Join('/', id.Split('.').Select(Uri.EscapeDataString))}";
}

public sealed class GenericPackageDetailsAdapter : PackageDetailsAdapterBase
{
    public GenericPackageDetailsAdapter() : base(PackageManagerCatalog.Default.GetDescriptor("other")) { }
    public override bool CanHandle(AppEntry app) => true;
    public override PackageDetailsRecord CreateRecord(AppEntry app) => FromCatalog(app, app.Source);
}
