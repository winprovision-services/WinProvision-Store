using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using WinProvision.Store.Services;
namespace WinProvision.Store;

// O smoke test navega e consulta; qualquer instalação, remoção ou backup é proibido.
internal sealed partial class SmokeClipboardService : IClipboardService
{
    public bool TrySetText(string text) => true;
}
internal sealed partial class SmokeAccountBackupService : IAccountBackupService
{
    public bool IsConnected => false;
    public string? ConnectedLogin => null;
    public string? AvatarUrl => null;
    public DateTime? LastSyncUtc => null;
    public DateTime? LastBackupUtc => null;
    public Task<GitHubConnectResult> ConnectAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
    public void Disconnect() { }
    public Task SaveLocalAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<ProfileBackupSet?> LoadLatestLocalAsync(CancellationToken cancellationToken) => Task.FromResult<ProfileBackupSet?>(null);
    public Task<GitHubBackupUploadResult> UploadCloudAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<GitHubBackupDownloadResult> DownloadCloudAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}
internal sealed partial class SmokePackageBackend : IPackageBackend
{
    public Task ShutdownAsync() => Task.CompletedTask;
    public void CancelAll() { }
    public Task<WingetExecutionResult> InstallAsync(AppEntry app, PackageInstallOptions? options = null) => throw new NotSupportedException();
    public Task<WingetExecutionResult> UpdateAsync(UpgradablePackage package, PackageInstallOptions? options = null) => throw new NotSupportedException();
    public Task<WingetExecutionResult> UninstallAsync(AppEntry app, bool interactive = false, bool requiresElevation = false) => throw new NotSupportedException();
    public Task<List<UpgradablePackage>> ScanUpdatesAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new List<UpgradablePackage> { new() { Id = "Test.Package", Name = "Aplicativo de teste", CurrentVersion = "1", AvailableVersion = "2", Source = "winget" } });
    }
    internal sealed partial class SmokeOfficeCollectionInstaller : IOfficeCollectionInstaller
    {
        public Task<bool> InstallAsync(OfficeInstallRequest request, CancellationToken token) => throw new NotSupportedException();
    }
    internal sealed partial class SmokeInstalledPackagesService : IInstalledPackagesService
    {
        public Task<IReadOnlyList<InstalledPackageInfo>> ListAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>((InstalledPackageInfo[])[new("Aplicativo de teste", "Test.Package", "1", "winget", "", "", "", "", false)]);
        }
    }
}
