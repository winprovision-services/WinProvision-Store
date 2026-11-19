using System.Diagnostics;
using System.Text;
using WinProvision.Elevation;
using WinProvision.Providers.WinGet.Cli.Commands;
using WinProvision.Providers.WinGet.Cli.Contracts;
using WinProvision.Providers.WinGet.Cli.Discovery;
using WinProvision.Providers.WinGet.Cli.Parsing;

namespace WinProvision.Providers.WinGet.Cli.Client;

public sealed class WinGetCliClient(WinGetCliDiscovery? discovery = null) : IWinGetCliClient
{
    private readonly WinGetCliDiscovery _discovery = discovery ?? new();
    private readonly object _pathGate = new();
    private string? _preferredPath;

    public void SetPreferredPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_pathGate) _preferredPath = path;
    }

    public Task<WinGetCliResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        RunAsync(new(WinGetCliOperation.Probe, [], IsMutation: false), null, cancellationToken);
    public Task<WinGetCliResult> SearchAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Search, false), progress, cancellationToken);
    public Task<WinGetCliResult> ListInstalledAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.List, false), progress, cancellationToken);
    public Task<WinGetCliResult> ShowAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Show, false), progress, cancellationToken);
    public Task<WinGetCliResult> DiscoverUpdatesAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.UpgradeList, false), progress, cancellationToken);
    public Task<WinGetCliResult> InstallAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Install, true), progress, cancellationToken);
    public Task<WinGetCliResult> UpgradeAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Upgrade, true), progress, cancellationToken);
    public Task<WinGetCliResult> UninstallAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Uninstall, true), progress, cancellationToken);
    public Task<WinGetCliResult> DownloadAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Download, false), progress, cancellationToken);
    public Task<WinGetCliResult> ExportAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Export, false), progress, cancellationToken);
    public Task<WinGetCliResult> ManageSourcesAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default) => RunAsync(Require(invocation, WinGetCliOperation.Source, false), progress, cancellationToken);

    private static WinGetCliInvocation Require(WinGetCliInvocation invocation, WinGetCliOperation operation, bool mutation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Operation != operation || invocation.IsMutation != mutation)
            throw new ArgumentException($"Invocation incompatível com {operation}.", nameof(invocation));
        return invocation;
    }

    private async Task<WinGetCliResult> RunAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress, CancellationToken token)
    {
        string? path;
        lock (_pathGate) path = _preferredPath;
        path = _discovery.Resolve(path);
        if (path is null) return Result(WinGetCliStatus.Unavailable, null, WinGetCliExitCodeOrigin.None, false, false, "winget.exe indisponível.");
        IReadOnlyList<string> args = WinGetCliCommandBuilder.Build(invocation.Operation, invocation.Arguments);
        return invocation.ExecutionMode == WinGetCliExecutionMode.Elevated
            ? await RunElevatedAsync(path, args, invocation, progress, token).ConfigureAwait(false)
            : await RunLocalAsync(path, args, invocation, progress, token).ConfigureAwait(false);
    }

    private static async Task<WinGetCliResult> RunLocalAsync(string path, IReadOnlyList<string> args, WinGetCliInvocation invocation,
        IProgress<WinGetCliEvent>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo { FileName = path, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = invocation.WorkingDirectory ?? Environment.CurrentDirectory };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return Result(WinGetCliStatus.Unavailable, null, WinGetCliExitCodeOrigin.Infrastructure, false, false, "Process.Start retornou false.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { return Result(WinGetCliStatus.Unavailable, ex.HResult, WinGetCliExitCodeOrigin.Infrastructure, false, false, ex.Message); }

        var stdout = new BoundedCapture();
        var stderr = new BoundedCapture();
        Task readOut = PumpAsync(process.StandardOutput, stdout, false, progress);
        Task readErr = PumpAsync(process.StandardError, stderr, true, progress);
        try
        {
            if (invocation.IsMutation)
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            else
                await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!invocation.IsMutation)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
            return Result(WinGetCliStatus.Cancelled, null, WinGetCliExitCodeOrigin.None, true, false, null, stdout, stderr);
        }
        await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
        int code = process.ExitCode;
        WinGetCliStatus status = WinGetCliParsing.MapExitCode(code, invocation.Operation);
        bool reboot = code is 3010 or 1641 || unchecked((uint)code) is 0x8A150109 or 0x8A15010A or 0x8A15010B;
        return Result(status, code, WinGetCliExitCodeOrigin.WinGet, true, invocation.IsMutation, null, stdout, stderr, reboot);
    }

    private static async Task<WinGetCliResult> RunElevatedAsync(string path, IReadOnlyList<string> args, WinGetCliInvocation invocation,
        IProgress<WinGetCliEvent>? progress, CancellationToken token)
    {
        var dispatch = new DispatchProgress(progress);
        var command = new ElevatedCommand(path, args, ElevationCategory.WinGet, invocation.WorkingDirectory);
        ElevationResult result = await ElevationRuntime.Current.RunAsync(command, dispatch, token).ConfigureAwait(false);
        WinGetCliStatus status = result.Status switch
        {
            ElevationStatus.Success => result.ExitCode is int code ? WinGetCliParsing.MapExitCode(code, invocation.Operation) : WinGetCliStatus.OutcomeUnknown,
            ElevationStatus.UacCancelled => WinGetCliStatus.UacCancelled,
            ElevationStatus.Rejected => WinGetCliStatus.ElevationRejected,
            ElevationStatus.InvalidCredentials => WinGetCliStatus.InvalidCredentials,
            ElevationStatus.HelperError or ElevationStatus.Failed when invocation.IsMutation && dispatch.Started => WinGetCliStatus.OutcomeUnknown,
            ElevationStatus.HelperError or ElevationStatus.Failed => WinGetCliStatus.HelperError,
            ElevationStatus.Cancelled when invocation.IsMutation && dispatch.Started => WinGetCliStatus.OutcomeUnknown,
            ElevationStatus.Cancelled => WinGetCliStatus.Cancelled,
            _ => WinGetCliStatus.UnexpectedError
        };
        int? codeResult = result.Status == ElevationStatus.Success ? result.ExitCode : null;
        WinGetCliExitCodeOrigin origin = codeResult.HasValue ? WinGetCliExitCodeOrigin.WinGet
            : result.Status == ElevationStatus.Success ? WinGetCliExitCodeOrigin.Infrastructure : WinGetCliExitCodeOrigin.None;
        string output = string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }.Where(s => !string.IsNullOrWhiteSpace(s)));
        bool reboot = codeResult is 3010 or 1641 || codeResult is int c && unchecked((uint)c) is 0x8A150109 or 0x8A15010A or 0x8A15010B;
        return new(status, codeResult, origin, null,
            reboot, dispatch.Started, invocation.IsMutation && dispatch.Started, dispatch.Started ? WinGetCliPhase.Started : WinGetCliPhase.None,
            WinGetCliParsing.BoundSummary(output), output.Length == 0 ? null : output, result.Error, result.StandardOutput, result.StandardError);
    }

    private static async Task PumpAsync(StreamReader reader, BoundedCapture capture, bool error, IProgress<WinGetCliEvent>? progress)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            capture.Append(line);
            progress?.Report(WinGetCliParsing.ParseProgressLine(line, error));
        }
    }

    private static WinGetCliResult Result(WinGetCliStatus status, int? code, WinGetCliExitCodeOrigin origin,
        bool processStarted, bool mayHaveStarted, string? message, BoundedCapture? stdout = null, BoundedCapture? stderr = null, bool reboot = false)
    {
        string outText = stdout?.Value ?? string.Empty;
        string errText = stderr?.Value ?? string.Empty;
        string summary = WinGetCliParsing.BoundSummary(string.Join(Environment.NewLine, new[] { outText, errText }.Where(s => s.Length > 0)));
        return new(status, code, origin, origin == WinGetCliExitCodeOrigin.Infrastructure ? code : null, reboot,
            processStarted, mayHaveStarted, processStarted ? WinGetCliPhase.Started : WinGetCliPhase.None,
            summary, null, message, outText, errText);
    }

    private sealed class DispatchProgress(IProgress<WinGetCliEvent>? progress) : IElevationDispatchProgress
    {
        private int _started;
        public bool Started => Volatile.Read(ref _started) != 0;
        public void OnStarted() => Interlocked.Exchange(ref _started, 1);
        public void Report(string value) => progress?.Report(WinGetCliParsing.ParseProgressLine(value));
    }

    private sealed class BoundedCapture(int maximum = 262_144)
    {
        private readonly StringBuilder _builder = new();
        private readonly object _gate = new();
        public string Value { get { lock (_gate) return _builder.ToString(); } }
        public void Append(string line)
        {
            lock (_gate)
            {
                _builder.AppendLine(line);
                if (_builder.Length > maximum)
                {
                    string value = _builder.ToString();
                    int keep = maximum / 2;
                    _builder.Clear().Append(value.AsSpan(0, keep)).Append("\n… output truncated …\n").Append(value.AsSpan(value.Length - keep));
                }
            }
        }
    }
}
