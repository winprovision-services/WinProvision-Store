using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class InnoUninstallTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\MPC-HC\\unins000.exe\" /SILENT", true)]
    [InlineData("\"C:\\Program Files\\Example\\unins001.exe\"", true)]
    [InlineData("\"C:\\Program Files\\Example\\Uninstall.exe\" /S", false)]
    [InlineData("cmd.exe /c unins000.exe", false)]
    public void RecognizesRegisteredInnoUninstallers(string command, bool expected)
    {
        var app = new InstalledAppDetail { Id = "Example.App", UninstallString = command };
        Assert.Equal(expected, UninstallerEngineService.HasInnoSetupUninstaller(app));
    }
}
