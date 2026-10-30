using System.Security.Cryptography;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

[Collection("ElevationState")]
public class GsudoElevationTests
{
    [Fact]
    public void HelperIntegrityRejectsModifiedBinary()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] contents = [1, 2, 3, 4];
            File.WriteAllBytes(path, contents);
            string hash = Convert.ToHexString(SHA256.HashData(contents));
            GsudoElevationService.VerifyHelper(path, hash);
            File.WriteAllBytes(path, [1, 2, 3, 5]);
            Assert.Throws<InvalidDataException>(() => GsudoElevationService.VerifyHelper(path, hash));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CanceledElevationNeverDispatchesOrRequestsUac()
    {
        bool started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GsudoElevationService.RunElevatedAsync(
            "unused.exe", "", new(true), () => started = true));
        Assert.False(started);
    }

    [Fact]
    public async Task PreviousRefusalPreventsRepeatedPromptsUntilExplicitAuthorization()
    {
        try
        {
            WinProvisionElevationState.MarkFailed();
            bool started = false;
            var error = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                GsudoElevationService.RunElevatedAsync("unused.exe", "", default, () => started = true));
            Assert.Equal(1223, error.NativeErrorCode);
            Assert.False(started);
            WinProvisionElevationState.MarkAuthorized();
            Assert.False(WinProvisionElevationState.HasFailedThisSession);
        }
        finally { WinProvisionElevationState.ResetForTests(); }
    }

    [Theory]
    [InlineData("Error: The operation was canceled by the user.", "Canceled")]
    [InlineData("error: operation CANCELLED BY THE USER", "Canceled")]
    [InlineData("Error: The user name or password is incorrect.", "BadCredentials")]
    [InlineData("Instalação concluída com aviso 999", "None")]
    [InlineData("", "None")]
    [InlineData(null, "None")]
    public void HelperOutputIsClassifiedOnlyByKnownGsudoErrors(string? output, string expected)
        => Assert.Equal(expected, GsudoElevationService.ClassifyHelperOutput(output).ToString());

    [Fact]
    public void CacheCommandsUseOfficialGsudoSyntax()
    {
        Assert.Equal(["cache", "on"], GsudoElevationService.BuildCacheOnArguments());
        Assert.Equal(["cache", "off"], GsudoElevationService.BuildCacheOffArguments());
        Assert.False(GsudoElevationService.IsCacheAvailableOutput("CacheAvailable: false"));
        Assert.True(GsudoElevationService.IsCacheAvailableOutput("CacheAvailable: true"));
        Assert.True(GsudoElevationService.IsCacheAvailableOutput("""{"CacheAvailable":true}"""));
    }

    [Fact]
    public async Task AlreadyElevatedProcessRunsDirectlyWithoutHelperOrPolicy()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            WinProvisionElevationState.ResetForTests();
            GsudoElevationService.ElevationProbe = () => true;
            // Política bloqueando e helper ausente não podem atrapalhar quem já é administrador.
            string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var blocked = new ElevationPreferencesService(Path.Combine(folder, "p.json"));
            blocked.SetAllowed(false);
            GsudoElevationService.PreferencesFactory = () => new ElevationPreferencesService(Path.Combine(folder, "p.json"));
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var lines = new List<string>();
            int code = await GsudoElevationService.RunElevatedAsync(cmd, "/c echo ola & exit 7", default, onLog: lines.Add);
            Assert.Equal(7, code);
            Assert.Contains(lines, line => line.Contains("ola"));
            Directory.Delete(folder, recursive: true);
        }
        finally { GsudoElevationService.ResetProbesForTests(); WinProvisionElevationState.ResetForTests(); }
    }

    [Fact]
    public async Task InstallerExitCode999IsNotTreatedAsElevationRefusal()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            WinProvisionElevationState.ResetForTests();
            GsudoElevationService.ElevationProbe = () => true;
            string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            int code = await GsudoElevationService.RunElevatedAsync(cmd, "/c exit 999", default);
            Assert.Equal(999, code);
            Assert.False(WinProvisionElevationState.HasFailedThisSession);
        }
        finally { GsudoElevationService.ResetProbesForTests(); WinProvisionElevationState.ResetForTests(); }
    }

    [Fact]
    public async Task NonInteractiveSessionFailsFastInsteadOfWaitingForUac()
    {
        try
        {
            WinProvisionElevationState.ResetForTests();
            string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            GsudoElevationService.PreferencesFactory = () => new ElevationPreferencesService(Path.Combine(folder, "p.json"));
            GsudoElevationService.ElevationProbe = () => false;
            GsudoElevationService.InteractiveProbe = () => false;
            var error = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                GsudoElevationService.RunElevatedAsync("unused.exe", "", default));
            Assert.Equal(GsudoElevationService.ErrorRequiresInteractive, error.NativeErrorCode);
        }
        finally { GsudoElevationService.ResetProbesForTests(); WinProvisionElevationState.ResetForTests(); }
    }

    [Fact]
    public async Task BlockedPolicyStopsBeforeAnyHelperIsTouched()
    {
        try
        {
            WinProvisionElevationState.ResetForTests();
            string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string path = Path.Combine(folder, "p.json");
            new ElevationPreferencesService(path).SetAllowed(false);
            GsudoElevationService.PreferencesFactory = () => new ElevationPreferencesService(path);
            GsudoElevationService.ElevationProbe = () => false;
            var error = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                GsudoElevationService.RunElevatedAsync("unused.exe", "", default));
            Assert.Equal(1625, error.NativeErrorCode);
            Directory.Delete(folder, recursive: true);
        }
        finally { GsudoElevationService.ResetProbesForTests(); WinProvisionElevationState.ResetForTests(); }
    }

    [Fact]
    public void WorkingDirectoryFallsBackWhenFolderIsMissingOrProtected()
    {
        if (!OperatingSystem.IsWindows()) return;
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Equal(Environment.SystemDirectory, GsudoElevationService.ResolveWorkingDirectory("x.exe", missing));
        Assert.Equal(Environment.SystemDirectory, GsudoElevationService.ResolveWorkingDirectory(
            @"C:\Users\a\AppData\Local\Microsoft\WindowsApps\winget.exe", null));
        Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            GsudoElevationService.ResolveWorkingDirectory("x.exe", Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar));
    }
}
