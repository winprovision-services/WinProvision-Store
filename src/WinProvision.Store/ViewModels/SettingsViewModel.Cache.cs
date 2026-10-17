using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    public partial string CacheSharedDirectory { get; set; } = "";
    [ObservableProperty]
    public partial string CacheMaximumMegabytes { get; set; } = "512";
    [ObservableProperty]
    public partial string CacheRevalidationHours { get; set; } = "6";
    [ObservableProperty]
    public partial string CacheUsageSummary { get; private set; } = "Consulte o espaço usado ou libere apenas arquivos antigos e excedentes.";

    private AsyncRelayCommand? _pickCacheDirectoryCommand;
    private RelayCommand? _saveCacheSettingsCommand;
    private AsyncRelayCommand? _cacheUsageCommand;
    private AsyncRelayCommand? _pruneCacheCommand;
    public IAsyncRelayCommand PickCacheDirectoryCommand => _pickCacheDirectoryCommand ??= new AsyncRelayCommand(PickCacheDirectoryAsync);
    public IRelayCommand SaveCacheSettingsCommand => _saveCacheSettingsCommand ??= new RelayCommand(SaveCacheSettings);
    public IAsyncRelayCommand CacheUsageCommand => _cacheUsageCommand ??= new AsyncRelayCommand(RefreshCacheUsageAsync);
    public IAsyncRelayCommand PruneCacheCommand => _pruneCacheCommand ??= new AsyncRelayCommand(PruneCacheAsync);

    private void LoadCacheSettings()
    {
        var settings = CatalogCacheSettingsStore.Load();
        CacheSharedDirectory = settings.SharedDirectory ?? "";
        CacheMaximumMegabytes = settings.MaximumMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CacheRevalidationHours = settings.RevalidationHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private async Task PickCacheDirectoryAsync()
    {
        try { if (await _presentation.PickFolderAsync() is { } folder) CacheSharedDirectory = folder; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.CacheDirectory"); StatusText = "Não foi possível selecionar a pasta."; }
    }
    private void SaveCacheSettings()
    {
        try
        {
            var settings = new CatalogCacheSettings(string.IsNullOrWhiteSpace(CacheSharedDirectory) ? null : CacheSharedDirectory.Trim(),
                int.Parse(CacheMaximumMegabytes, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(CacheRevalidationHours, System.Globalization.CultureInfo.InvariantCulture));
            CatalogCacheSettingsStore.Save(settings);
            StatusText = "Cache configurado. Reinicie o aplicativo para aplicar a nova pasta, limite e prazo. A CLI usará a configuração na próxima execução.";
        }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.SaveCache"); StatusText = "Não foi possível salvar. Confira a pasta, o limite e o prazo do cache."; }
    }
    private async Task RefreshCacheUsageAsync(CancellationToken token)
    {
        try
        {
            var usage = await _cache.GetCatalogUsageAsync(token);
            string last = usage.LastCleanupUtc?.ToLocalTime().ToString("g") ?? "ainda não executada";
            CacheUsageSummary = $"Catálogo local: {usage.Bytes / (1024d * 1024):N1} MB em {usage.Files:N0} arquivos. Última limpeza: {last}.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.CacheUsage"); CacheUsageSummary = "Não foi possível consultar o uso do cache."; }
    }
    private async Task PruneCacheAsync(CancellationToken token)
    {
        try
        {
            await _cache.MaintainAsync(token);
            await RefreshCacheUsageAsync(token);
            StatusText = "Arquivos antigos e excedentes removidos. O conteúdo recente foi preservado conforme o limite configurado.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Limpeza cancelada."; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.PruneCache"); StatusText = "Não foi possível concluir a limpeza."; }
    }
}
