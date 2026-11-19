using Xunit;
using WinProvision.Providers.WinGet.Com;

namespace WinProvision.Core.Tests;

public sealed class WinGetComArchitectureTests
{
    [Fact]
    public void WinGetComProjectionTypesAreConfinedToProviderExceptStoreAotMetadata()
    {
        string root = FindRepositoryRoot();
        string source = Path.Combine(root, "src");
        var violations = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(source, path).StartsWith("WinProvision.Providers.WinGet.Com" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !Path.GetRelativePath(source, path).Equals(Path.Combine("WinProvision.Store", "WinRtCollectionMetadata.cs"), StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("Microsoft.Management.Deployment", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ProviderDoesNotDependOnApplicationOrElevationProjects()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "src", "WinProvision.Providers.WinGet.Com", "WinProvision.Providers.WinGet.Com.csproj"));
        Assert.DoesNotContain("WinProvision.Core.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WinProvision.Elevation.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WinProvision.PackageManagement.csproj", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicProviderSurfaceDoesNotExposeWinGetProjectionTypes()
    {
        Type[] exposedTypes = typeof(WinGetComClient).Assembly.GetExportedTypes();
        var signatures = exposedTypes.SelectMany(type => type.GetMembers()
                .Where(member => member is System.Reflection.MethodBase or System.Reflection.PropertyInfo)
                .SelectMany(GetSignatureTypes))
            .Concat(exposedTypes);

        Assert.DoesNotContain(signatures, type => type.FullName?.StartsWith("Microsoft.Management.Deployment.", StringComparison.Ordinal) == true);
    }

    private static IEnumerable<Type> GetSignatureTypes(System.Reflection.MemberInfo member)
    {
        if (member is System.Reflection.MethodBase method)
        {
            if (method is System.Reflection.MethodInfo methodInfo) yield return methodInfo.ReturnType;
            foreach (var parameter in method.GetParameters()) yield return parameter.ParameterType;
        }
        else if (member is System.Reflection.PropertyInfo property)
        {
            yield return property.PropertyType;
        }
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WinProvision.Store.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate WinProvision.Store.slnx from the test output directory.");
    }
}
