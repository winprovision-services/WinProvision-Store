using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
namespace WinProvision.Store.ViewModels;
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class HomeViewModel : PageViewModel, IDisposable
{
    private readonly StoreService _store;
    private readonly InstalledAppsService _installed;
    private readonly IPackageBackend _backend;
    private readonly IPresentationService _presentation;
    private readonly CollectionsViewModel _collections;
    private readonly IUiDispatcher _dispatcher;
    private IReadOnlyList<AppEntry> _catalog = [];
    private int _limit = 80;
    private bool _loaded;
    public StorefrontViewModel Storefront { get; }
    public ObservableCollection<PackageItemViewModel> Apps { get; } = new();
    public IReadOnlyList<string> Sources { get; } = (string[])["Todas as origens", "WinGet", "Microsoft Store"];
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string SelectedSource { get; set; } = "Todas as origens";
    [ObservableProperty] public partial bool HasMore { get; set; }
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand LoadMoreCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public HomeViewModel(StoreService store, InstalledAppsService installed, IPackageBackend backend, IPresentationService presentation, CollectionsViewModel collections, IUiDispatcher dispatcher, StorefrontViewModel storefront)
    {
        _store = store; _installed = installed; _backend = backend; _presentation = presentation; _collections = collections; _dispatcher = dispatcher;
        Storefront = storefront;
        RefreshCommand = new AsyncRelayCommand(token => LoadAsync(true, token));
        CancelCommand = new RelayCommand(() => RefreshCommand.Cancel());
        LoadMoreCommand = new RelayCommand(() => { _limit += 80; ApplyFilter(); }, () => HasMore);
        store.CatalogUpdated += CatalogUpdated;
        installed.Changed += InstalledChanged;
    }
    internal void LoadPreview(IReadOnlyList<AppEntry> apps) { _catalog = apps; _loaded = true; ApplyFilter(); Storefront.LoadPreview(apps); }
    public void AddApp(AppEntry app) => _collections.Add(app);
    public async Task ShowDetailsAsync(AppEntry app)
    {
        try { await _presentation.ShowDetailsAsync(app); }
        catch (Exception exception) { ReportError(exception); }
    }
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : RefreshCommand.ExecutionTask is { IsCompleted: false } task ? task : RefreshCommand.ExecuteAsync(null);
    private async Task LoadAsync(bool force, CancellationToken token)
    {
        IsBusy = true; HasError = false;
        try
        {
            _catalog = await _store.LoadCatalogAsync(forceRefresh: _loaded && force, cancellationToken: token);
            _loaded = true; ApplyFilter();
            await _installed.EnsureLoadedAsync(token);
            RefreshInstalled();
        }
        catch (OperationCanceledException) { StatusText = "Carregamento cancelado."; }
        catch (Exception exception) { ReportError(exception); }
        finally { IsBusy = false; }
    }
    private void CatalogUpdated(IReadOnlyList<AppEntry> apps) => _dispatcher.Post(() => { _catalog = apps; ApplyFilter(); });
    private void InstalledChanged() => _dispatcher.Post(RefreshInstalled);
    private void RefreshInstalled()
    {
        foreach (var item in Apps) item.IsInstalled = _installed.IsInstalled(item.Name, item.Id);
    }
    partial void OnSearchTextChanged(string value) { _limit = 80; ApplyFilter(); }
    partial void OnSelectedSourceChanged(string value) { _limit = 80; ApplyFilter(); }
    partial void OnHasMoreChanged(bool value) => LoadMoreCommand.NotifyCanExecuteChanged();
    private void ApplyFilter()
    {
        IEnumerable<AppEntry> query = string.IsNullOrWhiteSpace(SearchText) ? _catalog.OrderByDescending(app => app.Score).ThenBy(app => app.Name) : _store.GetAll().Count > 0 ? _store.Search(SearchText) : _catalog.Where(app => app.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || app.Id.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || app.Publisher.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        query = query.Where(app => SelectedSource == "Todas as origens" || app.Source == (SelectedSource == "WinGet" ? "winget" : "msstore"));
        var matches = query.Take(_limit + 1).ToArray();
        HasMore = matches.Length > _limit;
        // Keep command state when a catalog refresh arrives during an installation.
        var previous = Apps.ToDictionary(item => $"{item.App.Source}:{item.Id}", StringComparer.OrdinalIgnoreCase);
        Apps.Clear();
        foreach (var app in matches.Take(_limit))
        {
            if (!previous.TryGetValue($"{app.Source}:{app.Id}", out var item))
                item = new PackageItemViewModel(app, InstallAsync, DetailsAsync, AddToCollection);
            Apps.Add(item);
        }
        RefreshInstalled();
        StatusText = $"{Apps.Count} aplicativos exibidos • {_catalog.Count:N0} no catálogo";
    }
    private void AddToCollection(PackageItemViewModel item)
    {
        _collections.Add(item.App);
        StatusText = $"{item.Name} adicionado à coleção {_collections.SelectedProfile?.Title}.";
    }
    private async Task InstallAsync(PackageItemViewModel item)
    {
        item.IsWorking = true; HasError = false;
        try
        {
            var result = await _backend.InstallAsync(item.App);
            item.IsInstalled = result.Success;
            StatusText = result.Success ? $"{item.Name} instalado." : WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", item.Name);
        }
        catch (Exception exception) { ReportError(exception); }
        finally { item.IsWorking = false; }
    }
    private async Task DetailsAsync(PackageItemViewModel item)
    {
        try { await _presentation.ShowDetailsAsync(item.App); }
        catch (Exception exception) { ReportError(exception); }
    }
    public void Dispose() { _store.CatalogUpdated -= CatalogUpdated; _installed.Changed -= InstalledChanged; }
}
