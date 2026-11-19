using System.ComponentModel;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class UninstallerProcessStarterTests
{
    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(false, 1603, false)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1603, false)]
    public async Task CharacterizesRegisteredUninstallRoutingAndExitCodes(bool elevated, int code, bool success)
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        await File.WriteAllBytesAsync(file, [1]);
        var starter = new RecordingUninstallerStarter { DirectCode = code, ElevatedCode = code };
        try
        {
            var service = new UninstallerEngineService(processStarter: starter);
            var result = await service.RunRegisteredUninstallAsync(new InstalledAppDetail
            { QuietUninstallString = $"\"{file}\" /quiet", IsWindowsInstaller = false }, requireElevation: elevated);
            Assert.Equal(success, result.Success);
            Assert.Equal(elevated ? 0 : 1, starter.DirectCalls);
            Assert.Equal(elevated ? 1 : 0, starter.ElevatedCalls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Error740BeforeStartRetriesOnceThroughElevator()
    {
        string file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        await File.WriteAllBytesAsync(file, [1]);
        var starter = new RecordingUninstallerStarter { DirectException = new Win32Exception(740) };
        try
        {
            var result = await new UninstallerEngineService(processStarter: starter).RunRegisteredUninstallAsync(new InstalledAppDetail
                { QuietUninstallString = $"\"{file}\" /quiet" });
            Assert.True(result.Success);
            Assert.Equal(1, starter.DirectCalls);
            Assert.Equal(1, starter.ElevatedCalls);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Error740AfterStartDoesNotRetry()
    {
        var starter = new RecordingUninstallerStarter { DirectException = new Win32Exception(740), MarkStartedBeforeFailure = true };
        var service = new UninstallerEngineService(processStarter: starter);
        await Assert.ThrowsAsync<Win32Exception>(() => service.StartDirectWith740RetryAsync(
            "uninstall.exe", "/quiet", default, () => starter.MarkStarted(), () => starter.Started));
        Assert.Equal(1, starter.DirectCalls);
        Assert.Equal(0, starter.ElevatedCalls);
    }

    [Fact]
    public async Task FailedElevatedRetryDoesNotMakeThirdAttempt()
    {
        var starter = new RecordingUninstallerStarter { DirectException = new Win32Exception(740), ElevatedException = new Win32Exception(1223) };
        var service = new UninstallerEngineService(processStarter: starter);
        await Assert.ThrowsAsync<Win32Exception>(() => service.StartDirectWith740RetryAsync("uninstall.exe", "/quiet", default));
        Assert.Equal(1, starter.DirectCalls);
        Assert.Equal(1, starter.ElevatedCalls);
    }

    [Fact]
    public async Task RequiredElevationUsesElevatorWithoutDirectAttempt()
    {
        var starter = new RecordingUninstallerStarter();
        var service = new UninstallerEngineService(processStarter: starter);
        var result = await service.TryRunUninstallProcess("uninstall.exe", "/quiet", default, requireElevation: true);
        Assert.True(result.Success);
        Assert.Equal(0, starter.DirectCalls);
        Assert.Equal(1, starter.ElevatedCalls);
    }

    [Fact]
    public void ParserCharacterizationCoversQuotesVariablesSpacesAndMsi()
    {
        Assert.True(UninstallerEngineService.TrySplitCommand("\"C:\\Program Files\\Vendor\\uninstall.exe\" /quiet", out var quoted, out var quotedArgs));
        Assert.Equal("C:\\Program Files\\Vendor\\uninstall.exe", quoted);
        Assert.Equal("/quiet", quotedArgs);
        Assert.True(UninstallerEngineService.TrySplitCommand("C:\\Program Files\\Vendor\\uninstall.exe /quiet", out var unquoted, out var unquotedArgs));
        Assert.Equal("C:\\Program", unquoted); // Atual: caminho sem aspas quebra no primeiro espaço.
        Assert.Equal("Files\\Vendor\\uninstall.exe /quiet", unquotedArgs);
        string expanded = Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\msiexec.exe /x {12345678-1234-1234-1234-123456789012} /qn");
        Assert.True(UninstallerEngineService.TrySplitCommand(expanded, out var msi, out var msiArgs));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe").ToUpperInvariant(),
            msi.ToUpperInvariant());
        Assert.Equal("/x {12345678-1234-1234-1234-123456789012} /qn", msiArgs);
        Assert.True(UninstallerEngineService.TrySplitCommand("msiexec.exe /x {12345678-1234-1234-1234-123456789012} /qn", out var directMsi, out _));
        Assert.Equal("msiexec.exe", directMsi);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public async Task RegisteredUninstallRejectsShellInterpreters(string executable)
    {
        var starter = new RecordingUninstallerStarter();
        var result = await new UninstallerEngineService(processStarter: starter).RunRegisteredUninstallAsync(new InstalledAppDetail
            { QuietUninstallString = $"{Path.Combine(Environment.SystemDirectory, executable)} /c noop" });
        Assert.False(result.Success);
        Assert.Equal(0, starter.DirectCalls + starter.ElevatedCalls);
    }

    private sealed class RecordingUninstallerStarter : IUninstallerProcessStarter
    {
        public int DirectCalls { get; private set; }
        public int ElevatedCalls { get; private set; }
        public int DirectCode { get; init; }
        public int ElevatedCode { get; init; }
        public Exception? DirectException { get; init; }
        public Exception? ElevatedException { get; init; }
        public bool MarkStartedBeforeFailure { get; init; }
        public bool Started { get; private set; }
        public void MarkStarted() => Started = true;
        public Task<int> StartDirectAsync(string fileName, string arguments, CancellationToken token, Action? onStarted = null,
            bool createNoWindow = false, bool hidden = false)
        {
            DirectCalls++;
            if (MarkStartedBeforeFailure) { Started = true; onStarted?.Invoke(); }
            return DirectException is null ? Task.FromResult(DirectCode) : Task.FromException<int>(DirectException);
        }
        public Task<int> StartElevatedAsync(string fileName, string arguments, CancellationToken token, Action? onStarted = null)
        { ElevatedCalls++; onStarted?.Invoke(); if (ElevatedException is not null) return Task.FromException<int>(ElevatedException); return Task.FromResult(ElevatedCode); }
    }
}
