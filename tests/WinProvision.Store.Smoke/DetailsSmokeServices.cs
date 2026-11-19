using Microsoft.UI.Xaml.Media;
using WinProvision.Core.Models;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal sealed partial class SmokePresentationService(PresentationService presentation) : IPresentationService
{
    public bool ConfirmResult { get; set; } = true;
    public int ConfirmCallCount { get; private set; }
    public string? LastConfirmTitle { get; private set; }
    public string? LastConfirmMessage { get; private set; }
    public bool RecordDetailsOnly { get; set; }
    public int DetailsCallCount { get; private set; }
    public AppEntry? LastDetailsApp { get; private set; }
    public string? OpenPath { get; set; }
    public string? SavePath { get; set; }
    public Task<string?>? FolderTask { get; set; }
    public Task<string?> PickOpenAsync() => Task.FromResult(OpenPath);
    public Task<string?> PickExecutableAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickImageAsync() => Task.FromResult<string?>(null);
    public Task<string?> PickSaveAsync(string name, string? extension = null) => Task.FromResult(SavePath);
    public Task<string?> PickFolderAsync() => FolderTask ?? Task.FromResult<string?>("C:\\Apps\\Exemplo");
    public Task<bool> ConfirmAsync(string title, string message, string primaryButtonText = "Confirmar", string closeButtonText = "Cancelar")
    {
        ConfirmCallCount++;
        LastConfirmTitle = title;
        LastConfirmMessage = message;
        return Task.FromResult(ConfirmResult);
    }
    public Task<int?> ChooseAsync(string title, IReadOnlyList<string> options, string primaryButtonText = "Importar") => Task.FromResult<int?>(null);
    public Task ShowDetailsAsync(AppEntry app, string? currentVersion = null, string? availableVersion = null, bool showOptions = false)
    {
        if (RecordDetailsOnly)
        {
            DetailsCallCount++;
            LastDetailsApp = app;
            return Task.CompletedTask;
        }
        return presentation.ShowDetailsAsync(app, currentVersion, availableVersion, showOptions);
    }
    public void ShowAppUpdates() => presentation.ShowAppUpdates();
}

internal sealed partial class SmokeDetailsProvider : IPackageDetailsProvider
{
    private readonly IPackageDetailsAdapter[] _adapters =
    [
        new ChocolateyPackageDetailsAdapter(),
        new MicrosoftStorePackageDetailsAdapter(new WingetPackageDetailsService()),
        new CuratedPackageDetailsAdapter(),
        new WingetPackageDetailsAdapter(new WingetPackageDetailsService()),
        new GenericPackageDetailsAdapter()
    ];

    public Task<AppEntry> LoadCatalogAsync(AppEntry app, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(app); }
    public PackageManagerDescriptor GetManager(AppEntry app) => GetAdapter(app).Manager;
    public PackageDetailsSnapshot CreateSnapshot(AppEntry app) => new(GetAdapter(app).CreateRecord(app), GetManager(app));
    public Task<PackageDetailsSnapshot> LoadDetailsAsync(AppEntry app, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(CreateSnapshot(app)); }
    public Task<string[]> LoadVersionsAsync(AppEntry app, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(new[] { "1.2.3", "1.2.2" }); }
    public Task<IReadOnlyList<CatalogInstaller>> LoadVersionInstallersAsync(AppEntry app, string? version, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<CatalogInstaller>>(
        new CatalogInstaller[] { new() { Scope = "user" }, new() { Scope = "machine" } }); }
    public string BuildCommandPreview(AppEntry app, PackageInstallOptions options, string operation) =>
        GetAdapter(app).BuildCommandPreview(app, options, operation);

    private IPackageDetailsAdapter GetAdapter(AppEntry app) => _adapters.First(adapter => adapter.CanHandle(app));
}
internal sealed partial class SmokeDetailsImages : IDetailsImageService
{
    public Task<ImageSource?> LoadAsync(string url, int width, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult<ImageSource?>(null); }
}
