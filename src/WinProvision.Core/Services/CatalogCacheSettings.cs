namespace WinProvision.Core.Services;

public sealed record CatalogCacheSettings(string? SharedDirectory = null, int MaximumMegabytes = 512, int RevalidationHours = 6)
{
    public static string LocalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "Cache", "Catalog-v1");
    public string? NetworkCacheDirectory => string.IsNullOrWhiteSpace(SharedDirectory) ? null
        : Path.Combine(SharedDirectory, "WinProvisionCache", "Catalog-v1");

    public void Validate()
    {
        if (MaximumMegabytes is not (128 or 256 or 512 or 1024 or 2048))
            throw new ArgumentException("Escolha um limite de cache válido.");
        if (RevalidationHours is not (6 or 12 or 24))
            throw new ArgumentException("Escolha um prazo de revalidação válido.");
        if (SharedDirectory is not null && (!Path.IsPathFullyQualified(SharedDirectory)
            || SharedDirectory.Any(char.IsControl)))
            throw new ArgumentException("Informe uma pasta absoluta ou um compartilhamento de rede.");
    }
}

public static class CatalogCacheSettingsStore
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "cache-preferences.json");
    public static CatalogCacheSettings Load(string? path = null)
    {
        try
        {
            var settings = File.Exists(path ?? FilePath)
                ? AotJson.Deserialize<CatalogCacheSettings>(File.ReadAllText(path ?? FilePath)) : null;
            settings?.Validate();
            return settings ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { CoreLog.Warning(ex, "CacheSettings.Load"); return new(); }
    }
    public static void Save(CatalogCacheSettings settings, string? path = null)
    {
        settings.Validate();
        AtomicFile.WriteAllText(path ?? FilePath, AotJson.Serialize(settings));
    }
}
