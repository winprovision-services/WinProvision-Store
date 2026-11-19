using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.IO;
using System.Globalization;
using Microsoft.UI.Dispatching;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class UpdatesViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public enum SortField { Name, Id, Version, NewVersion, Source }
    private readonly IStoreCatalog _storeService;
    private readonly IBackgroundUpdateStartupService _backgroundUpdateStartupService;
    private readonly ApplicationPreferencesService _applicationPreferences;
    private readonly PackageVisualPreferencesService _visuals;
    private readonly IUpdatesIgnoreStore _ignoredUpdatesService;
    private readonly OperationPreferencesService _operationPreferences;

    private readonly IPackageBackend _backend;
    private readonly PackageInstallOptionsService _packageOptions;
    private readonly WingetPackageDetailsService? _wingetDetails;
    private readonly IPresentationService? _presentation;
    private readonly HashSet<Task> _work = [];
    private readonly List<string> _sourceWarnings = [];
    private readonly HashSet<string> _pendingUpdateSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _successfulUpdateSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _lifetime = new();

    // Coleções
    private readonly List<UpgradablePackage> _rawPackages = [];
    private readonly ObservableCollection<UpgradablePackage> _filteredPackages = [];
    private readonly ObservableCollection<IgnoredUpdateEntry> _ignoredPackages = [];

    // Estado de ordenação e filtros
    private SortField _currentSortField = SortField.Name;
    private bool _sortAscending = true;

    private DateTime? _lastVerificationTime;
    private bool _lastCheckFailed;
    public bool SelectAllByDefault { get; set; } = true;
    public UpgradablePackage? SelectedPackage { get; set; }
    private bool _selectAllByDefault => SelectAllByDefault;
    public ObservableCollection<UpgradablePackage> Packages => _filteredPackages;
    public ObservableCollection<IgnoredUpdateEntry> IgnoredPackages => _ignoredPackages;
    public int RawPackageCount => _rawPackages.Count;
    public SortField CurrentSort => _currentSortField;
    public bool SortAscending => _sortAscending;
    public bool AllSelected { get; private set; }
    public event EventHandler? Changed;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        SetProperty(ref field, value, propertyName);
        ScanCommand?.NotifyCanExecuteChanged(); UpdateSelectedCommand?.NotifyCanExecuteChanged();
    }

    public string StatusText { get; set => Set(ref field, value); } = string.Empty;
    public string SubtitleText { get; set => Set(ref field, value); } = string.Empty;
    public string EmptyStateTitle { get; set => Set(ref field, value); } = string.Empty;
    public string EmptyStateText { get; set => Set(ref field, value); } = string.Empty;
    public string SelectionSummaryText { get; set => Set(ref field, value); } = string.Empty;
    public string SelectAllText { get; set => Set(ref field, value); } = "Selecionar todos";
    public bool CanReload { get; set => Set(ref field, value); } = true;
    public bool CanUpdate { get; set => Set(ref field, value); } = false;
    public bool CanSelect { get; set => Set(ref field, value); } = false;
    public bool CanIgnore { get; set => Set(ref field, value); } = false;
    public bool CanShowDetails { get; set => Set(ref field, value); } = false;
    public bool IsBusy { get; set => Set(ref field, value); } = false;
    public bool IsInitialLoading { get; set => Set(ref field, value); } = false;
    public bool IsEmpty { get; set => Set(ref field, value); } = true;
    public bool HasError { get; set => Set(ref field, value); } = false;
    public string ErrorMessage { get; set => Set(ref field, value); } = string.Empty;
    public bool HasIgnored { get; set => Set(ref field, value); } = false;
    public bool? MasterSelection { get; set => Set(ref field, value); } = false;
    public string SearchText { get; set => Set(ref field, value); } = string.Empty;
    public bool FilterWinget { get; set => Set(ref field, value); } = true;
    public bool FilterWinProvision { get; set => Set(ref field, value); } = true;
    public bool FilterMsStore { get; set => Set(ref field, value); } = true;
    public bool FilterChocolatey { get; set => Set(ref field, value); } = true;
    public bool HideSameVersion { get; set => Set(ref field, value); } = true;
    public bool ExactSearch { get; set => Set(ref field, value); } = false;
    public bool SearchName { get; set => Set(ref field, value); } = false;
    public bool SearchId { get; set => Set(ref field, value); } = false;
    public UpdatesViewModel(IPackageBackend backend, IStoreCatalog store, IBackgroundUpdateStartupService startup,
        ApplicationPreferencesService preferences, IUpdatesIgnoreStore ignored, PackageInstallOptionsService packageOptions,
        WingetPackageDetailsService? wingetDetails = null, IPresentationService? presentation = null,
        OperationPreferencesService? operationPreferences = null, PackageVisualPreferencesService? visuals = null)
    {
        _wingetDetails = wingetDetails;
        _presentation = presentation;
        _packageOptions = packageOptions; _backend = backend; _storeService = store; _backgroundUpdateStartupService = startup;
        _applicationPreferences = preferences; _ignoredUpdatesService = ignored;
        _visuals = visuals ?? new PackageVisualPreferencesService(preferences);
        _applicationPreferences.WingetEnabledChanged += OnWingetEnabledChanged;
        _applicationPreferences.ChocolateyEnabledChanged += OnChocolateyEnabledChanged;
        _visuals.PackageIconsChanged += OnPackageIconsChanged;
        _operationPreferences = operationPreferences ?? new OperationPreferencesService();
        ScanCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(CheckUpdatesCoreAsync, () => !IsBusy);
        CancelScanCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ScanCommand.Cancel);
        SelectAllCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => SelectAll(true));
        UpdateSelectedCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(UpdateSelectedAsync, () => !IsBusy && Packages.Any(p => p.IsSelectedForUpdate));
        CancelBatchCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { UpdateSelectedCommand.Cancel(); _backend.CancelAll(); });
        RefreshIgnoredUpdates();
    }
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand ScanCommand { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand CancelScanCommand { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand SelectAllCommand { get; }
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand UpdateSelectedCommand { get; }
    public CommunityToolkit.Mvvm.Input.IRelayCommand CancelBatchCommand { get; }
    public ObservableCollection<UpgradablePackage> Items => Packages;
    public bool IsScanning { get; private set => Set(ref field, value); }
    private Task UpdateSelectedAsync(CancellationToken token) => RunUpdateCoreAsync(Packages.Where(p => p.IsSelectedForUpdate).ToList(), false, false, false, token);
    private void OnWingetEnabledChanged(bool _) => ApplyFilters();
    private void OnChocolateyEnabledChanged(bool enabled)
    {
        if (enabled) _ = CheckUpdatesAsync();
        else ApplyFilters();
    }
    public Task CheckUpdatesAsync() => _lifetime.IsCancellationRequested ? Task.CompletedTask : ScanCommand.ExecuteAsync(null);
    public void SetSort(SortField field, bool? ascending = null)
    {
        _sortAscending = ascending ?? (_currentSortField == field ? !_sortAscending : true);
        _currentSortField = field;
        ApplyFilters();
    }
    public void SelectAll(bool selected)
    {
        foreach (var package in Packages) package.IsSelectedForUpdate = selected;
        RefreshSelection();
    }
    public void ToggleAll() => SelectAll(Packages.Any(p => !p.IsSelectedForUpdate));
    public void ToggleSelection(UpgradablePackage package) { SelectedPackage = package; package.IsSelectedForUpdate = !package.IsSelectedForUpdate; RefreshSelection(); }
    public void RefreshSelection() { UpdateSubtitleAndCounters(); SyncMasterCheckBoxState(); }
    public void Ignore(IEnumerable<UpgradablePackage> packages)
    {
        var selected = packages.ToArray();
        foreach (var package in selected)
        {
            _ignoredUpdatesService.Ignore(package.Id, package.AvailableVersion);
            _rawPackages.Remove(package);
        }
        RefreshIgnoredUpdates();
        ApplyFilters();
        StatusText = $"{selected.Length} pacote(s) ignorado(s).";
    }
    public Task RestoreAsync(IEnumerable<IgnoredUpdateEntry> entries) => ObserveAsync(RestoreCoreAsync(entries.ToArray()));
    private async Task RestoreCoreAsync(IEnumerable<IgnoredUpdateEntry> entries)
    {
        foreach (var entry in entries.ToArray()) _ignoredUpdatesService.Unignore(entry.AppId);
        RefreshIgnoredUpdates();
        await CheckUpdatesAsync();
    }
    public void ExportCsv(string path)
    {
        if (Packages.Count == 0) { StatusText = "Nenhum pacote para exportar."; return; }
        try
        {
            var csv = new StringBuilder("Nome,ID,VersaoAtual,NovaVersao,Origem\n");
            foreach (var p in Packages)
                csv.AppendLine($"\"{EscapeCsv(p.Name)}\",\"{EscapeCsv(p.Id)}\",\"{EscapeCsv(p.CurrentVersion)}\",\"{EscapeCsv(p.AvailableVersion)}\",\"{EscapeCsv(p.SourceLabel)}\"");
            AtomicFile.WriteAllText(path, csv.ToString(), Encoding.UTF8);
            StatusText = $"Exportado para '{Path.GetFileName(path)}' com sucesso.";
        }
        catch (Exception ex) { StatusText = $"Erro ao exportar CSV: {ex.Message}"; }
    }
    public AppEntry ToAppEntry(UpgradablePackage package)
    {
        var catalog = _storeService.GetAll();
        var appEntry = catalog.FirstOrDefault(a =>
            string.Equals(a.Id, package.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Source, package.Source, StringComparison.OrdinalIgnoreCase));

        appEntry ??= new AppEntry
        {
            Id = package.Id,
            Name = package.Name,
            Source = string.IsNullOrWhiteSpace(package.Source) ? "winget" : package.Source,
            Version = package.AvailableVersion,
            Publisher = package.Publisher,
            Description = package.Description,
            IconUrl = package.IconUrl
        };

        return appEntry;
    }
    private async Task CheckUpdatesCoreAsync(CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        bool isInitialCheck = !_lastVerificationTime.HasValue;
        _lastCheckFailed = false;
        _sourceWarnings.Clear();
        _pendingUpdateSources.Clear();
        _successfulUpdateSources.Clear();
        if (_applicationPreferences.WingetEnabled) _pendingUpdateSources.Add("WinGet");
        if (_applicationPreferences.ChocolateyEnabled) _pendingUpdateSources.Add("Chocolatey");
        CanReload = false;
        CanUpdate = false;
        IsBusy = true; IsScanning = true;
        if (isInitialCheck || _rawPackages.Count == 0)
        {
            IsInitialLoading = true;
        }
        HasError = false;
        ErrorMessage = string.Empty;
        StatusText = "Procurando atualizações disponíveis...";
        SubtitleText = "Procurando atualizações disponíveis...";
        using var scanLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        CancellationToken scanToken = scanLifetime.Token;
        Task<List<UpgradablePackage>>? scan = null;
        DispatcherQueue? dispatcher = DispatcherQueue.GetForCurrentThread();

        try
        {
            scan = _backend.ScanUpdatesAsync(scanToken, result => DispatchSourceResultAsync(result, dispatcher));
            await scan.ConfigureAwait(true);
            if (_successfulUpdateSources.Count > 0) _lastVerificationTime = DateTime.Now;
            _lastCheckFailed = _successfulUpdateSources.Count == 0 && _sourceWarnings.Count > 0;
            HasError = _lastCheckFailed;
            ErrorMessage = _lastCheckFailed
                ? string.Join(Environment.NewLine, _sourceWarnings)
                : string.Empty;
            ApplyFilters();
            StatusText = BuildUpdateScanStatus();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            StatusText = "Verificação cancelada.";
            IsInitialLoading = false;
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _lastCheckFailed = true;
            WinProvisionLog.Write($"UPDATE UI discovery failed {ex.GetType().Name}: {ex.Message}");
            StatusText = "Não foi possível verificar atualizações. Tente novamente.";
            ErrorMessage = $"A consulta falhou: {ex.GetType().Name}: {ex.Message}";
            HasError = true;
            ApplyFilters();
        }
        finally
        {
            CanReload = true;
            IsBusy = false;
            IsInitialLoading = false;
            IsScanning = false;
            UpdateSubtitleAndCounters();
            ApplyFilters();
        }
    }

    private Task DispatchSourceResultAsync(UpdateSourceScanResult result, DispatcherQueue? dispatcher)
    {
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            ApplySourceResult(result);
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
            {
                try { ApplySourceResult(result); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }))
            completion.TrySetException(new InvalidOperationException("A thread da interface não está disponível para atualizar a lista."));
        return completion.Task;
    }

    private void ApplySourceResult(UpdateSourceScanResult result)
    {
        _pendingUpdateSources.Remove(result.Source);
        IsInitialLoading = false;

        bool chocolatey = result.Source.Equals("Chocolatey", StringComparison.OrdinalIgnoreCase);
        _rawPackages.RemoveAll(package => chocolatey
            ? package.Source.Contains("chocolatey", StringComparison.OrdinalIgnoreCase)
            : !package.Source.Contains("chocolatey", StringComparison.OrdinalIgnoreCase));

        // Mesmo quando o CLI indica erro, linhas válidas de outdated continuam sendo
        // dados de atualização e não devem desaparecer por causa do código de saída.
        AddFilteredUpdates(result.Packages);
        if (result.ErrorMessage is null)
        {
            _successfulUpdateSources.Add(result.Source);
        }
        else
        {
            string reason = result.ErrorMessage.Contains("tempo esgotado", StringComparison.OrdinalIgnoreCase)
                ? "tempo esgotado"
                : result.ErrorMessage.Trim().TrimEnd('.');
            _sourceWarnings.Add($"{result.Source}: {reason}.");
        }

        HasError = false;
        ErrorMessage = string.Join(Environment.NewLine, _sourceWarnings);
        ApplyFilters();
        StatusText = BuildUpdateScanStatus();
    }

    private void AddFilteredUpdates(IEnumerable<UpgradablePackage> upgradable)
    {
        var catalogById = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry app in _storeService.GetAll())
            catalogById.TryAdd($"{app.Source}\u001f{app.Id}", app);

        foreach (UpgradablePackage package in upgradable)
        {
            if (catalogById.TryGetValue($"{package.Source}\u001f{package.Id}", out AppEntry? match))
            {
                package.IconUrl = match.IconUrl;
                if (string.IsNullOrWhiteSpace(package.Publisher)) package.Publisher = match.Publisher;
                if (string.IsNullOrWhiteSpace(package.Description)) package.Description = match.Description ?? string.Empty;
            }

            if (_visuals.ShouldLoad(PackageImageKind.PackageIcon)
                && (string.IsNullOrWhiteSpace(package.IconUrl) || package.IconUrl == IconService.DefaultIconPackUri))
                package.IconUrl = WinGetManifestIconService.GetPublishedIconUrl(package.Id, package.Source) ?? package.IconUrl;

            if (!HasNewerAvailableVersion(package)
                || _ignoredUpdatesService.IsIgnored(package.Id, package.AvailableVersion)
                || (_packageOptions.Get(package.Id, package.Source)?.SkipMinorVersionSegments is { } skipLevel
                    && IsMinorUpdate(package.CurrentVersion, package.AvailableVersion, skipLevel)))
                continue;

            package.IsSelectedForUpdate = _selectAllByDefault;
            _rawPackages.Add(package);
        }
    }

    private string BuildUpdateScanStatus()
    {
        string pending = _pendingUpdateSources.Count == 0
            ? string.Empty
            : "Aguardando resposta de " + string.Join(" e ", _pendingUpdateSources) + ".";
        string result = _rawPackages.Count > 0
            ? $"Encontradas {_rawPackages.Count} atualizações."
            : pending.Length > 0 ? pending : "Nenhuma atualização disponível. Tudo em dia.";
        string warnings = _sourceWarnings.Count == 0 ? string.Empty : string.Join(" ", _sourceWarnings);
        return string.Join(" ", new[] { result, warnings }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool HasNewerAvailableVersion(UpgradablePackage package)
    {
        string current = package.CurrentVersion.Trim();
        string available = package.AvailableVersion.Trim();

        if (string.IsNullOrEmpty(current)
            || string.IsNullOrEmpty(available)
            || current.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            || available.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (current.Equals(available, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // `choco outdated` has already applied NuGet/Chocolatey version semantics.
        // Re-parsing its SemVer output as a numeric-only version would hide valid
        // updates that include prerelease or metadata suffixes.
        if (package.Source.Contains("chocolatey", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return WinProvisionApiService.IsStrictlyNewerNumericVersion(NormalizeVersionQualifier(current), NormalizeVersionQualifier(available));
    }

    private static bool IsMinorUpdate(string current, string available, int level)
    {
        if (!TryVersionParts(current, out int[] oldParts) || !TryVersionParts(available, out int[] newParts))
            return false;
        for (int index = 0; index < 4; index++)
        {
            int oldPart = index < oldParts.Length ? oldParts[index] : 0;
            int newPart = index < newParts.Length ? newParts[index] : 0;
            if (oldPart != newPart)
                return index + 1 >= Math.Clamp(level, 2, 4);
        }
        return false;
    }

    private static bool TryVersionParts(string value, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized = NormalizeVersionQualifier(value).TrimStart('v', 'V').Split(['-', '+'], 2)[0];
        string[] segments = normalized.Split('.');
        if (segments.Length is < 2 or > 4) return false;
        var values = new int[segments.Length];
        for (int index = 0; index < segments.Length; index++)
            if (!int.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out values[index])) return false;
        parts = values;
        return true;
    }

    private static string NormalizeVersionQualifier(string value)
    {
        string normalized = value.Trim();
        if (normalized.StartsWith("<=", StringComparison.Ordinal)) return normalized[2..].TrimStart();
        if (normalized.StartsWith('<')) return normalized[1..].TrimStart();
        if (normalized.StartsWith(">=", StringComparison.Ordinal)) return normalized[2..].TrimStart();
        if (normalized.StartsWith('>')) return normalized[1..].TrimStart();
        return normalized;
    }

    public void ApplyFilters()
    {
        string query = SearchText?.Trim() ?? string.Empty;
        bool filterWinget = FilterWinget == true;
        bool filterMsStore = FilterMsStore == true;
        bool hideSameVersion = HideSameVersion == true;

        IEnumerable<UpgradablePackage> queryable = _rawPackages;

        // Filtro de fontes
        queryable = queryable.Where(p =>
        {
            string source = (p.Source ?? string.Empty).ToLowerInvariant();
            if (p.IsWinProvisionApiUpdate || source.Contains("winprovision")) return FilterWinProvision;
            if (source.Contains("winget")) return filterWinget && _applicationPreferences.WingetEnabled;
            if (source.Contains("msstore") || source.Contains("microsoft store")) return filterMsStore && _applicationPreferences.WingetEnabled;
            if (source.Contains("chocolatey")) return FilterChocolatey && _applicationPreferences.ChocolateyEnabled;
            return true;
        });

        // Ocultar versões idênticas se marcado
        if (hideSameVersion)
        {
            queryable = queryable.Where(p => !string.Equals(p.CurrentVersion.Trim(), p.AvailableVersion.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // Pesquisa de texto
        if (!string.IsNullOrWhiteSpace(query))
        {
            bool exact = ExactSearch == true;
            bool onlyName = SearchName == true;
            bool onlyId = SearchId == true;

            queryable = queryable.Where(p =>
            {
                if (exact)
                {
                    if (onlyName) return string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase);
                    if (onlyId) return string.Equals(p.Id, query, StringComparison.OrdinalIgnoreCase);
                    return string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(p.Id, query, StringComparison.OrdinalIgnoreCase);
                }

                if (onlyName) return p.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (onlyId) return p.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
                return p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       p.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
            });
        }

        // Ordenação
        queryable = _currentSortField switch
        {
            SortField.Name => _sortAscending
                ? queryable.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.Name, StringComparer.OrdinalIgnoreCase),
            SortField.Id => _sortAscending
                ? queryable.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase),
            SortField.Version => _sortAscending
                ? queryable.OrderBy(p => p.CurrentVersion, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.CurrentVersion, StringComparer.OrdinalIgnoreCase),
            SortField.NewVersion => _sortAscending
                ? queryable.OrderBy(p => p.AvailableVersion, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.AvailableVersion, StringComparer.OrdinalIgnoreCase),
            SortField.Source => _sortAscending
                ? queryable.OrderBy(p => p.SourceLabel, StringComparer.OrdinalIgnoreCase)
                : queryable.OrderByDescending(p => p.SourceLabel, StringComparer.OrdinalIgnoreCase),
            _ => queryable
        };

        var filteredList = queryable.ToList();
        _filteredPackages.Clear();
        foreach (var item in filteredList)
        {
            _filteredPackages.Add(item);
        }

        UpdateSubtitleAndCounters();
        SyncMasterCheckBoxState();

        // Estado Vazio
        bool isEmpty = _filteredPackages.Count == 0;
        if (isEmpty)
        {
            if (_lastCheckFailed)
            {
                EmptyStateTitle = "Não foi possível verificar";
                EmptyStateText = "Confira sua conexão e tente recarregar a lista de atualizações.";
            }
            else if (_rawPackages.Count == 0)
            {
                EmptyStateTitle = "Tudo atualizado";
                EmptyStateText = "Nenhuma atualização pendente encontrada para seus aplicativos.";
            }
            else if (!string.IsNullOrWhiteSpace(query))
            {
                EmptyStateTitle = "Nenhum resultado para a pesquisa";
                EmptyStateText = "Tente outro nome ou ID, ou limpe a pesquisa para ver as atualizações disponíveis.";
            }
            else
            {
                EmptyStateTitle = "Nenhuma atualização corresponde aos filtros";
                EmptyStateText = "Revise as fontes selecionadas e os filtros para exibir mais resultados.";
            }
        }
        IsEmpty = isEmpty && !IsInitialLoading;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSubtitleAndCounters()
    {
        int total = _rawPackages.Count;
        OnPropertyChanged(nameof(RawPackageCount));
        int filtered = _filteredPackages.Count;
        int selected = _filteredPackages.Count(p => p.IsSelectedForUpdate);

        string lastChecked = _lastVerificationTime.HasValue
            ? _lastVerificationTime.Value.ToString("dd/MM/yyyy HH:mm:ss")
            : DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");

        SubtitleText = $"{total} pacotes foram encontrados, dos quais {filtered} correspondem aos filtros especificados. ({selected} selecionado(s)) (Última verificação: {lastChecked})";
        SelectionSummaryText = $"Selecionados: {selected} · Ação: Atualizar seleção";

        bool hasSelection = selected > 0;
        CanUpdate = hasSelection && !IsBusy;
        CanIgnore = hasSelection;
        CanShowDetails = _filteredPackages.Count > 0;

        CanSelect = _filteredPackages.Count > 0 && !IsBusy;
        bool allSelected = _filteredPackages.Count > 0 && selected == _filteredPackages.Count;
        SelectAllText = allSelected ? "Limpar seleção" : "Selecionar todos";
        AllSelected = allSelected;
    }

    private void SyncMasterCheckBoxState()
    {
        if (_filteredPackages.Count == 0)
        {
            MasterSelection = false;
            return;
        }

        int selected = _filteredPackages.Count(p => p.IsSelectedForUpdate);
        if (selected == _filteredPackages.Count)
        {
            MasterSelection = true;
        }
        else if (selected == 0)
        {
            MasterSelection = false;
        }
        else
        {
            MasterSelection = null; // Indeterminado
        }
    }

    public async Task RunUpdateOnPackagesAsync(List<UpgradablePackage> selected, bool elevated = false, bool interactive = false,
        bool ignoreSecurityHash = false, CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await ObserveAsync(RunUpdateCoreAsync(selected, elevated, interactive, ignoreSecurityHash, linkedCancellation.Token));
    }
    private async Task ObserveAsync(Task task)
    {
        _work.Add(task);
        try { await task; }
        finally { _work.Remove(task); }
    }
    private async Task<T> ObserveAsync<T>(Task<T> task)
    {
        _work.Add(task);
        try { return await task; } finally { _work.Remove(task); }
    }
    public async Task ShutdownAsync()
    {
        _visuals.PackageIconsChanged -= OnPackageIconsChanged;
        _lifetime.Cancel(); ScanCommand.Cancel(); UpdateSelectedCommand.Cancel();
        foreach (var task in _work.ToArray()) { try { await task; } catch (OperationCanceledException) { WinProvisionLog.Write("UPDATE UI operação cancelada durante o encerramento."); } catch(Exception ex) { WinProvisionLog.Warning(ex,"Updates.Shutdown"); } }
    }

    private void OnPackageIconsChanged(bool enabled)
    {
        if (enabled)
        {
            foreach (UpgradablePackage package in _rawPackages)
                if (string.IsNullOrWhiteSpace(package.IconUrl) || package.IconUrl == IconService.DefaultIconPackUri)
                    package.IconUrl = WinGetManifestIconService.GetPublishedIconUrl(package.Id, package.Source)
                        ?? package.IconUrl;
        }
        ApplyFilters();
    }
    private async Task RunUpdateCoreAsync(List<UpgradablePackage> selected, bool elevated, bool interactive, bool ignoreSecurityHash, CancellationToken token)
    {
        if (IsBusy || _lifetime.IsCancellationRequested) return;
        if (selected.Count == 0) { StatusText = "Nenhum pacote selecionado. Marque ao menos um aplicativo para atualizar."; return; }
        selected = await ConfirmInstallerHostChangesAsync(selected, token);
        if (selected.Count == 0) { StatusText = "Atualização cancelada após revisar a mudança de domínio."; return; }
        IsBusy = true; CanUpdate = false; CanSelect = false; CanReload = false;
        StatusText = $"Iniciando atualização de {selected.Count} aplicativo(s)...";
        using var operationBatch = _backend.BeginBatch();
        int succeeded = 0, failed = 0;
        try
        {
            using var batchGate = new SemaphoreSlim(_operationPreferences.Load().ParallelOperations);
            await Task.WhenAll(selected.Select(async package =>
            {
                await batchGate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested(); package.IsUpdating = true;
                    try
                    {
                        PackageInstallOptions? options = elevated || interactive || ignoreSecurityHash
                            ? (_packageOptions.Get(package.Id, package.Source) ?? new PackageInstallOptions()) with
                            { Interactive = interactive || _packageOptions.Get(package.Id,package.Source)?.Interactive == true, IgnoreSecurityHash = ignoreSecurityHash, RequiresElevation = elevated || _packageOptions.Get(package.Id,package.Source)?.RequiresElevation == true } : null;
                        var result = await _backend.UpdateAsync(package, options);
                        if (result.Success) { succeeded++; _rawPackages.Remove(package); _filteredPackages.Remove(package); }
                        else { failed++; package.IsSelectedForUpdate = false; }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { WinProvisionLog.Write($"Update failed {package.Id}: {ex}"); failed++; package.IsSelectedForUpdate = false; }
                    finally { package.IsUpdating = false; }
                }
                finally { batchGate.Release(); }
            }));
            StatusText = failed == 0 ? $"Atualização concluída: {succeeded} pacote(s) atualizado(s) com sucesso."
                : $"Atualização concluída: {succeeded} sucesso(s), {failed} falha(s). Verifique a fila.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Atualização cancelada."; }
        finally { IsBusy = false; CanReload = true; ApplyFilters(); }
        if (succeeded > 0 && selected.Any(package => package.Source.Equals("chocolatey", StringComparison.OrdinalIgnoreCase)))
            await CheckUpdatesAsync();
    }

    private async Task<List<UpgradablePackage>> ConfirmInstallerHostChangesAsync(List<UpgradablePackage> packages, CancellationToken token)
    {
        if (!_operationPreferences.Load().WarnInstallerHostChange || _wingetDetails is null || _presentation is null) return packages;
        var approved = new List<UpgradablePackage>(packages.Count);
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            if (!package.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(package.CurrentVersion) || string.IsNullOrWhiteSpace(package.AvailableVersion))
            { approved.Add(package); continue; }
            try
            {
                var app = new AppEntry { Id = package.Id, Name = package.Name, Source = package.Source };
                var current = await _wingetDetails.GetAsync(app, package.CurrentVersion, token);
                var available = await _wingetDetails.GetAsync(app, package.AvailableVersion, token);
                if (Uri.TryCreate(current.InstallerUrl, UriKind.Absolute, out var oldUri)
                    && Uri.TryCreate(available.InstallerUrl, UriKind.Absolute, out var newUri)
                    && oldUri.Scheme == Uri.UriSchemeHttps && newUri.Scheme == Uri.UriSchemeHttps
                    && !oldUri.Host.Equals(newUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    bool proceed = await _presentation.ConfirmAsync("Domínio do instalador alterado",
                        $"{package.Name}: o instalador mudou de {oldUri.Host} para {newUri.Host}. Deseja continuar com a atualização?",
                        "Continuar atualização", "Pular este pacote");
                    if (!proceed) continue;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { WinProvisionLog.Warning(ex, "Updates.InstallerHostCheck"); }
            approved.Add(package);
        }
        return approved;
    }

    public void RefreshIgnoredUpdates()
    {
        _ignoredPackages.Clear();
        foreach (var entry in _ignoredUpdatesService.GetIgnoredUpdates())
        {
            _ignoredPackages.Add(entry);
        }

        HasIgnored = _ignoredPackages.Count > 0;
    }

    private static string EscapeCsv(string value) => value.Replace("\"", "\"\"");
    public Task<bool> ApplyAutomationOptionsAsync(string scheduleText, string modeText, string scopeText, string dayText, string time) => ObserveAsync(ApplyAutomationCoreAsync(scheduleText, modeText, scopeText, dayText, time));
    private async Task<bool> ApplyAutomationCoreAsync(string scheduleText, string modeText, string scopeText, string dayText, string time)
    {
        if (!Enum.TryParse(scheduleText, out BackgroundUpdateSchedule schedule)
            || !Enum.TryParse(modeText, out BackgroundUpdateMode mode)
            || !Enum.TryParse(scopeText, out BackgroundUpdateScope scope)
            || !Enum.TryParse(dayText, out DayOfWeek day))
        {
            StatusText = "Selecione uma agenda, uma ação e um escopo válidos.";
            return false;
        }

        time = time.Trim();
        if (schedule != BackgroundUpdateSchedule.AtLogon
            && !TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            StatusText = "Informe o horário no formato 24 horas, por exemplo 09:30.";
            return false;
        }

        var oldMode = _applicationPreferences.UpdateMode;
        var oldScope = _applicationPreferences.UpdateScope;
        var oldSchedule = _applicationPreferences.UpdateSchedule;
        string oldTime = _applicationPreferences.UpdateScheduleTime;
        var oldDay = _applicationPreferences.UpdateScheduleDay;

        try
        {
            _applicationPreferences.SetUpdateAutomationOptions(mode, scope, schedule, time, day);
            var result = await _backgroundUpdateStartupService.ApplyConfiguredScheduleAsync();
            if (!result.Success)
            {
                _applicationPreferences.SetUpdateAutomationOptions(oldMode, oldScope, oldSchedule, oldTime, oldDay);
                await _backgroundUpdateStartupService.ApplyConfiguredScheduleAsync();
                StatusText = $"Não foi possível aplicar a agenda: {result.Message}";
                return false;
            }

            StatusText = scope == BackgroundUpdateScope.SelectedPackagesOnly
                && _applicationPreferences.AutoUpdatePackageCount == 0
                    ? "Opções salvas. A seleção está vazia; nenhuma atualização automática será instalada até incluir apps."
                    : "Política e agenda de atualização salvas.";
            return true;
        }
        catch (Exception ex)
        {
            _applicationPreferences.SetUpdateAutomationOptions(oldMode, oldScope, oldSchedule, oldTime, oldDay);
            await _backgroundUpdateStartupService.ApplyConfiguredScheduleAsync();
            StatusText = $"Não foi possível salvar as opções de atualização: {ex.Message}";
            return false;
        }
    }
    public bool AutoUpdateEnabled { get; private set => Set(ref field, value); }
    public bool CanToggleAutoUpdate { get; private set => Set(ref field, value); } = true;
    public string AutomationSchedule => _applicationPreferences.UpdateSchedule.ToString();
    public string AutomationMode => _applicationPreferences.UpdateMode.ToString();
    public string AutomationScope => _applicationPreferences.UpdateScope.ToString();
    public string AutomationDay => _applicationPreferences.UpdateScheduleDay.ToString();
    public string AutomationTime => _applicationPreferences.UpdateScheduleTime;
    public string AutomationSelectionText => _applicationPreferences.AutoUpdatePackageCount == 0
        ? "Nenhum app foi incluído ainda. Use o menu de contexto em Aplicativos instalados para selecionar apps."
        : $"{_applicationPreferences.AutoUpdatePackageCount} app(s) marcado(s). Gerencie a seleção no menu de contexto em Aplicativos instalados.";
    public Task LoadAutoUpdateStateAsync() => ObserveAsync(LoadAutoUpdateStateCoreAsync());
    private async Task LoadAutoUpdateStateCoreAsync()
    {
        string? message = await _backgroundUpdateStartupService.MigrateLegacyTaskAsync();
        AutoUpdateEnabled = _backgroundUpdateStartupService.IsEnabled;
        if (message is not null) StatusText = message;
    }
    public Task SetAutoUpdateAsync(bool enabled) => ObserveAsync(SetAutoUpdateCoreAsync(enabled));
    private async Task SetAutoUpdateCoreAsync(bool enabled)
    {
        if (!CanToggleAutoUpdate || enabled == AutoUpdateEnabled) return;
        CanToggleAutoUpdate = false;
        try
        {
            var result = enabled ? await _backgroundUpdateStartupService.EnableAsync() : await _backgroundUpdateStartupService.DisableAsync();
            if (result.Success) AutoUpdateEnabled = enabled;
            StatusText = result.Success ? (result.Message ?? (enabled ? "Atualizações em segundo plano ativadas." : "Atualizações automáticas desativadas."))
                : $"Não foi possível alterar as atualizações em segundo plano: {result.Message}";
        }
        catch (Exception ex) { StatusText = $"Não foi possível alterar as atualizações em segundo plano: {ex.Message}"; }
        finally { CanToggleAutoUpdate = true; OnPropertyChanged(nameof(AutoUpdateEnabled)); }
    }
}
