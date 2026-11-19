using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>Single replay path shared by queue and persisted history.</summary>
public static class OperationReplayService
{
    public static Task<WingetExecutionResult> ReplayAsync(IPackageBackend backend, OperationSnapshot snapshot)
    {
        AppEntry app = new()
        {
            Id = snapshot.PackageId, Name = snapshot.PackageName,
            Source = snapshot.Manager.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) ? "chocolatey" : snapshot.Source ?? snapshot.Manager,
            Version = snapshot.Options.Version ?? snapshot.VersionTo ?? string.Empty,
            IconUrl = snapshot.IconUrl ?? string.Empty
        };
        return snapshot.Kind switch
        {
            OperationKind.Install => backend.InstallAsync(app, snapshot.Options),
            OperationKind.Update => backend.UpdateAsync(new UpgradablePackage
            {
                Id = snapshot.PackageId, Name = snapshot.PackageName,
                Source = snapshot.Manager.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) ? "chocolatey" : snapshot.Source ?? snapshot.Manager,
                CurrentVersion = snapshot.VersionFrom ?? string.Empty, AvailableVersion = snapshot.VersionTo ?? string.Empty,
                IconUrl = snapshot.IconUrl ?? string.Empty
            }, snapshot.Options),
            OperationKind.Uninstall => backend.UninstallAsync(app, snapshot.Options.Interactive,
                snapshot.Options.RequiresElevation, snapshot.Options),
            _ => Task.FromResult(new WingetExecutionResult { Success = false, ExitCode = 1, Output = "Esta operação não pode ser repetida." })
        };
    }
}
