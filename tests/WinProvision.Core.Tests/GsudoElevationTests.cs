using System.Security.Cryptography;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

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
}
