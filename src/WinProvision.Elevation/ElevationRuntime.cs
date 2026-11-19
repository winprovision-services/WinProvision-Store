namespace WinProvision.Elevation;

/// <summary>Process-wide bridge used by shared Core services and console entry points.</summary>
public static class ElevationRuntime
{
    private static IElevatedRunner? _configuredRunner;
    private static readonly Lazy<IElevatedRunner> DefaultRunner = new(CreateDefaultRunnerCore,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static IElevatedRunner Current => Volatile.Read(ref _configuredRunner) ?? DefaultRunner.Value;

    public static IElevationCacheManager CacheManager => Current as IElevationCacheManager
        ?? throw new InvalidOperationException("O runner configurado não gerencia o cache do helper.");

    public static void Configure(IElevatedRunner runner)
        => Interlocked.Exchange(ref _configuredRunner, runner ?? throw new ArgumentNullException(nameof(runner)));

    public static IElevatedRunner CreateDefaultRunner(Action<string>? diagnosticLog = null,
        Func<bool>? isElevationAllowed = null)
        => CreateDefaultRunnerCore(diagnosticLog, isElevationAllowed);

    private static IElevatedRunner CreateDefaultRunnerCore() => CreateDefaultRunnerCore(null, null);

    private static IElevatedRunner CreateDefaultRunnerCore(Action<string>? diagnosticLog,
        Func<bool>? isElevationAllowed)
    {
        GsudoRunnerOptions options = GsudoRunnerOptions.BundledDefault() with
        {
            DiagnosticLog = diagnosticLog,
            IsElevationAllowed = isElevationAllowed
        };
        var direct = new DirectRunner();
        var gsudo = new GsudoRunner(options);
        var policy = new ElevationPathPolicy(message =>
        {
            if (diagnosticLog is not null) diagnosticLog(message);
            else System.Diagnostics.Trace.TraceInformation(message);
        });
        return new ElevationRunner(policy, direct, gsudo);
    }
}
