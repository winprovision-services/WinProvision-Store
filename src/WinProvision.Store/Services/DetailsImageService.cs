using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Services;

public interface IDetailsImageService
{
    Task<ImageSource?> LoadAsync(string url, int width, CancellationToken token);
}

public sealed partial class DetailsImageService(PackageVisualPreferencesService preferences) : IDetailsImageService
{
    public Task<ImageSource?> LoadAsync(string url, int width, CancellationToken token) =>
        preferences.LoadAsync(PackageImageKind.Screenshot, url, width, token);
}
