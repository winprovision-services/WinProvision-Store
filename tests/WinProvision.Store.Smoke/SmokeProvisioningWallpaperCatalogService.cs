using WinProvision.Store.Services;

namespace WinProvision.Store;

/// <summary>Fixture local do smoke; não consulta o catálogo remoto nem mascara falhas da CDN.</summary>
internal sealed class SmokeProvisioningWallpaperCatalogService : ProvisioningWallpaperCatalogService
{
    private const string FixtureId = "smoke-wallpaper";
    private const string FixtureFileName = "wallpaper-dark.png";

    public override Task<IReadOnlyList<ProvisioningWallpaperDescriptor>> GetWallpapersAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string path = GetFixturePath();
        long size = new FileInfo(path).Length;
        ProvisioningWallpaperDescriptor[] fixtures =
        [new(FixtureId, FixtureFileName, new Uri(path).AbsoluteUri, size, "\"smoke-fixture-v1\"")];
        return Task.FromResult<IReadOnlyList<ProvisioningWallpaperDescriptor>>(fixtures);
    }

    public override async Task<byte[]> DownloadWallpaperAsync(ProvisioningWallpaperDescriptor wallpaper, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (wallpaper.Id != FixtureId || wallpaper.FileName != FixtureFileName
            || !Uri.TryCreate(wallpaper.Url, UriKind.Absolute, out Uri? uri) || !uri.IsFile
            || !string.Equals(uri.LocalPath, GetFixturePath(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O smoke tentou baixar um wallpaper fora da fixture local.");
        return await File.ReadAllBytesAsync(GetFixturePath(), token);
    }

    private static string GetFixturePath() => Path.Combine(AppContext.BaseDirectory,
        "Assets", "Windows11Preview", FixtureFileName);
}
