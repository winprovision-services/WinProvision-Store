using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class StuckUpgradeTrackerTests
{
    [Fact]
    public void SuppressesOnlyAfterThreeSuccessfulAttemptsWithUnchangedVersions()
    {
        string path = TempPath();
        try
        {
            var tracker = new StuckUpgradeTracker(path);
            var offered = Package("1", "2");
            for (int count = 1; count <= 2; count++)
            {
                tracker.RecordSuccess(offered);
                Assert.Single(tracker.FilterOffered(new[] { offered }));
            }
            tracker.RecordSuccess(offered);
            Assert.Empty(tracker.FilterOffered(new[] { offered }));
            Assert.Empty(new StuckUpgradeTracker(path).FilterOffered(new[] { offered }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChangedTargetOrInstalledVersionRestoresUpgrade()
    {
        string path = TempPath();
        try
        {
            var tracker = new StuckUpgradeTracker(path);
            for (int count = 0; count < 3; count++) tracker.RecordSuccess(Package("1", "2"));
            Assert.Single(tracker.FilterOffered(new[] { Package("1", "3") }));
            Assert.Single(tracker.FilterOffered(new[] { Package("2", "3") }));
            Assert.Single(tracker.FilterOffered(new[] { Package("1", "2") }));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OtherSourceAndUnknownVersionsNeverInheritSuppression()
    {
        string path = TempPath();
        try
        {
            var tracker = new StuckUpgradeTracker(path);
            for (int count = 0; count < 3; count++) tracker.RecordSuccess(Package("1", "2"));
            Assert.Single(tracker.FilterOffered(new[] { Package("1", "2", "msstore") }));
            tracker.RecordSuccess(Package("", "2"));
            Assert.Empty(tracker.FilterOffered(new[] { Package("1", "2") }));
        }
        finally { File.Delete(path); }
    }

    private static UpgradablePackage Package(string installed, string target, string source = "winget") => new()
    {
        Id = "Contoso.App", Name = "Contoso App", Source = source,
        CurrentVersion = installed, AvailableVersion = target
    };

    private static string TempPath() => Path.Combine(Path.GetTempPath(),
        "winprovision-stuck-" + Guid.NewGuid().ToString("N") + ".json");
}
