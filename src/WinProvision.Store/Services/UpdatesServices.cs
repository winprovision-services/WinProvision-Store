using WinProvision.Core.Services;
namespace WinProvision.Store.Services;
public interface IUpdatesIgnoreStore
{
    IReadOnlyList<IgnoredUpdateEntry> GetIgnoredUpdates();
    void Ignore(string id, string version); void Unignore(string id); bool IsIgnored(string id, string version);
}
public sealed partial class UpdatesIgnoreStore : IUpdatesIgnoreStore
{
    private readonly IgnoredUpdatesService _core = new();
    public IReadOnlyList<IgnoredUpdateEntry> GetIgnoredUpdates() => _core.GetIgnoredUpdates();
    public void Ignore(string id,string version) => _core.Ignore(id,version);
    public void Unignore(string id) => _core.Unignore(id);
    public bool IsIgnored(string id,string version) => _core.IsIgnored(id,version);
}
public interface IBackgroundUpdateStartupService
{
    bool IsEnabled { get; }
    Task<string?> MigrateLegacyTaskAsync(CancellationToken token = default);
    Task<(bool Success, string? Message)> EnableAsync(CancellationToken token = default);
    Task<(bool Success, string? Message)> DisableAsync(CancellationToken token = default);
    Task<(bool Success, string? Message)> ApplyConfiguredScheduleAsync(CancellationToken token = default);
}
