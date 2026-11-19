using WinProvision.Core.Services;
using WinProvision.Elevation;
using System.Diagnostics;

namespace WinProvision.Store.Services;

public sealed record ElevatorStatus(
    bool Present,
    bool Verified,
    bool Authorized,
    string Path = "",
    string Version = "",
    DateTimeOffset? LastVerified = null);
public interface IElevatorSettingsService
{
    bool IsAllowed { get; }
    Task SetPreferenceAsync(string key, bool value);
    Task SetAllowedAsync(bool allowed);
    Task<ElevatorStatus> InspectAsync(CancellationToken token);
    Task AuthorizeAsync(CancellationToken token);
    Task RevokeAsync();
}

public sealed class ElevatorSettingsService : IElevatorSettingsService
{
    private readonly ElevationPreferencesService _preferences;
    private readonly IElevationCacheManager _cache;

    public ElevatorSettingsService(ElevationPreferencesService? preferences = null, IElevationCacheManager? cache = null)
    {
        _preferences = preferences ?? new ElevationPreferencesService();
        _cache = cache ?? ElevationRuntime.CacheManager;
    }

    public bool IsAllowed => _preferences.AllowElevation;
    public async Task SetPreferenceAsync(string key, bool value)
    {
        _preferences.SetBoolean(key, value);
        if (!_preferences.AllowElevation)
            await _cache.StopAsync();
    }
    public Task SetAllowedAsync(bool allowed) => SetPreferenceAsync("allowElevation", allowed);
    public async Task<ElevatorStatus> InspectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_cache.IsHelperPresent)
            return new(false, false, false, _cache.HelperPath, "Não disponível", null);
        try { await Task.Run(_cache.VerifyHelperIntegrity, token); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
            { return new(true, false, false, _cache.HelperPath, GetVersion(), DateTimeOffset.UtcNow); }
        return new(true, true, await _cache.IsCacheAvailableAsync(token),
            _cache.HelperPath, GetVersion(), DateTimeOffset.UtcNow);
    }
    public async Task AuthorizeAsync(CancellationToken token)
    {
        ElevationResult result = await _cache.AuthorizeAsync(token);
        if (result.Success) return;
        if (result.Status == ElevationStatus.Cancelled) throw new OperationCanceledException(token);
        int errorCode = result.Status switch
        {
            ElevationStatus.UacCancelled => 1223,
            ElevationStatus.InvalidCredentials => 1326,
            ElevationStatus.Rejected => 1625,
            _ => 1627
        };
        throw new System.ComponentModel.Win32Exception(errorCode, result.Error ?? "A autorização do WinProvision Elevator falhou.");
    }
    public Task RevokeAsync() => _cache.StopAsync();

    private string GetVersion()
    {
        try { return FileVersionInfo.GetVersionInfo(_cache.HelperPath).FileVersion ?? "Desconhecida"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            CoreLog.Warning(exception, "Elevator.Version");
            return "Desconhecida";
        }
    }
}
