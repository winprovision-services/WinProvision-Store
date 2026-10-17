namespace WinProvision.Core.Services;

public static class InstallerElevationPolicy
{
    // Elevating a COM client does not elevate an already running out-of-process server.
    public static bool DelegateComExecutionToCli(string? source, string? scope, bool explicitRequirement,
        bool elevationProhibited, bool helperBundled)
        => helperBundled && string.Equals(source, "winget", StringComparison.OrdinalIgnoreCase)
            && RequiresElevation(scope, explicitRequirement, elevationProhibited);

    public static bool RequiresElevation(string? scope, bool explicitRequirement = false, bool elevationProhibited = false)
        => !elevationProhibited && (explicitRequirement
            || string.Equals(scope, "machine", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "system", StringComparison.OrdinalIgnoreCase));
}
