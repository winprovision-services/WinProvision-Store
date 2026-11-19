using System.Security.Principal;

namespace WinProvision.Elevation;

public sealed class ElevationRunner : IElevatedRunner, IElevationCacheManager
{
    private readonly IElevationPolicy _policy;
    private readonly IElevatedRunner _directRunner;
    private readonly IElevatedRunner _gsudoRunner;
    private readonly Func<bool> _isAlreadyElevated;
    private readonly IElevationCacheManager? _cacheManager;

    public ElevationRunner(IElevationPolicy policy, IElevatedRunner directRunner, IElevatedRunner gsudoRunner,
        Func<bool>? isAlreadyElevated = null)
    {
        _policy = policy;
        _directRunner = directRunner;
        _gsudoRunner = gsudoRunner;
        _cacheManager = gsudoRunner as IElevationCacheManager;
        _isAlreadyElevated = isAlreadyElevated ?? IsCurrentProcessElevated;
    }

    public string HelperPath => CacheManager.HelperPath;
    public bool IsHelperPresent => CacheManager.IsHelperPresent;
    public void VerifyHelperIntegrity() => CacheManager.VerifyHelperIntegrity();
    public Task<bool> IsCacheAvailableAsync(CancellationToken cancellationToken) => CacheManager.IsCacheAvailableAsync(cancellationToken);
    public Task<ElevationResult> AuthorizeAsync(CancellationToken cancellationToken) => CacheManager.AuthorizeAsync(cancellationToken);
    public Task StopAsync() => CacheManager.StopAsync();
    private IElevationCacheManager CacheManager => _cacheManager
        ?? throw new InvalidOperationException("O runner gsudo configurado não oferece gestão de cache.");

    public async Task<ElevationResult> RunAsync(ElevatedCommand command, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ElevationPolicyResult decision;
        try { decision = await _policy.EvaluateAsync(command, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(ElevationStatus.Cancelled, null, string.Empty, string.Empty); }

        if (!decision.Allowed)
            return new(ElevationStatus.Rejected, null, string.Empty, string.Empty, decision.Reason);
        if (cancellationToken.IsCancellationRequested)
            return new(ElevationStatus.Cancelled, null, string.Empty, string.Empty);

        return await (_isAlreadyElevated() ? _directRunner : _gsudoRunner)
            .RunAsync(command, progress, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsCurrentProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value == "S-1-5-18") return true;
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
