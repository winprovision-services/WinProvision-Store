using Microsoft.Management.Deployment;

namespace WinProvision.Providers.WinGet.Com;

internal sealed record WinGetComUninstallLookupPlan(
    string? Source,
    CompositeSearchBehavior SearchBehavior,
    PackageMatchField Field,
    PackageFieldMatchOption MatchOption,
    bool RequireInstalledVersion)
{
    internal static WinGetComUninstallLookupPlan Create(string? source) => new(
        source,
        CompositeSearchBehavior.LocalCatalogs,
        PackageMatchField.Id,
        PackageFieldMatchOption.EqualsCaseInsensitive,
        RequireInstalledVersion: true);
}
