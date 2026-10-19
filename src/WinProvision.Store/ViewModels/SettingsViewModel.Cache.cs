using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly ICacheSettingsService _cacheSettings;
    [ObservableProperty] public partial string ImagesCacheUsage { get; private set; } = "Ainda não medido.";
    [ObservableProperty] public partial string CatalogCacheUsage { get; private set; } = "Ainda não medido.";
    [ObservableProperty] public partial string InstallerCacheUsage { get; private set; } = "Ainda não medido.";
    [ObservableProperty] public partial string ImagesMemoryUsage { get; private set; } = "Memória gerenciada automaticamente.";
    [ObservableProperty] public partial string ImageCacheLimit { get; set; } = "512";
    [ObservableProperty] public partial string ImageCacheDays { get; set; } = "14";
    [ObservableProperty] public partial bool IsCacheBusy { get; private set; }
    public bool CanEditCache => !IsCacheBusy;
    public IAsyncRelayCommand ReleaseImageMemoryCommand => field ??= new AsyncRelayCommand(ReleaseImageMemoryAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand MeasureCacheCommand => field ??= new AsyncRelayCommand(MeasureCacheAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand ClearImagesCacheCommand => field ??= new AsyncRelayCommand(ClearImagesCacheAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand ClearCatalogCacheCommand => field ??= new AsyncRelayCommand(ClearCatalogCacheAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand ClearInstallerCacheCommand => field ??= new AsyncRelayCommand(ClearInstallerCacheAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand SaveImageCacheOptionsCommand => field ??= new AsyncRelayCommand(SaveImageCacheOptionsAsync, () => !IsCacheBusy);
    partial void OnIsCacheBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditCache));
        ReleaseImageMemoryCommand.NotifyCanExecuteChanged();
        MeasureCacheCommand.NotifyCanExecuteChanged(); ClearImagesCacheCommand.NotifyCanExecuteChanged();
        ClearCatalogCacheCommand.NotifyCanExecuteChanged(); ClearInstallerCacheCommand.NotifyCanExecuteChanged();
        SaveImageCacheOptionsCommand.NotifyCanExecuteChanged();
    }
    public void LoadCacheOptions()
    {
        var options = _cacheSettings.Options;
        ImageCacheLimit = options.MaxDiskMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ImageCacheDays = options.RevalidateAfterDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static string DescribeUsage(CacheUsage usage) => $"{usage.Bytes / 1048576d:N1} MB em {usage.Files:N0} arquivo(s){(usage.Partial ? " — medição parcial" : "")}.";
    private async Task UpdateCacheUsageAsync(CancellationToken token)
    {
        var usage = await _cacheSettings.MeasureAsync(token);
        ImagesCacheUsage = DescribeUsage(usage.Images);
        CatalogCacheUsage = DescribeUsage(usage.Catalog);
        InstallerCacheUsage = DescribeUsage(usage.Installers);
        ImagesMemoryUsage = $"{usage.MemoryImages} imagem(ns) na memória: {usage.MemoryBytes / 1048576d:N1} MB estimados; limite automático de {usage.MemoryBudget / 1048576d:N0} MB.";
    }
    private async Task MeasureCacheAsync(CancellationToken token)
    {
        if (IsCacheBusy) return;
        IsCacheBusy = true;
        try { await UpdateCacheUsageAsync(token); StatusText = "Espaço ocupado pelos caches atualizado."; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.Cache.Measure"); StatusText = "Não foi possível medir todos os caches."; }
        finally { IsCacheBusy = false; }
    }
    private Task ReleaseImageMemoryAsync(CancellationToken token) => ClearCacheCategoryAsync(_cacheSettings.ReleaseMemoryAsync,
        "Cache de imagens liberado da memória. Os arquivos no disco foram preservados.", token);
    private Task ClearImagesCacheAsync(CancellationToken token) => ClearCacheCategoryAsync(_cacheSettings.ClearImagesAsync,
        "Cache de imagens limpo. Imagens em exibição podem permanecer até recarregar a página.", token);
    private Task ClearCatalogCacheAsync(CancellationToken token) => ClearCacheCategoryAsync(_cacheSettings.ClearCatalogAsync,
        "Cache do catálogo e metadados limpo. Imagens e instaladores foram preservados.", token);
    private Task ClearInstallerCacheAsync(CancellationToken token) => ClearCacheCategoryAsync(_cacheSettings.ClearInstallersAsync,
        "Instaladores reutilizáveis removidos. Imagens e catálogo foram preservados.", token);
    private async Task ClearCacheCategoryAsync(Func<CancellationToken, Task> clear, string success, CancellationToken token)
    {
        if (IsCacheBusy) return;
        IsCacheBusy = true;
        try { await clear(token); await UpdateCacheUsageAsync(token); StatusText = success; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { StatusText = "Limpeza cancelada; parte dos arquivos pode ter sido removida."; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.Cache.Clear"); StatusText = "Não foi possível concluir a limpeza. Alguns arquivos podem estar em uso."; }
        finally { IsCacheBusy = false; }
    }
    private async Task SaveImageCacheOptionsAsync()
    {
        if (IsCacheBusy) return;
        if (!int.TryParse(ImageCacheLimit, out int limit) || !int.TryParse(ImageCacheDays, out int days))
        { StatusText = "Selecione um limite e um prazo válidos."; return; }
        IsCacheBusy = true;
        try
        {
            await _cacheSettings.SaveOptionsAsync(new(limit, days));
            StatusText = "Opções de imagens salvas. A limpeza automática ajustará o cache ao limite escolhido.";
        }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.Cache.Save"); LoadCacheOptions(); StatusText = "Não foi possível salvar as opções do cache de imagens."; }
        finally { IsCacheBusy = false; }
    }
}
