using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Services;

public enum PackageImageKind
{
    General,
    PackageIcon,
    Screenshot
}

public sealed record PackageImagePresentation(bool IsVisible, string? SourceUrl);

public sealed class PackageVisualPreferencesService(ApplicationPreferencesService preferences)
{
    private readonly ApplicationPreferencesService _preferences = preferences;

    public IReadOnlyList<WinProvision.Core.Models.PackageManagerSettingsField> Fields =>
        PackageVisualPreferencesCatalog.Default.Fields;

    public event Action<bool>? PackageIconsChanged
    {
        add => _preferences.PackageIconsChanged += value;
        remove => _preferences.PackageIconsChanged -= value;
    }

    public event Action<bool>? ScreenshotsChanged
    {
        add => _preferences.PackageScreenshotsChanged += value;
        remove => _preferences.PackageScreenshotsChanged -= value;
    }

    public bool ShouldLoad(PackageImageKind kind) => kind switch
    {
        PackageImageKind.PackageIcon => _preferences.ShowPackageIcons,
        PackageImageKind.Screenshot => _preferences.ShowPackageScreenshots,
        _ => true
    };

    public PackageImagePresentation GetPackageIcon(string? sourceUrl)
    {
        bool isVisible = ShouldLoad(PackageImageKind.PackageIcon);
        return new(isVisible, isVisible ? sourceUrl : null);
    }

    public IReadOnlyList<string> GetScreenshots(IEnumerable<string> sourceUrls) =>
        ShouldLoad(PackageImageKind.Screenshot)
            ? sourceUrls.Where(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme == Uri.UriSchemeHttps)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray()
            : [];

    public Task<T?> LoadIfEnabledAsync<T>(
        PackageImageKind kind,
        Func<Task<T?>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        return ShouldLoad(kind) ? load() : Task.FromResult<T?>(default);
    }

    public bool GetValue(string id) => id switch
    {
        "showPackageIcons" => _preferences.ShowPackageIcons,
        "showPackageScreenshots" => _preferences.ShowPackageScreenshots,
        _ => PackageVisualPreferencesCatalog.Default.GetDefaultValue(id)
    };

    public void SetValue(string id, bool enabled) => _preferences.SetPackageVisualPreference(id, enabled);

    public async Task<ImageSource?> LoadAsync(
        PackageImageKind kind,
        string url,
        int decodePixelWidth,
        CancellationToken cancellationToken) =>
        await LoadIfEnabledAsync(kind,
            () => AsyncImage.LoadImageAsync(url, decodePixelWidth, cancellationToken));
}
