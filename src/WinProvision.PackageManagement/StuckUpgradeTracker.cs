using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;

namespace WinProvision.PackageManagement;

/// <summary>Suppresses an upgrade only after repeated reported successes leave the same version installed.</summary>
internal sealed class StuckUpgradeTracker
{
    private const int Threshold = 3;
    private const int MaximumEntries = 200;
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, StuckUpgradeRecord> _attempts = new(StringComparer.OrdinalIgnoreCase);

    internal StuckUpgradeTracker(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "stuck-upgrades.json");
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var entry in JsonSerializer.Deserialize(File.ReadAllText(_path), StuckUpgradeJson.Default.ListStuckUpgradeRecord) ?? [])
            {
                if (Valid(entry)) _attempts[Key(entry.Source, entry.Id)] = entry;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            PackageDiagnostics.Warning(ex, "StuckUpgradeTracker.Load");
        }
    }

    internal void RecordSuccess(UpgradablePackage package)
    {
        if (!Valid(package)) return;
        lock (_gate)
        {
            string key = Key(package.Source, package.Id);
            int count = _attempts.TryGetValue(key, out var previous)
                && SameVersions(previous, package) ? Math.Min(Threshold, previous.Count + 1) : 1;
            _attempts[key] = new(package.Source, package.Id, package.CurrentVersion,
                package.AvailableVersion, count, DateTimeOffset.UtcNow);
            if (_attempts.Count > MaximumEntries)
            {
                string oldest = _attempts.OrderBy(pair => pair.Value.RecordedAt).First().Key;
                _attempts.Remove(oldest);
            }
            Save();
        }
    }

    internal List<UpgradablePackage> FilterOffered(IEnumerable<UpgradablePackage> packages)
    {
        lock (_gate)
        {
            var visible = new List<UpgradablePackage>();
            bool changed = false;
            foreach (var package in packages)
            {
                string key = Key(package.Source, package.Id);
                if (!_attempts.TryGetValue(key, out var previous))
                {
                    visible.Add(package);
                    continue;
                }
                if (!SameVersions(previous, package))
                {
                    _attempts.Remove(key);
                    changed = true;
                    visible.Add(package);
                    continue;
                }
                if (previous.Count >= Threshold)
                {
                    PackageDiagnostics.Write($"STUCK UPGRADE suppressed source={package.Source} id={package.Id} target={package.AvailableVersion}");
                    continue;
                }
                visible.Add(package);
            }
            if (changed) Save();
            return visible;
        }
    }

    private void Save()
    {
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_attempts.Values.ToList(), StuckUpgradeJson.Default.ListStuckUpgradeRecord));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PackageDiagnostics.Warning(ex, "StuckUpgradeTracker.Save");
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { PackageDiagnostics.Warning(ex, "StuckUpgradeTracker.Cleanup"); }
            }
        }
    }

    private static bool Valid(UpgradablePackage package) =>
        !string.IsNullOrWhiteSpace(package.Source) && !string.IsNullOrWhiteSpace(package.Id)
        && !string.IsNullOrWhiteSpace(package.CurrentVersion) && !string.IsNullOrWhiteSpace(package.AvailableVersion)
        && !package.CurrentVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        && !package.AvailableVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
        && !package.CurrentVersion.Equals(package.AvailableVersion, StringComparison.OrdinalIgnoreCase);

    private static bool Valid(StuckUpgradeRecord entry) =>
        !string.IsNullOrWhiteSpace(entry.Source) && !string.IsNullOrWhiteSpace(entry.Id)
        && !string.IsNullOrWhiteSpace(entry.InstalledVersion) && !string.IsNullOrWhiteSpace(entry.TargetVersion)
        && entry.Count is >= 1 and <= Threshold;

    private static bool SameVersions(StuckUpgradeRecord entry, UpgradablePackage package) =>
        entry.InstalledVersion.Equals(package.CurrentVersion, StringComparison.OrdinalIgnoreCase)
        && entry.TargetVersion.Equals(package.AvailableVersion, StringComparison.OrdinalIgnoreCase);

    private static string Key(string source, string id) => source + "\u001f" + id;
}

internal sealed record StuckUpgradeRecord(string Source, string Id, string InstalledVersion,
    string TargetVersion, int Count, DateTimeOffset RecordedAt);

[JsonSerializable(typeof(List<StuckUpgradeRecord>))]
internal partial class StuckUpgradeJson : JsonSerializerContext;
