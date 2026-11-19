using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageNameNormalizerTests
{
    [Theory]
    [InlineData("7-Zip 26.06 (x64 edition)", "26.06", "7-Zip")]
    [InlineData("Python 3.14", "3.14.7", "Python 3.14")]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x64) - 14.51.36247", "14.51.36247", "Microsoft Visual C++ v14 Redistributable")]
    [InlineData("Google Chrome", "157.0.0", "Google Chrome")]
    [InlineData("", "1.0", "")]
    [InlineData("(x64)", null, "(x64)")]
    public void ToDisplayName_NormalizesOnlyRecognizedInstalledSuffixes(string name, string? version, string expected)
    {
        Assert.Equal(expected, PackageNameNormalizer.ToDisplayName(name, version));
    }

    [Theory]
    [InlineData("Pacote 1.0 (x64)", "1.0", "Pacote")]
    [InlineData("Aplicativo (arm64)", null, "Aplicativo")]
    [InlineData("Aplicativo  32-bit", null, "Aplicativo")]
    [InlineData("Python 3.14", "3.14.7", "Python 3.14")]
    public void ToDisplayName_CleansSpecificArchitectureAndVersionPatterns(string name, string? version, string expected)
    {
        Assert.Equal(expected, PackageNameNormalizer.ToDisplayName(name, version));
    }
}
