using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly ICacheSettingsService _cacheSettings;
    private readonly SettingsCatalogDescriptionProviderRegistry _cacheDescriptionProviders = new();
    private static readonly CultureInfo BrazilianCulture = CultureInfo.GetCultureInfo("pt-BR");

    [ObservableProperty] public partial string ImagesCacheUsage { get; private set; } = "Ainda não medido.";
    [ObservableProperty] public partial string CatalogCacheUsage { get; private set; } = "Ainda não medido.";
    [ObservableProperty] public partial string InstallerCacheUsage { get; private set; } = "Ainda não medido.";
    [ObservableProperty] public partial string ImagesMemoryUsage { get; private set; } = "Ainda não medido.";
    [ObservableProperty] public partial string ImageCacheClearNotice { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool IsCacheBusy { get; private set; }
    [ObservableProperty] public partial bool IsCacheMeasuring { get; private set; }

    public bool CanEditCache => !IsCacheBusy;
    public bool HasImageCacheClearNotice => !string.IsNullOrWhiteSpace(ImageCacheClearNotice);
    public IReadOnlyList<SettingsCatalogGroupOption> CacheSettingsGroups { get; private set; } = [];
    public IAsyncRelayCommand ReleaseImageMemoryCommand => field ??= new AsyncRelayCommand(ReleaseImageMemoryAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand MeasureCacheCommand => field ??= new AsyncRelayCommand(MeasureCacheAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand ClearImagesCacheCommand => field ??= new AsyncRelayCommand(ClearImagesCacheAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand ClearCatalogCacheCommand => field ??= new AsyncRelayCommand(ClearCatalogCacheAsync, () => !IsCacheBusy);
    public IAsyncRelayCommand ClearInstallerCacheCommand => field ??= new AsyncRelayCommand(ClearInstallerCacheAsync, () => !IsCacheBusy);

    partial void OnIsCacheBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditCache));
        ReleaseImageMemoryCommand.NotifyCanExecuteChanged();
        MeasureCacheCommand.NotifyCanExecuteChanged();
        ClearImagesCacheCommand.NotifyCanExecuteChanged();
        ClearCatalogCacheCommand.NotifyCanExecuteChanged();
        ClearInstallerCacheCommand.NotifyCanExecuteChanged();
        RefreshCacheSettingsPresentation();
    }

    partial void OnIsCacheMeasuringChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCacheMeasuring));
        RefreshCacheDescriptions();
    }

    partial void OnImageCacheClearNoticeChanged(string value) =>
        OnPropertyChanged(nameof(HasImageCacheClearNotice));

    private void RegisterCacheDescriptionProviders()
    {
        _cacheDescriptionProviders.Register("cache-images-usage", () =>
            IsCacheMeasuring ? "Medindo…" : $"O cache de imagens ocupa {ImagesCacheUsage}");
        _cacheDescriptionProviders.Register("cache-images-memory", () =>
            IsCacheMeasuring ? "Medindo…" : ImagesMemoryUsage);
        _cacheDescriptionProviders.Register("cache-installers-usage", () =>
            IsCacheMeasuring ? "Medindo…" : InstallerCacheUsage);
    }

    public void LoadCacheOptions()
    {
        LoadCacheCatalogSettings();
    }

    private void LoadCacheCatalogSettings()
    {
        CacheSettingsGroups = _elevationCatalog.Catalog.GetGroups("cache")
            .Select(group =>
            {
                ElevationSettingDefinition[] definitions = _elevationCatalog.Catalog.GetSettings(group.Key).ToArray();
                SettingsCatalogSettingOption[] options = definitions.Select(definition =>
                {
                    string staticDescription = _elevationCatalog.ResolveText(definition.DescriptionTextKey);
                    string description = _cacheDescriptionProviders.TryGet(definition.DescriptionProviderId, out string dynamicDescription)
                        ? dynamicDescription
                        : staticDescription;
                    string value = GetCatalogPreferenceProviderValue(definition);
                    return new SettingsCatalogSettingOption(
                        definition,
                        _elevationCatalog.ResolveText(definition.TitleTextKey),
                        description,
                        definition.RiskWarningTextKey is null ? null : _elevationCatalog.ResolveText(definition.RiskWarningTextKey),
                        value,
                        _elevationCatalog.Text,
                        ResolveCatalogAction(definition.ActionKey),
                        secondaryDescription: definition.DescriptionProviderId is null ? null : staticDescription);
                }).ToArray();

                var values = options.ToDictionary(option => option.Key, option => option.Value,
                    StringComparer.OrdinalIgnoreCase);
                foreach (SettingsCatalogSettingOption option in options)
                    option.RefreshPresentation(values, definitions, isBusy: IsCacheBusy);

                return new SettingsCatalogGroupOption(
                    _elevationCatalog.ResolveText(group.TitleTextKey),
                    group.HelpTextKey is null ? null : _elevationCatalog.ResolveText(group.HelpTextKey),
                    group.DetailsTitleTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTitleTextKey),
                    group.DetailsTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTextKey),
                    options,
                    group.Icon);
            })
            .ToArray();
        OnPropertyChanged(nameof(CacheSettingsGroups));
    }

    private string GetCatalogPreferenceProviderValue(ElevationSettingDefinition definition)
    {
        if (definition.PreferenceProviderId is not { } providerId)
            return definition.DefaultTextValue;
        if (!_catalogPreferenceProviders.TryGetValue(providerId, out ISettingsCatalogPreferenceProvider? provider))
            throw new InvalidOperationException($"O provedor de preferências '{providerId}' não foi registrado.");
        return provider.GetValue(definition);
    }

    private void RefreshCacheSettingsPresentation()
    {
        foreach (SettingsCatalogGroupOption group in CacheSettingsGroups)
        {
            var values = group.Settings.ToDictionary(option => option.Key, option => option.Value,
                StringComparer.OrdinalIgnoreCase);
            ElevationSettingDefinition[] definitions = group.Settings.Select(option => option.Definition).ToArray();
            foreach (SettingsCatalogSettingOption option in group.Settings)
                option.RefreshPresentation(values, definitions, isBusy: IsCacheBusy);
        }
    }

    private void RefreshCacheDescriptions()
    {
        foreach (SettingsCatalogSettingOption option in CacheSettingsGroups.SelectMany(group => group.Settings))
            if (_cacheDescriptionProviders.TryGet(option.Definition.DescriptionProviderId, out string description))
                option.SetDescription(description);
    }

    private static string DescribeUsage(CacheUsage usage)
    {
        string size = $"{(usage.Bytes / 1048576d).ToString("N1", BrazilianCulture)} MB";
        string count = usage.Files.ToString("N0", BrazilianCulture);
        string fileWord = usage.Files == 1 ? "arquivo" : "arquivos";
        return $"{size} em {count} {fileWord}{(usage.Partial ? " · medição parcial" : string.Empty)}";
    }

    private static string DescribeMemory(CacheSettingsSnapshot usage) =>
        $"{usage.MemoryImages.ToString("N0", BrazilianCulture)} imagens na memória · limite automático de {(usage.MemoryBudget / 1048576d).ToString("N0", BrazilianCulture)} MB";

    private async Task<CacheSettingsSnapshot> UpdateCacheUsageAsync(CancellationToken token)
    {
        CacheSettingsSnapshot usage = await _cacheSettings.MeasureAsync(token);
        ImagesCacheUsage = DescribeUsage(usage.Images);
        CatalogCacheUsage = DescribeUsage(usage.Catalog);
        InstallerCacheUsage = DescribeUsage(usage.Installers);
        ImagesMemoryUsage = DescribeMemory(usage);
        RefreshCacheDescriptions();
        return usage;
    }

    private async Task MeasureCacheAsync(CancellationToken token)
    {
        if (IsCacheBusy)
            return;

        IsCacheBusy = true;
        try
        {
            await MeasureCacheUsageAsync(token);
            StatusText = "Espaço ocupado pelos caches atualizado.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WinProvisionLog.Warning(exception, "Settings.Cache.Measure");
            ImagesCacheUsage = "Não foi possível medir.";
            CatalogCacheUsage = "Não foi possível medir.";
            InstallerCacheUsage = "Não foi possível medir.";
            ImagesMemoryUsage = "Não foi possível medir.";
            StatusText = "Não foi possível medir todos os caches.";
        }
        finally
        {
            IsCacheBusy = false;
        }
    }

    private async Task<CacheSettingsSnapshot> MeasureCacheUsageAsync(CancellationToken token)
    {
        IsCacheMeasuring = true;
        ImagesCacheUsage = "Medindo…";
        CatalogCacheUsage = "Medindo…";
        InstallerCacheUsage = "Medindo…";
        ImagesMemoryUsage = "Medindo…";
        RefreshCacheDescriptions();
        try
        {
            return await UpdateCacheUsageAsync(token);
        }
        finally
        {
            IsCacheMeasuring = false;
            RefreshCacheDescriptions();
        }
    }

    private Task ReleaseImageMemoryAsync(CancellationToken token) =>
        ClearCacheCategoryAsync(_cacheSettings.ReleaseMemoryAsync,
            "Cache de imagens liberado da memória. Os arquivos no disco foram preservados.", token);

    private Task ClearImagesCacheAsync(CancellationToken token) =>
        ClearCacheCategoryAsync(_cacheSettings.ClearImagesAsync,
            "Cache de imagens limpo.", token, showImageClearNotice: true);

    private Task ClearCatalogCacheAsync(CancellationToken token) =>
        ClearCacheCategoryAsync(_cacheSettings.ClearCatalogAsync,
            "Cache do catálogo e metadados limpo. Imagens e instaladores foram preservados.", token);

    private async Task ClearInstallerCacheAsync(CancellationToken token)
    {
        if (IsCacheBusy)
            return;

        IsCacheBusy = true;
        try
        {
            CacheSettingsSnapshot usage = await MeasureCacheUsageAsync(token);

            string message = _elevationCatalog.ResolveText("cache.installers.confirm.message")
                .Replace("{size}", $"{(usage.Installers.Bytes / 1048576d).ToString("N1", BrazilianCulture)} MB", StringComparison.Ordinal)
                .Replace("{files}", usage.Installers.Files.ToString("N0", BrazilianCulture), StringComparison.Ordinal);
            bool confirmed = await _presentation.ConfirmAsync(
                _elevationCatalog.ResolveText("cache.installers.confirm.title"),
                message,
                _elevationCatalog.ResolveText("cache.installers.confirm.action"));
            if (!confirmed)
                return;

            await _cacheSettings.ClearInstallersAsync(token);
            await MeasureCacheUsageAsync(token);
            StatusText = "Instaladores reutilizáveis removidos.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText = "Limpeza cancelada; parte dos arquivos pode ter sido removida.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WinProvisionLog.Warning(exception, "Settings.Cache.ClearInstallers");
            StatusText = "Não foi possível concluir a limpeza dos instaladores reutilizáveis.";
        }
        finally
        {
            IsCacheBusy = false;
        }
    }

    private async Task ClearCacheCategoryAsync(
        Func<CancellationToken, Task> clear,
        string success,
        CancellationToken token,
        bool showImageClearNotice = false)
    {
        if (IsCacheBusy)
            return;

        IsCacheBusy = true;
        try
        {
            await clear(token);
            await MeasureCacheUsageAsync(token);
            if (showImageClearNotice)
                ImageCacheClearNotice = _elevationCatalog.ResolveText("cache.images.clear.notice");
            StatusText = success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText = "Limpeza cancelada; parte dos arquivos pode ter sido removida.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            WinProvisionLog.Warning(exception, "Settings.Cache.Clear");
            StatusText = "Não foi possível concluir a limpeza. Alguns arquivos podem estar em uso.";
        }
        finally
        {
            IsCacheBusy = false;
        }
    }

    private async Task ApplyCatalogPreferenceProviderAsync(SettingsCatalogSettingOption option, string value)
    {
        ElevationSettingDefinition definition = option.Definition;
        if (definition.PreferenceProviderId is not { } providerId
            || !_catalogPreferenceProviders.TryGetValue(providerId, out ISettingsCatalogPreferenceProvider? provider)
            || !definition.AppliesImmediately
            || string.IsNullOrWhiteSpace(definition.StorageKey))
            return;

        string previous = option.Value;
        if (previous.Equals(value, StringComparison.Ordinal))
            return;

        if (!option.IsCombo || !option.Options.Any(choice => choice.Value.Equals(value, StringComparison.Ordinal)))
        {
            option.Value = previous;
            StatusText = "O valor selecionado não está disponível nesta configuração.";
            return;
        }

        if (IsCacheBusy)
        {
            option.Value = previous;
            return;
        }

        IsCacheBusy = true;
        try
        {
            await provider.SetValueAsync(definition, value, CancellationToken.None);
            option.Value = value;
            option.RefreshBindings();
            _appLog.Write(AppLogLevel.Debug, "App", "Settings.Cache", "Preferência de cache alterada.",
                eventName: "PreferenceChanged");
            StatusText = "Preferência aplicada.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            bool rolledBack = false;
            try
            {
                await provider.SetValueAsync(definition, previous, CancellationToken.None);
                rolledBack = true;
            }
            catch (Exception rollbackException) when (rollbackException is ArgumentException or IOException
                or UnauthorizedAccessException or InvalidOperationException)
            {
                WinProvisionLog.Warning(rollbackException, "Settings.Cache.PreferenceRollback");
            }

            option.Value = previous;
            option.RefreshBindings();
            WinProvisionLog.Warning(exception, "Settings.Cache.PreferenceApply");
            StatusText = rolledBack
                ? "Não foi possível aplicar a opção. O valor anterior foi mantido."
                : "Não foi possível aplicar a opção nem confirmar a restauração do valor anterior.";
            if (_toastMessages is not null)
            {
                await _toastMessages.PublishAsync(new Notification(
                    Guid.NewGuid().ToString("N"),
                    NotificationCategories.BlockingError,
                    NotificationSeverity.Error,
                    "Não foi possível aplicar a configuração",
                    StatusText));
            }
        }
        finally
        {
            IsCacheBusy = false;
        }
    }
}

public sealed class ImageCacheCatalogPreferenceProvider(ICacheSettingsService settings)
    : ISettingsCatalogPreferenceProvider
{
    public string Id => "image-cache";

    public string GetValue(ElevationSettingDefinition definition) =>
        settings.GetPreferenceValue(GetStorageKey(definition));

    public Task SetValueAsync(ElevationSettingDefinition definition, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return settings.SetPreferenceValueAsync(GetStorageKey(definition), value);
    }

    private static string GetStorageKey(ElevationSettingDefinition definition) =>
        definition.StorageKey ?? throw new InvalidOperationException("A preferência não declara uma chave de armazenamento.");
}
