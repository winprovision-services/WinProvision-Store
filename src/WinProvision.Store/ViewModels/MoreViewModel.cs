using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MoreViewModel : ObservableObject, IDisposable
{
    public static readonly Uri ProjectUri = new("https://winprovision-services.github.io/WinProvision-Store/");
    private const int MaxDisplayedLogLines = 100;
    private readonly WingetBootstrapper _winget;
    private readonly WinProvisionApiService _catalogApi;
    private readonly OperationHistoryService _history;
    private readonly IDesktopIntegrationService _desktop;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private string _diagnosticText = string.Empty;
    private bool _disposed;

    public ObservableCollection<OperationHistoryEntry> HistoryEntries { get; } = new();

    [ObservableProperty]
    public partial bool IsStatusVisible { get; set; }

    [ObservableProperty]
    public partial bool IsHistoryVisible { get; set; }

    [ObservableProperty]
    public partial bool IsLogVisible { get; set; }

    [ObservableProperty]
    public partial bool IsAboutVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsBrowserStatusVisible { get; private set; }

    [ObservableProperty]
    public partial string BrowserStatus { get; private set; } = "Conectando ao GitHub Pages…";

    [ObservableProperty]
    public partial bool IsCheckingStatus { get; private set; }

    [ObservableProperty]
    public partial string WingetStatus { get; private set; } = "Ainda não verificado";

    [ObservableProperty]
    public partial string WingetVersion { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WingetSources { get; private set; } = "Fontes: —";

    [ObservableProperty]
    public partial string WingetDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WingetCatalogStatus { get; private set; } = "Ainda não verificado";

    [ObservableProperty]
    public partial string WingetCatalogDetails { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StoreCatalogStatus { get; private set; } = "Ainda não verificado";

    [ObservableProperty]
    public partial string StoreCatalogDetails { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastChecked { get; private set; } = "Ainda não verificado";

    [ObservableProperty]
    public partial bool CanCopyDiagnostic { get; private set; }

    [ObservableProperty]
    public partial string LogText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LogStatus { get; private set; } = "O log ainda não foi carregado.";

    [ObservableProperty]
    public partial string LogFilePath { get; private set; } = WinProvisionLog.FilePath;

    [ObservableProperty]
    public partial bool IsLoadingLog { get; private set; }

    [ObservableProperty]
    public partial bool HasStatusMessage { get; private set; }

    [ObservableProperty]
    public partial string StatusMessage { get; private set; } = string.Empty;

    public bool HasHistory => HistoryEntries.Count > 0;

    public IAsyncRelayCommand CheckStatusCommand { get; }
    public IRelayCommand CancelStatusCommand { get; }
    public IRelayCommand CopyDiagnosticCommand { get; }
    public IRelayCommand OpenHistoryCommand { get; }
    public IRelayCommand ClearHistoryCommand { get; }
    public IRelayCommand OpenTaskLogFileCommand { get; }
    public IRelayCommand OpenAboutCommand { get; }
    public IRelayCommand RetryAboutCommand { get; }
    public IRelayCommand CloseAboutCommand { get; }
    public IAsyncRelayCommand OpenProjectWebsiteCommand { get; }
    public IAsyncRelayCommand OpenLogCommand { get; }
    public IAsyncRelayCommand RefreshLogCommand { get; }
    public IRelayCommand CloseLogCommand { get; }
    public IRelayCommand OpenLogFileCommand { get; }
    public event EventHandler? AboutRetryRequested;

    public MoreViewModel(
        WingetBootstrapper winget,
        WinProvisionApiService catalogApi,
        OperationHistoryService history,
        IDesktopIntegrationService desktop,
        IClipboardService clipboard,
        IUiDispatcher dispatcher)
    {
        _winget = winget;
        _catalogApi = catalogApi;
        _history = history;
        _desktop = desktop;
        _clipboard = clipboard;
        _dispatcher = dispatcher;

        CheckStatusCommand = new AsyncRelayCommand(CheckStatusAsync, () => !IsCheckingStatus && !_disposed);
        CancelStatusCommand = new RelayCommand(CheckStatusCommand.Cancel, () => CheckStatusCommand.CanBeCanceled);
        CopyDiagnosticCommand = new RelayCommand(CopyDiagnostic, () => CanCopyDiagnostic);
        OpenHistoryCommand = new RelayCommand(ToggleHistory);
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => HasHistory);
        OpenTaskLogFileCommand = new RelayCommand(OpenTaskLogFile);
        OpenAboutCommand = new RelayCommand(ShowAbout);
        RetryAboutCommand = new RelayCommand(
            () => AboutRetryRequested?.Invoke(this, EventArgs.Empty),
            () => IsAboutVisible);
        CloseAboutCommand = new RelayCommand(() => IsAboutVisible = false);
        OpenProjectWebsiteCommand = new AsyncRelayCommand(OpenProjectWebsiteAsync);
        OpenLogCommand = new AsyncRelayCommand(OpenLogAsync);
        RefreshLogCommand = new AsyncRelayCommand(LoadLogAsync, () => IsLogVisible && !IsLoadingLog);
        CloseLogCommand = new RelayCommand(CloseLog);
        OpenLogFileCommand = new RelayCommand(OpenLogFile);
        _history.Changed += OnHistoryChanged;
        HistoryEntries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            ClearHistoryCommand.NotifyCanExecuteChanged();
        };
    }

    private async Task CheckStatusAsync(CancellationToken cancellationToken)
    {
        IsAboutVisible = false;
        IsStatusVisible = true;
        IsHistoryVisible = false;
        IsLogVisible = false;
        IsCheckingStatus = true;
        StatusMessage = string.Empty;
        HasStatusMessage = false;
        WingetStatus = "Verificando…";
        WingetVersion = "Versão: consultando";
        WingetSources = "Fontes: consultando";
        WingetDetail = string.Empty;
        WingetCatalogStatus = "Verificando…";
        StoreCatalogStatus = "Verificando…";
        WingetCatalogDetails = string.Empty;
        StoreCatalogDetails = string.Empty;
        CanCopyDiagnostic = false;
        CopyDiagnosticCommand.NotifyCanExecuteChanged();
        CancelStatusCommand.NotifyCanExecuteChanged();

        var report = new List<string>
        {
            "WinProvision Store Status",
            $"Verificado em: {DateTimeOffset.Now:dd/MM/yyyy HH:mm:ss zzz}",
            $"Windows: {Environment.OSVersion.VersionString}",
            $"Aplicativo: {typeof(MoreViewModel).Assembly.GetName().Version}"
        };

        try
        {
            await CheckWingetAsync(cancellationToken, report);
            var health = await _catalogApi.CheckCatalogHealthAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCatalogHealth(health, report);
            LastChecked = $"Verificado em {health.CheckedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
            _diagnosticText = string.Join(Environment.NewLine, report);
            CanCopyDiagnostic = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LastChecked = "Verificação cancelada";
            StatusMessage = "Verificação cancelada.";
            HasStatusMessage = true;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "MoreViewModel.CheckStatus");
            StatusMessage = "Não foi possível verificar um ou mais serviços. Tente novamente.";
            HasStatusMessage = true;
            LastChecked = "A verificação terminou com erro";
            report.Add($"Erro geral: {exception.GetType().Name}: {exception.Message}");
            _diagnosticText = string.Join(Environment.NewLine, report);
            CanCopyDiagnostic = true;
        }
        finally
        {
            IsCheckingStatus = false;
            CheckStatusCommand.NotifyCanExecuteChanged();
            CancelStatusCommand.NotifyCanExecuteChanged();
            CopyDiagnosticCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task CheckWingetAsync(CancellationToken cancellationToken, List<string> report)
    {
        bool available;
        try
        {
            available = await _winget.IsWingetAvailableAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            available = false;
            WingetDetail = $"Falha ao consultar o WinGet ({exception.GetType().Name}).";
        }

        WingetStatus = available ? "Disponível" : "Indisponível";
        if (!available)
        {
            WingetVersion = "Versão: não encontrada";
            WingetSources = "Fontes: não consultadas";
            if (string.IsNullOrEmpty(WingetDetail))
                WingetDetail = "O WinGet não respondeu à verificação local.";
            report.Add("WinGet CLI: indisponível");
            return;
        }

        string executable = WingetLocator.ExecutablePath;
        report.Add($"WinGet executável: {executable}");
        try
        {
            var version = await RunWingetCommandAsync(executable, (string[])["--version"], TimeSpan.FromSeconds(8), cancellationToken);
            string versionText = FirstNonEmptyLine(version.StandardOutput, version.StandardError);
            WingetVersion = $"Versão: {(version.ExitCode == 0 && versionText.Length > 0 ? versionText : "não informada")}";
            report.Add($"WinGet versão: {WingetVersion["Versão: ".Length..]}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WingetVersion = "Versão: consulta não concluída";
            report.Add($"WinGet versão: erro {exception.GetType().Name}");
        }

        try
        {
            var sources = await RunWingetCommandAsync(executable, (string[])["source", "list"], TimeSpan.FromSeconds(12), cancellationToken);
            string output = FirstNonEmptyLine(sources.StandardOutput, sources.StandardError);
            WingetSources = sources.ExitCode == 0
                ? "Fontes configuradas:" + Environment.NewLine + Truncate(sources.StandardOutput.Trim(), 1200)
                : $"Fontes: não foi possível listar (código {sources.ExitCode}).";
            report.Add("WinGet fontes:");
            report.Add(sources.ExitCode == 0 ? Truncate(sources.StandardOutput.Trim(), 3000) : Truncate(output, 1000));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WingetSources = "Fontes: consulta excedeu o tempo ou falhou.";
            report.Add($"WinGet fontes: erro {exception.GetType().Name}");
        }

        if (string.IsNullOrEmpty(WingetDetail))
            WingetDetail = $"Executável: {executable}";
    }

    private void ApplyCatalogHealth(CatalogApiHealth health, List<string> report)
    {
        foreach (var source in health.Sources)
        {
            bool isWinget = source.Source.Equals("WinGet", StringComparison.OrdinalIgnoreCase);
            bool isStore = source.Source.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase);
            string status = source.Available ? "Disponível" : "Indisponível";
            string details = source.Available
                ? $"{source.AppCount:N0} aplicativos · catálogo de {source.GeneratedUtc?.ToLocalTime():dd/MM/yyyy HH:mm} · resposta em {source.ResponseTime?.TotalSeconds:F1}s"
                : source.Error ?? "Falha ao consultar o catálogo.";

            if (isWinget)
            {
                WingetCatalogStatus = status;
                WingetCatalogDetails = details;
            }
            else if (isStore)
            {
                StoreCatalogStatus = status;
                StoreCatalogDetails = details;
            }

            report.Add(source.Available
                ? $"API {source.Source}: disponível; {source.AppCount} apps; catálogo {source.GeneratedUtc:O}; resposta {source.ResponseTime?.TotalMilliseconds:F0} ms"
                : $"API {source.Source}: indisponível; {source.Error}");
        }

        if (!health.Sources.Any(source => source.Source.Equals("WinGet", StringComparison.OrdinalIgnoreCase)))
        {
            WingetCatalogStatus = "Sem resposta";
            WingetCatalogDetails = "A API não retornou o catálogo WinGet.";
        }
        if (!health.Sources.Any(source => source.Source.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase)))
        {
            StoreCatalogStatus = "Sem resposta";
            StoreCatalogDetails = "A API não retornou o catálogo Microsoft Store.";
        }
    }

    private static async Task<WingetCommandResult> RunWingetCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("O processo do WinGet não iniciou.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task readers = Task.WhenAll(stdout, stderr);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new WingetCommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                WinProvisionLog.Error(exception, "StopWingetStatusCheck");
            }

            await process.WaitForExitAsync();
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException("A consulta do WinGet excedeu o tempo limite.");
        }
        finally
        {
            await readers;
        }
    }

    private static string FirstNonEmptyLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0) ?? string.Empty;

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private void ToggleHistory()
    {
        IsAboutVisible = false;
        IsHistoryVisible = !IsHistoryVisible;
        IsStatusVisible = false;
        IsLogVisible = false;
        if (IsHistoryVisible)
            RefreshHistory();
    }

    private void RefreshHistory()
    {
        HistoryEntries.Clear();
        foreach (var entry in _history.Entries)
            HistoryEntries.Add(entry);
    }

    private void OnHistoryChanged() => _dispatcher.Post(() =>
    {
        if (!_disposed && IsHistoryVisible)
            RefreshHistory();
    });

    private void ClearHistory() => _history.Clear();

    private void OpenTaskLogFile()
    {
        try { _desktop.OpenFile(_history.TaskLogPath); }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "MoreViewModel.OpenTaskLogFile");
            StatusMessage = "O log de operações ainda não está disponível.";
            HasStatusMessage = true;
        }
    }

    private void ShowAbout()
    {
        if (IsAboutVisible)
            return;

        IsStatusVisible = false;
        IsHistoryVisible = false;
        IsLogVisible = false;
        IsAboutVisible = true;
        ShowBrowserConnecting(retry: false);
    }

    private async Task OpenProjectWebsiteAsync()
    {
        try
        {
            await _desktop.OpenUriAsync(ProjectUri);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "MoreViewModel.OpenProjectWebsite");
            ShowBrowserError("Não foi possível abrir o site no navegador padrão. Tente novamente.");
        }
    }

    public void ShowBrowserConnecting(bool retry) =>
        SetBrowserStatus(retry ? "Tentando carregar o GitHub Pages…" : "Conectando ao GitHub Pages…");

    public void ShowBrowserError(string message) => SetBrowserStatus(message);

    public void HideBrowserStatus() => IsBrowserStatusVisible = false;

    private void SetBrowserStatus(string message)
    {
        BrowserStatus = message;
        IsBrowserStatusVisible = true;
    }

    private async Task OpenLogAsync(CancellationToken cancellationToken)
    {
        IsAboutVisible = false;
        IsStatusVisible = false;
        IsHistoryVisible = false;
        IsLogVisible = true;
        await LoadLogAsync(cancellationToken);
    }

    private async Task LoadLogAsync(CancellationToken cancellationToken)
    {
        IsLoadingLog = true;
        RefreshLogCommand.NotifyCanExecuteChanged();
        try
        {
            (LogText, LogStatus) = await ReadLogSnapshotAsync(LogFilePath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "MoreViewModel.LoadLog");
            LogStatus = $"Não foi possível ler o log: {exception.Message}";
        }
        finally
        {
            IsLoadingLog = false;
            RefreshLogCommand.NotifyCanExecuteChanged();
        }
    }

    internal static async Task<(string Text, string Status)> ReadLogSnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return (string.Empty, "O log ainda não foi criado. Ele aparecerá aqui quando houver registros.");

        var lines = new Queue<string>(MaxDisplayedLogLines);
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lines.Count == MaxDisplayedLogLines)
                lines.Dequeue();
            lines.Enqueue(line);
        }

        return (string.Join(Environment.NewLine, lines), $"Exibindo as {lines.Count:N0} linhas mais recentes.");
    }

    private void CopyDiagnostic()
    {
        LastChecked = _clipboard.TrySetText(_diagnosticText)
            ? "Diagnóstico copiado"
            : "Não foi possível copiar o diagnóstico";
    }

    private void CloseLog()
    {
        IsLogVisible = false;
        OpenLogCommand.Cancel();
        RefreshLogCommand.Cancel();
    }

    private void OpenLogFile()
    {
        try
        {
            _desktop.OpenFile(LogFilePath);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "MoreViewModel.OpenLogFile");
            StatusMessage = "O log ainda não está disponível.";
            HasStatusMessage = true;
        }
    }

    public void CancelLogLoad()
    {
        OpenLogCommand.Cancel();
        RefreshLogCommand.Cancel();
    }

    public void Dispose()
    {
        _disposed = true;
        CheckStatusCommand.Cancel();
        CancelLogLoad();
        _history.Changed -= OnHistoryChanged;
    }

    private readonly record struct WingetCommandResult(int ExitCode, string StandardOutput, string StandardError);

    partial void OnIsCheckingStatusChanged(bool value)
    {
        CheckStatusCommand?.NotifyCanExecuteChanged();
        CancelStatusCommand?.NotifyCanExecuteChanged();
    }

    partial void OnIsLogVisibleChanged(bool value) => RefreshLogCommand?.NotifyCanExecuteChanged();

    partial void OnIsLoadingLogChanged(bool value) => RefreshLogCommand?.NotifyCanExecuteChanged();

    partial void OnIsAboutVisibleChanged(bool value) => RetryAboutCommand?.NotifyCanExecuteChanged();
}
