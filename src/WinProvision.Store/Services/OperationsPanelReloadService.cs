using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>Reloads user operation-panel overlays after filesystem writes have settled.</summary>
public sealed partial class OperationsPanelReloadService : IDisposable
{
    private const long MaxFileBytes = 1024 * 1024;
    private readonly IOperationsPanelCatalog _catalog;
    private readonly OperationsPanelCatalog _catalogLoader;
    private readonly OperationTextProvider _texts;
    private readonly DispatcherQueue _dispatcher;
    private readonly string _panelPath;
    private readonly string _textPath;
    private readonly TimeSpan _debounceDelay;
    private readonly object _gate = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private string _panelHash;
    private string _textHash;
    private long _generation;
    private bool _disposed;

    public OperationsPanelReloadService(IOperationsPanelCatalog catalog, OperationsPanelCatalog catalogLoader,
        OperationTextProvider texts, DispatcherQueue dispatcher, string? panelPath = null, string? textPath = null,
        TimeSpan? debounceDelay = null)
    {
        _catalog = catalog;
        _catalogLoader = catalogLoader;
        _texts = texts;
        _dispatcher = dispatcher;
        _panelPath = panelPath ?? OperationsPanelCatalog.UserFilePath;
        _textPath = textPath ?? OperationTextProvider.UserOverlayPath(_texts.Language);
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(300);
        string root = Path.GetDirectoryName(_panelPath)!;
        Directory.CreateDirectory(root);
        _panelHash = HashPath(_panelPath);
        _textHash = HashPath(_textPath);
        _debounce = new Timer(DebounceElapsed);
        _watcher = new FileSystemWatcher(root, "*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    private void OnChanged(object sender, FileSystemEventArgs args) => ScheduleIfRelevant(args.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        ScheduleIfRelevant(args.FullPath);
        ScheduleIfRelevant(args.OldFullPath);
    }

    private void ScheduleIfRelevant(string path)
    {
        if (!PathEquals(path, _panelPath) && !PathEquals(path, _textPath)) return;
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _debounce.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void DebounceElapsed(object? state)
    {
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            generation = _generation;
        }
        _ = Task.Run(() => ReloadSnapshotsAsync(generation));
    }

    private async Task ReloadSnapshotsAsync(long generation)
    {
        try
        {
            (string? panelJson, string panelHash, bool panelReadable) = await ReadStableAsync(_panelPath).ConfigureAwait(false);
            (string? textJson, string textHash, bool textReadable) = await ReadStableAsync(_textPath).ConfigureAwait(false);
            bool panelChanged;
            bool textChanged;
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;
                panelChanged = panelHash != _panelHash;
                textChanged = textHash != _textHash;
                if (!panelReadable) panelChanged = false;
                if (!textReadable) textChanged = false;
            }
            if (!panelChanged && !textChanged) return;

            OperationsPanelConfiguration? panelCandidate = null;
            if (panelChanged && !_catalogLoader.TryBuildReloadCandidate(panelJson, out panelCandidate!))
                panelCandidate = null;
            IReadOnlyDictionary<string, string>? textCandidate = null;
            if (textChanged && !_texts.TryBuildUserOverlay(_texts.Language, textJson, out textCandidate!))
                textCandidate = null;
            _dispatcher.TryEnqueue(() =>
            {
                lock (_gate)
                {
                    if (_disposed || generation != _generation) return;
                    if (panelChanged) _panelHash = panelHash;
                    if (textChanged) _textHash = textHash;
                }
                if (panelCandidate is not null && _catalog is OperationsPanelCatalog mutableCatalog)
                    mutableCatalog.ReplaceConfiguration(panelCandidate);
                if (textCandidate is not null)
                    _texts.ReplaceUserOverlay(_texts.Language, textCandidate);
            });
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "OperationsPanel.Reload");
        }
    }

    private static async Task<(string? Json, string Hash, bool Readable)> ReadStableAsync(string path)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return (null, "missing", true);
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes) throw new InvalidDataException($"O arquivo excede 1 MB: {path}");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var memory = new MemoryStream();
                byte[] buffer = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    if (memory.Length + read > MaxFileBytes) throw new InvalidDataException($"O arquivo excede 1 MB: {path}");
                    memory.Write(buffer, 0, read);
                }
                byte[] bytes = memory.ToArray();
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return (await reader.ReadToEndAsync().ConfigureAwait(false), hash, true);
            }
            catch (FileNotFoundException) { return (null, "missing", true); }
            catch (DirectoryNotFoundException) { return (null, "missing", true); }
            catch (InvalidDataException exception)
            {
                CoreLog.Warning(exception, "OperationsPanel.ReloadRead");
                return (null, "oversized", false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3)
                {
                    CoreLog.Warning(exception, "OperationsPanel.ReloadRead");
                    return (null, string.Empty, false);
                }
                await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
            }
        }
        return (null, string.Empty, false);
    }

    private static string HashPath(string path)
    {
        try
        {
            if (!File.Exists(path)) return "missing";
            if (new FileInfo(path).Length > MaxFileBytes) return "oversized";
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return string.Empty; }
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private void OnError(object sender, ErrorEventArgs args) => CoreLog.Warning(args.GetException(), "OperationsPanel.FileWatcher");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
        }
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
