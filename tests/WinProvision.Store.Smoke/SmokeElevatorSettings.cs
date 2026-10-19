using WinProvision.Store.Services;

namespace WinProvision.Store;

internal sealed class SmokeElevatorSettings : IElevatorSettingsService
{
    public bool IsAllowed { get; private set; } = true;
    public bool Authorized { get; private set; }
    public Task SetAllowedAsync(bool allowed) { IsAllowed = allowed; if (!allowed) Authorized = false; return Task.CompletedTask; }
    public Task<ElevatorStatus> InspectAsync(CancellationToken token) => Task.FromResult(new ElevatorStatus(true, true, Authorized));
    public Task AuthorizeAsync(CancellationToken token) { if (!IsAllowed) throw new InvalidOperationException("Bloqueado."); Authorized = true; return Task.CompletedTask; }
    public Task RevokeAsync() { Authorized = false; return Task.CompletedTask; }
}
