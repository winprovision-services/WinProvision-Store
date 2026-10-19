using System.ComponentModel;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class ElevationPreferencesTests
{
    [Fact]
    public void PolicyIsSharedAcrossReadersAndCanBeReenabled()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "elevation.json");
        try
        {
            var preferences = new ElevationPreferencesService(path);
            Assert.True(preferences.AllowElevation);
            preferences.SetAllowed(false);
            var reader = new ElevationPreferencesService(path);
            var failure = Assert.Throws<Win32Exception>(reader.EnsureAllowed);
            Assert.Equal(1625, failure.NativeErrorCode);
            Assert.Equal(WingetFailureReason.BlockedByPolicy, InstallerExitCodes.Classify(failure.NativeErrorCode));
            reader.SetAllowed(true);
            new ElevationPreferencesService(path).EnsureAllowed();
            File.WriteAllText(path, "incompleto");
            Assert.False(new ElevationPreferencesService(path).AllowElevation);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void MissingFileKeepsDefaultAndIsNotAReadFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "elevation.json");
        var preferences = new ElevationPreferencesService(path);
        Assert.True(preferences.AllowElevation);
        Assert.False(preferences.ReadFailed);
        preferences.EnsureAllowed();
    }

    [Fact]
    public void CorruptFileFailsClosedWithADistinctMessageAndRecoversOnSave()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "elevation.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "{ quebrado");
            var preferences = new ElevationPreferencesService(path);
            Assert.False(preferences.AllowElevation);
            Assert.True(preferences.ReadFailed);
            var failure = Assert.Throws<Win32Exception>(preferences.EnsureAllowed);
            Assert.Equal(1625, failure.NativeErrorCode);
            Assert.DoesNotContain("bloqueada nas Configurações", failure.Message);
            preferences.SetAllowed(true);
            Assert.False(preferences.ReadFailed);
            var reloaded = new ElevationPreferencesService(path);
            Assert.True(reloaded.AllowElevation);
            Assert.False(reloaded.ReadFailed);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void FileHeldByAnotherProcessFailsClosedAsReadFailureNotAsUserBlock()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "elevation.json");
        try
        {
            new ElevationPreferencesService(path).SetAllowed(true);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var preferences = new ElevationPreferencesService(path);
                Assert.False(preferences.AllowElevation);
                Assert.True(preferences.ReadFailed);
            }
            Assert.True(new ElevationPreferencesService(path).AllowElevation);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
