using WinProvision.Core.Models;
using WinProvision.WinGet.Contracts;

namespace WinProvision.PackageManagement;

internal static class WinGetVariantSelectionBuilder
{
    internal static WinGetVariantSelectionOptions Build(PackageInstallOptions options,
        string? installedScope = null, string? installedInstallerType = null) =>
        WinGetVariantSelectionOptions.Create(options.Version, options.Scope, options.Architecture,
            options.InstallerType, options.Locale, installedScope, installedInstallerType);
}
