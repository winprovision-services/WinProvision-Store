using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OperationHistoryViewModel : ObservableObject
{
    private readonly HistoryStore _history;
    private readonly IPackageBackend _backend;
    private readonly IPresentationService _presentation;
    private readonly IDesktopIntegrationService _desktop;
    private readonly IClipboardService _clipboard;
    private readonly IAppLog _log;
    private readonly IUiDispatcher _dispatcher;
    private readonly ApplicationPreferencesService _preferences;
    private readonly ManagerTaskLogStore _managerTaskLogs;
    private bool _isDarkTheme;

    public ObservableCollection<OperationHistoryRecord> Records { get; } = [];
    public ObservableCollection<OperationHistoryRowViewModel> Rows { get; } = [];
    public ObservableCollection<HistoryFilterOption> StatusOptions { get; } = [new("Todos os status", "") , new("Bem-sucedida", "succeeded"), new("Falhou", "failed"), new("Cancelada", "cancelled")];
    public ObservableCollection<HistoryFilterOption> TypeOptions { get; } = [new("Todos os tipos", ""), new("Instalar", "install"), new("Desinstalar", "uninstall"), new("Atualizar", "update"), new("Baixar", "download")];
    public ObservableCollection<HistoryFilterOption> ManagerOptions { get; } = [new("Todos os gerenciadores", "")];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SelectedStatus { get; set; } = string.Empty;
    [ObservableProperty] public partial string SelectedType { get; set; } = string.Empty;
    [ObservableProperty] public partial string SelectedManager { get; set; } = string.Empty;
    [ObservableProperty] public partial string LogText { get; private set; } = "Nenhum registro ainda";
    [ObservableProperty] public partial string LogStatus { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool IsLoadingLog { get; private set; }
    [ObservableProperty] public partial string StatusMessage { get; private set; } = string.Empty;
    public bool HasRecords => Records.Count > 0;
    public bool IsEmpty => Records.Count == 0;
    public string EmptyMessage => _history.Query().Count == 0 ? "Nenhuma operação registrada ainda" : "Nenhuma operação corresponde aos filtros.";
    public string LogsFolder => _log.LogDirectory;
    public ManagerTaskLogStore ManagerTaskLogs => _managerTaskLogs;
    public HistoryStore History => _history;
    public IAppLog AppLog => _log;
    public IClipboardService Clipboard => _clipboard;
    public IPresentationService Presentation => _presentation;
    public PackageManagerDescriptor[] EnabledManagers => _preferences.WingetEnabled
        ? _preferences.ChocolateyEnabled
            ? [new PackageManagerDescriptor { Id = "winget", DisplayName = "WinGet", SortOrder = 0 }, new PackageManagerDescriptor { Id = "chocolatey", DisplayName = "Chocolatey", SortOrder = 2 }]
            : [new PackageManagerDescriptor { Id = "winget", DisplayName = "WinGet", SortOrder = 0 }]
        : _preferences.ChocolateyEnabled
            ? [new PackageManagerDescriptor { Id = "chocolatey", DisplayName = "Chocolatey", SortOrder = 2 }]
            : [];

    public OperationHistoryViewModel(HistoryStore history, IPackageBackend backend, IPresentationService presentation,
        IDesktopIntegrationService desktop, IClipboardService clipboard, IAppLog log, IUiDispatcher dispatcher,
        ApplicationPreferencesService preferences, ManagerTaskLogStore managerTaskLogs)
    {
        _history = history; _backend = backend; _presentation = presentation; _desktop = desktop; _clipboard = clipboard; _log = log; _preferences = preferences;
        _managerTaskLogs = managerTaskLogs;
        _dispatcher = dispatcher;
        RefreshManagerOptions();
        _preferences.WingetEnabledChanged += OnManagerEnabledChanged;
        _preferences.ChocolateyEnabledChanged += OnManagerEnabledChanged;
        _history.Changed += History_Changed;
        Records.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasRecords)); OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyMessage)); };
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSelectedStatusChanged(string value) => Refresh();
    partial void OnSelectedTypeChanged(string value) => Refresh();
    partial void OnSelectedManagerChanged(string value) => Refresh();

    public void Refresh()
    {
        Records.Clear();
        Rows.Clear();
        foreach (OperationHistoryRecord record in _history.Query(SearchText, SelectedStatus, SelectedType, SelectedManager))
        {
            Records.Add(record);
            var row = new OperationHistoryRowViewModel(record);
            row.SetDarkTheme(_isDarkTheme);
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(EmptyMessage));
    }

    public async Task ClearHistoryAsync()
    {
        if (_history.Records.Count == 0) return;
        if (!await _presentation.ConfirmAsync("Limpar histórico", "Todos os registros e arquivos de saída das operações serão removidos.", "Limpar")) return;
        _history.Clear();
        StatusMessage = "Histórico limpo.";
    }

    public void Remove(OperationHistoryRecord record) => _history.Remove(record);
    public bool CanRepeat(OperationHistoryRecord record) => record.Snapshot is not null;
    public string RepeatToolTip(OperationHistoryRecord record) => record.Snapshot is null
        ? "Registro antigo sem dados suficientes" : "Tentar novamente";

    public async Task RepeatAsync(OperationHistoryRecord record)
    {
        if (record.Snapshot is not { } snapshot) { StatusMessage = "Registro antigo sem dados suficientes"; return; }
        try
        {
            if (snapshot.Kind == OperationKind.Uninstall
                && !await _presentation.ConfirmAsync("Repetir desinstalação", $"Desinstalar {record.PackageName}?", "Desinstalar")) return;
            await OperationReplayService.ReplayAsync(_backend, snapshot);
        }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "OperationHistory.Repeat"); StatusMessage = $"Não foi possível repetir a operação: {exception.Message}"; }
    }

    public async Task UndoAsync(OperationHistoryRecord record)
    {
        if (record.Status != OperationHistoryStatus.Succeeded) return;
        try
        {
            switch (record.Type)
            {
                case OperationHistoryType.Install:
                    if (!await _presentation.ConfirmAsync("Desfazer instalação", $"Desinstalar {record.PackageName}?", "Desinstalar")) return;
                    await _backend.UninstallAsync(CreateApp(record));
                    break;
                case OperationHistoryType.Uninstall:
                    AppEntry app = CreateApp(record);
                    app.Version = record.VersionFrom ?? string.Empty;
                    await _backend.InstallAsync(app, new PackageInstallOptions(Version: app.Version));
                    break;
                case OperationHistoryType.Update:
                    if (string.IsNullOrWhiteSpace(record.VersionFrom)) { StatusMessage = "A versão anterior não foi registrada; não é possível desfazer."; return; }
                    if (!await _presentation.ConfirmAsync("Desfazer atualização", $"Reverter {record.PackageName} para {record.VersionFrom}?", "Reverter")) return;
                    await _backend.UpdateAsync(new UpgradablePackage { Id = record.PackageId ?? string.Empty, Name = record.PackageName ?? string.Empty,
                        Source = GetPackageSource(record), CurrentVersion = record.VersionTo ?? string.Empty, AvailableVersion = record.VersionFrom });
                    break;
            }
        }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "OperationHistory.Undo"); StatusMessage = $"Não foi possível desfazer a operação: {exception.Message}"; }
    }

    public async Task LoadLogAsync()
    {
        IsLoadingLog = true;
        try
        {
            OperationHistoryRecord[] records = _history.Records.ToArray();
            string text = await Task.Run(() => FormatOperationLogs(records));
            LogText = string.IsNullOrWhiteSpace(text) ? "Nenhum registro ainda" : text;
            LogStatus = string.Empty;
        }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "OperationHistory.LoadLog"); LogText = $"Não foi possível ler o registro: {exception.Message}"; LogStatus = string.Empty; }
        finally { IsLoadingLog = false; }
    }

    public Task<string> BuildOperationLogForViewerAsync() => Task.Run(() => FormatOperationLogs(_history.Records));
    public Task<string> BuildOperationLogForExportAsync() => Task.Run(() => FormatOperationLogs(_history.Records, int.MaxValue, long.MaxValue));

    public bool CopyLog() => _clipboard.TrySetText(LogText);
    public async Task<bool> ExportLogAsync()
    {
        try
        {
            string? path = await _presentation.PickSaveAsync("Registro-de-operacoes.txt", ".txt");
            if (path is null) return false;
            OperationHistoryRecord[] records = _history.Records.ToArray();
            string text = await Task.Run(() => FormatOperationLogs(records, int.MaxValue, long.MaxValue));
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { WinProvisionLog.Warning(exception, "OperationHistory.ExportLog"); return false; }
    }

    private string FormatOperationLogs(IReadOnlyList<OperationHistoryRecord> records, int maximumBlocks = 200, long maximumCharacters = 2 * 1024 * 1024)
    {
        var blocks = new List<string>(Math.Min(records.Count, maximumBlocks));
        long totalCharacters = 0;
        foreach (OperationHistoryRecord record in records.Take(maximumBlocks))
        {
            string output = _history.ReadOutput(record);
            string block = BuildOperationBlock(record, output);
            if (blocks.Count > 0 && totalCharacters + block.Length > maximumCharacters) break;
            blocks.Add(block);
            totalCharacters += block.Length + Environment.NewLine.Length * 2;
        }
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static string BuildOperationBlock(OperationHistoryRecord record, string output)
    {
        string type = record.Type switch
        {
            OperationHistoryType.Install => "Instalar",
            OperationHistoryType.Update => "Atualizar",
            OperationHistoryType.Uninstall => "Desinstalar",
            _ => "Baixar"
        };
        string manager = record.Manager?.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) == true ? "Chocolatey"
            : record.Manager?.Equals("winget", StringComparison.OrdinalIgnoreCase) == true ? "WinGet"
            : "Office";
        string name = record.PackageName ?? record.PackageId ?? "Pacote desconhecido";
        string version = record.VersionTo ?? record.VersionFrom ?? string.Empty;
        string status = record.Status switch
        {
            OperationHistoryStatus.Succeeded => "Bem-sucedida",
            OperationHistoryStatus.Failed => "Falhou",
            _ => "Cancelada"
        };
        string date = record.FinishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        string header = $"Operação: {type} {manager} {name}{(string.IsNullOrWhiteSpace(version) ? string.Empty : $" ({version})")} [{status}] — {date}";
        string separator = new('█', 83);
        var lines = new List<string> { separator, header };
        if (!string.IsNullOrWhiteSpace(output)) lines.AddRange(output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'));
        string finalMessage = record.Status switch
        {
            OperationHistoryStatus.Succeeded when record.Type == OperationHistoryType.Uninstall => $"{name} foi desinstalado com sucesso",
            OperationHistoryStatus.Succeeded when record.Type == OperationHistoryType.Update => $"{name} foi atualizado com sucesso",
            OperationHistoryStatus.Succeeded when record.Type == OperationHistoryType.Install => $"{name} foi instalado com sucesso",
            OperationHistoryStatus.Succeeded => $"{name} foi baixado com sucesso",
            OperationHistoryStatus.Failed => $"Falha ao {type.ToLowerInvariant()} {name}",
            _ => $"Operação cancelada: {name}"
        };
        lines.Add(finalMessage);
        return string.Join(Environment.NewLine, lines);
    }

    public void OpenLogsFolder()
    {
        try { _desktop.OpenFolder(LogsFolder); }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "OperationHistory.OpenLogsFolder"); StatusMessage = "A pasta de logs ainda não existe."; }
    }
    public string ReadOutput(OperationHistoryRecord record) => _history.ReadOutput(record);
    public void SetStatusMessage(string message) => StatusMessage = message;

    private static AppEntry CreateApp(OperationHistoryRecord record) => new() { Id = record.PackageId ?? string.Empty, Name = record.PackageName ?? record.PackageId ?? "Office", Source = GetPackageSource(record), Version = record.VersionTo ?? string.Empty };
    private static string GetPackageSource(OperationHistoryRecord record) => record.Manager?.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) == true
        ? "chocolatey" : record.SourceName?.Equals("msstore", StringComparison.OrdinalIgnoreCase) == true ? "msstore" : "winget";
    private async Task<string> ReadTailAsync(string path, int limit)
    {
        if (!File.Exists(path)) return "Nenhum registro ainda";
        var lines = new Queue<string>(limit);
        await using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous | FileOptions.SequentialScan });
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        while (await reader.ReadLineAsync() is { } line) { if (lines.Count == limit) lines.Dequeue(); lines.Enqueue(line); }
        string text = string.Join(Environment.NewLine, lines);
        return string.IsNullOrWhiteSpace(text) ? "Nenhum registro ainda" : text;
    }
    private void History_Changed() => _dispatcher.Post(Refresh);

    public void SetDarkTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        foreach (OperationHistoryRowViewModel row in Rows) row.SetDarkTheme(isDark);
    }

    private void OnManagerEnabledChanged(bool _) => _dispatcher.Post(RefreshManagerOptions);

    private void RefreshManagerOptions()
    {
        ManagerOptions.Clear();
        ManagerOptions.Add(new("Todos os gerenciadores", string.Empty));
        if (_preferences.WingetEnabled) ManagerOptions.Add(new("WinGet", "winget"));
        if (_preferences.ChocolateyEnabled) ManagerOptions.Add(new("Chocolatey", "chocolatey"));
        if (!ManagerOptions.Any(option => option.Value.Equals(SelectedManager, StringComparison.OrdinalIgnoreCase)))
            SelectedManager = string.Empty;
    }
}

public sealed record HistoryFilterOption(string Label, string Value);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OperationHistoryRowViewModel(OperationHistoryRecord record) : ObservableObject
{
    private static readonly Brush LightSuccessBackground = new SolidColorBrush(Color.FromArgb(35, 46, 160, 67));
    private static readonly Brush DarkSuccessBackground = new SolidColorBrush(Color.FromArgb(45, 46, 160, 67));
    private static readonly Brush LightFailureBackground = new SolidColorBrush(Color.FromArgb(35, 196, 43, 28));
    private static readonly Brush DarkFailureBackground = new SolidColorBrush(Color.FromArgb(45, 196, 43, 28));
    private static readonly Brush LightNeutralBackground = new SolidColorBrush(Color.FromArgb(32, 128, 128, 128));
    private static readonly Brush DarkNeutralBackground = new SolidColorBrush(Color.FromArgb(38, 128, 128, 128));
    private static readonly Brush LightSuccessForeground = new SolidColorBrush(Color.FromArgb(255, 24, 108, 46));
    private static readonly Brush DarkSuccessForeground = new SolidColorBrush(Color.FromArgb(255, 121, 217, 154));
    private static readonly Brush LightFailureForeground = new SolidColorBrush(Color.FromArgb(255, 165, 32, 25));
    private static readonly Brush DarkFailureForeground = new SolidColorBrush(Color.FromArgb(255, 255, 129, 122));
    private static readonly Brush LightNeutralForeground = new SolidColorBrush(Color.FromArgb(255, 95, 99, 104));
    private static readonly Brush DarkNeutralForeground = new SolidColorBrush(Color.FromArgb(255, 194, 194, 194));
    private bool _isDarkTheme;
    public OperationHistoryRecord Record { get; } = record;

    public void SetDarkTheme(bool isDark)
    {
        if (_isDarkTheme == isDark) return;
        _isDarkTheme = isDark;
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
    }
    public string TypeLabel => Record.Type switch { OperationHistoryType.Install => "Instalar", OperationHistoryType.Uninstall => "Desinstalar", OperationHistoryType.Update => "Atualizar", _ => "Baixar" };
    public string TypeGlyph => Record.Type switch { OperationHistoryType.Install => "\uE930", OperationHistoryType.Uninstall => "\uE74D", OperationHistoryType.Update => "\uE895", _ => "\uE896" };
    public string StatusGlyph => Record.Status switch { OperationHistoryStatus.Succeeded => "\uE8FB", OperationHistoryStatus.Failed => "\uE711", _ => string.Empty };
    public bool HasStatusGlyph => Record.Status != OperationHistoryStatus.Cancelled;
    public string PackageNameLabel => Record.PackageName ?? Record.PackageId ?? "—";
    public string PackageIdLabel => string.Equals(Record.PackageId, "Office", StringComparison.OrdinalIgnoreCase) ? "Office" : Record.PackageId ?? string.Empty;
    public bool HasPackageId => string.Equals(Record.PackageId, "Office", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(Record.PackageId);
    public string PackageNameTooltip => PackageNameLabel;
    public string PackageIdTooltip => PackageIdLabel;
    public string VersionLabel => Record.Type switch
    {
        OperationHistoryType.Install => Record.VersionTo is null ? "—" : $"→ {Record.VersionTo}",
        OperationHistoryType.Uninstall => Record.VersionFrom ?? "—",
        _ => Record.VersionFrom is null && Record.VersionTo is null ? "—"
            : $"{Record.VersionFrom ?? "—"} → {Record.VersionTo ?? "—"}"
    };
    public string SourceLabel => string.Equals(Record.PackageId, "Office", StringComparison.OrdinalIgnoreCase) ? "Office"
        : string.IsNullOrWhiteSpace(Record.Manager) ? "—"
        : string.IsNullOrWhiteSpace(Record.SourceName) || Record.SourceName.Equals(Record.Manager, StringComparison.OrdinalIgnoreCase)
            ? Record.Manager : Record.Manager + ": " + Record.SourceName;
    public string StatusLabel => Record.Status switch { OperationHistoryStatus.Succeeded => "Bem-sucedida", OperationHistoryStatus.Failed => "Falhou", _ => "Cancelada" };
    public string StatusTooltip => StatusLabel;
    public string TimestampLabel
    {
        get
        {
            TimeSpan age = DateTimeOffset.UtcNow - Record.FinishedAt;
            if (age.TotalDays >= 7) return Record.FinishedAt.ToLocalTime().ToString("dd/MM/yyyy");
            if (age.TotalDays >= 1) return $"há {(int)age.TotalDays} d";
            if (age.TotalHours >= 1) return $"há {(int)age.TotalHours} h";
            return $"há {Math.Max(1, (int)age.TotalMinutes)} min";
        }
    }
    public string TimestampTooltip => Record.FinishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    private bool HasActionContext => (Record.Manager is "winget" or "chocolatey")
        && !string.IsNullOrWhiteSpace(Record.PackageId)
        && (Record.Type is OperationHistoryType.Install or OperationHistoryType.Uninstall or OperationHistoryType.Update);
    public bool CanUndo => HasActionContext && Record.Status == OperationHistoryStatus.Succeeded
        && (Record.Type is OperationHistoryType.Install or OperationHistoryType.Uninstall
            || Record.Type == OperationHistoryType.Update && Record.VersionFrom is not null);
    public bool CanRepeat => HasActionContext && Record.Snapshot is not null;
    public string UndoTooltip => CanUndo ? "Desfazer operação"
        : string.IsNullOrWhiteSpace(Record.PackageId) ? "Registro antigo sem ID do pacote"
        : Record.Manager is null ? "Registro antigo sem gerenciador"
        : Record.Status != OperationHistoryStatus.Succeeded ? "Só é possível desfazer operações bem-sucedidas"
        : Record.Type == OperationHistoryType.Update && Record.VersionFrom is null ? "A versão anterior não foi registrada"
        : "Esta operação não tem reversão";
    public string RepeatTooltip => Record.Snapshot is null ? "Registro antigo sem dados suficientes" : CanRepeat ? "Repetir operação"
        : string.IsNullOrWhiteSpace(Record.PackageId) ? "Registro antigo sem ID do pacote"
        : Record.Manager is null ? "Registro antigo sem gerenciador"
        : "Esta operação não pode ser repetida";
    public bool HasOutput => !string.IsNullOrWhiteSpace(Record.OutputFile);
    public Brush StatusBackground => Record.Status switch
    {
        OperationHistoryStatus.Succeeded => _isDarkTheme ? DarkSuccessBackground : LightSuccessBackground,
        OperationHistoryStatus.Failed => _isDarkTheme ? DarkFailureBackground : LightFailureBackground,
        _ => _isDarkTheme ? DarkNeutralBackground : LightNeutralBackground
    };
    public Brush StatusForeground => Record.Status switch
    {
        OperationHistoryStatus.Succeeded => _isDarkTheme ? DarkSuccessForeground : LightSuccessForeground,
        OperationHistoryStatus.Failed => _isDarkTheme ? DarkFailureForeground : LightFailureForeground,
        _ => _isDarkTheme ? DarkNeutralForeground : LightNeutralForeground
    };
}
