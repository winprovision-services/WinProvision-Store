using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record ElevatorStatus(bool Present, bool Verified, bool Authorized);
public interface IElevatorSettingsService
{
    bool IsAllowed { get; }
    Task SetAllowedAsync(bool allowed);
    Task<ElevatorStatus> InspectAsync(CancellationToken token);
    Task AuthorizeAsync(CancellationToken token);
    Task RevokeAsync();
}

public sealed class ElevatorSettingsService : IElevatorSettingsService
{
    public bool IsAllowed => new ElevationPreferencesService().AllowElevation;
    public async Task SetAllowedAsync(bool allowed)
    {
        new ElevationPreferencesService().SetAllowed(allowed);
        if (!allowed) await GsudoElevationService.StopCacheAsync();
    }
    public async Task<ElevatorStatus> InspectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!GsudoElevationService.IsBundled) return new(false, false, false);
        try { await Task.Run(GsudoElevationService.VerifyBundledIntegrity, token); }
        catch (System.IO.InvalidDataException) { return new(true, false, false); }
        return new(true, true, await GsudoElevationService.IsCacheAvailableAsync(token));
    }
    public Task AuthorizeAsync(CancellationToken token) => GsudoElevationService.AuthorizeCacheAsync(token);
    public Task RevokeAsync() => GsudoElevationService.StopCacheAsync();
}
