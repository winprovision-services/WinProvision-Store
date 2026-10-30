using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
    private readonly IStoreCatalog _storeService;
    private readonly WinGetService _winGetService;
    private readonly IconService _iconService;
    private readonly WingetExecutor _wingetExecutor;
    private readonly OperationsQueueService _queueService;
    private readonly InstalledAppsService _installedAppsService;
    private readonly FeaturedCatalogService _featuredCatalogService;
    private readonly ApplicationPreferencesService _preferences;
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
    private static readonly string StoreBannerCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "Cache", "featured-store-banners.json");
    private int _heroBannerIndex;
    private string _selectedCategoryTag = "all";
    private CatalogSortMode _catalogSortMode;
    private ShelfView _shelfView;

    public enum ShelfView { Overview, Popular, Store }
    public enum CatalogSortMode { Popularity, Name, Id, MicrosoftStore, WinGet }

    public Task InitialContentReady => _initialContentReady.Task;

    private static readonly Dictionary<string, string[]> CategoryTagMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["productivity"] =
        [
            "productivity", "productivity suite", "office", "office suite", "business", "business tools",
            "work", "workflow", "task management", "project management", "note taking", "notes", "calendar",
            "email", "document", "spreadsheet", "presentation", "collaboration", "communication",
            "produtividade", "suíte de escritório", "escritório", "negócios", "trabalho", "fluxo de trabalho",
            "gerenciamento de tarefas", "gestão de projetos", "anotações", "notas", "calendário", "documentos",
            "planilhas", "apresentações", "colaboração", "comunicação"
        ],
        ["development"] =
        [
            "development", "developer", "dev tools", "programming", "coding", "code editor", "ide",
            "software development", "web development", "debugging", "version control", "git", "database",
            "api", "sdk", "terminal", "shell", "virtualization", "container", "devops", "development tools",
            "desenvolvimento", "desenvolvedor", "ferramentas de desenvolvimento", "programação", "código",
            "editor de código", "depuração", "controle de versão", "banco de dados", "virtualização", "contêiner"
        ],
        ["utilities"] =
        [
            "utilities", "utility", "tools", "system", "system tools", "file management", "file manager",
            "backup", "disk", "storage", "network", "remote access", "automation", "compression", "archive",
            "clipboard", "keyboard", "mouse", "display", "customization", "tweaks", "monitoring", "hardware",
            "driver", "drivers", "cleaner", "search", "launcher", "utilities tools", "utilitários", "ferramentas",
            "sistema", "ferramentas do sistema", "gerenciamento de arquivos", "arquivos", "cópia de segurança",
            "armazenamento", "rede", "acesso remoto", "automação", "compactação", "área de transferência",
            "teclado", "personalização", "monitoramento", "hardware", "drivers", "limpeza", "pesquisa", "inicializador"
        ],
        ["multimedia"] =
        [
            "multimedia", "media", "audio", "music", "video", "photo", "photography", "image", "graphics",
            "design", "drawing", "painting", "animation", "streaming", "media player", "video editor", "audio editor",
            "screen recording", "screenshot", "camera", "podcast", "multimídia", "mídia", "áudio", "música",
            "vídeo", "foto", "fotografia", "imagem", "imagens", "gráficos", "desenho", "pintura", "animação",
            "reprodução de mídia", "edição de vídeo", "edição de áudio", "gravação de tela", "captura de tela", "câmera"
        ],
        ["security"] =
        [
            "security", "antivirus", "anti-virus", "endpoint protection", "firewall", "vpn", "privacy", "encryption",
            "password manager", "authentication", "two-factor", "2fa", "malware", "ransomware", "internet security",
            "network security", "secure browsing", "identity protection", "segurança", "antivírus", "proteção", "firewall",
            "privacidade", "criptografia", "gerenciador de senhas", "autenticação", "verificação em duas etapas",
            "proteção contra malware", "segurança de rede", "navegação segura", "proteção de identidade"
        ],
        ["gaming"] =
        [
            "game", "games", "gaming", "game store", "games store", "game launcher", "games launcher",
            "gaming platform", "video game", "video games", "jogo", "jogos", "jogos eletrônicos",
            "loja de jogos", "plataforma de jogos"
        ],
        ["browsers"] =
        [
            "browser", "browsers", "web browser", "web browsers", "internet browser", "internet browsers",
            "web-browser", "internet-browser", "desktop browser", "mobile browser", "browser software", "browser app",
            "opera-browser", "firefox-browser", "arc-browser", "tor-browser", "zen-browser",
            "chromium", "gecko", "firefox", "opera", "vivaldi", "brave", "waterfox", "librewolf", "floorp",
            "web browsing", "internet browsing", "private browser", "privacy browser", "secure browser", "anonymous browser",
            "web navigator", "internet navigator", "tor browser", "chromium-based browser", "firefox-based browser",
            "navegador", "navegadores", "navegador web", "navegadores web", "navegador da web",
            "navegador de internet", "navegadores de internet", "navegação web", "navegação na internet",
            "navegador para desktop", "navegador privado", "navegador seguro", "navegação privada"
        ],
    };

    private static readonly Dictionary<string, string[]> FeaturedCategoryAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gaming"] = ["Valve.Steam", "EpicGames.EpicGamesLauncher", "RockstarGames.Launcher", "HydraLauncher.Hydra"],
        ["security"] = ["Emsisoft.EmergencyKit", "Malwarebytes.Malwarebytes", "ESET.Nod32", "IObit.MalwareFighter"],
        ["communication"] = ["Microsoft.Teams.Free", "Zoom.Zoom", "SlackTechnologies.Slack", "Discord.Discord"],
        ["multimedia"] = ["Spotify.Spotify", "VideoLAN.VLC", "OBSProject.OBSStudio", "GIMP.GIMP"],
        ["utilities"] = ["Microsoft.PowerToys", "M2Team.NanaZip", "ShareX.ShareX", "Rufus.Rufus"],
        ["productivity"] = ["Obsidian.Obsidian", "Notion.Notion", "TheDocumentFoundation.LibreOffice", "Anki.Anki"],
        ["development"] = ["Microsoft.VisualStudioCode", "Microsoft.VisualStudio.2022.Community", "Git.Git", "Docker.DockerDesktop"]
    };
    private static readonly string[] FeaturedPopularAppIds =
    [
        "Devolutions.UniGetUI", "Microsoft.PowerToys", "7zip.7zip", "Google.Chrome"
    ];
    private static readonly string[] FeaturedPopularStoreIds =
    ["9NKSQGP7F2NH", "9WZDNCRFJ3TJ", "9NXQXXLFST89", "9N0866FS04W8"];
    private static readonly string[] FeaturedBrowserIds =
    [
        "Google.Chrome", "Mozilla.Firefox.pt-br", "Vivaldi.Vivaldi", "Opera.Opera",
        "Microsoft.Edge", "Mozilla.Firefox", "Brave.Brave", "Zen-Team.Zen-Browser", "TorProject.TorBrowser",
        "Waterfox.Waterfox", "LibreWolf.LibreWolf", "Ablaze.Floorp", "Alex313031.Thorium"
    ];

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
        IconService iconService, IStoreCatalog storeService, WinGetService winGetService, IUiDispatcher dispatcher, IPackageBackend backend,
        ApplicationPreferencesService preferences)
    {
        _msStoreCatalogService = msStoreCatalogService;
        _storeService = storeService;
        _winGetService = winGetService;
        _iconService = iconService;
        _wingetExecutor = wingetExecutor;
        _queueService = queueService;
        _installedAppsService = installedAppsService;
        _featuredCatalogService = featuredCatalogService;
        _preferences = preferences;

        _dispatcher = dispatcher;
        _backend = backend;
        _storeService.CatalogUpdated += OnCatalogUpdated;
        _featuredCatalogService.Updated += OnFeaturedCatalogUpdated;
        _storeService.CacheCleared += OnCacheCleared;
        _installedAppsService.Changed += OnInstalledAppsChanged;
        _preferences.SearchOnDemandChanged += OnSearchOnDemandChanged;
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
        set { if (EqualityComparer<string>.Default.Equals(_searchText, value)) return; _searchText = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSearchOnDemandIdle)); }
    }
    public bool IsSearchOnDemandIdle => _preferences.SearchOnDemand && string.IsNullOrWhiteSpace(SearchText);
    public bool IsSearchOnDemandEnabled => _preferences.SearchOnDemand;

    private void OnSearchOnDemandChanged(bool enabled)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => OnSearchOnDemandChanged(enabled));
            return;
        }

        OnPropertyChanged(nameof(IsSearchOnDemandIdle));
        OnPropertyChanged(nameof(IsSearchOnDemandEnabled));
        if (enabled && string.IsNullOrWhiteSpace(SearchText))
            _ = ReturnToSearchIdleAsync();
        else if (!enabled)
            ApplyFilter();
    }

    private async Task ReturnToSearchIdleAsync()
    {
        _searchCancellation?.Cancel();
        await _navigationLoad;
        if (_isDisposed || !_preferences.SearchOnDemand || !string.IsNullOrWhiteSpace(SearchText)) return;
        ReleaseCatalogMemory();
        await _storeService.ReleaseCatalogMemoryAsync();
        IsBrowsing = false;
        ShowHero = false;
        ShowResults = false;
        CatalogSyncText = "Catálogo sob demanda.";
        StatusText = "Pesquise para carregar aplicativos.";
        OnPropertyChanged(nameof(IsSearchOnDemandIdle));
        _initialContentReady.TrySetResult(true);
    }

    public async Task SubmitSearchAsync(string query)
    {
        SearchText = query;
        if (_preferences.SearchOnDemand && string.IsNullOrWhiteSpace(query))
        {
            await ReturnToSearchIdleAsync();
            return;
        }

        if (_preferences.SearchOnDemand && !_catalogLoaded)
        {
            IsBrowsing = false;
            ShowHero = false;
            ShowResults = true;
            StatusText = $"Carregando catálogo para \"{query.Trim()}\"...";
            await LoadAsync(_navigationCancellation?.Token ?? CancellationToken.None);
        }
        ApplyFilter();
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
        if (_preferences.SearchOnDemand) return;
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
        if (_preferences.SearchOnDemand && string.IsNullOrWhiteSpace(SearchText))
        {
            _allApps = [];
            _catalogLoaded = false;
            Apps.Clear();
            _ = _storeService.ReleaseCatalogMemoryAsync();
            CatalogSyncText = "Catálogo sob demanda.";
            StatusText = "Pesquise para carregar aplicativos.";
            return;
        }
        SyncInstalledFlags(_allApps);
        if (_preferences.SearchOnDemand)
        {
            _catalogLoaded = true;
            ApplyFilter();
            return;
        }
        if (_storeBannersLoading) _featuredRefreshPending = true;
        else _ = LoadStoreBannersAsync(force: true);
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
        try { File.Delete(StoreBannerCachePath); }
        catch (IOException ex) { WinProvisionLog.Warning(ex, "Storefront.ClearBannerCache"); }
        catch (UnauthorizedAccessException ex) { WinProvisionLog.Warning(ex, "Storefront.ClearBannerCache"); }
        StoreBannerApps.Clear();
        SideStoreBannerApps.Clear();
        RightPromoApp = null;
        SelectedBanner = null;
        Apps.Clear();
        PopularApps.Clear();
        StoreHighlights.Clear();
        FeaturedCategories.Clear();
        _featuredCatalog = null;
        StoreHighlights.Clear();
        FeaturedCategories.Clear();
        StatusText = "Cache limpo. O catálogo será atualizado ao abrir esta tela.";
    }

    private void SyncInstalledFlags(IEnumerable<AppEntry> apps)
    {
        foreach (AppEntry app in apps)
        {
            app.IsInstalled = app.IsSelf || _installedAppsService.IsInstalled(app.Name, app.Id);
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
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            return;
        }
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
        ReleasePageVisualReferences();
        AsyncImage.ReleasePageMemory();
    }

    private void ReleasePageVisualReferences()
    {
        _liveSearchResults = [];
        _liveSearchQuery = null;
        _searchQueryInFlight = null;
        _searchedStoreBannerApps = [];
        _storeBannersLoaded = false;
        _storeBannerWeek = null;
        _storeBannerSearchWeek = null;
        StoreBannerApps.Clear();
        SideStoreBannerApps.Clear();
        RightPromoApp = null;
        SelectedBanner = null;
        Apps.Clear();
        PopularApps.Clear();
        StoreHighlights.Clear();
        FeaturedCategories.Clear();
        _featuredCatalog = null;
    }

    public void ReleaseCatalogMemory()
    {
        _catalogLoaded = false;
        _allApps = [];
        ReleasePageVisualReferences();
        CatalogSyncText = "Catálogo em pausa para liberar memória.";
        StatusText = "A vitrine será recarregada ao retornar ao aplicativo.";
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_preferences.SearchOnDemand && string.IsNullOrWhiteSpace(SearchText))
        {
            await _storeService.ReleaseCatalogMemoryAsync(cancellationToken);
            IsBrowsing = false;
            ShowHero = false;
            ShowResults = false;
            Apps.Clear();
            CatalogSyncText = "Catálogo sob demanda.";
            StatusText = "Pesquise para carregar aplicativos.";
            _initialContentReady.TrySetResult(true);
            return;
        }

        if (_catalogLoaded)
        {
            ApplyFilter();
            _initialContentReady.TrySetResult(true);
            if (_preferences.SearchOnDemand)
                return;
            Task cachedInventoryTask = _installedAppsService.EnsureLoadedAsync(cancellationToken);
            await LoadStoreBannersAsync(cancellationToken: cancellationToken);
            await cachedInventoryTask;
            cancellationToken.ThrowIfCancellationRequested();
            SyncInstalledFlags(_allApps);
            UpdatePopularApps();
            ApplyFilter();
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
            // The local catalog and editorial document are independent startup I/O.
            // Start both before awaiting either so a slow catalog read does not delay
            // the metadata needed to render the first storefront sections.
            Task<List<AppEntry>> catalogTask = _storeService.LoadCatalogAsync(cancellationToken: cancellationToken);
            Task<FeaturedCatalogDocument?> featuredTask = !_preferences.SearchOnDemand && _featuredCatalog is null
                ? _featuredCatalogService.LoadAsync(cancellationToken)
                : Task.FromResult<FeaturedCatalogDocument?>(_featuredCatalog);
            await Task.WhenAll(catalogTask, featuredTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (_preferences.SearchOnDemand && string.IsNullOrWhiteSpace(SearchText))
            {
                ReleaseCatalogMemory();
                await _storeService.ReleaseCatalogMemoryAsync(cancellationToken);
                CatalogSyncText = "Catálogo sob demanda.";
                StatusText = "Pesquise para carregar aplicativos.";
                _initialContentReady.TrySetResult(true);
                return;
            }
            _allApps = await catalogTask;
            _featuredCatalog ??= await featuredTask;
            UpdateCatalogSyncText();
            SyncInstalledFlags(_allApps);
            UpdatePopularApps();
            _catalogLoaded = true;
            App.MemoryLog?.RecordOnce("catalog_loaded");
            ApplyFilter();
            _initialContentReady.TrySetResult(true);

            if (_preferences.SearchOnDemand)
                return;

            // Editorial content must not wait for the potentially slow gerenciador de pacotes inventory.
            Task inventoryTask = _installedAppsService.EnsureLoadedAsync(cancellationToken);
            await LoadStoreBannersAsync(cancellationToken: cancellationToken);
            App.MemoryLog?.RecordOnce("editorial_banners_loaded");
            await inventoryTask;
            App.MemoryLog?.RecordOnce("installed_inventory_loaded");
            cancellationToken.ThrowIfCancellationRequested();
            SyncInstalledFlags(_allApps);
            UpdatePopularApps();
            ApplyFilter();
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

            List<AppEntry> curated = _featuredCatalog is null
                ? []
                : ResolveFeaturedApps(_featuredCatalog.Hero)
                    .Where(app => !string.IsNullOrWhiteSpace(app.StoreBannerUrl))
                    .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();

            List<AppEntry> availableStoreApps = _allApps
                .Where(IsEligibleFeaturedStoreApp)
                .ToList();

            // Render the best candidates already available before issuing the slower
            // supplemental catálogo visual searches. The final list is refreshed below.
            List<AppEntry> initialCandidates = availableStoreApps
                .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(app => app.StoreRatingCount ?? 0)
                .ThenByDescending(app => app.StoreRating ?? 0)
                .ThenByDescending(app => app.Score)
                .Take(StoreFeaturedCandidatePoolSize)
                .ToList();
            List<AppEntry> initialFeatured = curated.Concat(initialCandidates
                    .OrderBy(app => GetWeeklyBannerOrderKey(app, week), StringComparer.Ordinal)
                    .Take(6))
                .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            if (initialFeatured.Count > 0)
            {
                ApplyFeaturedBanners(initialFeatured);
                App.MemoryLog?.RecordOnce("editorial_banners_provisional");
            }

            // A busca ampla do catálogo pode não conter todos os produtos editoriais.
            // Se ainda faltarem banners, consulta diretamente os títulos conhecidos.
            if (availableStoreApps.Select(GetProductIdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 6)
            {
                if (_storeBannerSearchWeek != week)
                {
                    _searchedStoreBannerApps = await LoadCachedStoreBannersAsync(week, cancellationToken) ?? [];
                    if (_searchedStoreBannerApps.Count == 0)
                    {
                        var discovered = new System.Collections.Concurrent.ConcurrentBag<AppEntry>();
                        int provisionalBannersPublished = 0;
                        await Parallel.ForEachAsync(
                            FeaturedStoreSearchTerms,
                            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
                            async (term, token) =>
                            {
                                foreach (AppEntry app in await _msStoreCatalogService.SearchAsync(term, token))
                                    if (IsEligibleFeaturedStoreApp(app))
                                        discovered.Add(app);

                                if (Volatile.Read(ref provisionalBannersPublished) != 0)
                                    return;

                                List<AppEntry> earlyCandidates = discovered
                                    .DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                                    .OrderByDescending(app => app.StoreRatingCount ?? 0)
                                    .ThenByDescending(app => app.StoreRating ?? 0)
                                    .Take(StoreFeaturedCandidatePoolSize)
                                    .ToList();
                                if (earlyCandidates.Count < 3 ||
                                    Interlocked.CompareExchange(ref provisionalBannersPublished, 1, 0) != 0)
                                    return;

                                await _dispatcher.InvokeAsync(() =>
                                {
                                    if (!_isNavigationActive || _isDisposed || token.IsCancellationRequested)
                                        return;

                                    List<AppEntry> earlyFeatured = curated.Concat(earlyCandidates
                                            .OrderBy(app => GetWeeklyBannerOrderKey(app, week), StringComparer.Ordinal)
                                            .Take(6))
                                        .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                                        .Take(6)
                                        .ToList();
                                    if (earlyFeatured.Count == 0)
                                        return;

                                    ApplyFeaturedBanners(earlyFeatured);
                                    UpdatePopularApps();
                                    App.MemoryLog?.RecordOnce("editorial_banners_provisional");
                                }, token);
                            });
                        cancellationToken.ThrowIfCancellationRequested();
                        _searchedStoreBannerApps = discovered
                            .DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(app => app.StoreRatingCount ?? 0)
                            .ThenByDescending(app => app.StoreRating ?? 0)
                            .Take(StoreFeaturedCandidatePoolSize)
                            .ToList();
                        if (_searchedStoreBannerApps.Count > 0)
                            await SaveCachedStoreBannersAsync(week, _searchedStoreBannerApps, cancellationToken);
                    }
                    foreach (AppEntry app in _searchedStoreBannerApps)
                    {
                        app.IconUrl = _iconService.ResolveIconUrl(app);
                        app.IsInstalled = _installedAppsService.IsInstalled(app.Name, app.Id);
                    }
                    if (_searchedStoreBannerApps.Count > 0) _storeBannerSearchWeek = week;
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

            List<AppEntry> featured = curated.Concat(candidatePool
                .OrderBy(app => GetWeeklyBannerOrderKey(app, week), StringComparer.Ordinal)
                .Take(6))
                .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();

            cancellationToken.ThrowIfCancellationRequested();
            ApplyFeaturedBanners(featured);
            _storeBannerWeek = week;
            _storeBannersLoaded = true;
            UpdatePopularApps();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            WinProvisionLog.Warning(ex, "Storefront.LoadBanners");
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

    private static async Task<List<AppEntry>?> LoadCachedStoreBannersAsync(DateOnly week, CancellationToken token)
    {
        try
        {
            if (!File.Exists(StoreBannerCachePath) || new FileInfo(StoreBannerCachePath).Length > 2 * 1024 * 1024)
                return null;
            string content = await File.ReadAllTextAsync(StoreBannerCachePath, token);
            int separator = content.IndexOf('\n');
            if (separator < 0 || content[..separator].TrimEnd('\r') != week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                return null;
            return WinProvision.Core.Services.AotJson.Deserialize<List<AppEntry>>(
                content[(separator + 1)..], WinProvisionJsonOptions.Default)?
                .Where(IsEligibleFeaturedStoreApp).ToList();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            WinProvisionLog.Warning(ex, "Storefront.LoadBannerCache");
            return null;
        }
    }

    private static async Task SaveCachedStoreBannersAsync(DateOnly week, List<AppEntry> apps, CancellationToken token)
    {
        string temporary = StoreBannerCachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StoreBannerCachePath)!);
            string content = week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n"
                + WinProvision.Core.Services.AotJson.Serialize(apps, WinProvisionJsonOptions.Default);
            await File.WriteAllTextAsync(temporary, content, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, StoreBannerCachePath, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WinProvisionLog.Warning(ex, "Storefront.SaveBannerCache");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException ex) { WinProvisionLog.Warning(ex, "Storefront.DeleteTemporaryBannerCache"); }
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
        && app.StoreRating is >= StoreFeaturedMinimumRating and <= 5
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
        string.Equals(app.CatalogDetailBaseUrl, WingetCdnCatalog.BaseUrl.TrimEnd('/'), StringComparison.Ordinal)
        ||
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
        // precisam repetir o mesmo produto já disponível pelo gerenciador de pacotes. Para marcas
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

        // Mantém o ID e a origem de instalação gerenciador de pacotes, mas usa as mídias oficiais da Store.
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

    private static int GetFeaturedBrowserPriority(string appId)
    {
        int index = Array.FindIndex(FeaturedBrowserIds,
            id => id.Equals(appId, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    private IEnumerable<AppEntry> GetFeaturedCategoryApps(
        string categoryId, List<AppEntry> curatedApps, IEnumerable<AppEntry> tagMatches)
    {
        if (!FeaturedCategoryAppIds.TryGetValue(categoryId, out string[]? preferredIds))
            return curatedApps.Concat(tagMatches.OrderByDescending(app => app.Score));

        List<AppEntry> candidates = _allApps.Concat(curatedApps).ToList();
        IEnumerable<AppEntry> preferredApps = preferredIds
            .Select(id => candidates.FirstOrDefault(app => app.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OfType<AppEntry>();

        return preferredApps.Concat(curatedApps).Concat(tagMatches.OrderByDescending(app => app.Score));
    }

    private static bool MatchesCategory(AppEntry app, string category)
    {
        if (!CategoryTagMap.TryGetValue(category, out string[]? keywords))
            return false;

        if (category.Equals("browsers", StringComparison.OrdinalIgnoreCase)
            && FeaturedBrowserIds.Contains(app.Id, StringComparer.OrdinalIgnoreCase))
            return true;

        IEnumerable<string[]> metadata = app.Tags
            .Concat((string?[])[app.StoreCategory, app.StoreSubCategory])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeCategoryWords(value!));

        string[][] normalizedKeywords = keywords
            .Select(NormalizeCategoryWords)
            .Where(words => words.Length > 0)
            .ToArray();

        return metadata.Any(words => normalizedKeywords.Any(keyword => ContainsWordSequence(words, keyword)));
    }

    private static string[] NormalizeCategoryWords(string value) => string.Join(' ', value
        .Normalize(NormalizationForm.FormD)
        .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
        .Select(char.ToLowerInvariant)
        .Select(character => char.IsLetterOrDigit(character) ? character : ' '))
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool ContainsWordSequence(string[] words, string[] phrase)
    {
        if (phrase.Length == 0 || phrase.Length > words.Length)
            return false;

        for (int start = 0; start <= words.Length - phrase.Length; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < phrase.Length; offset++)
                if (!words[start + offset].Equals(phrase[offset], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }

            if (matches)
                return true;
        }

        return false;
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
                List<AppEntry> curatedApps = ResolveFeaturedApps(category.Apps);
                IEnumerable<AppEntry> tagMatches = CategoryTagMap.ContainsKey(category.Id)
                    ? _allApps.Where(app => MatchesCategory(app, category.Id))
                    : [];
                IEnumerable<AppEntry> apps = GetFeaturedCategoryApps(category.Id, curatedApps, tagMatches);

                foreach (AppEntry app in apps.DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase).Take(4))
                    shelf.Apps.Add(app);
                if (shelf.Apps.Count > 0) FeaturedCategories.Add(shelf);
            }
        }

        var browserShelf = new FeaturedCategoryShelf { Title = "Navegadores" };
        foreach (AppEntry app in _allApps
                     .Where(app => MatchesCategory(app, "browsers"))
                     .OrderBy(app => GetFeaturedBrowserPriority(app.Id))
                     .ThenByDescending(app => app.Score)
                     .ThenByDescending(app => app.GitHubStars ?? 0)
                     .Take(4))
            browserShelf.Apps.Add(app);
        if (browserShelf.Apps.Count > 0)
            FeaturedCategories.Add(browserShelf);

        int limit = _shelfView == ShelfView.Popular ? 48 : FeaturedPopularAppIds.Length;
        List<AppEntry> selectedStoreApps = SelectPopularStoreApps(
            _allApps.Concat(_searchedStoreBannerApps).Concat(StoreBannerApps),
            _shelfView == ShelfView.Store ? 48 : FeaturedPopularStoreIds.Length);
        HashSet<string> storeProductKeys = selectedStoreApps
            .Select(GetProductIdentityKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        PopularApps.Clear();
        IEnumerable<AppEntry> catalogPopular = _featuredCatalog is { Popular.Count: > 0 }
            ? ResolveFeaturedApps(_featuredCatalog.Popular)
            : _allApps
                     .Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                   && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                   && app.Score >= 25 && MatchesLocalRegion(app)
                                   && !storeProductKeys.Contains(GetProductIdentityKey(app)))
                     .OrderByDescending(app => app.Score)
                     .ThenByDescending(app => app.GitHubStars ?? 0)
                     .Take(limit);
        IEnumerable<AppEntry> preferredPopular = FeaturedPopularAppIds
            .Select(id => _allApps.FirstOrDefault(app => app.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .OfType<AppEntry>();
        IEnumerable<AppEntry> ranked = preferredPopular.Concat(catalogPopular)
            .DistinctBy(GetProductIdentityKey, StringComparer.OrdinalIgnoreCase);
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
            .OrderBy(app => GetFeaturedStorePriority(app.Id))
            .ThenByDescending(IsFamiliarStoreApp)
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

    private static int GetFeaturedStorePriority(string appId)
    {
        int index = Array.FindIndex(FeaturedPopularStoreIds,
            id => id.Equals(appId, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
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
        if (_preferences.SearchOnDemand && string.IsNullOrWhiteSpace(SearchText))
        {
            IsBrowsing = false;
            ShowHero = false;
            ShowResults = false;
            Apps.Clear();
            HasEmptyResults = false;
            CatalogSyncText = "Catálogo sob demanda.";
            StatusText = "Pesquise para carregar aplicativos.";
            OnPropertyChanged(nameof(IsSearchOnDemandIdle));
            return;
        }

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
            ? InterleaveCatalogSources(MergeProductEntries(_allApps.Concat(_searchedStoreBannerApps).Concat(StoreBannerApps)
                .Where(app =>
                    MatchesSelectedCatalogSource(app)
                    && ((app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                         && HasRealIcon(app) && HasKnownPtBrSupport(app)
                         && app.Score >= 25 && MatchesLocalRegion(app))
                        || (app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                            && HasRealIcon(app) && HasKnownPtBrSupport(app)
                            && MatchesLocalRegion(app))))
                .Where(app => MatchesCategory(app, _selectedCategoryTag))))
            : string.IsNullOrWhiteSpace(query)
            ? _shelfView == ShelfView.Store
                ? SelectPopularStoreApps(_allApps.Concat(_searchedStoreBannerApps).Concat(StoreBannerApps), 48)
                : _allApps.Where(app => app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                             && HasRealIcon(app) && HasKnownPtBrSupport(app)
                                             && app.Score >= 25 && MatchesLocalRegion(app))
                    .OrderByDescending(app => app.Score)
                    .ThenByDescending(app => app.GitHubStars ?? 0)
            : GetSearchResults(query);

        if (_selectedCategoryTag != "all")
            source = source.Where(app => MatchesCategory(app, _selectedCategoryTag));

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
            || _catalogLoaded;
        HasEmptyResults = results.Count == 0 && searchCompleted;

        StatusText = string.IsNullOrWhiteSpace(query)
            ? $"{results.Count} aplicativos nesta categoria."
            : results.Count == 0
                ? _catalogLoaded ? $"Nenhum resultado para \"{query}\"" : $"Carregando catálogo para \"{query}\"..."
                : results.Count == 1 ? "1 resultado." : $"Resultados: {results.Count}";

        // O gerenciador de pacotes inclui a origem msstore, mas não fornece os metadados visuais da
        // Store. A catálogo visual completa os resultados em tempo real, como no outras interfaces.
        if (!string.IsNullOrWhiteSpace(query)
            && !string.Equals(_liveSearchQuery, query, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_searchQueryInFlight, query, StringComparison.OrdinalIgnoreCase))
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
                WinProvision.Core.Services.CoreLog.Debug($"[HomePage] Uma das fontes de busca falhou: {ex.Message}");
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
                    // enriquecer IDs retornados pelo gerenciador de pacotes não deve descartá-los.
                    WinProvision.Core.Services.CoreLog.Debug($"[HomePage] Falha ao enriquecer resultados da Store: {ex.Message}");
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
            return;
        }
        catch (Exception ex)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[HomePage] Falha na busca ao vivo: {ex.Message}");
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
            _storeBannerSearchWeek = null;
            _storeBannersLoaded = false;
            try { File.Delete(StoreBannerCachePath); }
            catch (IOException ex) { WinProvisionLog.Warning(ex, "Storefront.RefreshBannerCache"); }
            catch (UnauthorizedAccessException ex) { WinProvisionLog.Warning(ex, "Storefront.RefreshBannerCache"); }
            string query = SearchText.Trim() ?? string.Empty;
        if (_preview) { _liveSearchQuery = query; _liveSearchResults = _allApps; }
            if (query.Length > 0 && _catalogLoaded)
            {
                StatusText = "Atualizando resultados da pesquisa...";
                _allApps = await _storeService.LoadCatalogAsync(forceRefresh: true);
                UpdateCatalogSyncText();
                SyncInstalledFlags(_allApps);
                ApplyFilter();
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
        if (app.IsSelf) return;
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
        _preferences.SearchOnDemandChanged -= OnSearchOnDemandChanged;
    }

}
