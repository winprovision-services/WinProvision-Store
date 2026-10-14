using WinProvision.Core.Services;
using WinProvision.Store.Services;
namespace WinProvision.Store;
internal sealed partial class SmokeIgnoredUpdates : IUpdatesIgnoreStore
{
    private readonly Dictionary<string,string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<IgnoredUpdateEntry> GetIgnoredUpdates() => _ignored.Select(p=>new IgnoredUpdateEntry(p.Key,p.Value)).ToArray();
    public void Ignore(string id,string version)=>_ignored[id]=version;
    public void Unignore(string id)=>_ignored.Remove(id);
    public bool IsIgnored(string id,string version)=>_ignored.TryGetValue(id,out var stored)&&(stored=="*"||stored==version);
}
internal sealed partial class SmokeUpdateStartup(ApplicationPreferencesService preferences) : IBackgroundUpdateStartupService
{
    public bool IsEnabled=>preferences.AutoUpdateAtStartup;
    public Task<string?> MigrateLegacyTaskAsync(CancellationToken token=default)=>Task.FromResult<string?>(null);
    public Task<(bool Success,string? Message)> EnableAsync(CancellationToken token=default) { preferences.SetAutoUpdateAtStartup(true);return Task.FromResult<(bool,string?)>((true,null)); }
    public Task<(bool Success,string? Message)> DisableAsync(CancellationToken token=default) { preferences.SetAutoUpdateAtStartup(false);return Task.FromResult<(bool,string?)>((true,null)); }
    public Task<(bool Success,string? Message)> ApplyConfiguredScheduleAsync(CancellationToken token=default)=>Task.FromResult<(bool,string?)>((true,null));
}
