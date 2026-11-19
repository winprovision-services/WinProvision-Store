using System.Text.Json;
using Xunit;
using WinProvision.Providers.WinGet.Com;
using WinProvision.Providers.WinGet.Com.Contracts;

namespace WinProvision.Core.Tests;

public sealed class WinGetComContractTests
{
    [Fact]
    public void OperationResultRoundTripsThroughGeneratedJsonContext()
    {
        var expected = new WinGetComOperationResult(WinGetComStatus.OutcomeUnknown, unchecked((int)0x800706BE),
            23, true, "RPC disconnected", WinGetComPhase.Installing, unchecked((int)0x800706BE));

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(expected, WinGetComJsonContext.Default.WinGetComOperationResult);
        var actual = JsonSerializer.Deserialize(json, WinGetComJsonContext.Default.WinGetComOperationResult);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void InstallAndUninstallProgressRoundTripWithUnknownValues()
    {
        var install = new WinGetComInstallProgress(WinGetComInstallState.Installing, null, null, null, 0.25);
        var uninstall = new WinGetComUninstallProgress(WinGetComUninstallState.Uninstalling, null);

        Assert.Equal(install, JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(install, WinGetComJsonContext.Default.WinGetComInstallProgress),
            WinGetComJsonContext.Default.WinGetComInstallProgress));
        Assert.Equal(uninstall, JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(uninstall, WinGetComJsonContext.Default.WinGetComUninstallProgress),
            WinGetComJsonContext.Default.WinGetComUninstallProgress));
    }

    [Theory]
    [InlineData(unchecked((int)0x80040154), WinGetComStatus.ClassNotRegistered)]
    [InlineData(unchecked((int)0x80070005), WinGetComStatus.AccessDenied)]
    [InlineData(unchecked((int)0x800401F0), WinGetComStatus.ApartmentError)]
    [InlineData(unchecked((int)0x800706BA), WinGetComStatus.ComUnavailable)]
    public void ActivationFailuresMapToTypedStatus(int hresult, WinGetComStatus expected)
    {
        var result = WinGetComClient.OperationFailure(new System.Runtime.InteropServices.COMException("test", hresult), WinGetComPhase.None);

        Assert.Equal(expected, result.Status);
        Assert.Equal(hresult, result.HResult);
    }
}
