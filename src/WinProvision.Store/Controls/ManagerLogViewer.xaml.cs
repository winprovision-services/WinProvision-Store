using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.UI;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.Controls;

public sealed partial class ManagerLogViewer : UserControl
{
    private sealed record SourceOption(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly string Separator = new('_', 76);
    private static readonly SolidColorBrush NormalBrush = new(Color.FromArgb(255, 235, 235, 235));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromArgb(255, 105, 105, 105));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(255, 205, 0, 0));
    private static readonly SolidColorBrush BlueBrush = new(Color.FromArgb(255, 0, 0, 205));
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(255, 0, 150, 0));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromArgb(255, 170, 145, 0));
    private IManagerTaskLogStore? _store;
    private IAppLog? _appLog;
    private IClipboardService? _clipboard;
    private IPresentationService? _presentation;
    private Func<Task<string>>? _operationHistoryLoader;
    private Func<Task<string>>? _operationHistoryExportLoader;
    private IReadOnlyList<ManagerTaskLogRecord> _visibleTasks = [];
    private string _visibleText = "Nenhum registro ainda";
    private bool _configuring;
    private const int MaximumVisibleCharacters = 2 * 1024 * 1024;
    private readonly StringBuilder _renderBuffer = new();
    private readonly List<(int Start, int Length, Color Color)> _colorRanges = [];
    private bool _renderLimitReached;
    private Action? _themeRender;
    private IReadOnlyList<(string Text, AppLogLevel Level)> _visibleStoreEvents = [];
    private IReadOnlyList<(string Text, AppLogLevel Level)> _visibleManagerLegacyEvents = [];
    private int _loadVersion;
    private bool _isLoaded;

    public ManagerLogViewer()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => _themeRender?.Invoke();
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            if (_store is not null) _ = ReloadAsync();
        };
        Unloaded += (_, _) => _isLoaded = false;
    }

    public void Configure(IManagerTaskLogStore store, IAppLog appLog, IClipboardService clipboard,
        IPresentationService presentation, IEnumerable<PackageManagerDescriptor> enabledManagers, string? selectedManager = null,
        Func<Task<string>>? operationHistoryLoader = null, Func<Task<string>>? operationHistoryExportLoader = null)
    {
        _store = store;
        _appLog = appLog;
        _clipboard = clipboard;
        _presentation = presentation;
        _operationHistoryLoader = operationHistoryLoader;
        _operationHistoryExportLoader = operationHistoryExportLoader;
        _configuring = true;
        SourceSelector.Items.Clear();
        SourceSelector.Items.Add(new SourceOption("store", "WinProvision Store"));
        foreach (PackageManagerDescriptor manager in enabledManagers.OrderBy(manager => manager.SortOrder))
            SourceSelector.Items.Add(new SourceOption(manager.Id, manager.DisplayName));
        SourceSelector.Items.Add(new SourceOption("history", "Histórico de operações"));
        int index = 0;
        if (!string.IsNullOrWhiteSpace(selectedManager))
        {
            for (int i = 0; i < SourceSelector.Items.Count; i++)
                if (SourceSelector.Items[i] is SourceOption option && option.Id.Equals(selectedManager, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        }
        SourceSelector.SelectedIndex = index;
        SourceSelector.Visibility = Visibility.Visible;
        _configuring = false;
    }

    public async Task LoadAsync(string? selectedManager = null, CancellationToken cancellationToken = default)
    {
        if (selectedManager is not null)
        {
            _configuring = true;
            for (int i = 0; i < SourceSelector.Items.Count; i++)
                if (SourceSelector.Items[i] is SourceOption option && option.Id.Equals(selectedManager, StringComparison.OrdinalIgnoreCase)) { SourceSelector.SelectedIndex = i; break; }
            _configuring = false;
        }
        if (_isLoaded) await ReloadAsync(cancellationToken);
        else RenderEmpty();
    }

    private async void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_configuring && SourceSelector.SelectedItem is not null && _isLoaded)
        {
            await ReloadAsync();
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null || _appLog is null)
        {
            RenderEmpty();
            return;
        }
        SourceOption source = SourceSelector.SelectedItem as SourceOption ?? new("store", "WinProvision Store");
        int loadVersion = Interlocked.Increment(ref _loadVersion);
        LoadingRing.Visibility = Visibility.Visible;
        LoadingRing.IsActive = true;
        StatusText.Text = $"Carregando {source.Label}…";
        int parsedEntryCount = 0;
        int generatedBlockCount = 0;
        string diagnosticPaths = string.Empty;
        try
        {
            if (source.Id == "history" && _operationHistoryLoader is not null)
            {
                _visibleTasks = [];
                _visibleText = await _operationHistoryLoader().ConfigureAwait(false);
                parsedEntryCount = _visibleText.Split('\n').Count(line => line.StartsWith("Operação:", StringComparison.Ordinal));
                generatedBlockCount = parsedEntryCount;
                diagnosticPaths = Path.Combine(_appLog.LogDirectory, "operation-history.jsonl");
            }
            else if (source.Id == "store")
            {
                IReadOnlyList<(string Text, AppLogLevel Level)> events = await PackageManagerLogReader
                    .ReadApplicationEventsAsync(_appLog, cancellationToken).ConfigureAwait(false);
                _visibleTasks = [];
                _visibleStoreEvents = events;
                _visibleText = events.Count == 0 ? "Nenhum registro ainda" : string.Join(Environment.NewLine, events.Select(entry => entry.Text));
                parsedEntryCount = events.Count;
                generatedBlockCount = events.Count;
                diagnosticPaths = await DescribeApplicationLogFilesAsync(_appLog.LogDirectory, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _appLog.FlushAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ManagerTaskLogRecord> tasks = await ReadManagerTasksAsync(source.Id, 100, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<(string Text, AppLogLevel Level)> legacy = await PackageManagerLogReader
                    .ReadLegacyManagerEventsAsync(source.Id, _appLog.LogDirectory, cancellationToken).ConfigureAwait(false);
                _visibleManagerLegacyEvents = legacy;
                _visibleTasks = tasks;
                parsedEntryCount = tasks.Count + legacy.Count;
                generatedBlockCount = tasks.Count;
                diagnosticPaths = await DescribeManagerTaskFileAsync(_store.FilePath, source.Id, tasks.Count, cancellationToken).ConfigureAwait(false);
                diagnosticPaths += "; legado=" + await DescribeApplicationLogFilesAsync(_appLog.LogDirectory, cancellationToken).ConfigureAwait(false);
                _visibleText = await Task.Run(() =>
                {
                    string taskText = ManagerTaskLogFormatter.Format(tasks);
                    string legacyText = string.Join(Environment.NewLine, legacy.Select(entry => entry.Text));
                    return string.Join(Environment.NewLine + Environment.NewLine, new[] { taskText, legacyText }.Where(value => !string.IsNullOrWhiteSpace(value)));
                }, cancellationToken)
                    .ConfigureAwait(false);
            }
            string status = parsedEntryCount == 0 || string.IsNullOrWhiteSpace(_visibleText)
                ? $"Nenhum registro ainda (arquivo: {diagnosticPaths})"
                : source.Id is not ("store" or "history")
                    ? $"{source.Label}: {_visibleTasks.Count} tarefas, {_visibleText.Length / 1024.0:0.0} KB, carregado às {DateTime.Now:HH:mm:ss}"
                    : $"{source.Label}: {parsedEntryCount} registros, {_visibleText.Length / 1024.0:0.0} KB, carregado às {DateTime.Now:HH:mm:ss}";
            try { _appLog.Write(AppLogLevel.Debug, "Logs.Viewer.Load", source.Id,
                $"Caminhos={diagnosticPaths}; entradas interpretadas={parsedEntryCount}; blocos gerados={generatedBlockCount}; caracteres finais={_visibleText.Length}; {status}"); }
            catch { }
            await RunOnUiAsync(() =>
            {
                if (loadVersion != _loadVersion) return;
                if (source.Id == "history") RenderOperationHistory(_visibleText);
                else if (source.Id == "store") RenderStoreEvents(_visibleStoreEvents);
                else Render(_visibleTasks, _visibleManagerLegacyEvents);
                StatusText.Text = status;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RunOnUiAsync(() => { if (loadVersion == _loadVersion) RenderEmpty(); });
        }
        catch (OperationCanceledException)
        {
            await RunOnUiAsync(() => { if (loadVersion == _loadVersion) RenderError(new OperationCanceledException("A leitura foi cancelada.")); });
        }
        catch (Exception exception)
        {
            try { _appLog.Write(AppLogLevel.Error, "Logs.Viewer", source.Id,
                "Falha ao carregar os registros do visualizador.", exception: exception); }
            catch { }
            await RunOnUiAsync(() => { if (loadVersion == _loadVersion) StatusText.Text = $"Não foi possível carregar: {exception.Message}"; });
            try
            {
                await RunOnUiAsync(() =>
                {
                    if (loadVersion != _loadVersion) return;
                    StatusText.Text = $"Não foi possível carregar: {exception.Message}";
                    try { RenderError(exception); } catch { }
                });
            }
            catch { }
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                if (loadVersion == _loadVersion)
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
            });
        }
    }

    private void RenderEmpty()
    {
        _visibleText = "Nenhum registro ainda";
        BeginRender();
        AddLine(_visibleText, '0', ActualTheme == ElementTheme.Dark);
        CommitRender();
    }

    private void RenderOperationHistory(string text)
    {
        _themeRender = () => RenderOperationHistory(_visibleText);
        BeginRender();
        if (string.IsNullOrWhiteSpace(text)) { AddLine("Nenhum registro ainda", '0'); CommitRender(); return; }
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
            AddLine(line, line.StartsWith("Operação:", StringComparison.Ordinal) ? '3' : '0', ActualTheme == ElementTheme.Dark);
        CommitRender();
    }

    private void RenderStoreEvents(IReadOnlyList<(string Text, AppLogLevel Level)> events)
    {
        _visibleStoreEvents = events;
        _themeRender = () => RenderStoreEvents(_visibleStoreEvents);
        BeginRender();
        if (events.Count == 0) { AddLine("Nenhum registro ainda", '0', ActualTheme == ElementTheme.Dark); CommitRender(); return; }
        bool dark = ActualTheme == ElementTheme.Dark;
        foreach ((string text, AppLogLevel level) in events.TakeLast(100))
        {
            char color = level == AppLogLevel.Error ? '2' : level == AppLogLevel.Warning ? '5'
                : level.ToString().Equals("Success", StringComparison.OrdinalIgnoreCase) ? '4'
                : level is AppLogLevel.Debug or AppLogLevel.Trace ? '1' : '0';
            AddLine(text, color, dark);
        }
        CommitRender();
    }

    private void Render(IReadOnlyList<ManagerTaskLogRecord> tasks, IReadOnlyList<(string Text, AppLogLevel Level)>? legacyEvents = null)
    {
        _themeRender = () => Render(_visibleTasks, _visibleManagerLegacyEvents);
        BeginRender();
        if (tasks.Count == 0 && (legacyEvents is null || legacyEvents.Count == 0))
        {
            AddLine("Nenhum registro ainda", '0');
            CommitRender();
            return;
        }
        bool dark = ActualTheme == ElementTheme.Dark;
        foreach (ManagerTaskLogRecord task in tasks)
        {
            if (_renderLimitReached) break;
            AddLine(Separator, '0', dark);
            string manager = SourceSelector.SelectedItem is SourceOption option && option.Id != "store"
                ? option.Label : task.Manager;
            DateTimeOffset end = task.FinishedAt ?? DateTimeOffset.UtcNow;
            bool native = task.Kind.Equals("Native", StringComparison.OrdinalIgnoreCase);
            AddLine(native ? $"Tarefa nativa registrada no gerenciador {manager}. Tipo da tarefa: {task.TaskType}"
                : $"Tarefa por subprocesso registrada no gerenciador {manager}. Tipo: {task.TaskType}", '0', dark);
            if (!native)
            {
                AddLine($"Executável: \"{task.Executable}\"", '0', dark);
                AddLine($"Argumentos: \"{task.Arguments}\"", '0', dark);
            }
            AddLine($"Início do processo: {task.StartedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}", '0', dark);
            AddLine($"Fim do processo:    {end.ToLocalTime():dd/MM/yyyy HH:mm:ss}", task.FinishedAt is null ? '5' : '0', dark);
            if (native)
            {
                AddBlank(); AddLine("-- Informações da tarefa", '0', dark);
                AddPayload(task.TaskInformation, '1');
                if (!string.IsNullOrWhiteSpace(task.StandardError)) { AddBlank(); AddLine("-- Erros da tarefa", '0', dark); AddPayload(task.StandardError, '2'); }
                AddLine(task.Result == "success" ? "A tarefa foi concluída com sucesso" : task.Result == "error" ? "A tarefa falhou" : "A tarefa terminou com avisos",
                    task.Result == "success" ? '4' : task.Result == "error" ? '2' : '5', dark);
            }
            else
            {
                AddBlank(); AddLine("-- Saída do processo (STDOUT)", '0', dark); AddPayload(task.StandardOutput, '1');
                if (!string.IsNullOrWhiteSpace(task.StandardError)) { AddBlank(); AddLine("-- Erro do processo (STDERR)", '0', dark); AddPayload(task.StandardError, '2'); }
                AddLine(task.ExitCode is null ? "Código de retorno: não informado"
                    : task.ExitCode == 0 ? "Código de retorno: SUCESSO (0)"
                    : $"Código de retorno: FALHA (0x{(uint)task.ExitCode.Value:X}, {task.ExitCode})",
                    task.Result == "success" ? '4' : task.Result == "error" ? '2' : '5', dark);
            }
            AddBlank();
        }
        foreach ((string text, AppLogLevel level) in legacyEvents ?? [])
        {
            if (_renderLimitReached) break;
            AddLine(text, level == AppLogLevel.Error ? '2' : level == AppLogLevel.Warning ? '5' : '1', dark);
        }
        CommitRender();
    }

    private void AddPayload(string? text, char color)
    {
        if (_renderLimitReached) return;
        if (string.IsNullOrWhiteSpace(text)) { AddLine(" ...", '1'); return; }
        const int maxChars = 50 * 1024;
        bool truncated = text.Length > maxChars;
        string normalized = System.Text.RegularExpressions.Regex.Replace(text, "\\\\u([0-9a-fA-F]{4})", match =>
        {
            int code = Convert.ToInt32(match.Groups[1].Value, 16);
            if (code is >= 0xD800 and <= 0xDBFF && match.Index + match.Length + 6 <= text.Length
                && text.AsSpan(match.Index + match.Length, 2).SequenceEqual("\\u"))
            {
                string lowHex = text.Substring(match.Index + match.Length + 2, 4);
                if (int.TryParse(lowHex, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out int low) && low is >= 0xDC00 and <= 0xDFFF)
                    return char.ConvertFromUtf32(0x10000 + ((code - 0xD800) << 10) + low - 0xDC00);
            }
            return code is >= 0xD800 and <= 0xDFFF ? "�" : char.ConvertFromUtf32(code);
        });
        if (truncated) normalized = normalized[..maxChars] + Environment.NewLine + "[saída truncada — excedeu 50 KB]";
        foreach (string line in normalized.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (_renderLimitReached) break;
            AddLine("  " + line, color);
        }
    }

    private void AddBlank() => AppendLineToBuffer(string.Empty, NormalBrush.Color);
    private void AddLine(string value, char color, bool dark = false)
    {
        SolidColorBrush brush = color switch
        {
            '1' => dark ? new SolidColorBrush(Color.FromArgb(255, 145, 145, 145)) : GrayBrush,
            '2' => dark ? new SolidColorBrush(Color.FromArgb(255, 255, 80, 80)) : RedBrush,
            '3' => dark ? new SolidColorBrush(Color.FromArgb(255, 120, 120, 255)) : BlueBrush,
            '4' => dark ? new SolidColorBrush(Color.FromArgb(255, 80, 255, 80)) : GreenBrush,
            '5' => dark ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 90)) : YellowBrush,
            _ => dark ? new SolidColorBrush(Color.FromArgb(255, 250, 250, 250)) : NormalBrush
        };
        AppendLineToBuffer(value, brush.Color);
    }

    private void BeginRender()
    {
        _renderBuffer.Clear();
        _colorRanges.Clear();
        _renderLimitReached = false;
    }

    private void ViewerRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double compactWidth = Math.Clamp(e.NewSize.Width - 590, 180, 320);
        SourceSelector.Width = compactWidth;
        bool compact = e.NewSize.Width < 900;
        foreach (Button button in new[] { CopyButton, ExportButton, ReloadButton })
        {
            button.FontSize = compact ? 11 : 14;
            button.Padding = compact ? new Thickness(5, 2, 5, 2) : new Thickness(12, 4, 12, 4);
        }
        LogLevelLabel.FontSize = compact ? 12 : 14;
    }

    private void AppendLineToBuffer(string line, Color color)
    {
        if (_renderLimitReached) return;
        int required = line.Length + 2;
        if (_renderBuffer.Length + required > MaximumVisibleCharacters)
        {
            const string notice = "[Exibição limitada a 2 MB; exporte o arquivo para incluir o restante.]";
            if (_renderBuffer.Length + notice.Length + 2 <= MaximumVisibleCharacters)
            {
                int start = _renderBuffer.Length;
                _renderBuffer.Append(notice).Append("\r\n");
                _colorRanges.Add((start, notice.Length, YellowBrush.Color));
            }
            _renderLimitReached = true;
            return;
        }
        int offset = _renderBuffer.Length;
        _renderBuffer.Append(line).Append("\r\n");
        if (line.Length > 0)
        {
            if (_colorRanges.Count > 0)
            {
                var last = _colorRanges[^1];
                if (last.Color == color && last.Start + last.Length + 2 == offset)
                    _colorRanges[^1] = (last.Start, last.Length + 2 + line.Length, color);
                else _colorRanges.Add((offset, line.Length, color));
            }
            else _colorRanges.Add((offset, line.Length, color));
        }
    }

    private void CommitRender()
    {
        string text = _renderBuffer.ToString();
        _visibleText = string.IsNullOrWhiteSpace(text) ? "Nenhum registro ainda" : text.TrimEnd('\r', '\n');
        LogDocument.IsReadOnly = false;
        try
        {
            LogDocument.Document.SetText(TextSetOptions.None, text);
            foreach ((int start, int length, Color color) in _colorRanges)
            {
                var range = LogDocument.Document.GetRange(start, start + length);
                var format = range.CharacterFormat;
                format.ForegroundColor = color;
                range.CharacterFormat = format;
            }
        }
        finally { LogDocument.IsReadOnly = true; }
        LogDocument.UpdateLayout();
        ScrollLogToBottom();
    }

    private void ScrollLogToBottom()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            LogDocument.UpdateLayout();
            ScrollViewer? scroll = FindDescendant<ScrollViewer>(LogDocument);
            scroll?.ChangeView(null, scroll.ScrollableHeight, null, true);
        });
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void RenderError(Exception exception)
    {
        _themeRender = () => RenderError(exception);
        BeginRender();
        AddLine($"Não foi possível carregar o registro: {exception.Message}", '2', ActualTheme == ElementTheme.Dark);
        CommitRender();
    }

    private static async Task<string> DescribeApplicationLogFilesAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return $"{directory} (diretório ausente)";
        var descriptions = new List<string>();
        foreach (string path in Directory.EnumerateFiles(directory, AppLogPaths.DiagnosticLogFilePattern)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            long bytes = new FileInfo(path).Length;
            int lines = 0;
            int parsed = 0;
            await foreach (AppLogEvent entry in AppLogJsonLinesReader.ReadForDisplayAsync(path, cancellationToken).ConfigureAwait(false))
            {
                lines++;
                if (!entry.Event.Equals("UnparsedLine", StringComparison.OrdinalIgnoreCase)) parsed++;
            }
            descriptions.Add($"{path} (existe=True; bytes={bytes}; linhas={lines}; interpretadas={parsed})");
        }
        return descriptions.Count == 0 ? $"{Path.Combine(directory, AppLogPaths.DiagnosticLogFilePattern)} (nenhum arquivo)" : string.Join(" | ", descriptions);
    }

    private static async Task<string> DescribeManagerTaskFileAsync(string path, string manager, int parsed, CancellationToken cancellationToken)
    {
        bool exists = File.Exists(path);
        long bytes = exists ? new FileInfo(path).Length : 0;
        int lines = 0;
        if (exists)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null) lines++;
        }
        return $"{path} (existe={exists}; bytes={bytes}; linhas={lines}; interpretadas para {manager}={parsed})";
    }

    private Task RunOnUiAsync(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        })) completion.SetException(new InvalidOperationException("A fila da interface foi encerrada."));
        return completion.Task;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        _clipboard?.TrySetText(_visibleText);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_presentation is null) return;
        string? path = await _presentation.PickSaveAsync("Registros-do-gerenciador.txt", ".txt");
        if (path is null) return;
        string text = _visibleText;
        if (SourceSelector.SelectedItem is SourceOption { Id: "history" } && _operationHistoryExportLoader is not null)
            text = await _operationHistoryExportLoader();
        else if (SourceSelector.SelectedItem is SourceOption { Id: not ("store" or "history") } source)
        {
            await _appLog!.FlushAsync();
            IReadOnlyList<ManagerTaskLogRecord> tasks = await ReadManagerTasksAsync(source.Id, 100_000, CancellationToken.None);
            string taskText = ManagerTaskLogFormatter.Format(tasks, int.MaxValue);
            string legacyText = string.Join(Environment.NewLine, _visibleManagerLegacyEvents.Select(entry => entry.Text));
            text = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { taskText, legacyText }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
    }

    private async Task<IReadOnlyList<ManagerTaskLogRecord>> ReadManagerTasksAsync(string manager, int maximumTasks,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ManagerTaskLogRecord> current = await Task.Run(
            () => _store!.ReadAsync(manager, cancellationToken, maximumTasks), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ManagerTaskLogRecord> legacy = await Task.Run(
            () => PackageManagerLogReader.ReadLegacyTasksAsync(manager, _appLog!.LogDirectory, cancellationToken, maximumTasks), cancellationToken).ConfigureAwait(false);
        return current.Concat(legacy).GroupBy(task => task.TaskId).Select(group => group.Last())
            .OrderBy(task => task.StartedAt).TakeLast(maximumTasks).ToArray();
    }
}
