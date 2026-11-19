using System.Diagnostics;

namespace WinProvision.Elevation;

public sealed class GsudoCacheController
{
    public const int IdleDurationMinutes = 5;
    public static TimeSpan IdleDuration => TimeSpan.FromMinutes(IdleDurationMinutes);
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static bool _started;
    private readonly GsudoRunnerOptions _options;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ElevationResult>>? _runControlForTests;

    public GsudoCacheController(GsudoRunnerOptions options) => _options = options;

    internal GsudoCacheController(GsudoRunnerOptions options,
        Func<IReadOnlyList<string>, CancellationToken, Task<ElevationResult>> runControlForTests)
    {
        _options = options;
        _runControlForTests = runControlForTests ?? throw new ArgumentNullException(nameof(runControlForTests));
    }

    public async Task<ElevationResult?> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await CacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                ElevationResult status = await RunControlAsync(["status", "CacheAvailable"], cancellationToken,
                    TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                if (status.Success && GsudoRunner.IsCacheAvailable(status.StandardOutput + status.StandardError))
                    return null;
                _started = false;
            }

            string duration = IdleDuration.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
            ElevationResult result = await RunControlAsync(["cache", "on", "--duration", duration],
                cancellationToken, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            if (result.Success) _started = true;
            return result.Success ? null : result;
        }
        finally { CacheGate.Release(); }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        await CacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started) return false;
            ElevationResult status = await RunControlAsync(["status", "CacheAvailable"], cancellationToken,
                TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            bool available = status.Success && GsudoRunner.IsCacheAvailable(status.StandardOutput + status.StandardError);
            if (!available) _started = false;
            return available;
        }
        finally { CacheGate.Release(); }
    }

    public async Task StopAsync()
    {
        await CacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_started) return;
            try
            {
                _ = await RunControlAsync(["cache", "off"], CancellationToken.None,
                    TimeSpan.FromSeconds(2), tolerateHashFailure: true).ConfigureAwait(false);
            }
            catch { /* Cache may already have expired or its helper may have exited. */ }
            _started = false;
        }
        finally { CacheGate.Release(); }
    }

    private async Task<ElevationResult> RunControlAsync(IReadOnlyList<string> arguments, CancellationToken token,
        TimeSpan timeout, bool tolerateHashFailure = false)
    {
        if (_runControlForTests is not null)
            return await _runControlForTests(arguments, token).ConfigureAwait(false);

        if (!tolerateHashFailure) GsudoRunner.VerifyHelper(_options);
        var startInfo = ProcessRunner.CreateStartInfo(_options.HelperPath, Environment.CurrentDirectory);
        startInfo.ArgumentList.Add("--loglevel");
        startInfo.ArgumentList.Add("Error");
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutToken.CancelAfter(timeout);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            timeoutToken.Token.ThrowIfCancellationRequested();
            if (!process.Start())
                return new(ElevationStatus.HelperError, null, string.Empty, string.Empty, "Não foi possível iniciar o helper.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(ElevationStatus.Cancelled, null, string.Empty, string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return new(ElevationStatus.HelperError, null, string.Empty, string.Empty, exception.Message);
        }

        Task<string> stdout = DrainControlOutputAsync(process.StandardOutput);
        Task<string> stderr = DrainControlOutputAsync(process.StandardError);
        try { await process.WaitForExitAsync(timeoutToken.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new(token.IsCancellationRequested ? ElevationStatus.Cancelled : ElevationStatus.HelperError,
                null, string.Empty, string.Empty, token.IsCancellationRequested ? null : "Tempo limite do helper excedido.");
        }

        return new(process.ExitCode == 0 ? ElevationStatus.Success : ElevationStatus.HelperError,
            process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<string> DrainControlOutputAsync(StreamReader reader)
    {
        var capture = new ElevationOutputCapture();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            capture.Add(line);
        return capture.GetText();
    }
}
