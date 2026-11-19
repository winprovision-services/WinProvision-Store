namespace WinProvision.Elevation;

public enum ElevationCategory
{
    WinGet,
    Chocolatey,
    Office,
    DownloadedInstaller,
    Uninstall,
    Network,
    AppUpdate,
    ComHost,
    LanClient,
    Other
}

public enum ElevationStatus
{
    Success,
    Failed,
    UacCancelled,
    InvalidCredentials,
    HelperError,
    Cancelled,
    Rejected
}

public enum ElevationPolicyMode
{
    LogOnly,
    Enforce
}

public sealed record ElevatedCommand
{
    public ElevatedCommand(string executablePath, IReadOnlyList<string> arguments,
        ElevationCategory category, string? workingDirectory = null, string? expectedSha256 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(argument => argument is null))
            throw new ArgumentException("A lista de argumentos não pode conter null.", nameof(arguments));

        ExecutablePath = executablePath;
        Arguments = Array.AsReadOnly(arguments.ToArray());
        Category = category;
        WorkingDirectory = workingDirectory;
        ExpectedSha256 = expectedSha256;
    }

    public string ExecutablePath { get; }
    public IReadOnlyList<string> Arguments { get; }
    public string? WorkingDirectory { get; }
    public string? ExpectedSha256 { get; }
    public ElevationCategory Category { get; }
}

public sealed record ElevationResult(
    ElevationStatus Status,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    string? Error = null)
{
    public bool Success => Status == ElevationStatus.Success;
}

public interface IElevatedRunner
{
    Task<ElevationResult> RunAsync(
        ElevatedCommand command,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IElevationDispatchProgress : IProgress<string>
{
    void OnStarted();
}

public interface IElevationCacheManager
{
    string HelperPath { get; }
    bool IsHelperPresent { get; }
    void VerifyHelperIntegrity();
    Task<bool> IsCacheAvailableAsync(CancellationToken cancellationToken);
    Task<ElevationResult> AuthorizeAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

public sealed record ElevationPolicyResult(bool Allowed, string Reason)
{
    public static ElevationPolicyResult Permit { get; } = new(true, "permitido");
}

public interface IElevationPolicy
{
    ValueTask<ElevationPolicyResult> EvaluateAsync(ElevatedCommand command, CancellationToken cancellationToken);
}

public sealed class AllowAllElevationPolicy : IElevationPolicy
{
    public ValueTask<ElevationPolicyResult> EvaluateAsync(ElevatedCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ElevationPolicyResult.Permit);
    }
}
