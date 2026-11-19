using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class AppUpdateInstallationCapabilityTests
{
    [Fact]
    public void ReadsUpdateSupportFromInstallationMetadataInsteadOfInferringFromPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "unrelated-name.exe");
        string marker = Path.Combine(directory, "update-capability.json");
        try
        {
            File.WriteAllText(marker, """{"schemaVersion":1,"updateProvider":"inno","scope":"user","supportsSelfUpdate":true}""");
            Assert.True(AppUpdateInstallationCapability.IsSupported(executable));

            File.WriteAllText(marker, """{"schemaVersion":1,"updateProvider":"inno","supportsSelfUpdate":false}""");
            Assert.False(AppUpdateInstallationCapability.IsSupported(executable));

            File.WriteAllText(marker, "{invalid");
            Assert.False(AppUpdateInstallationCapability.IsSupported(executable));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
