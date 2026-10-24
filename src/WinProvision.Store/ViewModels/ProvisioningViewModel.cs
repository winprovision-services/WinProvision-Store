using System.Text.Json;
using System.Collections.ObjectModel;
using Windows.UI;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProvisioningViewModel : ObservableObject
{
    private readonly CollectionsViewModel _collections;
    private readonly IPresentationService _presentation;
    private readonly ProvisioningWallpaperCatalogService _wallpaperCatalog;
    private bool _wallpaperCatalogLoaded;

    [ObservableProperty] public partial string ProfileName { get; set; } = "Perfil de provisionamento";
    [ObservableProperty] public partial string Creator { get; set; } = string.Empty;
    [ObservableProperty] public partial string MachineName { get; set; } = string.Empty;
    [ObservableProperty] public partial string WallpaperFileName { get; private set; } = "Nenhuma imagem selecionada.";
    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.ImageSource? WallpaperPreview { get; private set; }
    [ObservableProperty] public partial bool HasWallpaper { get; private set; }
    [ObservableProperty] public partial bool IsWallpaperPickerExpanded { get; set; }
    [ObservableProperty] public partial bool IsLoadingWallpaperCatalog { get; private set; }
    [ObservableProperty] public partial string WallpaperCatalogStatusText { get; private set; } = "Expanda para carregar os wallpapers disponíveis.";
    public ObservableCollection<ProvisioningWallpaperOption> WallpaperOptions { get; } = [];
    [ObservableProperty] public partial string AccentColor { get; set; } = "#0078D4";
    [ObservableProperty] public partial Color AccentColorValue { get; set; } = Color.FromArgb(255, 0, 120, 212);
    [ObservableProperty] public partial Choice<SystemThemeMode?>? SelectedSystemTheme { get; set; }
    [ObservableProperty] public partial Choice<SystemThemeMode?>? SelectedAppsTheme { get; set; }
    [ObservableProperty] public partial Choice<AccentColorMode?>? SelectedAccentMode { get; set; }
    [ObservableProperty] public partial Choice<TaskbarAlignmentMode?>? SelectedTaskbarAlignment { get; set; }
    [ObservableProperty] public partial Choice<TaskbarSearchBoxMode?>? SelectedTaskbarSearch { get; set; }
    [ObservableProperty] public partial Choice<PowerPlanMode?>? SelectedPowerPlan { get; set; }
    [ObservableProperty] public partial Choice<bool?>? SelectedTaskbarAutoHide { get; set; }
    [ObservableProperty] public partial Choice<bool?>? SelectedShowFileExtensions { get; set; }
    [ObservableProperty] public partial Choice<bool?>? SelectedShowHiddenFiles { get; set; }
    [ObservableProperty] public partial Choice<bool?>? SelectedOpenExplorerToThisPc { get; set; }
    [ObservableProperty] public partial Choice<bool?>? SelectedEnableAutomaticTime { get; set; }
    [ObservableProperty] public partial Choice<bool?>? SelectedEnableAutomaticTimeZone { get; set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "As configurações são opcionais e só serão aplicadas ao executar o provisionamento.";
    [ObservableProperty] public partial PreviewSurface OpenPreviewSurface { get; private set; }
    public bool IsPreviewStartOpen => OpenPreviewSurface == PreviewSurface.Start;
    public bool IsPreviewSearchOpen => OpenPreviewSurface == PreviewSurface.Search;
    public bool IsPreviewQuickSettingsOpen => OpenPreviewSurface == PreviewSurface.QuickSettings;
    public bool IsPreviewNotificationsOpen => OpenPreviewSurface == PreviewSurface.Notifications;
    public bool IsPreviewWidgetsOpen => OpenPreviewSurface == PreviewSurface.Widgets;
    public bool IsCustomAccentSelected => SelectedAccentMode?.Value == AccentColorMode.Personalizado;

    public List<Choice<SystemThemeMode?>> ThemeOptions { get; } =
    [new("Não alterar", null), new("Claro", SystemThemeMode.Claro), new("Escuro", SystemThemeMode.Escuro)];
    public List<Choice<AccentColorMode?>> AccentOptions { get; } =
    [new("Não alterar", null), new("Automática", AccentColorMode.Automatico), new("Personalizada", AccentColorMode.Personalizado)];
    public List<Choice<TaskbarAlignmentMode?>> AlignmentOptions { get; } =
    [new("Não alterar", null), new("Esquerda", TaskbarAlignmentMode.Esquerda), new("Centro", TaskbarAlignmentMode.Centro)];
    public List<Choice<TaskbarSearchBoxMode?>> SearchOptions { get; } =
    [new("Não alterar", null), new("Oculta", TaskbarSearchBoxMode.Oculta), new("Apenas ícone", TaskbarSearchBoxMode.ApenasIcone), new("Caixa completa", TaskbarSearchBoxMode.CaixaCompleta)];
    public List<Choice<PowerPlanMode?>> PowerOptions { get; } =
    [new("Não alterar", null), new("Economia", PowerPlanMode.Economia), new("Equilibrado", PowerPlanMode.Equilibrado), new("Alto desempenho", PowerPlanMode.AltoDesempenho)];
    public List<Choice<bool?>> BooleanOptions { get; } =
    [new("Não alterar", null), new("Sim", true), new("Não", false)];

    public IRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand SelectWallpaperCommand { get; }
    public IAsyncRelayCommand LoadWallpaperOptionsCommand { get; }
    public IAsyncRelayCommand<ProvisioningWallpaperOption> SelectCatalogWallpaperCommand { get; }
    public IRelayCommand ClearWallpaperCommand { get; }
    public IRelayCommand PreviewStartCommand { get; }
    public IRelayCommand PreviewSearchCommand { get; }
    public IRelayCommand PreviewQuickSettingsCommand { get; }
    public IRelayCommand PreviewNotificationsCommand { get; }
    public IRelayCommand PreviewWidgetsCommand { get; }
    public IRelayCommand ClosePreviewSurfaceCommand { get; }

    public ProvisioningViewModel(CollectionsViewModel collections, IPresentationService presentation, ProvisioningWallpaperCatalogService wallpaperCatalog)
    {
        _collections = collections;
        _presentation = presentation;
        _wallpaperCatalog = wallpaperCatalog;
        WallpaperOptions.Add(new ProvisioningWallpaperOption("local", "Escolher imagem local", string.Empty, 0, null, true));
        SaveCommand = new RelayCommand(Save);
        ImportCommand = new AsyncRelayCommand(ImportAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        SelectWallpaperCommand = new AsyncRelayCommand(SelectWallpaperAsync);
        LoadWallpaperOptionsCommand = new AsyncRelayCommand(LoadWallpaperOptionsAsync);
        SelectCatalogWallpaperCommand = new AsyncRelayCommand<ProvisioningWallpaperOption>(SelectCatalogWallpaperAsync);
        ClearWallpaperCommand = new RelayCommand(ClearWallpaper);
        PreviewStartCommand = new RelayCommand(() => TogglePreviewSurface(PreviewSurface.Start));
        PreviewSearchCommand = new RelayCommand(() => TogglePreviewSurface(PreviewSurface.Search));
        PreviewQuickSettingsCommand = new RelayCommand(() => TogglePreviewSurface(PreviewSurface.QuickSettings));
        PreviewNotificationsCommand = new RelayCommand(() => TogglePreviewSurface(PreviewSurface.Notifications));
        PreviewWidgetsCommand = new RelayCommand(() => TogglePreviewSurface(PreviewSurface.Widgets));
        ClosePreviewSurfaceCommand = new RelayCommand(() => OpenPreviewSurface = PreviewSurface.None);
        SelectedSystemTheme = ThemeOptions[0]; SelectedAppsTheme = ThemeOptions[0];
        SelectedAccentMode = AccentOptions[0]; SelectedTaskbarAlignment = AlignmentOptions[0];
        SelectedTaskbarSearch = SearchOptions[0]; SelectedPowerPlan = PowerOptions[0];
        SelectedTaskbarAutoHide = BooleanOptions[0]; SelectedShowFileExtensions = BooleanOptions[0];
        SelectedShowHiddenFiles = BooleanOptions[0]; SelectedOpenExplorerToThisPc = BooleanOptions[0];
        SelectedEnableAutomaticTime = BooleanOptions[0]; SelectedEnableAutomaticTimeZone = BooleanOptions[0];
        Load(_collections.CreateProvisioningProfile().Provisioning);
    }

    private async Task LoadWallpaperOptionsAsync(CancellationToken token)
    {
        if (_wallpaperCatalogLoaded || IsLoadingWallpaperCatalog) return;
        IsLoadingWallpaperCatalog = true;
        WallpaperCatalogStatusText = "Carregando wallpapers...";
        try
        {
            var entries = await _wallpaperCatalog.GetWallpapersAsync(token);
            token.ThrowIfCancellationRequested();
            WallpaperOptions.Clear();
            WallpaperOptions.Add(new ProvisioningWallpaperOption("local", "Escolher imagem local", string.Empty, 0, null, true));
            foreach (var entry in entries)
                WallpaperOptions.Add(new ProvisioningWallpaperOption(entry.Id, entry.FileName, entry.Url, entry.SizeBytes, entry.ETag));
            _wallpaperCatalogLoaded = true;
            SyncSelectedWallpaperOption();
            WallpaperCatalogStatusText = entries.Count == 0
                ? "Nenhum wallpaper está disponível no catálogo."
                : $"{entries.Count} wallpapers disponíveis. Selecione uma imagem para incorporá-la ao perfil.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Provisioning.LoadWallpaperCatalog");
            WallpaperCatalogStatusText = "Não foi possível carregar os wallpapers. Reabra esta seção para tentar novamente.";
        }
        finally { IsLoadingWallpaperCatalog = false; }
    }

    partial void OnIsWallpaperPickerExpandedChanged(bool value)
    {
        if (value)
            LoadWallpaperOptionsCommand.Execute(null);
    }

    private async Task SelectCatalogWallpaperAsync(ProvisioningWallpaperOption? option, CancellationToken token)
    {
        if (option is null) return;
        WallpaperCatalogStatusText = $"Baixando {option.FileName}...";
        try
        {
            var descriptor = new ProvisioningWallpaperDescriptor(option.Id, option.FileName, option.Url, option.SizeBytes, option.ETag);
            byte[] bytes = await _wallpaperCatalog.DownloadWallpaperAsync(descriptor, token);
            token.ThrowIfCancellationRequested();
            using var encodedStream = new MemoryStream(bytes, writable: false);
            using var stream = encodedStream.AsRandomAccessStream();
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(stream).AsTask(token);
            token.ThrowIfCancellationRequested();
            _wallpaperBase64 = Convert.ToBase64String(bytes);
            WallpaperFileName = option.FileName;
            WallpaperPreview = bitmap;
            HasWallpaper = true;
            SyncSelectedWallpaperOption();
            StatusText = $"Imagem {WallpaperFileName} incorporada ao perfil.";
            WallpaperCatalogStatusText = $"{option.FileName} selecionado.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Provisioning.SelectCatalogWallpaper");
            WallpaperCatalogStatusText = $"Não foi possível carregar {option.FileName}. Tente novamente.";
        }
    }

    private void SyncSelectedWallpaperOption()
    {
        foreach (var option in WallpaperOptions)
            option.IsSelected = HasWallpaper && option.FileName.Equals(WallpaperFileName, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnWallpaperFileNameChanged(string value) => SyncSelectedWallpaperOption();
    partial void OnHasWallpaperChanged(bool value) => SyncSelectedWallpaperOption();

    private void TogglePreviewSurface(PreviewSurface surface) =>
        OpenPreviewSurface = OpenPreviewSurface == surface ? PreviewSurface.None : surface;

    partial void OnOpenPreviewSurfaceChanged(PreviewSurface value)
    {
        OnPropertyChanged(nameof(IsPreviewStartOpen));
        OnPropertyChanged(nameof(IsPreviewSearchOpen));
        OnPropertyChanged(nameof(IsPreviewQuickSettingsOpen));
        OnPropertyChanged(nameof(IsPreviewNotificationsOpen));
        OnPropertyChanged(nameof(IsPreviewWidgetsOpen));
    }

    partial void OnSelectedAccentModeChanged(Choice<AccentColorMode?>? value) =>
        OnPropertyChanged(nameof(IsCustomAccentSelected));

    partial void OnAccentColorChanged(string value)
    {
        if (TryParseAccentColor(value, out Color color) && AccentColorValue != color)
            AccentColorValue = color;
    }

    partial void OnAccentColorValueChanged(Color value)
    {
        string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        if (!AccentColor.Equals(hex, StringComparison.OrdinalIgnoreCase))
            AccentColor = hex;
    }

    private static bool TryParseAccentColor(string? value, out Color color)
    {
        color = Color.FromArgb(255, 0, 120, 212);
        return value is { Length: 7 } && value[0] == '#'
            && uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint rgb)
            && AssignColor(rgb, out color);
    }

    private static bool AssignColor(uint rgb, out Color color)
    {
        color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private void Load(ProvisioningManifest? manifest)
    {
        if (manifest is null) return;
        ProfileName = manifest.Name ?? "Perfil de provisionamento";
        Creator = manifest.Creator ?? string.Empty;
        MachineName = manifest.MachineName ?? string.Empty;
        WallpaperFileName = manifest.WallpaperFileName ?? "Nenhuma imagem selecionada.";
        _wallpaperBase64 = manifest.WallpaperImageBase64;
        WallpaperPreview = !string.IsNullOrWhiteSpace(manifest.WallpaperImageBase64)
            ? DecodeWallpaper(manifest.WallpaperImageBase64) : null;
        HasWallpaper = WallpaperPreview is not null;
        AccentColor = manifest.AccentColor ?? "#0078D4";
        Select(ThemeOptions, manifest.SystemTheme ?? manifest.Theme, value => SelectedSystemTheme = value);
        Select(ThemeOptions, manifest.AppsTheme ?? manifest.Theme, value => SelectedAppsTheme = value);
        Select(AccentOptions, manifest.AccentColorMode, value => SelectedAccentMode = value);
        Select(AlignmentOptions, manifest.TaskbarAlignment, value => SelectedTaskbarAlignment = value);
        Select(SearchOptions, manifest.TaskbarSearchBox, value => SelectedTaskbarSearch = value);
        Select(PowerOptions, manifest.PowerPlan, value => SelectedPowerPlan = value);
        Select(BooleanOptions, manifest.TaskbarAutoHide, value => SelectedTaskbarAutoHide = value);
        Select(BooleanOptions, manifest.ShowFileExtensions, value => SelectedShowFileExtensions = value);
        Select(BooleanOptions, manifest.ShowHiddenFiles, value => SelectedShowHiddenFiles = value);
        Select(BooleanOptions, manifest.OpenExplorerToThisPc, value => SelectedOpenExplorerToThisPc = value);
        Select(BooleanOptions, manifest.EnableAutomaticTime, value => SelectedEnableAutomaticTime = value);
        Select(BooleanOptions, manifest.EnableAutomaticTimeZone, value => SelectedEnableAutomaticTimeZone = value);
    }

    private static void Select<T>(IEnumerable<Choice<T>> choices, T? value, Action<Choice<T>> set)
    {
        Choice<T>? choice = choices.FirstOrDefault(candidate => EqualityComparer<T?>.Default.Equals(candidate.Value, value));
        if (choice is not null) set(choice);
    }

    private async Task SelectWallpaperAsync(CancellationToken token)
    {
        try
        {
            string? path = await _presentation.PickImageAsync();
            if (path is null) return;
            await LoadWallpaperFromFileAsync(path, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Provisioning.SelectWallpaper"); StatusText = "Não foi possível carregar essa imagem. Tente outro arquivo JPG ou PNG."; }
    }

    internal async Task LoadWallpaperFromFileAsync(string path, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 25L * 1024 * 1024) { StatusText = "A imagem deve ter no máximo 25 MB."; return; }
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".jpg" or ".jpeg" or ".png")) { StatusText = "Escolha uma imagem JPG ou PNG."; return; }
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            token.ThrowIfCancellationRequested();
            string base64 = Convert.ToBase64String(bytes);
            var preview = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using (var imageStream = File.OpenRead(path))
                await preview.SetSourceAsync(imageStream.AsRandomAccessStream()).AsTask(token);
            _wallpaperBase64 = base64;
            WallpaperFileName = Path.GetFileName(path);
            WallpaperPreview = preview;
            HasWallpaper = true;
            StatusText = $"Imagem {WallpaperFileName} incorporada ao perfil.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Provisioning.SelectWallpaper"); StatusText = "Não foi possível carregar essa imagem. Tente outro arquivo JPG ou PNG."; }
    }

    private string? _wallpaperBase64;

    private static Microsoft.UI.Xaml.Media.ImageSource? DecodeWallpaper(string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            stream.Seek(0);
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            bitmap.SetSource(stream);
            return bitmap;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private void ClearWallpaper()
    {
        _wallpaperBase64 = null;
        WallpaperFileName = "Nenhuma imagem selecionada.";
        WallpaperPreview = null;
        HasWallpaper = false;
        StatusText = "Papel de parede removido do perfil.";
    }

    internal void SetWallpaperForValidation(string fileName, string base64, Microsoft.UI.Xaml.Media.ImageSource preview)
    {
        WallpaperFileName = fileName;
        _wallpaperBase64 = base64;
        WallpaperPreview = preview;
        HasWallpaper = true;
    }

    private ProvisioningManifest CreateManifest() => new()
    {
        Name = ProfileName.Trim(), Creator = string.IsNullOrWhiteSpace(Creator) ? null : Creator.Trim(),
        MachineName = string.IsNullOrWhiteSpace(MachineName) ? null : MachineName.Trim(),
        WallpaperFileName = _wallpaperBase64 is null ? null : WallpaperFileName,
        WallpaperImageBase64 = _wallpaperBase64,
        SystemTheme = SelectedSystemTheme?.Value, AppsTheme = SelectedAppsTheme?.Value, AccentColorMode = SelectedAccentMode?.Value,
        AccentColor = SelectedAccentMode?.Value == AccentColorMode.Personalizado ? AccentColor : null,
        TaskbarAlignment = SelectedTaskbarAlignment?.Value, TaskbarSearchBox = SelectedTaskbarSearch?.Value,
        PowerPlan = SelectedPowerPlan?.Value, TaskbarAutoHide = SelectedTaskbarAutoHide?.Value,
        ShowFileExtensions = SelectedShowFileExtensions?.Value, ShowHiddenFiles = SelectedShowHiddenFiles?.Value,
        OpenExplorerToThisPc = SelectedOpenExplorerToThisPc?.Value,
        EnableAutomaticTime = SelectedEnableAutomaticTime?.Value, EnableAutomaticTimeZone = SelectedEnableAutomaticTimeZone?.Value
    };

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(ProfileName)) { StatusText = "Informe um nome para o perfil."; return; }
        if (SelectedAccentMode?.Value == AccentColorMode.Personalizado
            && (AccentColor.Length != 7 || AccentColor[0] != '#' || !uint.TryParse(AccentColor.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _)))
        { StatusText = "Use uma cor hexadecimal no formato #RRGGBB."; return; }
        if (MachineName.Length > 15 || MachineName.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(MachineName, "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,13}[A-Za-z0-9])?$"))
        { StatusText = "O nome do computador deve ter até 15 letras, números ou hífens."; return; }
        _collections.SaveProvisioning(CreateManifest());
        StatusText = "Perfil salvo. Importe-o na máquina de destino para executar o provisionamento.";
    }

    private async Task ImportAsync(CancellationToken token)
    {
        try
        {
            string? path = await _presentation.PickOpenAsync();
            if (path is null) return;
            token.ThrowIfCancellationRequested();
            string json = await File.ReadAllTextAsync(path, token);
            ProfileBackupSet parsed = ProfileDocumentService.Parse(json, Path.GetFileNameWithoutExtension(path));
            ProvisioningManifest? manifest = parsed.Provisioning ?? parsed.Tabs.FirstOrDefault(tab => tab.Provisioning is not null)?.Provisioning;
            if (manifest is null) throw new InvalidDataException("O JSON selecionado não contém configurações de provisionamento.");
            Load(manifest);
            _collections.SaveProvisioning(manifest);
            StatusText = "Configurações importadas e salvas.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Provisioning.Import"); StatusText = "Não foi possível importar esse perfil JSON."; }
    }

    private async Task ExportAsync(CancellationToken token)
    {
        try
        {
            string? path = await _presentation.PickSaveAsync("perfil-provisionamento.json");
            if (path is null) return;
            token.ThrowIfCancellationRequested();
            var profile = new ProfileManifest { Name = ProfileName.Trim(), Apps = [], Provisioning = CreateManifest() };
            await File.WriteAllTextAsync(path, AotJson.Serialize(profile, WinProvisionJsonOptions.Profile), token);
            StatusText = "Perfil de provisionamento exportado.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Provisioning.Export"); StatusText = "Não foi possível exportar o perfil."; }
    }

    public sealed record Choice<T>(string Label, T Value);
    public enum PreviewSurface { None, Start, Search, QuickSettings, Notifications, Widgets }
}
