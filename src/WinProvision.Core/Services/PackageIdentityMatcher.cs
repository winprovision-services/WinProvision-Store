namespace WinProvision.Core.Services;

/// <summary>Compara a identidade de um pacote entre fontes de inventário.</summary>
public static class PackageIdentityMatcher
{
    public static bool SamePackage(string? leftId, string? leftSource, string? rightId, string? rightSource) =>
        !string.IsNullOrWhiteSpace(leftId)
        && !string.IsNullOrWhiteSpace(rightId)
        && !string.IsNullOrWhiteSpace(leftSource)
        && !string.IsNullOrWhiteSpace(rightSource)
        && leftId.Trim().Equals(rightId.Trim(), StringComparison.OrdinalIgnoreCase)
        && leftSource.Trim().Equals(rightSource.Trim(), StringComparison.OrdinalIgnoreCase);
}
