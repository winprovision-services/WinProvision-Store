using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal sealed class SmokeElevatorSettings : IElevatorSettingsService
{
    private readonly ElevationPreferencesService _preferences;
    public SmokeElevatorSettings(ElevationPreferencesService preferences) => _preferences = preferences;
    public bool IsAllowed => _preferences.AllowElevation;
    public bool Authorized { get; private set; }
    public Task SetPreferenceAsync(string key, bool value)
    {
        _preferences.SetBoolean(key, value);
        if (!IsAllowed) Authorized = false;
        return Task.CompletedTask;
    }
    public Task SetAllowedAsync(bool allowed) => SetPreferenceAsync("allowElevation", allowed);
    public Task<ElevatorStatus> InspectAsync(CancellationToken token) => Task.FromResult(new ElevatorStatus(true, true, Authorized));
    public Task AuthorizeAsync(CancellationToken token) { if (!IsAllowed) throw new InvalidOperationException("Bloqueado."); Authorized = true; return Task.CompletedTask; }
    public Task RevokeAsync() { Authorized = false; return Task.CompletedTask; }
}
