namespace WinProvision.Store.Services;

// ChocolateyService only needs the value shape from the Store service layer.
public sealed record InstalledPackageInfo(
    string Name,
    string Id,
    string Version,
    string Source,
    string IconUrl,
    string UninstallString,
    string QuietUninstallString,
    string InstallLocation,
    bool IsSystemComponent);
