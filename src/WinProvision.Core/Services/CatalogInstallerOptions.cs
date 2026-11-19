using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public static class CatalogInstallerOptions
{
    public static InstallerSilentArgumentResolution ResolveSilent(CatalogInstaller installer)
    {
        if (installer.SilentArgs is null || installer.SilentSource is not ("manifest" or "default"))
            return new(null, InstallerSilentArgumentSource.Unsupported);
        InstallerSilentArgumentResolution checkedArguments = InstallerSilentArgumentsResolver.Resolve(
            manualArguments: null, manifestSilent: installer.SilentArgs, manifestSilentWithProgress: null,
            manifestInstallerType: null, providedSource: InstallerSilentArgumentSource.Manifest);
        return checkedArguments with
        {
            Source = installer.SilentSource == "default" ? InstallerSilentArgumentSource.TypeDefault : InstallerSilentArgumentSource.Manifest
        };
    }
}
