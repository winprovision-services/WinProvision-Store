namespace WinProvision.Core.Services;

public static class InstallerElevationPolicy
{
    public static bool RequiresElevation(string? scope, bool explicitRequirement = false, bool elevationProhibited = false)
        => !elevationProhibited && (explicitRequirement
            || string.Equals(scope, "machine", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "system", StringComparison.OrdinalIgnoreCase));
}
