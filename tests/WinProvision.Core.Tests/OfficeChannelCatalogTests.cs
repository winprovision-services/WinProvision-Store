using WinProvision.Core.Models.Office;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OfficeChannelCatalogTests
{
    [Theory]
    [InlineData("https://officecdn.microsoft.com/pr/492350f6-3a01-4f97-b9c0-c7c6ddf67d60", "Canal Atual")]
    [InlineData("https://officecdn.microsoft.com/pr/55336b82-a18d-4dd6-b5f6-9e5095c314a6", "Enterprise Mensal")]
    [InlineData("https://officecdn.microsoft.com/pr/unrecognized-channel", "Canal personalizado")]
    public void DetectedUpdateChannelUsesFriendlyCatalogName(string channel, string expected)
    {
        Assert.Equal(expected, OfficeChannelCatalog.ResolveDetectedName(channel));
    }
}
