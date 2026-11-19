using System.Reflection;
using System.Text.Json;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallerElevationPolicyTests
{
    [Fact]
    public void SharedVectorsMatchCoreResolver()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinProvision.Core.Tests.Fixtures.elevation-vectors.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        foreach (JsonElement vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            ResolvedInstallerElevation actual = InstallerElevationResolver.Resolve(
                vector.GetProperty("type").GetString(),
                vector.GetProperty("scope").ValueKind == JsonValueKind.Null ? null : vector.GetProperty("scope").GetString(),
                vector.GetProperty("requirement").ValueKind == JsonValueKind.Null ? null : vector.GetProperty("requirement").GetString(),
                vector.TryGetProperty("nestedType", out JsonElement nested) && nested.ValueKind == JsonValueKind.String ? nested.GetString() : null);
            Assert.Equal(vector.GetProperty("decision").GetString(), actual.Decision);
            Assert.Equal(vector.GetProperty("source").GetString(), actual.Source);
            Assert.Equal(vector.GetProperty("reason").GetString(), actual.Reason);
            Assert.Equal(vector.GetProperty("prohibited").GetBoolean(), actual.Prohibited);
        }
    }
}
