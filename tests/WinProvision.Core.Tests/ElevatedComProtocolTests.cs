using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public class ElevatedComProtocolTests
{
    [Fact]
    public void WorkerRequestPreservesPathsWithSpacesWithoutShellInterpolation()
    {
        var request = new ElevatedComRequest("install", "7zip.7zip", @"C:\Program Files\Teste & (nome)");
        string encoded = ElevatedComProtocol.EncodeRequest(request);
        Assert.DoesNotContain(' ', encoded);
        Assert.Equal(request, ElevatedComProtocol.DecodeRequest(encoded));
    }

    [Theory]
    [InlineData("remove", "7zip.7zip")]
    [InlineData("install", "7zip.7zip & cmd")]
    [InlineData("install", "--help")]
    [InlineData("install", "")]
    public void UnsupportedOrAmbiguousRequestsAreRejected(string operation, string id)
    {
        Assert.Throws<ArgumentException>(() => ElevatedComProtocol.EncodeRequest(new(operation, id, null)));
    }

    [Fact]
    public void WorkerResultPreservesRebootFailureAndDispatchState()
    {
        var result = new WingetExecutionResult
        {
            Success = false, ExitCode = 1602, Output = "Cancelado pelo usuário.",
            InstallerStarted = true, FailureReason = WingetFailureReason.ElevationCanceled, RebootRequired = true
        };
        var decoded = ElevatedComProtocol.DecodeResult(ElevatedComProtocol.EncodeResult(result));
        Assert.Equal(result.ExitCode, decoded.ExitCode);
        Assert.Equal(result.Output, decoded.Output);
        Assert.Equal(result.FailureReason, decoded.FailureReason);
        Assert.True(decoded.InstallerStarted);
        Assert.True(decoded.RebootRequired);
        Assert.False(decoded.Success);
    }

    [Fact]
    public void WorkerProgressPreservesActualComMethodAndPercentage()
    {
        var progress = new InstallProgressUpdate(InstallProgressPhase.Installing, 42, WingetMethod.ComApi);
        Assert.Equal(progress, ElevatedComProtocol.DecodeProgress(ElevatedComProtocol.EncodeProgress(progress)));
        Assert.Throws<FormatException>(() => ElevatedComProtocol.DecodeRequest("not base64"));
    }

    [Fact]
    public void WorkerInitializationFailureCanFallbackButDispatchedOrCanceledOperationCannot()
    {
        var startupFailure = new WingetExecutionResult
        {
            Success = false, InstallerStarted = false, FailureReason = WingetFailureReason.InternalError
        };
        Assert.True(WinGetService.CanFallbackFromElevatedWorker(startupFailure, false, default));
        Assert.False(WinGetService.CanFallbackFromElevatedWorker(startupFailure, true, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(WinGetService.CanFallbackFromElevatedWorker(startupFailure, false, cancellation.Token));
        startupFailure.InstallerStarted = true;
        Assert.False(WinGetService.CanFallbackFromElevatedWorker(startupFailure, false, default));
        startupFailure.InstallerStarted = false;
        startupFailure.FailureReason = WingetFailureReason.ElevationCanceled;
        Assert.False(WinGetService.CanFallbackFromElevatedWorker(startupFailure, false, default));
    }
}
