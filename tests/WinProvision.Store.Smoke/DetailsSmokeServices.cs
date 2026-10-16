using Microsoft.UI.Xaml.Media.Imaging;
using WinProvision.Core.Models;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal sealed partial class SmokePresentationService(PresentationService presentation) : IPresentationService
{
    public bool ConfirmResult { get; set; } = true;
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public Task<string?> PickOpenAsync() => Task.FromResult(OpenPath);
    public Task<string?> PickSaveAsync(string name, string? extension = null) => Task.FromResult(SavePath);
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>("C:\\Apps\\Exemplo");
    public Task<bool> ConfirmAsync(string title, string message, string primaryButtonText = "Confirmar", string closeButtonText = "Cancelar") => Task.FromResult(ConfirmResult);
    public Task ShowDetailsAsync(AppEntry app, string? currentVersion = null, string? availableVersion = null) => presentation.ShowDetailsAsync(app, currentVersion, availableVersion);
    public void ShowAppUpdates() => presentation.ShowAppUpdates();
}

internal sealed partial class SmokeDetailsProvider : IPackageDetailsProvider
{
    public Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(app); }
    public Task<WingetPackageDetailsService.ExtendedPackageInfo> LoadExtendedAsync(AppEntry app, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new WingetPackageDetailsService.ExtendedPackageInfo()); }
}
internal sealed partial class SmokeDetailsImages : IDetailsImageService
{
    public Task<BitmapImage?> LoadAsync(string url, int width, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult<BitmapImage?>(null); }
}
