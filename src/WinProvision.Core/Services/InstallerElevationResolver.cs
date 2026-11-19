using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed record ResolvedInstallerElevation(string Decision, string Source, string Reason, string? Requirement, bool Prohibited)
{
    public bool RequiresElevation => Decision == "required";
}

/// <summary>Single policy for catalog and WinGet installer metadata. Installer type is always the declared type.</summary>
public static class InstallerElevationResolver
{
    private static readonly HashSet<string> MachineTypes = new(StringComparer.OrdinalIgnoreCase)
        { "msi", "wix", "exe", "inno", "nullsoft", "burn" };
    private static readonly HashSet<string> UserTypes = new(StringComparer.OrdinalIgnoreCase)
        { "portable", "pwa", "msix", "appx" };

    public static ResolvedInstallerElevation ApplyInspection(ResolvedInstallerElevation state, InstallerElevationInspection inspection)
    {
        if (state.Decision != "unknown" || state.Source is "declared" or "scope" or "type") return state;
        if (state.Prohibited && inspection.Decision == "required")
            return state with { Decision = "not-required", Source = "declared", Reason = "conflict:elevationProhibited-vs-inspection-required" };
        return inspection.Decision is "required" or "not-required"
            ? state with { Decision = inspection.Decision, Source = "inspected", Reason = inspection.Reason }
            : state with { Source = "none", Reason = inspection.Reason };
    }

    public static ResolvedInstallerElevation Resolve(CatalogInstaller installer) =>
        Resolve(installer.Type, installer.Scope, installer.ElevationRequirement, installer.NestedType);

    public static ResolvedInstallerElevation Resolve(CatalogInstaller installer, bool useResolvedState)
    {
        if (!useResolvedState || installer.Elevation is not { } state)
            return Resolve(installer);
        if (state.Decision is not ("required" or "not-required" or "unknown")
            || state.Source is not ("declared" or "scope" or "type" or "none"))
            throw new InvalidDataException("Estado de elevação v3 inválido.");
        string reason = Resolve(installer.Type, installer.Scope, state.Requirement, installer.NestedType).Reason;
        return new(state.Decision, state.Source, reason, state.Requirement, state.Prohibited);
    }

    public static ResolvedInstallerElevation Resolve(string? type, string? scope, string? requirement, string? nestedType = null)
    {
        string req = requirement?.Trim() ?? "";
        string normalizedScope = scope?.Trim() ?? "";
        string normalizedType = type?.Trim() ?? "";
        string normalizedNestedType = nestedType?.Trim() ?? "";
        string effectiveType = normalizedType.Equals("zip", StringComparison.OrdinalIgnoreCase)
            ? normalizedNestedType : normalizedType;
        bool prohibited = req.Equals("elevationProhibited", StringComparison.OrdinalIgnoreCase);
        if (req.Equals("elevationRequired", StringComparison.OrdinalIgnoreCase))
            return prohibited
                ? new("not-required", "declared", "conflict:elevationProhibited-vs-elevationRequired", requirement, true)
                : new("required", "declared", "declared:elevationRequired", requirement, false);
        if (req.Equals("elevatesSelf", StringComparison.OrdinalIgnoreCase))
            return new("required", "declared", "declared:elevatesSelf", requirement, prohibited);
        if (normalizedScope.Equals("user", StringComparison.OrdinalIgnoreCase))
            return new("not-required", "scope", prohibited ? "scope:user+elevationProhibited" : "scope:user", requirement, prohibited);
        if (normalizedScope.Equals("machine", StringComparison.OrdinalIgnoreCase) && MachineTypes.Contains(effectiveType))
            return prohibited
                ? new("unknown", "none", $"conflict:elevationProhibited-vs-scope:machine+{effectiveType.ToLowerInvariant()}", requirement, true)
                : new("required", "scope", $"scope:machine+{effectiveType.ToLowerInvariant()}", requirement, false);
        if (prohibited) return new("unknown", "none", "declared:elevationProhibited-awaiting-inspection", requirement, true);
        if (!string.IsNullOrWhiteSpace(normalizedScope))
            return new("unknown", "none", "scope:unresolved-for-installer-type", requirement, false);
        if (effectiveType.Length > 0 && UserTypes.Contains(effectiveType))
            return new("not-required", "type", $"type:{effectiveType.ToLowerInvariant()}", requirement, false);
        return new("unknown", "none", "no-positive-declaration-or-supported-machine-scope", requirement, prohibited);
    }
}
