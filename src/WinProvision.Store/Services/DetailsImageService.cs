using Microsoft.UI.Xaml.Media.Imaging;

namespace WinProvision.Store.Services;

public interface IDetailsImageService
{
    Task<BitmapImage?> LoadAsync(string url, int width, CancellationToken token);
}

public sealed partial class DetailsImageService : IDetailsImageService
{
    public Task<BitmapImage?> LoadAsync(string url, int width, CancellationToken token) =>
        AsyncImage.LoadBitmapAsync(url, width, token);
}
