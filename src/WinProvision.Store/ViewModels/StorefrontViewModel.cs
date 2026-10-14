using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StorefrontViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IPackageBackend _backend;
    private bool _preview;
    private readonly WinProvision.Core.Services.Indexing.MsStoreCatalogService _msStoreCatalogService;
    private readonly StoreService _storeService;
    private readonly WinGetService _winGetService;
    private readonly IconService _iconService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OperationsQueueService _queueService;
    private readonly InstalledAppsService _installedAppsService;
    private readonly FeaturedCatalogService _featuredCatalogService;
    private readonly TaskCompletionSource<bool> _initialContentReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _navigationCancellation;
    private Task _navigationLoad = Task.CompletedTask;
    private bool _isNavigationActive;
    private bool _isDisposed;
    private string? _searchQueryInFlight;
    private string? _liveSearchQuery;
    private IReadOnlyList<AppEntry> _liveSearchResults = [];
    private IReadOnlyList<AppEntry> _allApps = [];
    private bool _catalogLoaded;
    private bool _catalogLoading;
    private bool _isRefreshing;
    private bool _storeBannersLoading;
    private bool _storeBannersLoaded;
    private bool _featuredRefreshPending;
    private FeaturedCatalogDocument? _featuredCatalog;
    private DateOnly? _storeBannerWeek;
    private DateOnly? _storeBannerSearchWeek;
    private List<AppEntry> _searchedStoreBannerApps = [];
    private int _heroBannerIndex;
    private string _selectedCategoryTag = "all";
    private CatalogSortMode _catalogSortMode;
    private ShelfView _shelfView;

    public enum ShelfView { Overview, Popular, Store }
    public enum CatalogSortMode { Popularity, Name, Id, MicrosoftStore, WinGet }

    public Task InitialContentReady => _initialContentReady.Task;

    private static readonly Dictionary<string, string[]> CategoryTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productivity"] = ["productivity", "produtividade", "office", "business", "negócios"],
        ["development"] = ["development", "developer", "programming", "desenvolvimento", "programação"],
        ["utilities"] = ["utilities", "tools", "system", "utilitários", "ferramentas", "sistema"],
        ["multimedia"] = ["multimedia", "photo", "video", "music", "multimídia", "foto", "vídeo", "música"],
        ["security"] = ["security", "antivirus", "privacy", "segurança", "antivírus", "privacidade"],
    };

    // Apenas marcas conhecidas entram nos banners; a lista não limita o catálogo.
    private static readonly string[] FamiliarStoreAppNames =
    [
        "WhatsApp", "Spotify", "Disney+", "Netflix", "TikTok", "CapCut", "Instagram",
        "Facebook", "Messenger", "Telegram", "Prime Video", "Amazon Prime Video", "Canva",
        "Zoom", "VLC", "Microsoft 365", "OneDrive", "Xbox", "PowerToys", "Lively Wallpaper",
        "TranslucentTB", "Dolby Access", "Microsoft To Do", "Phone Link", "Clipchamp",
        "Microsoft Photos", "Windows Terminal", "Paint", "Word", "Excel", "PowerPoint", "Outlook",
        "Adobe", "Pinterest", "Discord", "Brave", "ChatGPT"
    ];

    private static readonly string[] FeaturedStoreSearchTerms =
    [
        "Disney+", "Spotify", "WhatsApp", "Adobe", "TikTok", "Netflix", "Instagram",
        "Canva", "Pinterest", "Discord", "Brave", "ChatGPT", "Telegram"
    ];

    private const double StoreFeaturedMinimumRating = 4.0;
    private const int StoreFeaturedMinimumRatingCount = 3000;
    private const int StoreFeaturedCandidatePoolSize = 30;

    public ObservableCollection<AppEntry> Apps { get; } = new();

    public ObservableCollection<AppEntry> StoreBannerApps { get; } = new();
    public ObservableCollection<AppEntry> SideStoreBannerApps { get; } = new();
    public ObservableCollection<AppEntry> PopularApps { get; } = new();
    public ObservableCollection<AppEntry> StoreHighlights { get; } = new();
    public ObservableCollection<FeaturedCategoryShelf> FeaturedCategories { get; } = new();

    [WinRT.GeneratedBindableCustomProperty]
public sealed partial class FeaturedCategoryShelf
    {
        public string Title { get; init; } = string.Empty;
        public ObservableCollection<AppEntry> Apps { get; } = new();
    }

    public StorefrontViewModel(WingetExecutor wingetExecutor,
        OperationsQueueService queueService, InstalledAppsService installedAppsService,
        FeaturedCatalogService featuredCatalogService,
        WinProvision.Core.Services.Indexing.MsStoreCatalogService msStoreCatalogService,
        IconService iconService, StoreService storeService, WinGetService winGetService, IUiDispatcher dispatcher, IPackageBackend backend)
    {
        _msStoreCatalogService = msStoreCatalogService;
        _storeService = storeService;
        _winGetService = winGetService;
        _iconService = iconService;
        _wingetExecutor = wingetExecutor;
        _queueService = queueService;
        _installedAppsService = installedAppsService;
        _featuredCatalogService = featuredCatalogService;

        _dispatcher = dispatcher;
        _backend = backend;
        _storeService.CatalogUpdated += OnCatalogUpdated;
        _featuredCatalogService.Updated += OnFeaturedCatalogUpdated;
        _storeService.CacheCleared += OnCacheCleared;
        _installedAppsService.Changed += OnInstalledAppsChanged;
    }
    public int HeroBannerIndex => _heroBannerIndex;
    public ShelfView CurrentShelf => _shelfView;
    public void SelectCategory(string category) { _selectedCategoryTag = category; ApplyFilter(); }
    public void SelectSort(int index) { _catalogSortMode = (CatalogSortMode)index; ApplyFilter(); }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (EqualityComparer<string>.Default.Equals(_searchText, value)) return; _searchText = value; OnPropertyChanged(); }
    }
    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        set { if (EqualityComparer<string>.Default.Equals(_statusText, value)) return; _statusText = value; OnPropertyChanged(); }
    }
    private string _catalogSyncText = string.Empty;
    public string CatalogSyncText
    {
        get => _catalogSyncText;
        set { if (EqualityComparer<string>.Default.Equals(_catalogSyncText, value)) return; _catalogSyncText = value; OnPropertyChanged(); }
    }
    private string _featuredRankingInfo = string.Empty;
    public string FeaturedRankingInfo
    {
        get => _featuredRankingInfo;
        set { if (EqualityComparer<string>.Default.Equals(_featuredRankingInfo, value)) return; _featuredRankingInfo = value; OnPropertyChanged(); }
    }
    private string _resultsTitle = string.Empty;
    public string ResultsTitle
    {
        get => _resultsTitle;
        set { if (EqualityComparer<string>.Default.Equals(_resultsTitle, value)) return; _resultsTitle = value; OnPropertyChanged(); }
    }
    private bool _isBrowsing = true;
    public bool IsBrowsing
    {
        get => _isBrowsing;
        set { if (EqualityComparer<bool>.Default.Equals(_isBrowsing, value)) return; _isBrowsing = value; OnPropertyChanged(); }
    }
    private bool _showHero = true;
    public bool ShowHero
    {
        get => _showHero;
        set { if (EqualityComparer<bool>.Default.Equals(_showHero, value)) return; _showHero = value; OnPropertyChanged(); }
    }
    private bool _showResults = false;
    public bool ShowResults
    {
        get => _showResults;
        set { if (EqualityComparer<bool>.Default.Equals(_showResults, value)) return; _showResults = value; OnPropertyChanged(); }
    }
    private bool _hasEmptyResults = false;
    public bool HasEmptyResults
    {
        get => _hasEmptyResults;
        set { if (EqualityComparer<bool>.Default.Equals(_hasEmptyResults, value)) return; _hasEmptyResults = value; OnPropertyChanged(); }
    }
    private bool _canRefresh = true;
    public bool CanRefresh
    {
        get => _canRefresh;
        set { if (EqualityComparer<bool>.Default.Equals(_canRefresh, value)) return; _canRefresh = value; OnPropertyChanged(); }
    }
    private AppEntry? _selectedBanner = null;
    public AppEntry? SelectedBanner
    {
        get => _selectedBanner;
        set { if (EqualityComparer<AppEntry?>.Default.Equals(_selectedBanner, value)) return; _selectedBanner = value; OnPropertyChanged(); }
    }
    private AppEntry? _rightPromoApp = null;
    public AppEntry? RightPromoApp
    {
        get => _rightPromoApp;
        set { if (EqualityComparer<AppEntry?>.Default.Equals(_rightPromoApp, value)) return; _rightPromoApp = value; OnPropertyChanged(); }
    }
    private void OnFeaturedCatalogUpdated(FeaturedCatalogDocument document)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => OnFeaturedCatalogUpdated(document));
            return;
        }

        _featuredCatalog = document;
        _storeBannersLoaded = false;
        if (!_isNavigationActive || _isDisposed) return;
        if (_storeBannersLoading)
            _featuredRefreshPending = true;
        else
            _ = LoadStoreBannersAsync(force: true);
        UpdatePopularApps();
    }

    private void OnInstalledAppsChanged()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(OnInstalledAppsChanged);
            return;
        }

        if (!_isNavigationActive || _isDisposed) return;
        SyncInstalledFlags(_allApps);
        ApplyFilter();
    }

    private void OnCatalogUpdated(IReadOnlyList<AppEntry> catalog)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => OnCatalogUpdated(catalog));
            return;
        }

        _allApps = catalog;
        UpdateCatalogSyncText();
        if (!_isNavigationActive || _isDisposed) return;
        SyncInstalledFlags(_allApps);
        _ = LoadStoreBannersAsync(force: true);
        UpdatePopularApps();
        ApplyFilter();
    }

    private void OnCacheCleared()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(OnCacheCleared);
            return;
        }

        _catalogLoaded = false;
        CatalogSyncText = "Catálogo ainda não sincronizado nesta sessão.";
        _allApps = [];
        _storeBannersLoaded = false;
        _storeBannerWeek = null;
        _storeBannerSearchWeek = null;
        _searchedStoreBannerApps = [];
        StoreBannerApps.Clear();
        SideStoreBannerApps.Clear();
        RightPromoApp = null;
        SelectedBanner = null;
        Apps.Clear();
        PopularApps.Clear();
        StoreHighlights.Clear();
        FeaturedCategories.Clear();
        StatusText = "Cache limpo. O catálogo será atualizado ao abrir esta tela.";
    }

    private void SyncInstalledFlags(IEnumerable<AppEntry> apps)
    {
        foreach (AppEntry app in apps)
        {
            app.IsInstalled = _installedAppsService.IsInstalled(app.Name, app.Id);
        }
    }

    public Task OnNavigatedToAsync()
    {
        if (_isDisposed || _isNavigationActive) return Task.CompletedTask;
        _isNavigationActive = true;
        var previousCancellation = _navigationCancellation;
        var operation = new CancellationTokenSource();
        _navigationCancellation = operation;
        _navigationLoad = LoadForNavigationAsync(_navigationLoad, previousCancellation, operation);
        return _navigationLoad;
    }

    private async Task LoadForNavigationAsync(Task previous, CancellationTokenSource? previousCancellation, CancellationTokenSource operation)
    {
        try
        {
            await previous;
            previousCancellation?.Dispose();
            operation.Token.ThrowIfCancellationRequested();
            await LoadAsync(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "HomeNavigation"); }
        finally
        {
            if (_isDisposed && ReferenceEquals(_navigationCancellation, operation))
            {
                _navigationCancellation = null;
                operation.Dispose();
            }
        }
    }

    public async Task OnNavigatedFromAsync()
    {
        _isNavigationActive = false;
        var operation = _navigationCancellation;
        operation?.Cancel();
        _searchCancellation?.Cancel();
        await _navigationLoad;
        if (ReferenceEquals(_navigationCancellation, operation))
        {
            _navigationCancellation = null;
            operation?.Dispose();
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_catalogLoaded)
        {
            ApplyFilter();
            _initialContentReady.TrySetResult(true);
            await _installedAppsService.EnsureLoadedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SyncInstalledFlags(_allApps);
            UpdatePopularApps();
            ApplyFilter();
            await LoadStoreBannersAsync(cancellationToken: cancellationToken);
            return;
        }

        if (_catalogLoading)
        {
            return;
        }

        _catalogLoading = true;
        StatusText = "Carregando catálogo...";

        try
        {
            _allApps = await _storeService.LoadCatalogAsync(cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCatalogSyncText();
            SyncInstalledFlags(_allApps);
            UpdatePopularApps();
            _catalogLoaded = true;
            ApplyFilter();
            _initialContentReady.TrySetResult(true);

            // Inventário WinGet e banners de rede atualizam a vitrine já aberta.
            await _installedAppsService.EnsureLoadedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SyncInstalledFlags(_allApps);
            UpdatePopularApps();
            ApplyFilter();
            await LoadStoreBannersAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "StorefrontViewModel.LoadAsync");

            StatusText = "Não foi possível carregar o catálogo. Tente novamente.";
        }
        finally
        {
            _catalogLoading = false;
            _initialContentReady.TrySetResult(true);
        }
    }

    private static DateOnly GetCurrentBannerWeek()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        return today.AddDays(-daysSinceMonday);
    }

    private static string GetWeeklyBannerOrderKey(AppEntry app, DateOnly week)
    {
        string key = $"{week:yyyy-MM-dd}:{app.Source}:{app.Id}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    private async Task LoadStoreBannersAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!_isNavigationActive || _isDisposed) return;
        if (_navigationCancellation is { } navigation)
            cancellationToken = navigation.Token;
        if (cancellationToken.IsCancellationRequested) return;
        DateOnly week = GetCurrentBannerWeek();
        if (_storeBannersLoading || (!force && _storeBannersLoaded && _storeBannerWeek == week))
        {
            return;
        }

        _storeBannersLoading = true;
        try
        {
            if (_featuredCatalog is null)
                _featuredCatalog = await _featuredCatalogService.LoadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (_featuredCatalog is not null)
            {
                List<AppEntry> curated = ResolveFeaturedApps(_featuredCatalog.Hero)
                    .Where(app => !string.IsNullOrWhiteSpace(app.StoreBannerUrl))
                    .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();
                if (curated.Count > 0)
                {
                    ApplyFeaturedBanners(curated);
                    _storeBannerWeek = week;
                    _storeBannersLoaded = true;
                    UpdatePopularApps();
                    return;
                }
            }

            List<AppEntry> availableStoreApps = _allApps
                .Where(IsEligibleFeaturedStoreApp)
                .ToList();

            // A busca ampla do catálogo pode não conter todos os produtos editoriais.
            // Se ainda faltarem banners, consulta diretamente os títulos conhecidos.
            if (availableStoreApps.Select(GetProductIdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 6)
            {
                if (_storeBannerSearchWeek != week)
                {
                    var discovered = new System.Collections.Concurrent.ConcurrentBag<AppEntry>();
                    await Parallel.ForEachAsync(
                        FeaturedStoreSearchTerms,
                        new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
                        async (term, cancellationToken) =>
                        {
                            foreach (AppEntry app in await _msStoreCatalogService.SearchAsync(term, cancellationToken))
                            {
                                if (IsEligibleFeaturedStoreApp(app))
                                {
                                    app.IconUrl = _iconService.ResolveIconUrl(app);
                                    app.IsInstalled = _installedAppsService.IsInstalled(app.Name, app.Id);
                                    discovered.Add(app);
                                }
                            }
                        });
                    cancellationToken.ThrowIfCancellationRequested();
                    _searchedStoreBannerApps = discovered.ToList();
                    _storeBannerSearchWeek = week;
                }

                availableStoreApps.AddRange(_searchedStoreBannerApps);
            }

            // A Store não fornece downloads no catálogo; o volume de avaliações serve
            // como sinal de popularidade. Primeiro limitamos aos produtos com nota alta,
            // volume mínimo de avaliações e banner. A rotação semanal escolhe entre os
            // 30 mais populares, sem completar a vitrine com apps fora dos critérios.
            List<AppEntry> candidatePool = availableStoreApps
                .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(app => app.StoreRatingCount ?? 0)
                .ThenByDescending(app => app.StoreRating ?? 0)
                .ThenByDescending(app => app.Score)
                .Take(StoreFeaturedCandidatePoolSize)
                .ToList();

            List<AppEntry> featured = candidatePool
                .OrderBy(app => GetWeeklyBannerOrderKey(app, week), StringComparer.Ordinal)
                .Take(6)
                .ToList();

            cancellationToken.ThrowIfCancellationRequested();
            ApplyFeaturedBanners(featured);
            _storeBannerWeek = week;
            _storeBannersLoaded = true;
            UpdatePopularApps();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Falha ao carregar banners do catálogo: {ex.Message}");
        }
        finally
        {
            _storeBannersLoading = false;
            if (_featuredRefreshPending || (cancellationToken.IsCancellationRequested && _isNavigationActive))
            {
                _featuredRefreshPending = false;
                _ = LoadStoreBannersAsync(force: true);
            }
        }
    }

    private void ApplyFeaturedBanners(IReadOnlyList<AppEntry> featured)
    {
        SyncInstalledFlags(featured);
        StoreBannerApps.Clear();
        SideStoreBannerApps.Clear();
        foreach (AppEntry app in featured.Take(3))
            StoreBannerApps.Add(app);

        RightPromoApp = featured.Skip(3).FirstOrDefault();
        foreach (AppEntry app in featured.Skip(4).Take(2))
            SideStoreBannerApps.Add(app);

        if (StoreBannerApps.Count > 0) SetHeroBanner(0);
        else SelectedBanner = null;
    }

    private List<AppEntry> ResolveFeaturedApps(IEnumerable<FeaturedAppReference> references)
    {
        var resolved = new List<AppEntry>();
        foreach (FeaturedAppReference reference in references)
        {
            AppEntry? match = _allApps.FirstOrDefault(app =>
                app.Id.Equals(reference.Id, StringComparison.OrdinalIgnoreCase)
                && app.Source.Equals(reference.Source, StringComparison.OrdinalIgnoreCase));
            match ??= _allApps.FirstOrDefault(app =>
                NormalizeSearchText(app.Name) == NormalizeSearchText(reference.Name)
                && NormalizeSearchText(app.Publisher) == NormalizeSearchText(reference.Publisher));
            if (match is not null)
            {
                match.FeaturedInstallCount30d = reference.InstallCount30d > 0 ? reference.InstallCount30d : null;
                resolved.Add(match);
            }
        }
        SyncInstalledFlags(resolved);
        return resolved;
    }

    private static bool IsEligibleFeaturedStoreApp(AppEntry app) =>
        app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(app.StoreBannerUrl)
        && IsFamiliarStoreApp(app)
        && app.StoreRating is > StoreFeaturedMinimumRating and <= 5
        && app.StoreRatingCount >= StoreFeaturedMinimumRatingCount;

    public void SetHeroBanner(int index)
    {
        if (StoreBannerApps.Count == 0) return;
        _heroBannerIndex = (index % StoreBannerApps.Count + StoreBannerApps.Count) % StoreBannerApps.Count;
        SelectedBanner = StoreBannerApps[_heroBannerIndex];
        OnPropertyChanged(nameof(HeroBannerIndex));
    }

    private static bool HasRealIcon(AppEntry app) =>
        !string.IsNullOrWhiteSpace(app.IconUrl) &&
        !app.IconUrl.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesLocalRegion(AppEntry app) =>
        app.RegionTags.Count == 0 || app.RegionTags.Contains("BR", StringComparer.OrdinalIgnoreCase);

    private static bool HasKnownPtBrSupport(AppEntry app) =>
        string.Equals(app.PackageLocale, "pt-BR", StringComparison.OrdinalIgnoreCase);

    private static bool IsFamiliarStoreApp(AppEntry app) =>
        FamiliarStoreAppNames.Any(knownName =>
            app.Name.Equals(knownName, StringComparison.OrdinalIgnoreCase)
            || app.Name.StartsWith(knownName + " - ", StringComparison.OrdinalIgnoreCase)
            || app.Name.StartsWith(knownName + " ", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeSearchText(string? value) => string.Concat((value ?? string.Empty)
        .Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant));

    private static string? GetFamiliarProductKey(string? name)
    {
        foreach (string knownName in FamiliarStoreAppNames)
        {
            if (string.Equals(name, knownName, StringComparison.OrdinalIgnoreCase)
                || name?.StartsWith(knownName + " - ", StringComparison.OrdinalIgnoreCase) == true
                || name?.StartsWith(knownName + ": ", StringComparison.OrdinalIgnoreCase) == true
                || name?.StartsWith(knownName + " — ", StringComparison.OrdinalIgnoreCase) == true)
            {
                return $"known\0{NormalizeSearchText(knownName)}";
            }
        }

        return null;
    }

    private static string GetProductIdentityKey(AppEntry app)
    {
        // Títulos editoriais da Store, como "Spotify - Música e podcasts", não
        // precisam repetir o mesmo produto já disponível pelo WinGet. Para marcas
        // conhecidas, a identidade usa o nome canônico; nos demais casos, mantém a
        // combinação nome + publicador, que evita juntar aplicativos diferentes.
        if (GetFamiliarProductKey(app.Name) is { } familiarKey)
            return familiarKey;

        string name = NormalizeSearchText(app.Name);
        string publisher = NormalizeSearchText(app.Publisher);
        return name.Length == 0 || publisher.Length == 0
            ? $"{app.Source}\0{app.Id}"
            : $"{name}\0{publisher}";
    }

    private static AppEntry MergeProductSources(IEnumerable<AppEntry> entries)
    {
        List<AppEntry> group = entries.ToList();
        AppEntry primary = group.FirstOrDefault(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                           ?? group[0];
        AppEntry? store = group.FirstOrDefault(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase));
        if (store is null || ReferenceEquals(primary, store))
            return primary;

        // Mantém o ID e a origem de instalação WinGet, mas usa as mídias oficiais da Store.
        if (!string.IsNullOrWhiteSpace(store.StoreBannerUrl))
            primary.StoreBannerUrl = store.StoreBannerUrl;
        if (store.StoreScreenshotUrls is { Count: > 0 })
            primary.StoreScreenshotUrls = store.StoreScreenshotUrls;
        if (!string.IsNullOrWhiteSpace(store.StoreIconUrl))
        {
            primary.StoreIconUrl = store.StoreIconUrl;
            primary.IconUrl = store.StoreIconUrl;
        }
        else if (!string.IsNullOrWhiteSpace(store.IconUrl))
        {
            // Resultados ao vivo podem já vir com o ícone da Store resolvido em
            // IconUrl. Ele continua sendo preferível ao ícone genérico do manifesto.
            primary.IconUrl = store.IconUrl;
        }
        primary.StoreRating ??= store.StoreRating;
        primary.StoreRatingCount ??= store.StoreRatingCount;
        primary.StoreCategory ??= store.StoreCategory;
        primary.StoreSubCategory ??= store.StoreSubCategory;
        primary.Description = string.IsNullOrWhiteSpace(store.Description) ? primary.Description : store.Description;
        primary.PackageLocale ??= store.PackageLocale;
        return primary;
    }

    private static IEnumerable<AppEntry> MergeProductEntries(IEnumerable<AppEntry> entries) =>
        entries.GroupBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(MergeProductSources);

    private static IEnumerable<AppEntry> InterleaveCatalogSources(IEnumerable<AppEntry> entries)
    {
        List<AppEntry> wingetApps = entries
            .Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(app => app.Score)
            .ThenByDescending(app => app.GitHubStars ?? 0)
            .ToList();
        List<AppEntry> storeApps = entries
            .Where(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(GetStoreRatingScore)
            .ThenByDescending(app => app.StoreRatingCount ?? 0)
            .ToList();

        for (int index = 0; index < Math.Max(wingetApps.Count, storeApps.Count); index++)
        {
            if (index < wingetApps.Count)
                yield return wingetApps[index];
            if (index < storeApps.Count)
                yield return storeApps[index];
        }
    }

    private static bool MatchesCategory(AppEntry app, string[] keywords)
    {
        IEnumerable<string> metadata = app.Tags
            .Concat((string?[])[app.StoreCategory, app.StoreSubCategory])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);

        return metadata.Any(value => keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
    }

    private const int StorePopularMinimumRatingCount = 50;
    private const int StorePopularFallbackRatingCount = 5;
    private const double StorePopularMinimumRating = 4.0;

    private void UpdatePopularApps()
    {
        FeaturedRankingInfo = _featuredCatalog?.InstallMetricsAvailable == true
            ? "O ranking combina instalações concluídas nos últimos 30 dias com avaliações e qualidade dos dados do catálogo."
            : "O ranking inicial usa sinais do catálogo. As instalações concluídas entram após a publicação do serviço de métricas.";

        FeaturedCategories.Clear();
        if (_featuredCatalog is not null)
        {
            foreach (FeaturedCategory category in _featuredCatalog.Categories)
            {
                var shelf = new FeaturedCategoryShelf { Title = category.Title };
                foreach (AppEntry app in ResolveFeaturedApps(category.Apps).Take(4))
                    shelf.Apps.Add(app);
                if (shelf.Apps.Count > 0) FeaturedCategories.Add(shelf);
            }
        }

        int limit = _shelfView == ShelfView.Popular ? 48 : 6;
        List<AppEntry> selectedStoreApps = SelectPopularStoreApps(_allApps.Concat(StoreBannerApps),
            _shelfView == ShelfView.Store ? 48 : 6);
        HashSet<string> storeProductKeys = selectedStoreApps
            .Select(GetProductIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        PopularApps.Clear();
        IEnumerable<AppEntry> ranked = _featuredCatalog is { Popular.Count: > 0 }
            ? ResolveFeaturedApps(_featuredCatalog.Popular)
            : _allApps
                     .Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                   && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                   && app.Score >= 25 && MatchesLocalRegion(app)
                                   && !storeProductKeys.Contains(GetProductIdentityKey(app)))
                     .OrderByDescending(app => app.Score)
                     .ThenByDescending(app => app.GitHubStars ?? 0)
                     .Take(limit);
        foreach (AppEntry app in ranked.Take(limit))
            PopularApps.Add(app);

        limit = _shelfView == ShelfView.Store ? 48 : 6;
        StoreHighlights.Clear();
        foreach (AppEntry app in selectedStoreApps.Take(limit))
            StoreHighlights.Add(app);
    }

    private static List<AppEntry> SelectPopularStoreApps(IEnumerable<AppEntry> apps, int limit)
    {
        if (limit <= 0)
            return [];

        List<AppEntry> ratedApps = apps
            .Where(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                          && HasRealIcon(app) && HasKnownPtBrSupport(app) && MatchesLocalRegion(app)
                          && app.StoreRating is >= StorePopularMinimumRating and <= 5
                          && app.StoreRatingCount is > 0)
            .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(IsFamiliarStoreApp)
            .ThenByDescending(GetStoreRatingScore)
            .ThenByDescending(app => app.StoreRatingCount ?? 0)
            .ThenByDescending(app => app.Score)
            .ThenBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Prioriza produtos bem avaliados com volume consistente de opiniões. Se
        // necessário, completa com os que têm menos avaliações, sem dados ausentes.
        List<AppEntry> popularApps = ratedApps
            .Where(app => app.StoreRatingCount >= StorePopularMinimumRatingCount)
            .Take(limit)
            .ToList();

        if (popularApps.Count < limit)
        {
            var selectedIds = popularApps.Select(app => app.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            popularApps.AddRange(ratedApps
                .Where(app => app.StoreRatingCount >= StorePopularFallbackRatingCount
                              && !selectedIds.Contains(app.Id))
                .Take(limit - popularApps.Count));
        }

        return popularApps;
    }

    private static double GetStoreRatingScore(AppEntry app) =>
        app.StoreRating is > 0 and <= 5 && app.StoreRatingCount is > 0
            ? ((app.StoreRating.Value * app.StoreRatingCount.Value) + 4000.0)
                / (app.StoreRatingCount.Value + 1000.0)
            : 0;

    public void SetShelfView(ShelfView view)
    {
        _shelfView = view;
        OnPropertyChanged(nameof(CurrentShelf));
        UpdatePopularApps();
        ApplyFilter();
    }

    internal void LoadPreview(IReadOnlyList<AppEntry> apps)
    {
        _preview = true; _catalogLoaded = true; _allApps = apps;
        StoreBannerApps.Clear(); PopularApps.Clear(); StoreHighlights.Clear(); SideStoreBannerApps.Clear();
        foreach (var app in apps.Take(3)) StoreBannerApps.Add(app);
        foreach (var app in apps.Where(app => app.Source == "winget")) PopularApps.Add(app);
        foreach (var app in apps.Where(app => app.Source == "msstore")) StoreHighlights.Add(app);
        SelectedBanner = apps.FirstOrDefault(); RightPromoApp = apps.LastOrDefault();
        foreach (var app in apps.Skip(1).Take(2)) SideStoreBannerApps.Add(app);
        CatalogSyncText = "Prévia com dados de teste";
        FeaturedRankingInfo = "";
        ApplyFilter();
        _initialContentReady.TrySetResult(true);
    }
    public void ApplyFilter()
    {
        if (!_catalogLoaded)
            return;

        string query = SearchText.Trim() ?? string.Empty;
        if (_preview) { _liveSearchQuery = query; _liveSearchResults = _allApps; }
        bool browsing = query.Length == 0 && _selectedCategoryTag == "all";
        IsBrowsing = browsing;
        ShowHero = browsing && _shelfView == ShelfView.Overview;
        ShowResults = !browsing;
        ResultsTitle = query.Length > 0 ? "Resultados da pesquisa" : "Aplicativos da categoria";

        if (string.IsNullOrWhiteSpace(query))
        {
            _searchCancellation?.Cancel();
            _searchCancellation = null;
            _searchQueryInFlight = null;
            _liveSearchQuery = null;
            _liveSearchResults = [];
        }

        if (browsing)
        {
            Apps.Clear();
            return;
        }

        bool categoryBrowsing = string.IsNullOrWhiteSpace(query) && _selectedCategoryTag != "all";
        IEnumerable<AppEntry> source = categoryBrowsing
            ? InterleaveCatalogSources(MergeProductEntries(_allApps.Concat(StoreBannerApps)
                .Where(app =>
                    MatchesSelectedCatalogSource(app)
                    && ((app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                         && HasRealIcon(app) && HasKnownPtBrSupport(app)
                         && app.Score >= 25 && MatchesLocalRegion(app))
                        || (app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                            && HasRealIcon(app) && HasKnownPtBrSupport(app)
                            && MatchesLocalRegion(app))))
                .Where(app => CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords)
                              && MatchesCategory(app, keywords))))
            : string.IsNullOrWhiteSpace(query)
            ? _shelfView == ShelfView.Store
                ? SelectPopularStoreApps(_allApps.Concat(StoreBannerApps), 48)
                : _allApps.Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                             && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                             && app.Score >= 25 && MatchesLocalRegion(app))
                    .OrderByDescending(app => app.Score)
                    .ThenByDescending(app => app.GitHubStars ?? 0)
            : GetSearchResults(query);

        if (_selectedCategoryTag != "all" && CategoryTagMap.TryGetValue(_selectedCategoryTag, out string[]? keywords))
            source = source.Where(app => MatchesCategory(app, keywords));

        source = _catalogSortMode switch
        {
            CatalogSortMode.Name => source.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase),
            CatalogSortMode.Id => source.OrderBy(app => app.Id, StringComparer.OrdinalIgnoreCase),
            _ => source,
        };

        List<AppEntry> results = source.Take(48).ToList();
        Apps.Clear();
        foreach (AppEntry app in results)
            Apps.Add(app);

        bool searchCompleted = string.IsNullOrWhiteSpace(query)
            || string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase);
        HasEmptyResults = results.Count == 0 && searchCompleted;

        StatusText = string.IsNullOrWhiteSpace(query)
            ? $"{results.Count} aplicativos nesta categoria."
            : results.Count == 0
                ? _liveSearchQuery == query ? $"Nenhum resultado para \"{query}\"" : $"Pesquisando fontes de pacotes por \"{query}\"..."
                : results.Count == 1 ? "1 resultado." : $"Resultados: {results.Count}";

        if (!string.IsNullOrWhiteSpace(query) &&
            !string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_searchQueryInFlight, query, StringComparison.OrdinalIgnoreCase))
            _ = SearchLiveCatalogsAsync(query);
    }

    private IEnumerable<AppEntry> GetSearchResults(string query)
    {
        // Une o catálogo local e as duas fontes consultadas ao vivo antes de limitar a
        // quantidade. A API da Store pode retornar correspondências aproximadas; sem
        // um ranking comum, elas podiam ocupar as primeiras posições e esconder um
        // resultado exato como WhatsApp quando pesquisado pelo nome.
        List<AppEntry> localResults = _storeService.Search(query, maxResults: 256).ToList();
        IEnumerable<AppEntry> liveResults = string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase)
            ? _liveSearchResults
            : [];

        return localResults.Concat(liveResults)
            .Where(MatchesSelectedCatalogSource)
            .GroupBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                List<AppEntry> sourceEntries = group.ToList();
                return (App: MergeProductSources(sourceEntries),
                    Relevance: sourceEntries.Min(app => GetSearchRelevance(app, query)));
            })
            .Where(result => result.Relevance < int.MaxValue)
            .OrderBy(result => result.Relevance)
            .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(result => result.App);
    }

    private bool MatchesSelectedCatalogSource(AppEntry app) => _catalogSortMode switch
    {
        CatalogSortMode.MicrosoftStore => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase),
        CatalogSortMode.WinGet => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase),
        _ => true,
    };

    private static int GetSearchRelevance(AppEntry app, string query)
    {
        string name = app.Name ?? string.Empty;
        string id = app.Id ?? string.Empty;
        string term = query.Trim();
        if (term.Length == 0)
            return int.MaxValue;

        string normalizedTerm = NormalizeSearchText(term);
        string normalizedName = NormalizeSearchText(name);
        string normalizedId = NormalizeSearchText(id);
        if (normalizedTerm.Length == 0)
            return int.MaxValue;

        if (normalizedName.Equals(normalizedTerm, StringComparison.Ordinal)) return 0;
        if (normalizedId.Equals(normalizedTerm, StringComparison.Ordinal)) return 1;
        if (normalizedName.StartsWith(normalizedTerm, StringComparison.Ordinal)) return 2;
        if (name.Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(token => NormalizeSearchText(token).StartsWith(normalizedTerm, StringComparison.Ordinal))) return 3;
        if (normalizedName.Contains(normalizedTerm, StringComparison.Ordinal)) return 4;
        if (normalizedId.StartsWith(normalizedTerm, StringComparison.Ordinal)) return 5;
        if (normalizedId.Contains(normalizedTerm, StringComparison.Ordinal)) return 6;
        if ((app.Tags ?? []).Any(tag => NormalizeSearchText(tag).Contains(normalizedTerm, StringComparison.Ordinal))) return 7;
        if (NormalizeSearchText(app.Publisher).Contains(normalizedTerm, StringComparison.Ordinal)) return 8;
        if (NormalizeSearchText(app.Description).Contains(normalizedTerm, StringComparison.Ordinal)) return 9;

        return int.MaxValue;
    }

    private async Task SearchLiveCatalogsAsync(string query)
    {
        _searchCancellation?.Cancel();
        _liveSearchResults = [];
        if (!_isNavigationActive || _isDisposed) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_navigationCancellation?.Token ?? CancellationToken.None);
        _searchCancellation = cancellation;
        _searchQueryInFlight = query;
        CancellationToken cancellationToken = cancellation.Token;

        try
        {
            var winGetSearchTask = _winGetService.SearchAsync(query, cancellationToken);
            var storeSearchTask = _msStoreCatalogService.SearchAsync(query, cancellationToken);
            try
            {
                await Task.WhenAll(winGetSearchTask, storeSearchTask);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HomePage] Uma das fontes de busca falhou: {ex.Message}");
            }

            var matches = winGetSearchTask.IsCompletedSuccessfully
                ? await winGetSearchTask
                : [];
            var directStoreEntries = storeSearchTask.IsCompletedSuccessfully
                ? await storeSearchTask
                : [];
            var storeIds = matches
                .Where(match => string.Equals(match.Source, "msstore", StringComparison.OrdinalIgnoreCase))
                .Select(match => match.PackageId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            IReadOnlyList<AppEntry> resolvedStoreEntries = [];
            if (storeIds.Length > 0)
            {
                try
                {
                    resolvedStoreEntries = await _msStoreCatalogService.FetchAsync(storeIds, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A busca direta da Store já trouxe resultados úteis. Falhar ao
                    // enriquecer IDs retornados pelo WinGet não deve descartá-los.
                    System.Diagnostics.Debug.WriteLine($"[HomePage] Falha ao enriquecer resultados da Store: {ex.Message}");
                }
            }
            var storeEntries = directStoreEntries.Concat(resolvedStoreEntries).ToArray();
            var storeById = storeEntries
                .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var liveResults = matches.Select(match =>
            {
                if (match.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) && storeById.TryGetValue(match.PackageId, out var storeEntry))
                {
                    storeEntry.IconUrl = _iconService.ResolveIconUrl(storeEntry);
                    storeEntry.IsInstalled = _installedAppsService.IsInstalled(storeEntry.Name, storeEntry.Id);
                    return storeEntry;
                }

                var app = new AppEntry
                {
                    Id = match.PackageId,
                    Name = match.Name,
                    Version = string.IsNullOrWhiteSpace(match.Version) ? "-" : match.Version,
                    Publisher = match.Publisher,
                    Source = match.Source,
                };
                app.IconUrl = _iconService.ResolveIconUrl(app);
                app.IsInstalled = _installedAppsService.IsInstalled(app.Name, app.Id);
                return app;
            }).Concat(directStoreEntries.Select(storeEntry =>
            {
                storeEntry.IconUrl = _iconService.ResolveIconUrl(storeEntry);
                storeEntry.IsInstalled = _installedAppsService.IsInstalled(storeEntry.Name, storeEntry.Id);
                return storeEntry;
            })).GroupBy(app => $"{app.Source}\0{app.Id}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(SearchText.Trim(), query, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _liveSearchQuery = query;
            _liveSearchResults = liveResults;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HomePage] Falha na busca ao vivo: {ex.Message}");
            if (string.Equals(SearchText.Trim(), query, StringComparison.OrdinalIgnoreCase))
            {
                _liveSearchResults = [];
                _liveSearchQuery = query;
                ApplyFilter();
                if (Apps.Count == 0)
                {
                    StatusText = "Não foi possível consultar as fontes do WinGet. Exibindo resultados do catálogo local.";
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                _searchCancellation = null;
                _searchQueryInFlight = null;
            }
            cancellation.Dispose();
        }
    }

    public async Task RefreshAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        CanRefresh = false;

        try
        {
            string query = SearchText.Trim() ?? string.Empty;
        if (_preview) { _liveSearchQuery = query; _liveSearchResults = _allApps; }
            if (query.Length > 0 && _catalogLoaded)
            {
                StatusText = "Atualizando resultados da pesquisa...";
                await SearchLiveCatalogsAsync(query);
                return;
            }

            StatusText = "Atualizando catálogo...";
            _allApps = await _storeService.LoadCatalogAsync(forceRefresh: true);
            UpdateCatalogSyncText();
            await _installedAppsService.RefreshAsync();
            SyncInstalledFlags(_allApps);
            await LoadStoreBannersAsync(force: true);
            UpdatePopularApps();
            _catalogLoaded = true;
            ApplyFilter();
        }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "StorefrontViewModel.RefreshAsync");

            StatusText = "Não foi possível atualizar o catálogo. Tente novamente.";
        }
        finally
        {
            _isRefreshing = false;
            CanRefresh = true;
        }
    }

    private void UpdateCatalogSyncText()
    {
        DateTime? syncUtc = _storeService.LastCatalogSyncUtc;
        CatalogSyncText = syncUtc is DateTime timestamp
            ? $"Catálogo sincronizado em {timestamp.ToLocalTime():dd/MM/yyyy HH:mm}."
            : "Data de sincronização do catálogo indisponível.";
    }

    public async Task InstallAsync(AppEntry app)
    {
        if (app.IsInstalled)
        {
            return;
        }

        if (app.IsInstalling)
        {
            return;
        }

        app.IsInstalling = true;
        StatusText = $"{app.Name} adicionado à fila de instalação.";

        try
        {
            var result = await _backend.InstallAsync(app);

            // Reset installing flag after operation completes
            app.IsInstalling = false;

            if (result.Success)
            {
                app.IsInstalled = true;
                StatusText = $"{app.Name} instalado com sucesso.";
            }
            else
            {
                // Failure: status text may already be set by OperationRunner; provide a generic message
                StatusText = $"Falha ao instalar {app.Name}.";
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "StorefrontViewModel.InstallAsync");

            // Ensure flag reset and report error
            app.IsInstalling = false;
            StatusText = $"Não foi possível instalar {app.Name}. Tente novamente.";
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _isNavigationActive = false;
        _navigationCancellation?.Cancel();
        if (_navigationLoad.IsCompleted)
        {
            _navigationCancellation?.Dispose();
            _navigationCancellation = null;
        }
        _searchCancellation?.Cancel();
        _storeService.CatalogUpdated -= OnCatalogUpdated;
        _featuredCatalogService.Updated -= OnFeaturedCatalogUpdated;
        _storeService.CacheCleared -= OnCacheCleared;
        _installedAppsService.Changed -= OnInstalledAppsChanged;
    }

}
