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
    private readonly AppLogSettingsStore _appLogSettings;
    private readonly IAppLog _appLog;
    private readonly IClipboardService? _settingsClipboard;
    private readonly AppUpdateWorkflow _updateWorkflow = new();
    private AppUpdateCheckResult? _pendingUpdate;
    private string? _downloadedUpdateInstallerPath;
    private bool _hasCheckedForUpdates;
    public Task EnsureUpdateCheckAsync() => _hasCheckedForUpdates ? Task.CompletedTask : CheckUpdateCommand.ExecuteAsync(null);
    public string VersionText => $"Versão {AppUpdateService.GetCurrentVersion().ToString(3)}";
    public bool LaunchAtStartup => _preferences.LaunchAtStartup;
    public string LogDirectoryPath => _appLog.LogDirectory;
    public string LogMinimumLevelName
    {
        get => _appLogSettings.Current.MinimumLevel switch
        {
            AppLogLevel.Trace or AppLogLevel.Debug => nameof(AppLogLevel.Trace),
            AppLogLevel.Warning => nameof(AppLogLevel.Warning),
            AppLogLevel.Error => nameof(AppLogLevel.Error),
            _ => nameof(AppLogLevel.Info)
        };
        set
        {
            if (Enum.TryParse(value, out AppLogLevel level) && level != _appLogSettings.Current.MinimumLevel)
                UpdateAppLogSettings(current => current with { MinimumLevel = level });
        }
    }
    public double LogRetentionDays
    {
        get => _appLogSettings.Current.RetentionDays;
        set
        {
            int days = (int)Math.Clamp(Math.Round(value), 1, 3650);
            if (days != _appLogSettings.Current.RetentionDays)
                UpdateAppLogSettings(current => current with { RetentionDays = days });
        }
    }
    public double LogMaximumMegabytes
    {
        get => _appLogSettings.Current.MaximumTotalMegabytes;
        set
        {
            int megabytes = (int)Math.Clamp(Math.Round(value), 1, 10_240);
            if (megabytes != _appLogSettings.Current.MaximumTotalMegabytes)
                UpdateAppLogSettings(current => current with { MaximumTotalMegabytes = megabytes });
        }
    }
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
    public string SelectedWinGetEngineName
    {
        get => _installation.PreferredWinGetEngine.ToString();
        set
        {
            if (Enum.TryParse<WinGetEnginePreference>(value, out var engine) && engine != _installation.PreferredWinGetEngine)
                Run(() => _installation.SetPreferredWinGetEngine(engine), "Mecanismo do WinGet salvo.");
            OnPropertyChanged();
            OnPropertyChanged(nameof(WinGetEngineDescription));
        }
    }
    public string WinGetEngineDescription => _installation.PreferredWinGetEngine switch
    {
        WinGetEnginePreference.ComApi => "Usa somente a API COM do WinGet. Falhas são exibidas sem trocar de rota.",
        WinGetEnginePreference.Automatic => "Prefere a API COM; usa o WinGet CLI após erro, timeout ou indisponibilidade antes do despacho.",
        _ => "Usa somente o executável winget.exe. Falhas são exibidas sem trocar de rota."
    };
    [ObservableProperty] public partial string UpdateSubtitle { get; set; } = "Ainda não verificado.";
    public AppUpdateWorkflowState UpdateState => _updateWorkflow.State;
    public bool IsUpdateBusy => UpdateState is AppUpdateWorkflowState.Checking
        or AppUpdateWorkflowState.Downloading or AppUpdateWorkflowState.Applying;
    public bool IsDownloadingUpdate => UpdateState == AppUpdateWorkflowState.Downloading;
    public bool IsCheckingUpdate => UpdateState == AppUpdateWorkflowState.Checking;
    public bool HasPendingUpdate => UpdateState is AppUpdateWorkflowState.UpdateAvailable
        or AppUpdateWorkflowState.Downloading or AppUpdateWorkflowState.ReadyToApply
        or AppUpdateWorkflowState.Applying;
    public bool HasCancelableUpdateOperation => IsCheckingUpdate || IsDownloadingUpdate;
    public string CancelUpdateText => IsDownloadingUpdate
        ? _elevationCatalog.ResolveText("general.update.cancelDownload.button")
        : _elevationCatalog.ResolveText("general.update.cancelCheck.button");
    [ObservableProperty] public partial double UpdateProgress { get; private set; }
    public bool CanCheckUpdate => UpdateState is AppUpdateWorkflowState.ReadyToCheck
        or AppUpdateWorkflowState.UpdateAvailable;
    public bool CanDownloadUpdate => UpdateState == AppUpdateWorkflowState.UpdateAvailable && _updater.CanSelfUpdate;
    public bool CanApplyUpdate => UpdateState == AppUpdateWorkflowState.ReadyToApply
        && !string.IsNullOrWhiteSpace(_downloadedUpdateInstallerPath);
    public event EventHandler? RestartRequested;
    partial void OnUpdateProgressChanged(double value) => RefreshGeneralUpdateAction();

    private async Task CheckUpdateAsync(CancellationToken cancellationToken)
    {
        _hasCheckedForUpdates = true;
        _updateWorkflow.BeginCheck();
        NotifyUpdateState();
        _pendingUpdate = null;
        _downloadedUpdateInstallerPath = null;
        try
        {
            UpdateSubtitle = "Verificando…";
            NotifyUpdateState();
            var result = await _updater.CheckForUpdateAsync(
                cancellationToken, _preferences.IncludePrereleaseAppUpdates);
            cancellationToken.ThrowIfCancellationRequested();
            try { _preferences.MarkAppUpdateCheck(DateTimeOffset.UtcNow); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WinProvisionLog.Warning(exception, "Settings.UpdateCheck.SaveTimestamp");
            }

            if (!result.Success)
            {
                _updateWorkflow.CompleteCheck(updateAvailable: false);
                UpdateSubtitle = "Sem conexão ou não foi possível consultar as versões.";
                NotifyUpdateState();
                return;
            }

            if (!result.UpdateAvailable)
            {
                _updateWorkflow.CompleteCheck(updateAvailable: false);
                UpdateSubtitle = "Atualizado";
                NotifyUpdateState();
                return;
            }

            _pendingUpdate = result;
            _updateWorkflow.CompleteCheck(updateAvailable: true);
            UpdateSubtitle = result.LatestVersion is not null
                ? $"Nova versão {result.LatestVersion.ToString(3)} disponível"
                : "Novo build nightly disponível";
            NotifyUpdateState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _updateWorkflow.CancelCheck();
            UpdateSubtitle = "Verificação de atualizações cancelada.";
            NotifyUpdateState();
        }
        catch (Exception ex)
        {
            WinProvisionLog.Error(ex, "Settings.CheckUpdate");
            _updateWorkflow.CompleteCheck(updateAvailable: false);
            UpdateSubtitle = "Não foi possível verificar atualizações.";
            NotifyUpdateState();
        }
    }

    private async Task DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        if (_pendingUpdate is not { Success: true, UpdateAvailable: true } update || !_updater.CanSelfUpdate)
            return;

        _updateWorkflow.BeginDownload();
        NotifyUpdateState();
        UpdateProgress = 0;
        UpdateSubtitle = "Baixando a atualização WinUI...";
        string? setupPath = null;
        var progress = new Progress<double>(fraction => UpdateProgress = Math.Clamp(fraction, 0, 1));

        try
        {
            setupPath = await _updater.DownloadInstallerAsync(update, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _downloadedUpdateInstallerPath = setupPath;
            setupPath = null;
            _updateWorkflow.CompleteDownload();
            UpdateSubtitle = "Download verificado. Confirme para fechar e aplicar a atualização.";
            NotifyUpdateState();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _updateWorkflow.CancelDownload();
            UpdateSubtitle = "Download da atualização cancelado.";
            NotifyUpdateState();
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "Settings.DownloadUpdate");
            _updateWorkflow.FailDownload();
            UpdateSubtitle = "Falha ao baixar e preparar a atualização.";
            NotifyUpdateState();
        }
        finally
        {
            if (setupPath is not null)
                TryDeleteUpdateInstaller(setupPath);
            UpdateProgress = 0;
            NotifyUpdateState();
        }
    }

    private async Task ApplyUpdateAsync(CancellationToken cancellationToken)
    {
        string? setupPath = _downloadedUpdateInstallerPath;
        if (setupPath is null)
            return;

        _updateWorkflow.BeginApply();
        NotifyUpdateState();
        try
        {
            if (!await _presentation.ConfirmAsync("Aplicar atualização?",
                    "O download foi verificado. O WinProvision Store será fechado; o instalador externo atualizará o aplicativo e o abrirá novamente.",
                    "Fechar e atualizar", "Agora não"))
            {
                _updateWorkflow.CancelApply();
                UpdateSubtitle = "Atualização não aplicada. Você pode confirmar novamente quando estiver pronto.";
                NotifyUpdateState();
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            UpdateSubtitle = "Preparando a atualização e a autorização necessária...";
            await AppUpdateService.ScheduleSilentInstallAndRestartAsync(setupPath, "WinProvision.Store.exe", cancellationToken);
            _downloadedUpdateInstallerPath = null;
            _updateWorkflow.CompleteApply();
            UpdateSubtitle = "Atualização preparada. O aplicativo será fechado e reiniciado.";
            NotifyUpdateState();
            await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _updateWorkflow.CancelApply();
            UpdateSubtitle = "Aplicação da atualização cancelada.";
            NotifyUpdateState();
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "Settings.ApplyUpdate");
            if (_downloadedUpdateInstallerPath is { } failedPath)
                TryDeleteUpdateInstaller(failedPath);
            _downloadedUpdateInstallerPath = null;
            _updateWorkflow.FailApply();
            UpdateSubtitle = "Falha ao preparar a atualização. Verifique novamente para tentar outra vez.";
            NotifyUpdateState();
        }
    }

    private void CancelUpdate()
    {
        switch (UpdateState)
        {
            case AppUpdateWorkflowState.Checking:
                CheckUpdateCommand.Cancel();
                break;
            case AppUpdateWorkflowState.Downloading:
                DownloadUpdateCommand.Cancel();
                break;
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
            if (Uri.TryCreate(_pendingUpdate?.ReleaseUrl ?? _updater.ReleasePageUrl, UriKind.Absolute, out var uri)
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
    private async Task ResetPreferencesAsync()
    {
        bool confirmed = await _presentation.ConfirmAsync(
            "Restaurar padrões?",
            "Serão restaurados o tema, o plano de fundo, as preferências visuais de pacotes, a página inicial, a inicialização do Windows e os métodos de instalação e atualização. Preferências de notificações, manutenção agendada, gerenciadores de pacotes, configurações portáteis e dados de conta não serão alterados.",
            "Restaurar padrões");
        if (!confirmed)
            return;

        Run(() => { ApplyTheme(AppThemePreference.System); _preferences.SetBackdropPreference(WindowBackdropCatalog.Default.Document.Default); SelectedBackdropOption = BackdropOptions.Single(option => option.Id == WindowBackdropCatalog.Default.Document.Default); ResetPackageVisualPreferences(); SelectedStartupPageOption = StartupPageOptions.Single(option => option.Page == ApplicationStartupPage.Discover); _preferences.SetLaunchAtStartup(false); _installation.SetPreferredWinGetEngine(WinGetEnginePreference.WingetCli); }, "Configurações restauradas para os padrões originais.");
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
        string path = _appLog.LogDirectory;
        Directory.CreateDirectory(path);
        _desktop.OpenFolder(path);
    }, "Pasta de logs aberta.");

    private void CopyLogsPath()
    {
        StatusText = _settingsClipboard?.TrySetText(_appLog.LogDirectory) == true
            ? "Caminho da pasta de logs copiado."
            : "Não foi possível copiar o caminho da pasta de logs.";
    }

    private async Task ExportLogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? path = await _presentation.PickSaveAsync("WinProvision-logs.zip", ".zip");
            cancellationToken.ThrowIfCancellationRequested();
            if (path is null)
                return;
            await AppLogExportService.ExportRedactedZipAsync(_appLog.LogDirectory, path, cancellationToken);
            StatusText = "Logs exportados em um arquivo ZIP com redação.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Exportação dos logs cancelada.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "Settings.ExportLogs");
            StatusText = "Não foi possível exportar os logs.";
        }
    }

    private async Task CleanOldLogsAsync()
    {
        try
        {
            await _appLog.CleanupAsync();
            StatusText = "Logs fora do prazo ou do limite de armazenamento removidos.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "Settings.CleanLogs");
            StatusText = "Não foi possível limpar os logs antigos.";
        }
    }

    private void UpdateAppLogSettings(Func<AppLogSettings, AppLogSettings> update)
    {
        try
        {
            _appLogSettings.Save(update(_appLogSettings.Current));
            _appLog.MinimumLevel = _appLogSettings.Current.MinimumLevel;
            OnPropertyChanged(nameof(LogMinimumLevelName));
            OnPropertyChanged(nameof(LogRetentionDays));
            OnPropertyChanged(nameof(LogMaximumMegabytes));
            StatusText = "Preferências dos registros salvas.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "Settings.SaveLogPreferences");
            StatusText = "Não foi possível salvar as preferências dos registros.";
            OnPropertyChanged(nameof(LogMinimumLevelName));
            OnPropertyChanged(nameof(LogRetentionDays));
            OnPropertyChanged(nameof(LogMaximumMegabytes));
        }
    }
    private void Run(Action action, string status)
    {
        try { action(); StatusText = status; }
        catch (Exception ex) { WinProvisionLog.Error(ex, "Settings.Preferences"); StatusText = "Não foi possível aplicar a configuração."; }
    }
    private void NotifyPreferences() { OnPropertyChanged(nameof(LaunchAtStartup)); OnPropertyChanged(nameof(SelectedWinGetEngineName)); OnPropertyChanged(nameof(WinGetEngineDescription)); }
    public async Task ShutdownAsync()
    {
        var commands = new IAsyncRelayCommand[] { CheckUpdateCommand, DownloadUpdateCommand, ApplyUpdateCommand, ResetPreferencesCommand, ClearCacheCommand, RefreshCatalogCommand, OpenReleaseCommand, ExportLogsCommand, CleanOldLogsCommand, AuthorizeElevationCommand, RefreshElevatorCommand, RevokeElevationCommand, MeasureCacheCommand, ReleaseImageMemoryCommand, ClearImagesCacheCommand, ClearCatalogCacheCommand, ClearInstallerCacheCommand, StartLanServerCommand, StopLanServerCommand, TestLanConnectionCommand, SyncLanFolderCommand, InstallLanPackagesCommand };
        foreach (var command in commands) command.Cancel();
        foreach (var command in commands) if (command.ExecutionTask is { } task) await task;
        if (_lanProvisioning is not null)
        {
            try { await _lanProvisioning.StopServerAsync(); }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "ShutdownLanServer"); }
        }
    }

    private void NotifyUpdateState()
    {
        OnPropertyChanged(nameof(UpdateState));
        OnPropertyChanged(nameof(IsUpdateBusy));
        OnPropertyChanged(nameof(IsDownloadingUpdate));
        OnPropertyChanged(nameof(IsCheckingUpdate));
        OnPropertyChanged(nameof(HasPendingUpdate));
        OnPropertyChanged(nameof(HasCancelableUpdateOperation));
        OnPropertyChanged(nameof(CancelUpdateText));
        OnPropertyChanged(nameof(CanCheckUpdate));
        OnPropertyChanged(nameof(CanDownloadUpdate));
        OnPropertyChanged(nameof(CanApplyUpdate));
        CheckUpdateCommand?.NotifyCanExecuteChanged();
        DownloadUpdateCommand?.NotifyCanExecuteChanged();
        ApplyUpdateCommand?.NotifyCanExecuteChanged();
        CancelUpdateCommand?.NotifyCanExecuteChanged();
        RefreshGeneralUpdateAction();
    }
}
