using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class InstalledPackagesViewModel : ObservableObject
{
    private readonly IInstalledPackagesService _installedPackages;
    private readonly IPackageBackend _backend;
    private readonly IPresentationService _presentation;
    private readonly IClipboardService _clipboard;
    private readonly CollectionsViewModel _collections;
    private readonly IUpdatesIgnoreStore _ignoredUpdates;
    private readonly ApplicationPreferencesService _preferences;
    private readonly WindowsAppNotificationService? _notifications;
    private readonly OperationPreferencesService _operationPreferences;

    public ObservableCollection<InstalledPackageRow> Packages { get; } = new();
    public ObservableCollection<InstalledPackageRow> VisiblePackages { get; } = new();
    public ObservableCollection<IgnoredUpdateEntry> IgnoredUpdates { get; } = new();

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchMode { get; set; } = "Ambos";
    [ObservableProperty] public partial bool IsExactSearch { get; set; }
    [ObservableProperty] public partial bool FilterWinget { get; set; } = true;
    [ObservableProperty] public partial bool FilterStore { get; set; } = true;
    [ObservableProperty] public partial bool FilterLocal { get; set; } = true;
    [ObservableProperty] public partial bool OnlyRemovable { get; set; }
    [ObservableProperty] public partial bool HideSystemComponents { get; set; } = true;
    [ObservableProperty] public partial bool OnlySelected { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsInitialLoading { get; private set; }
    [ObservableProperty] public partial bool HasError { get; private set; }
    [ObservableProperty] public partial string ErrorMessage { get; private set; } = string.Empty;
    [ObservableProperty] public partial string Status { get; set; } = "Carregue os aplicativos instalados.";
    [ObservableProperty] public partial string ViewMode { get; private set; } = "list";
    [ObservableProperty] public partial bool IsFiltersVisible { get; private set; } = true;
    [ObservableProperty] public partial string SortColumn { get; private set; } = "Name";
    [ObservableProperty] public partial bool SortAscending { get; private set; } = true;

    private DateTime _lastChecked;
    private int _loadVersion;

    public InstalledPackagesViewModel(
        IInstalledPackagesService installedPackages,
        IPackageBackend backend,
        IPresentationService presentation,
        IClipboardService clipboard,
        CollectionsViewModel collections,
        IUpdatesIgnoreStore ignoredUpdates,
        ApplicationPreferencesService preferences,
        WindowsAppNotificationService? notifications = null,
        OperationPreferencesService? operationPreferences = null)
    {
        _installedPackages = installedPackages;
        _backend = backend;
        _presentation = presentation;
        _clipboard = clipboard;
        _collections = collections;
        _ignoredUpdates = ignoredUpdates;
        _preferences = preferences;
        _notifications = notifications;
        _operationPreferences = operationPreferences ?? new OperationPreferencesService();

        RefreshCommand = new AsyncRelayCommand(
            (CancellationToken cancellationToken) => LoadAsync(cancellationToken));
        RemoveSelectedCommand = new AsyncRelayCommand(
            (CancellationToken cancellationToken) => RemoveSelectedAsync(cancellationToken: cancellationToken),
            () => CanRemove,
            AsyncRelayCommandOptions.None);
        DownloadSelectedCommand = new AsyncRelayCommand(
            (CancellationToken cancellationToken) => DownloadSelectedAsync(cancellationToken),
            () => !IsBusy && SelectedPackages.Any(),
            AsyncRelayCommandOptions.None);
        CancelBatchCommand = new RelayCommand(CancelBatch, () => IsBusy);
        ToggleSelectAllCommand = new RelayCommand(ToggleSelectAll);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, () => !IsBusy);
        RefreshIgnoredUpdates();
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand RemoveSelectedCommand { get; }
    public IAsyncRelayCommand DownloadSelectedCommand { get; }
    public IRelayCommand CancelBatchCommand { get; }
    public IRelayCommand ToggleSelectAllCommand { get; }
    public IAsyncRelayCommand ExportCsvCommand { get; }

    public int TotalCount => Packages.Count;
    public int VisibleCount => Packages.Count(package => package.IsSearchMatch);
    public int SelectedCount => Packages.Count(package => package.IsSearchMatch && package.IsSelected);
    public bool HasPackages => VisibleCount > 0;
    public bool IsEmpty => !IsInitialLoading && !HasError && !HasPackages;
    public bool CanRemove => !IsBusy && SelectedPackages.Any(package => package.CanRemove);
    public bool CanAddToCollection => !IsBusy && SelectedPackages.Any(package => package.CanAddToCollection);
    public bool? MasterSelection
    {
        get
        {
            var selectable = Packages.Where(package => package.IsSearchMatch && package.CanSelect).ToArray();
            if (selectable.Length == 0)
                return false;
            int selected = selectable.Count(package => package.IsSelected);
            return selected == 0 ? false : selected == selectable.Length ? true : null;
        }
    }

    public string Subtitle =>
        $"{VisibleCount} de {TotalCount} aplicativos · {SelectedCount} selecionados" +
        (_lastChecked == default ? string.Empty : $" · Verificado em {_lastChecked:dd/MM/yyyy HH:mm}");

    private IEnumerable<InstalledPackageRow> SelectedPackages =>
        Packages.Where(package => package.IsSearchMatch && package.IsSelected);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        int version = Interlocked.Increment(ref _loadVersion);
        IsInitialLoading = Packages.Count == 0;
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        Status = "Carregando aplicativos instalados…";
        try
        {
            var packages = await _installedPackages.ListAsync(cancellationToken);
            if (version != _loadVersion)
                return;

            var selectedKeys = Packages.Where(package => package.IsSelected)
                .Select(package => package.SelectionKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Packages.Clear();
            foreach (var package in packages)
            {
                var row = new InstalledPackageRow(package, this);
                row.IsSelected = selectedKeys.Contains(row.SelectionKey);
                Packages.Add(row);
            }

            _lastChecked = DateTime.Now;
            ApplyFilters();
            Status = FormatCount(VisibleCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Carregamento cancelado.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "InstalledPackagesViewModel.LoadAsync");
            HasError = true;
            ErrorMessage = "Não foi possível carregar os aplicativos instalados. Tente novamente.";
            Status = ErrorMessage;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
                IsInitialLoading = false;
            }
        }
    }

    public void ApplyFilters()
    {
        string query = SearchText.Trim();
        foreach (var package in Packages)
        {
            bool sourceMatch = package.SourceCategory switch
            {
                "WinGet" => FilterWinget,
                "Microsoft Store" => FilterStore,
                _ => FilterLocal
            };
            bool nameMatch = IsExactSearch
                ? string.Equals(package.Name, query, StringComparison.OrdinalIgnoreCase)
                : package.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool idMatch = IsExactSearch
                ? string.Equals(package.Id, query, StringComparison.OrdinalIgnoreCase)
                : package.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool queryMatch = query.Length == 0 || SearchMode switch
            {
                "Nome" => nameMatch,
                "ID" => idMatch,
                _ => nameMatch || idMatch
            };

            package.IsSearchMatch = sourceMatch
                && queryMatch
                && (!OnlyRemovable || package.CanRemove)
                && (!HideSystemComponents || !package.IsSystemComponent)
                && (!OnlySelected || package.IsSelected);
        }

        SyncVisiblePackages();
        UpdateCounts();
    }

    public void SetSort(string column, bool? ascending = null)
    {
        SortAscending = ascending ?? (string.Equals(SortColumn, column, StringComparison.OrdinalIgnoreCase)
            ? !SortAscending
            : true);
        SortColumn = column;
        var ordered = column switch
        {
            "Id" => SortAscending
                ? Packages.OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray()
                : Packages.OrderByDescending(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Version" => SortAscending
                ? Packages.OrderBy(package => package.Version, StringComparer.OrdinalIgnoreCase).ToArray()
                : Packages.OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase).ToArray(),
            "Source" => SortAscending
                ? Packages.OrderBy(package => package.SourceLabel, StringComparer.OrdinalIgnoreCase).ToArray()
                : Packages.OrderByDescending(package => package.SourceLabel, StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => SortAscending
                ? Packages.OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase).ToArray()
                : Packages.OrderByDescending(package => package.Name, StringComparer.OrdinalIgnoreCase).ToArray()
        };

        for (int index = 0; index < ordered.Length; index++)
        {
            int currentIndex = Packages.IndexOf(ordered[index]);
            if (currentIndex != index)
                Packages.Move(currentIndex, index);
        }
        SyncVisiblePackages();
    }

    private void SyncVisiblePackages()
    {
        var visible = Packages.Where(package => package.IsSearchMatch).ToArray();
        var visibleSet = visible.ToHashSet();
        for (int index = VisiblePackages.Count - 1; index >= 0; index--)
        {
            if (!visibleSet.Contains(VisiblePackages[index]))
                VisiblePackages.RemoveAt(index);
        }

        for (int index = 0; index < visible.Length; index++)
        {
            if (index == VisiblePackages.Count)
                VisiblePackages.Add(visible[index]);
            else if (!ReferenceEquals(VisiblePackages[index], visible[index]))
            {
                int currentIndex = VisiblePackages.IndexOf(visible[index]);
                if (currentIndex >= 0)
                    VisiblePackages.Move(currentIndex, index);
                else
                    VisiblePackages.Insert(index, visible[index]);
            }
        }
    }

    public void SetViewMode(string mode) => ViewMode = mode;

    public void SetFiltersVisible(bool visible) => IsFiltersVisible = visible;

    public void SetAllSelected(bool selected)
    {
        foreach (var package in Packages.Where(package => package.IsSearchMatch && package.CanSelect))
            package.IsSelected = selected;
        UpdateCounts();
    }

    public async Task RemoveSelectedAsync(
        bool interactive = false,
        CancellationToken cancellationToken = default,
        bool requiresElevation = false)
    {
        var selected = SelectedPackages.Where(package => package.CanRemove).ToArray();
        if (selected.Length == 0)
        {
            Status = "Selecione ao menos um aplicativo compatível com a desinstalação.";
            return;
        }

        if (!await _presentation.ConfirmAsync(
                "Desinstalar aplicativos?",
                $"Os {selected.Length} aplicativos selecionados serão removidos do computador."
                    + (selected.Any(package => package.IsOffice) ? " A remoção do Microsoft Office inclui todas as versões e edições instaladas." : ""),
                "Desinstalar"))
            return;

        IsBusy = true;
        using var operationBatch = _backend.BeginBatch();
        int removed = 0;
        var failures = new List<string>();
        try
        {
            var office = selected.Where(package => package.IsOffice).ToArray();
            if (office.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Status = "Removendo todas as versões do Office…";
                var result = await _backend.UninstallAsync(office[0].ToAppEntry(), interactive, requiresElevation);
                if (result.Success) removed += office.Length;
                else failures.Add("Microsoft Office");
            }
            selected = selected.Where(package => !package.IsOffice).ToArray();
            using var batchGate = new SemaphoreSlim(_operationPreferences.Load().ParallelOperations);
            await Task.WhenAll(selected.Select(async (package, index) =>
            {
                await batchGate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Status = $"Desinstalando {index + 1} de {selected.Length}: {package.Name}…";
                    var result = await _backend.UninstallAsync(package.ToAppEntry(), interactive, requiresElevation);
                    if (result.Success)
                        removed++;
                    else
                        failures.Add(package.Name);
                }
                finally { batchGate.Release(); }
            }));

            await LoadAsync(cancellationToken);
            Status = failures.Count == 0
                ? $"Desinstalação concluída. Aplicativos removidos: {removed}."
                : $"Removidos: {removed}. Falha ao remover: {string.Join(", ", failures)}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = $"Desinstalação interrompida. Aplicativos removidos: {removed}.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "InstalledPackagesViewModel.RemoveSelectedAsync");
            Status = $"Não foi possível concluir a desinstalação: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
            UpdateCounts();
        }
    }

    public async Task RemoveSingleAsync(
        InstalledPackageRow package,
        bool interactive = false,
        bool requiresElevation = false)
    {
        if (!package.CanRemove)
        {
            Status = $"A desinstalação de '{package.Name}' não está disponível.";
            return;
        }
        if (!await _presentation.ConfirmAsync(
                "Desinstalar aplicativo?",
                package.IsOffice ? "Todas as versões e edições do Microsoft Office serão removidas do computador." : $"'{package.Name}' será removido do computador.",
                "Desinstalar"))
            return;

        IsBusy = true;
        try
        {
            var result = await _backend.UninstallAsync(package.ToAppEntry(), interactive, requiresElevation);
            if (result.Success)
            {
                await LoadAsync();
                Status = $"'{package.Name}' desinstalado.";
            }
            else
            {
                Status = WingetErrorTranslator.ToMessage(result.FailureReason, "desinstalar", package.Name);
            }
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "InstalledPackagesViewModel.RemoveSingle");
            Status = $"Não foi possível desinstalar '{package.Name}': {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ShowDetailsAsync(InstalledPackageRow package)
    {
        try
        {
            await _presentation.ShowDetailsAsync(package.ToAppEntry());
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "InstalledPackagesViewModel.ShowDetails");
            Status = $"Não foi possível abrir os detalhes de '{package.Name}'.";
        }
    }

    public void AddToCollection(InstalledPackageRow package)
    {
        if (!package.CanAddToCollection)
        {
            Status = "Pacotes locais não podem ser adicionados a uma coleção.";
            return;
        }

        _collections.Add(package.ToAppEntry());
        Status = $"'{package.Name}' adicionado à coleção ativa.";
    }

    public void AddSelectedToCollection()
    {
        var selected = SelectedPackages.Where(package => package.CanAddToCollection).ToArray();
        if (selected.Length == 0)
        {
            Status = "Selecione um pacote WinGet ou Microsoft Store.";
            return;
        }
        foreach (var package in selected)
            _collections.Add(package.ToAppEntry());
        Status = $"{selected.Length} pacote(s) adicionado(s) à coleção ativa.";
    }

    public void IgnoreSelectedUpdates()
    {
        var selected = SelectedPackages.ToArray();
        if (selected.Length == 0)
        {
            Status = "Nenhum pacote selecionado para ignorar atualizações.";
            return;
        }
        foreach (var package in selected)
            _ignoredUpdates.Ignore(package.Id, "*");
        RefreshIgnoredUpdates();
        Status = $"Atualizações de {selected.Length} pacote(s) ignoradas.";
    }

    public void Ignore(InstalledPackageRow package)
    {
        _ignoredUpdates.Ignore(package.Id, "*");
        RefreshIgnoredUpdates();
        Status = $"Atualizações de '{package.Name}' ignoradas.";
    }

    public void RestoreIgnored(string id)
    {
        _ignoredUpdates.Unignore(id);
        RefreshIgnoredUpdates();
        Status = $"Atualizações para '{id}' restauradas.";
    }

    public void RestoreAllIgnored()
    {
        foreach (var entry in IgnoredUpdates.ToArray())
            _ignoredUpdates.Unignore(entry.AppId);
        RefreshIgnoredUpdates();
        Status = "Todas as atualizações ignoradas foram restauradas.";
    }

    public bool IsAutoUpdateSelected(InstalledPackageRow package) =>
        _preferences.IsAutoUpdatePackageSelected(package.Id, package.Source);

    public void ToggleAutoUpdate(InstalledPackageRow package)
    {
        if (package.SourceCategory is not ("WinGet" or "Microsoft Store"))
        {
            Status = "A automação está disponível para pacotes WinGet e Microsoft Store.";
            return;
        }

        bool included = _preferences.ToggleAutoUpdatePackage(package.Id, package.Source);
        Status = included
            ? $"'{package.Name}' incluído nas atualizações automáticas."
            : $"'{package.Name}' removido das atualizações automáticas.";
    }

    public void OpenLocation(InstalledPackageRow package)
    {
        if (string.IsNullOrWhiteSpace(package.InstallLocation) || !Directory.Exists(package.InstallLocation))
        {
            Status = $"Local de instalação não registrado para '{package.Name}'.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(package.InstallLocation)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "InstalledPackagesViewModel.OpenLocation");
            Status = $"Não foi possível abrir a pasta de '{package.Name}'.";
        }
    }

    public void CopyId(InstalledPackageRow package) =>
        Status = _clipboard.TrySetText(package.Id)
            ? $"ID '{package.Id}' copiado para a área de transferência."
            : "Não foi possível copiar o ID.";

    public void CopyName(InstalledPackageRow package) =>
        Status = _clipboard.TrySetText(package.Name)
            ? $"Nome '{package.Name}' copiado para a área de transferência."
            : "Não foi possível copiar o nome.";

    public void CopyManualUninstallCommand(InstalledPackageRow package)
    {
        string command = !string.IsNullOrWhiteSpace(package.UninstallString)
            ? package.UninstallString
            : $"winget uninstall --id \"{package.Id}\"";
        Status = _clipboard.TrySetText(command)
            ? "Comando de desinstalação copiado para a área de transferência."
            : "Não foi possível copiar o comando.";
    }

    public async Task DownloadSelectedAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedPackages
            .Where(package => package.SourceCategory is "WinGet" or "Microsoft Store")
            .ToArray();
        if (selected.Length == 0)
        {
            Status = "Selecione um pacote WinGet ou Microsoft Store para baixar o instalador.";
            return;
        }

        string? folder = _operationPreferences.Load().DownloadDirectory ?? await _presentation.PickFolderAsync();
        if (string.IsNullOrWhiteSpace(folder))
            return;

        IsBusy = true;
        int downloaded = 0;
        var failures = new List<string>();
        try
        {
            using var batchGate = new SemaphoreSlim(_operationPreferences.Load().ParallelDownloads);
            await Task.WhenAll(selected.Select(async (package, index) =>
            {
                await batchGate.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Status = $"Baixando instalador {index + 1} de {selected.Length}: {package.Name}…";
                    var result = await _backend.DownloadAsync(
                        package.Id,
                        package.SourceCategory == "Microsoft Store" ? "msstore" : "winget",
                        folder,
                        cancellationToken);
                    if (result.Success)
                        downloaded++;
                    else
                        failures.Add(package.Name);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { WinProvisionLog.Warning(ex, "Installed.Download"); failures.Add(package.Name); }
                finally { batchGate.Release(); }
            }));
            _notifications?.ShowDownloadSummary(downloaded, failures.Count);
            Status = failures.Count == 0
                ? $"{downloaded} instalador(es) baixado(s) em {folder}."
                : $"{downloaded} baixado(s); falha em {failures.Count}: {string.Join(", ", failures)}.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = $"Download interrompido. Arquivos baixados: {downloaded}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportCsvAsync(CancellationToken cancellationToken)
    {
        string? path = await _presentation.PickSaveAsync($"PacotesInstalados_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var csv = new StringBuilder("Nome,ID,Versao,Origem\n");
            foreach (var package in Packages.Where(package => package.IsSearchMatch))
            {
                cancellationToken.ThrowIfCancellationRequested();
                csv.AppendLine($"\"{EscapeCsv(package.Name)}\",\"{EscapeCsv(package.Id)}\",\"{EscapeCsv(package.Version)}\",\"{EscapeCsv(package.SourceLabel)}\"");
            }
            AtomicFile.WriteAllText(path, csv.ToString(), Encoding.UTF8);
            Status = $"Exportado para '{Path.GetFileName(path)}' com sucesso.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Exportação cancelada.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "InstalledPackagesViewModel.ExportCsv");
            Status = $"Erro ao exportar: {exception.Message}";
        }
    }

    public void RefreshIgnoredUpdates()
    {
        IgnoredUpdates.Clear();
        foreach (var entry in _ignoredUpdates.GetIgnoredUpdates())
            IgnoredUpdates.Add(entry);
    }

    internal void SelectionChanged()
    {
        if (OnlySelected)
            ApplyFilters();
        else
            UpdateCounts();
    }

    private void ToggleSelectAll() => SetAllSelected(MasterSelection != true);

    private void CancelBatch()
    {
        RemoveSelectedCommand.Cancel();
        DownloadSelectedCommand.Cancel();
        _backend.CancelAll();
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanAddToCollection));
        OnPropertyChanged(nameof(MasterSelection));
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        DownloadSelectedCommand.NotifyCanExecuteChanged();
        ToggleSelectAllCommand.NotifyCanExecuteChanged();
    }

    private static string FormatCount(int count) => count switch
    {
        0 => "Nenhum aplicativo instalado.",
        1 => "1 aplicativo instalado.",
        _ => $"{count} aplicativos instalados."
    };

    private static string EscapeCsv(string value) => value.Replace("\"", "\"\"", StringComparison.Ordinal);

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSearchModeChanged(string value) => ApplyFilters();
    partial void OnIsExactSearchChanged(bool value) => ApplyFilters();
    partial void OnFilterWingetChanged(bool value) => ApplyFilters();
    partial void OnFilterStoreChanged(bool value) => ApplyFilters();
    partial void OnFilterLocalChanged(bool value) => ApplyFilters();
    partial void OnOnlyRemovableChanged(bool value) => ApplyFilters();
    partial void OnHideSystemComponentsChanged(bool value) => ApplyFilters();
    partial void OnOnlySelectedChanged(bool value) => ApplyFilters();

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        DownloadSelectedCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class InstalledPackageRow : ObservableObject
{
    private readonly InstalledPackagesViewModel _owner;
    public InstalledPackageInfo Package { get; }

    public InstalledPackageRow(InstalledPackageInfo package, InstalledPackagesViewModel owner)
    {
        Package = package;
        _owner = owner;
    }

    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Version => Package.Version;
    public string VersionLabel => string.IsNullOrWhiteSpace(Version)
        || Version.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        || Version.Equals("(Unknown)", StringComparison.OrdinalIgnoreCase)
            ? "Versão desconhecida"
            : Version.Trim().TrimStart('v', 'V');
    public string Source => Package.Source;
    public string SourceCategory => InstalledPackagesService.SourceCategory(Source);
    public string SourceLabel => SourceCategory;
    public FluentIcons.Common.Icon SourceIcon => SourceCategory switch
    {
        "WinGet" => FluentIcons.Common.Icon.ArrowDownload,
        "Microsoft Store" => FluentIcons.Common.Icon.StoreMicrosoft,
        _ => FluentIcons.Common.Icon.Laptop
    };
    public string IconUrl => Package.IconUrl;
    public string InstallLocation => Package.InstallLocation;
    public string UninstallString => Package.UninstallString;
    public bool IsSystemComponent => Package.IsSystemComponent;
    public bool IsOffice => InstalledPackagesService.IsOffice(Id, Name);
    public bool CanSelect => !IsSystemComponent && !ApplicationPackagePolicy.IsSelf(Id, Name);
    public bool CanRemove => !IsSystemComponent && !ApplicationPackagePolicy.IsSelf(Id, Name)
        && (IsOffice
            || SourceCategory is "WinGet" or "Microsoft Store"
            || !string.IsNullOrWhiteSpace(UninstallString));
    public bool CanAddToCollection => ApplicationPackagePolicy.IsSelf(Id, Name) || SourceCategory is "WinGet" or "Microsoft Store";
    public string SelectionKey => $"{SourceCategory}|{Id}";

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsSearchMatch { get; set; } = true;

    public AppEntry ToAppEntry() => new()
    {
        Id = ApplicationPackagePolicy.IsSelf(Id, Name) ? "WinProvision.Store" : Id,
        Name = Name,
        Version = Version,
        IconUrl = IconUrl,
        Source = ApplicationPackagePolicy.IsSelf(Id, Name) ? "winget" : SourceCategory switch
        {
            "WinGet" => "winget",
            "Microsoft Store" => "msstore",
            _ => string.Empty
        }
    };

    partial void OnIsSelectedChanged(bool value) => _owner.SelectionChanged();
}
