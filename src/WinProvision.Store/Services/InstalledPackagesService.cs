using System.Text.RegularExpressions;
using Microsoft.Management.Deployment;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public interface IInstalledPackagesService
{
    Task<IReadOnlyList<InstalledPackageInfo>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed record InstalledPackageInfo(
    string Name,
    string Id,
    string Version,
    string Source,
    string IconUrl,
    string UninstallString,
    string QuietUninstallString,
    string InstallLocation,
    bool IsSystemComponent);

public sealed partial class InstalledPackagesService(
    WingetBootstrapper bootstrapper,
    WingetExecutor executor,
    StoreService store,
    InstalledAppsService installedApps, InstalledIconResolver icons) : IInstalledPackagesService, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _comGate = new();
    private readonly object _listGate = new();
    private Task<IReadOnlyList<InstalledPackageInfo>>? _listTask;
    private CancellationTokenSource? _listCancellation;
    private int _listWaiters;
    private bool _listAbandoned;
    private Task<IReadOnlyList<InstalledPackageInfo>>? _comTask;
    private CancellationToken _comTaskCallerToken;
    private Task<IReadOnlyList<InstalledPackageInfo>> GetComTask(CatalogLookup catalog, CancellationToken token)
    {
        lock (_comGate)
        {
            if (_comTask is null || _comTask.IsCompleted || _comTaskCallerToken.IsCancellationRequested)
            {
                _comTaskCallerToken = token;
                _comTask = Task.Run(async () =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    return await ListFromComAsync(catalog, timeout.Token).ConfigureAwait(false);
                });
                _ = _comTask.ContinueWith(task => WinProvisionLog.Warning(task.Exception!, "InstalledCOMWorker"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            return _comTask;
        }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        Task? worker;
        lock (_comGate) worker = _comTask;
        if (worker is not null)
            try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "InstalledCOMShutdown"); }
        lock (_listGate) worker = _listTask;
        if (worker is not null)
            try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (_lifetime.IsCancellationRequested)
            {
                WinProvisionLog.Write($"Installed inventory cancellation confirmed during shutdown: {exception.Message}");
            }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "InstalledListShutdown"); }
        _lifetime.Dispose();
    }

    private static readonly string[] SystemTerms =
    [
        "visual c++", "redistributable", ".net desktop runtime", "windows desktop runtime",
        "webview2", "app installer", "microsoft.winget", "winprovision", "vclibs",
        "ui.xaml", "windows app runtime", "windows app sdk", "net native", "directx",
        "security intelligence", "subsystem for linux", "edge update", "edge webview",
        "windows security", "windows defender", "windows firewall", "windows sdk",
        "system component", "optional feature", "language pack"
    ];

    public async Task<IReadOnlyList<InstalledPackageInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<IReadOnlyList<InstalledPackageInfo>> scan;
        CancellationTokenSource scanCancellation;
        bool started = false;
        lock (_listGate)
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            if (_listTask is null || _listAbandoned || _listCancellation?.IsCancellationRequested == true)
            {
                scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _listCancellation = scanCancellation;
                scan = _listTask = ListCoreAsync(scanCancellation.Token);
                _listWaiters = 0;
                _listAbandoned = false;
                started = true;
            }
            else
            {
                scan = _listTask;
                scanCancellation = _listCancellation!;
            }
            _listWaiters++;
        }

        if (started)
            _ = ObserveListTaskAsync(scan, scanCancellation);
        try
        {
            return await scan.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CancellationTokenSource? cancel = null;
            lock (_listGate)
            {
                if (ReferenceEquals(_listTask, scan) && --_listWaiters == 0 && !scan.IsCompleted)
                {
                    _listAbandoned = true;
                    cancel = _listCancellation;
                }
            }
            try { cancel?.Cancel(); }
            catch (ObjectDisposedException exception)
            {
                WinProvisionLog.Write($"Installed inventory cancellation raced with completed scan cleanup: {exception.Message}");
            }
        }
    }

    private async Task ObserveListTaskAsync(
        Task<IReadOnlyList<InstalledPackageInfo>> scan,
        CancellationTokenSource scanCancellation)
    {
        try
        {
            await scan.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            WinProvisionLog.Write("Installed inventory scan canceled after all callers left or service shutdown.");
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "InstalledPackagesService.ListScan");
        }
        finally
        {
            lock (_listGate)
                if (ReferenceEquals(_listTask, scan) && ReferenceEquals(_listCancellation, scanCancellation))
                {
                    _listTask = null;
                    _listWaiters = 0;
                    _listCancellation = null;
                    _listAbandoned = false;
                }
            scanCancellation.Dispose();
        }
    }

    private async Task<IReadOnlyList<InstalledPackageInfo>> ListCoreAsync(CancellationToken cancellationToken)
    {
        var catalog = new CatalogLookup(store.GetAll());
        var packages = new List<InstalledPackageInfo>();
        var failures = new List<Exception>();
        bool hasSuccessfulSource = false;
        try
        {
            var bootstrap = await bootstrapper.EnsureOnceAsync(WinProvisionLog.Write, cancellationToken);
            if (bootstrap.IsUsable && !WinGetFactoryHelper.IsComDisabled)
            {
                packages.AddRange(await GetComTask(catalog, cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
                hasSuccessfulSource = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            WinProvisionLog.Warning(exception, "InstalledPackagesService.ListFromCom");
        }

        try
        {
            foreach (string? source in new string?[] { null, "msstore" })
            {
                try
                {
                    var cli = await executor.ListInstalledPackagesAsync(cancellationToken, source);
                    hasSuccessfulSource = true;
                    WinProvisionLog.Write($"INSTALLED LIST CLI source={source ?? "all"} count={cli.Count}");
                    foreach (var package in cli)
                    {
                        var app = catalog.FindById(package.Id);
                        Merge(packages, new InstalledPackageInfo(package.Name, package.Id, package.CurrentVersion,
                            string.IsNullOrWhiteSpace(package.Source) ? source ?? (package.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) ? "msstore" : "local") : package.Source,
                            app?.IconUrl ?? "", "", "", "", IsSystemComponent(package.Name, package.Id)));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { failures.Add(exception); WinProvisionLog.Warning(exception, "InstalledCLI:" + source); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            WinProvisionLog.Warning(exception, "InstalledPackagesService.ListFromWingetList");
        }

        try
        {
            await installedApps.EnsureLoadedAsync(cancellationToken);
            hasSuccessfulSource = true;
            foreach (var app in installedApps.GetAllInstalledApps())
            {
                var catalogApp = catalog.FindByIdOrName(app.Id, app.DisplayName);
                Merge(packages, new InstalledPackageInfo(
                    app.DisplayName,
                    app.Id,
                    app.DisplayVersion,
                    "local",
                    catalogApp?.IconUrl ?? string.Empty,
                    app.UninstallString,
                    app.QuietUninstallString,
                    app.InstallLocation,
                    IsSystemComponent(app.DisplayName, app.Id)));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            WinProvisionLog.Warning(exception, "InstalledPackagesService.ListFromRegistry");
        }

        if (!hasSuccessfulSource)
        {
            if (failures.Count == 0)
                throw new InvalidOperationException("Nenhuma fonte de inventário de aplicativos está disponível.");
            throw new AggregateException("Todas as fontes de inventário de aplicativos falharam.", failures);
        }

        var metadata = installedApps.GetAllInstalledApps();
        return await icons.ResolveAsync(packages.Select(package => package with { IsSystemComponent = IsSystemComponent(package.Name, package.Id, !string.IsNullOrWhiteSpace(package.UninstallString)) })
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            metadata, cancellationToken).ConfigureAwait(false);
    }

    internal static void Merge(List<InstalledPackageInfo> packages, InstalledPackageInfo incoming)
    {
        int index = packages.FindIndex(existing => PackageIdentityMatcher.SamePackage(
            existing.Id, existing.Source, incoming.Id, incoming.Source));
        if (index < 0 && !string.IsNullOrWhiteSpace(incoming.UninstallString))
        {
            var sameName = packages.Select((package, candidateIndex) => (package, candidateIndex))
                .Where(candidate => candidate.package.Name.Trim().Equals(incoming.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var managedMatches = sameName.Where(candidate => SourceCategory(candidate.package.Source) != "PC Local").ToArray();
            if (managedMatches.Length == 1)
                index = managedMatches[0].candidateIndex;
            else if (managedMatches.Length == 0 && sameName.Length == 1
                && SourceCategory(sameName[0].package.Source) == "PC Local")
                index = sameName[0].candidateIndex;
        }
        if (index < 0) { packages.Add(incoming); return; }
        var original = packages[index];
        packages[index] = original with
        {
            Version = string.IsNullOrWhiteSpace(original.Version) ? incoming.Version : original.Version,
            IconUrl = string.IsNullOrWhiteSpace(original.IconUrl) ? incoming.IconUrl : original.IconUrl,
            UninstallString = string.IsNullOrWhiteSpace(incoming.UninstallString) ? original.UninstallString : incoming.UninstallString,
            QuietUninstallString = string.IsNullOrWhiteSpace(incoming.QuietUninstallString) ? original.QuietUninstallString : incoming.QuietUninstallString,
            InstallLocation = string.IsNullOrWhiteSpace(incoming.InstallLocation) ? original.InstallLocation : incoming.InstallLocation,
            Source = SourceCategory(original.Source) == "PC Local" && SourceCategory(incoming.Source) != "PC Local" ? incoming.Source : original.Source,
            IsSystemComponent = original.IsSystemComponent || incoming.IsSystemComponent
        };
    }

    public static bool IsOffice(string id, string name)
    {
        string value = id + " " + name;
        if (new[] { "LibreOffice", "OpenOffice", "OnlyOffice", "FreeOffice", "WPS Office", "SoftMaker", "OfficeDeploymentTool", "Office Deployment Tool" }
            .Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase))) return false;
        return new[] { "Microsoft.Office", "Microsoft.365", "Office.Desktop", "ClickToRun", "Microsoft 365", "Microsoft Office", "Office LTSC", "ProPlus" }
            .Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)) || id.StartsWith("O365", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<InstalledPackageInfo>> ListFromComAsync(
        CatalogLookup catalog,
        CancellationToken cancellationToken)
    {
        var manager = WinGetFactoryHelper.CreateResilientPackageManager();
        var reference = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
        reference.InstalledPackageInformationOnly = true;
        var connected = await reference.ConnectAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null)
            throw new InvalidOperationException($"Falha ao conectar ao catálogo de pacotes instalados: {connected.Status}.");

        var options = WinGetFactoryHelper.CreateFindPackagesOptions();

        var result = await connected.PackageCatalog.FindPackagesAsync(options)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != FindPackagesResultStatus.Ok)
            throw new InvalidOperationException($"Falha na consulta do inventário COM: {result.Status}.");
        var packages = new List<InstalledPackageInfo>();
        foreach (var match in IndexedCollectionReader.Read(result.Matches, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = match.CatalogPackage;
            var installed = package.InstalledVersion;
            if (installed is null || string.IsNullOrWhiteSpace(package.Id))
                continue;

            string source = installed.PackageCatalog?.Info?.Name ?? string.Empty;
            var catalogApp = catalog.FindByIdOrName(package.Id, package.Name);
            packages.Add(new InstalledPackageInfo(
                package.Name,
                package.Id,
                installed.Version,
                source,
                catalogApp?.IconUrl ?? string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                IsSystemComponent(package.Name, package.Id)));
        }

        WinGetFactoryHelper.ReportComSuccess();
        WinProvisionLog.Write($"INSTALLED LIST COM count={packages.Count}");
        return packages;
    }

    public static string SourceCategory(string? sourceValue)
    {
        string source = (sourceValue ?? string.Empty).Trim();
        if (source.StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase))
            source = source[(source.IndexOf(':') + 1)..].Trim();
        if (source.Equals("winget", StringComparison.OrdinalIgnoreCase))
            return "WinGet";
        if (source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            || source.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase))
            return "Microsoft Store";
        return "PC Local";
    }

    private static bool ContainsPackage(IEnumerable<InstalledPackageInfo> packages, string id, string name) =>
        packages.Any(package =>
            string.Equals(package.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(package.Name, name, StringComparison.OrdinalIgnoreCase));

    private sealed class CatalogLookup
    {
        private readonly Dictionary<string, (AppEntry Entry, int Index)> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (AppEntry Entry, int Index)> _byName = new(StringComparer.OrdinalIgnoreCase);

        public CatalogLookup(IEnumerable<AppEntry> entries)
        {
            int index = 0;
            foreach (AppEntry entry in entries)
            {
                if (!string.IsNullOrWhiteSpace(entry.Id))
                    _byId.TryAdd(entry.Id, (entry, index));
                if (!string.IsNullOrWhiteSpace(entry.Name))
                    _byName.TryAdd(entry.Name, (entry, index));
                index++;
            }
        }

        public AppEntry? FindById(string? id) =>
            !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out var match) ? match.Entry : null;

        public AppEntry? FindByIdOrName(string? id, string? name)
        {
            (AppEntry Entry, int Index) idMatch = default;
            (AppEntry Entry, int Index) nameMatch = default;
            bool hasId = !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out idMatch);
            bool hasName = !string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name, out nameMatch);
            return hasId && (!hasName || idMatch.Index <= nameMatch.Index) ? idMatch.Entry
                : hasName ? nameMatch.Entry : null;
        }
    }

    private static readonly string[] InboxAppFamilyPrefixes =
    [
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Windows.Photos",
        "Microsoft.WindowsStore", "Microsoft.WindowsCamera", "Microsoft.WindowsSoundRecorder",
        "Microsoft.WindowsAlarms", "Microsoft.WindowsMaps", "Microsoft.WindowsFeedbackHub",
        "Microsoft.Getstarted", "Microsoft.MicrosoftStickyNotes",
        "Microsoft.Windows.CloudExperienceHost", "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost", "Microsoft.Windows.SecHealthUI",
        "Microsoft.SecHealthUI", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        "Microsoft.LockApp", "Microsoft.CredDialogHost", "Microsoft.ECApp",
        "Microsoft.Win32WebViewHost", "Microsoft.XboxGameCallableUI", "Microsoft.XboxIdentityProvider",
        "Microsoft.PPIProjection", "Microsoft.Windows.CapturePicker", "Microsoft.Windows.NarratorQuickStart",
        "Microsoft.Windows.ParentalControls", "Microsoft.Windows.PeopleExperienceHost",
        "Microsoft.Windows.PinningConfirmationDialog", "Microsoft.Windows.PrintDialog",
        "Microsoft.549981C3F5F10", "MicrosoftWindows.Client.CBS", "MicrosoftWindows.Client.Core",
        "MicrosoftWindows.Client.WebExperience",
        // Adicionados: Edge, Terminal, e outros apps do sistema comuns
        "Microsoft.MicrosoftEdge", "Microsoft.Edge", "Microsoft.WindowsTerminal",
        "Microsoft.WindowsTerminalPreview", "Microsoft.Paint", "Microsoft.WindowsPaint",
        "Microsoft.ScreenSketch", "Microsoft.WindowsAppRuntime", "Microsoft.VCLibs",
        "Microsoft.UI.Xaml", "Microsoft.NET", "Microsoft.WindowsAppRuntime",
        "Microsoft.Windows.CallingShellApp", "Microsoft.Windows.ConnectNow",
        "Microsoft.Windows.People", "Microsoft.Windows.ShareTarget",
        "Microsoft.Windows.SecureAssessmentBrowser", "Microsoft.Windows.Spotlight",
        "Microsoft.WindowsStore", "Microsoft.XboxApp", "Microsoft.YourPhone"
    ];

    // PackageFamilyName clássico: "Publicador.NomeDoApp_" + 13 caracteres em
    // base32 (0-9, a-h, j-k, m-n, p-t, v-z — sem i/l/o/u) identificando o publisher.
    // Nenhum PackageIdentifier do catálogo winget tem essa forma; só aparece quando
    // o catálogo COM devolve o nome de família de um pacote MSIX/AppX diretamente.
    private static readonly Regex PackageFamilyNamePattern =
        new(@"^[\w.]+_[0-9a-hj-km-np-tv-z]{13}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="hasArpEntry">
    /// True quando o pacote tem uma entrada clássica de desinstalação (Painel de
    /// Programas/registro) — a forma "de verdade" de gerenciar um app no Windows.
    /// </param>
    private static bool IsSystemComponent(string name, string id, bool hasArpEntry = true)
    {
        if (id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase)) id = id[5..];
        string value = $"{name} {id}".ToLowerInvariant();
        if (SystemTerms.Any(term => value.Contains(term, StringComparison.Ordinal)))
            return true;

        if (InboxAppFamilyPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Filtro específico por nome para apps do sistema comuns
        var systemAppNames = new[]
        {
            "bloco de notas", "windows notepad",
            "calculadora", "windows calculator",
            "microsoft edge", "windows terminal",
            "windows defender", "segurança do windows", "windows security",
            "windows firewall", "microsoft store", "windows store",
            "windows camera", "gravador de som", "windows sound recorder",
            "notas autoadesivas", "microsoft sticky notes"
        };

        if (systemAppNames.Any(name => value.Contains(name, StringComparison.Ordinal)))
            return true;

        // Componente da própria Microsoft sem nenhuma entrada clássica de
        // desinstalação (sem ícone, sem pasta de instalação navegável) — sobra do
        // catálogo COM completo (host interno, framework não listado acima etc.),
        // não um app que o usuário reconheceria ou instalou de propósito.
        if (!hasArpEntry
            && id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            && PackageFamilyNamePattern.IsMatch(id))
            return true;

        return false;
    }
}
