using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
namespace WinProvision.Store.ViewModels;
public sealed partial class SettingsViewModel
{
    private readonly ApplicationPreferencesService _preferences;
    private readonly InstallationPreferencesService _installation;
    private readonly IDesktopIntegrationService _desktop;
    private readonly CacheService _cache;
    private readonly AppUpdateService _updater;
    private readonly IPresentationService _presentation;
    private readonly bool _isInstalledFromSetup;
    private AppUpdateCheckResult? _pendingUpdate;
    private bool _hasCheckedForUpdates;
    public Task EnsureUpdateCheckAsync() => _hasCheckedForUpdates ? Task.CompletedTask : CheckUpdateCommand.ExecuteAsync(null);
    public string VersionText => $"Versão {AppUpdateService.GetCurrentVersion().ToString(3)}";
    public bool LaunchAtStartup => _preferences.LaunchAtStartup;
    public bool UseViperOfficeDownload
    {
        get => _preferences.UseViperOfficeDownload;
        set
        {
            if (value == _preferences.UseViperOfficeDownload) return;
            Run(() => _preferences.SetUseViperOfficeDownload(value), "Mecanismo de download offline do Office salvo.");
            OnPropertyChanged();
        }
    }
    public string SelectedMethodName
    {
        get => _installation.PreferredMethod.ToString();
        set
        {
            if (Enum.TryParse<PackageInstallMethod>(value, out var method) && method != _installation.PreferredMethod)
                Run(() => _installation.SetPreferredMethod(method), "Método de instalação salvo.");
            OnPropertyChanged(); OnPropertyChanged(nameof(InstallMethodDescription));
        }
    }
    public string InstallMethodDescription => _installation.PreferredMethod switch
    {
        PackageInstallMethod.ComApi => "Usa somente o WinGet COM.",
        PackageInstallMethod.WinProvisionApi => "Usa somente a WinProvision API.",
        PackageInstallMethod.WingetCli => "Usa somente o WinGet CLI.",
        _ => "Seleciona a rota disponível e usa fallback quando necessário."
    };
    public string SelectedUpdateMethodName
    {
        get => _installation.PreferredUpdateMethod.ToString();
        set
        {
            if (Enum.TryParse<PackageUpdateMethod>(value, out var method)
                && method != _installation.PreferredUpdateMethod)
                Run(() => _installation.SetPreferredUpdateMethod(method), "Método de atualização salvo.");
            OnPropertyChanged(); OnPropertyChanged(nameof(UpdateMethodDescription));
        }
    }
    public string UpdateMethodDescription => _installation.PreferredUpdateMethod switch
    {
        PackageUpdateMethod.ComApi => "Atualiza somente pela API COM do WinGet.",
        PackageUpdateMethod.WinProvisionApi => "Compara os aplicativos instalados com o catálogo WinProvision e atualiza pelo Viper, preservando o escopo e o tipo do instalador.",
        PackageUpdateMethod.WingetCli => "Atualiza somente pela CLI do WinGet.",
        _ => "Tenta COM, depois WinProvision API compatível e WinGet CLI como fallback."
    };
    [ObservableProperty] public partial string UpdateSubtitle { get; set; } = "Verifique se existe uma nova versão.";
    [ObservableProperty] public partial string UpdateAvailableText { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool HasPendingUpdate { get; private set; }
    [ObservableProperty] public partial bool IsUpdateBusy { get; private set; }
    [ObservableProperty] public partial bool IsDownloadingUpdate { get; private set; }
    [ObservableProperty] public partial double UpdateProgress { get; private set; }
    public bool CanCheckUpdate => !IsUpdateBusy;
    public bool CanInstallUpdate => HasPendingUpdate && _isInstalledFromSetup && !IsUpdateBusy;
    public event EventHandler? RestartRequested;

    private async Task CheckUpdateAsync(CancellationToken cancellationToken)
    {
        _hasCheckedForUpdates = true;
        IsUpdateBusy = true;
        HasPendingUpdate = false;
        _pendingUpdate = null;
        UpdateAvailableText = string.Empty;
        try
        {
            UpdateSubtitle = "Procurando atualizações no GitHub...";
            var result = await _updater.CheckForUpdateAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                UpdateSubtitle = result.Error ?? "Não foi possível verificar atualizações.";
                return;
            }

            if (!result.UpdateAvailable)
            {
                UpdateSubtitle = result.LatestVersion is not null
                    ? $"Você já está na versão mais recente ({result.CurrentVersion?.ToString(3)})."
                    : "Seu build já é o mais recente publicado no canal nightly.";
                return;
            }

            _pendingUpdate = result;
            UpdateSubtitle = _isInstalledFromSetup
                ? $"Versão instalada: {result.CurrentVersion?.ToString(3)}"
                : $"Versão atual: {result.CurrentVersion?.ToString(3)}. Esta cópia portátil não pode ser atualizada por instalador; instale o pacote WinUI separadamente.";
            UpdateAvailableText = result.LatestVersion is not null
                ? $"Nova versão disponível: {result.LatestVersion.ToString(3)}"
                : $"Novo build nightly disponível ({result.ReleaseLabel}).";
            HasPendingUpdate = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateSubtitle = "Verificação de atualizações cancelada.";
        }
        catch (Exception ex) { WinProvisionLog.Error(ex, "Settings.CheckUpdate"); UpdateSubtitle = "Não foi possível verificar atualizações."; }
        finally { IsUpdateBusy = false; }
    }

    private async Task InstallUpdateAsync(CancellationToken cancellationToken)
    {
        if (_pendingUpdate is not { Success: true, UpdateAvailable: true } update)
            return;

        IsUpdateBusy = true;
        IsDownloadingUpdate = true;
        UpdateProgress = 0;
        UpdateSubtitle = "Baixando a atualização WinUI...";
        string? setupPath = null;
        var progress = new Progress<double>(fraction => UpdateProgress = Math.Clamp(fraction, 0, 1));

        try
        {
            setupPath = await _updater.DownloadInstallerAsync(update, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            IsDownloadingUpdate = false;
            if (!await _presentation.ConfirmAsync("Aplicar atualização?",
                    "O download foi verificado. O WinProvision Store será fechado; o instalador externo atualizará o aplicativo e o abrirá novamente.",
                    "Fechar e atualizar", "Agora não"))
            {
                UpdateSubtitle = "Atualização não aplicada. Você pode baixar novamente quando estiver pronto.";
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            UpdateSubtitle = "Preparando a atualização e a autorização necessária...";
            await AppUpdateService.ScheduleSilentInstallAndRestartAsync(setupPath, "WinProvision.Store.exe", cancellationToken);
            setupPath = null;
            HasPendingUpdate = false;
            UpdateSubtitle = "Atualização preparada. O aplicativo será fechado e reiniciado.";
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            UpdateSubtitle = "Download da atualização cancelado.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "Settings.InstallUpdate");
            UpdateSubtitle = "Falha ao baixar e preparar a atualização.";
        }
        finally
        {
            if (setupPath is not null)
                TryDeleteUpdateInstaller(setupPath);
            IsDownloadingUpdate = false;
            IsUpdateBusy = false;
        }
    }

    private static void TryDeleteUpdateInstaller(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WinProvisionLog.Warning(exception, "Settings.DeleteUpdateInstaller");
        }
    }
    private void LightTheme() => ApplyTheme(AppThemePreference.Light);
    private async Task OpenReleaseAsync()
    {
        try
        {
            if (Uri.TryCreate(_pendingUpdate?.ReleaseUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com")
                await _desktop.OpenUriAsync(uri);
        }
        catch (Exception ex) { WinProvisionLog.Error(ex, "Settings.OpenRelease"); UpdateSubtitle = "Não foi possível abrir a página da release."; }
    }
    private void DarkTheme() => ApplyTheme(AppThemePreference.Dark);
    private void SystemTheme() => ApplyTheme(AppThemePreference.System);
    private void ApplyTheme(AppThemePreference theme) => SelectedThemeOption = ThemeOptions.Single(o => o.Preference == theme);
    public async void ChangeStartup(bool enabled)
    {
        try
        {
            var result = _backgroundUpdates is null
                ? (true, (string?)null)
                : await _backgroundUpdates.ConfigureApplicationStartupAsync(enabled);
            if (!result.Item1)
            {
                StatusText = result.Item2 ?? "Não foi possível configurar a inicialização elevada.";
                return;
            }
            _preferences.SetLaunchAtStartup(enabled);
            StatusText = enabled ? "Inicialização elevada e minimizada ativada." : "Inicialização automática desativada.";
            OnPropertyChanged(nameof(LaunchAtStartup));
        }
        catch (Exception ex)
        {
            WinProvisionLog.Error(ex, "Settings.ChangeStartup");
            StatusText = "Não foi possível configurar a inicialização automática.";
        }
    }
    private void ResetPreferences()
    {
        Run(() => { ApplyTheme(AppThemePreference.System); SelectedStartupPageOption = StartupPageOptions.Single(option => option.Page == ApplicationStartupPage.Discover); _preferences.SetLaunchAtStartup(false); _installation.SetPreferredMethod(PackageInstallMethod.Automatic); _installation.SetPreferredUpdateMethod(PackageUpdateMethod.Automatic); }, "Configurações restauradas para os padrões originais.");
        NotifyPreferences();
    }
    private async Task ClearCacheAsync()
    {
        try
        {
            await _cache.ClearAsync();
            AsyncImage.ClearCache();
            StatusText = "Caches locais de busca, instalação e imagens limpos.";
        }
        catch (Exception ex) { WinProvisionLog.Error(ex, "Settings.ClearCache"); StatusText = "Não foi possível limpar os caches."; }
    }
    private async Task RefreshCatalogAsync(CancellationToken token)
    {
        StatusText = "Buscando a versão mais recente do catálogo...";
        try
        {
            int count = await _cache.RefreshCatalogAsync(token);
            StatusText = $"Catálogo atualizado: {count:N0} aplicativos. Instaladores e imagens em cache foram preservados.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Atualização do catálogo cancelada."; }
        catch (Exception ex) { WinProvisionLog.Error(ex, "Settings.RefreshCatalog"); StatusText = "Não foi possível atualizar o catálogo. Os dados anteriores foram mantidos."; }
    }
    private void OpenLogs() => Run(() =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore", "logs");
        Directory.CreateDirectory(path); _desktop.OpenFolder(path);
    }, "Pasta de logs aberta.");
    private void Run(Action action, string status)
    {
        try { action(); StatusText = status; }
        catch (Exception ex) { WinProvisionLog.Error(ex, "Settings.Preferences"); StatusText = "Não foi possível aplicar a configuração."; }
    }
    private void NotifyPreferences() { OnPropertyChanged(nameof(LaunchAtStartup)); OnPropertyChanged(nameof(SelectedMethodName)); OnPropertyChanged(nameof(InstallMethodDescription)); OnPropertyChanged(nameof(SelectedUpdateMethodName)); OnPropertyChanged(nameof(UpdateMethodDescription)); }
    public async Task ShutdownAsync()
    {
        var commands = new IAsyncRelayCommand[] { CheckUpdateCommand, InstallUpdateCommand, ClearCacheCommand, RefreshCatalogCommand, OpenReleaseCommand, AuthorizeElevationCommand, RefreshElevatorCommand, RevokeElevationCommand, SaveElevatorOptionsCommand, MeasureCacheCommand, ReleaseImageMemoryCommand, ClearImagesCacheCommand, ClearCatalogCacheCommand, SaveImageCacheOptionsCommand, PickDownloadDirectoryCommand };
        foreach (var command in commands) command.Cancel();
        foreach (var command in commands) if (command.ExecutionTask is { } task) await task;
    }

    partial void OnHasPendingUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInstallUpdate));
        InstallUpdateCommand?.NotifyCanExecuteChanged();
    }

    partial void OnIsUpdateBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckUpdate));
        OnPropertyChanged(nameof(CanInstallUpdate));
        CheckUpdateCommand?.NotifyCanExecuteChanged();
        InstallUpdateCommand?.NotifyCanExecuteChanged();
    }
}
