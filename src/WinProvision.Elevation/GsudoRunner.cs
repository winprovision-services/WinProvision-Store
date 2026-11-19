using System.Diagnostics;
using System.Security.Cryptography;

namespace WinProvision.Elevation;

public sealed record GsudoRunnerOptions(string HelperPath, string ExpectedSha256, Action<string>? DiagnosticLog = null,
    Func<bool>? IsElevationAllowed = null)
{
    public static GsudoRunnerOptions BundledDefault() => new(
        Path.Combine(AppContext.BaseDirectory, "Tool", "gsudo", "WinProvision Elevator.exe"),
        "806c1160adcc50f168dd9037880a593d12aace9601206368368b8288689238e0");
}

public sealed class GsudoRunner : IElevatedRunner, IElevationCacheManager
{
    internal const int HelperErrorExitCode = 999;
    private readonly GsudoRunnerOptions _options;
    private readonly GsudoCacheController _cache;
    private readonly Func<ElevatedCommand, IProgress<string>?, CancellationToken, Task<ElevationResult>>? _runCommandForTests;

    public string HelperPath => _options.HelperPath;
    public bool IsHelperPresent => File.Exists(_options.HelperPath);

    public GsudoRunner(GsudoRunnerOptions options, GsudoCacheController? cacheController = null)
    {
        _options = options;
        _cache = cacheController ?? new GsudoCacheController(options);
    }

    internal GsudoRunner(GsudoRunnerOptions options, GsudoCacheController cacheController,
        Func<ElevatedCommand, IProgress<string>?, CancellationToken, Task<ElevationResult>> runCommandForTests)
    {
        _options = options;
        _cache = cacheController;
        _runCommandForTests = runCommandForTests;
    }

    public async Task<ElevationResult> RunAsync(ElevatedCommand command, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Environment.UserInteractive)
                return new(ElevationStatus.HelperError, null, string.Empty, string.Empty,
                    "Não há área de trabalho interativa para exibir o UAC.");
            if (_options.IsElevationAllowed?.Invoke() == false)
                return new(ElevationStatus.Rejected, null, string.Empty, string.Empty,
                    "A preferência do WinProvision Store bloqueia pedidos pelo WinProvision Elevator.");

            VerifyHelper(_options);
            ElevationResult? cacheResult = await _cache.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            if (cacheResult is not null)
            {
                if (cacheResult.Status == ElevationStatus.Cancelled)
                    return cacheResult;
                ElevationStatus cacheStatus = ClassifyHelperFailure(cacheResult.StandardOutput + Environment.NewLine + cacheResult.StandardError);
                return new(cacheStatus, null, string.Empty, string.Empty, cacheResult.Error);
            }

            cancellationToken.ThrowIfCancellationRequested();
            VerifyHelper(_options);
            if (command.ExpectedSha256 is not null)
            {
                try
                {
                    if (!await ElevationIntegrityVerifier.VerifyAsync(command.ExecutablePath, command.ExpectedSha256, cancellationToken).ConfigureAwait(false))
                        return new(ElevationStatus.Rejected, null, string.Empty, string.Empty,
                            "O SHA-256 do arquivo não corresponde ao valor esperado.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return new(ElevationStatus.Rejected, null, string.Empty, string.Empty, exception.Message);
                }
            }
            if (_runCommandForTests is not null)
                return await _runCommandForTests(command, progress, cancellationToken).ConfigureAwait(false);
            var startInfo = ProcessRunner.CreateStartInfo(_options.HelperPath,
                command.WorkingDirectory ?? Path.GetDirectoryName(command.ExecutablePath));
            startInfo.Arguments = GsudoCommandLineBuilder.BuildHelperCommandLine(command);

            return await ProcessRunner.RunAsync(startInfo, progress, cancellationToken, waitAfterStart: true,
                classifyHelperExitCode: true, diagnosticLog: _options.DiagnosticLog).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ElevationStatus.Cancelled, null, string.Empty, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(ElevationStatus.HelperError, null, string.Empty, string.Empty, exception.Message);
        }
    }

    internal static void VerifyHelper(GsudoRunnerOptions options)
    {
        using var stream = File.OpenRead(options.HelperPath);
        byte[] expected = Convert.FromHexString(options.ExpectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expected))
            throw new InvalidDataException("O helper de elevação diverge da versão distribuída.");
    }

    public void VerifyHelperIntegrity() => VerifyHelper(_options);

    public Task<bool> IsCacheAvailableAsync(CancellationToken cancellationToken) => _cache.IsAvailableAsync(cancellationToken);

    public async Task<ElevationResult> AuthorizeAsync(CancellationToken cancellationToken)
    {
        if (_options.IsElevationAllowed?.Invoke() == false)
            return new(ElevationStatus.Rejected, null, string.Empty, string.Empty,
                "A preferência do WinProvision Store bloqueia pedidos pelo WinProvision Elevator.");
        if (ElevationRunner.IsCurrentProcessElevated())
            return new(ElevationStatus.Success, 0, string.Empty, string.Empty);

        ElevationResult? result = await _cache.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        if (result is null) return new(ElevationStatus.Success, 0, string.Empty, string.Empty);
        if (result.Status == ElevationStatus.Cancelled) return result;
        ElevationStatus status = ClassifyHelperFailure(result.StandardOutput + Environment.NewLine + result.StandardError);
        return new(status, null, string.Empty, string.Empty, result.Error);
    }

    public Task StopAsync() => _cache.StopAsync();

    internal static ElevationStatus ClassifyHelperFailure(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return ElevationStatus.HelperError;
        if (output.Contains("canceled by the user", StringComparison.OrdinalIgnoreCase)
            || output.Contains("cancelled by the user", StringComparison.OrdinalIgnoreCase))
            return ElevationStatus.UacCancelled;
        if (output.Contains("user name or password is incorrect", StringComparison.OrdinalIgnoreCase))
            return ElevationStatus.InvalidCredentials;
        return ElevationStatus.HelperError;
    }

    internal static bool IsCacheAvailable(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        string normalized = output.Trim();
        return normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\"CacheAvailable\":true", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("CacheAvailable: true", StringComparison.OrdinalIgnoreCase);
    }
}
