using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using WinProvision.Store.Services;
namespace WinProvision.Store;

// O smoke verificação navega e consulta; qualquer instalação, remoção ou backup é proibido.
internal sealed partial class SmokeClipboardService : IClipboardService
{
    public string? LastText { get; private set; }
    public bool TrySetText(string text) { LastText = text; return true; }
    public Task<string?> TryGetTextAsync() => Task.FromResult(LastText);
}
internal sealed class SmokeManualWinGetCommandService : IManualWinGetCommandService
{
    public string? LastCommand { get; private set; }
    public int OpenCount { get; private set; }
    public bool TryOpenInTerminal(string command, out string status)
    {
        LastCommand = command;
        OpenCount++;
        status = "Comando de atualização aberto no PowerShell.";
        return true;
    }
}
internal sealed partial class SmokeAccountBackupService : IAccountBackupService
{
    public bool SimulateConnected { get; set; }
    public bool UseMutableBackups { get; set; }
    public ProfileBackupSet? SavedSnapshot { get; private set; }
    private readonly List<CloudBackupFile> _files = [];
    public bool IsConnected => SimulateConnected;
    public string? ConnectedLogin => SimulateConnected ? "usuario-teste" : null;
    public string? AvatarUrl => null;
    public DateTime? LastSyncUtc => null;
    public DateTime? LastBackupUtc => null;
    public Task<CloudConnectResult> ConnectAsync(string token, CancellationToken cancellationToken, string? refreshToken = null) => throw new NotSupportedException();
    public void Disconnect() { }
    public Task SaveLocalAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken)
    {
        if (!UseMutableBackups) throw new NotSupportedException();
        SavedSnapshot = backupSet;
        return Task.CompletedTask;
    }
    public Task<ProfileBackupSet?> LoadLatestLocalAsync(CancellationToken cancellationToken) => Task.FromResult<ProfileBackupSet?>(null);
    public Task<CloudBackupUploadResult> UploadCloudAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken)
    {
        if (!UseMutableBackups) throw new NotSupportedException();
        var collections = backupSet.Tabs.Select(tab => new CloudBackupCollection(tab.Name ?? "Coleção",
            tab.Apps.Select(app => new CloudBackupApp(app.Name ?? app.Id, app.Id, app.Source ?? "winget")).ToArray())).ToArray();
        _files.Clear();
        _files.Add(new CloudBackupFile("profile.json", "Backup principal", DateTime.UtcNow,
            collections.Length, collections.Sum(c => c.Apps.Count), true, false, true, Collections: collections, GistId: "novo"));
        return Task.FromResult(CloudBackupUploadResult.Ok());
    }
    public Task<CloudBackupDownloadResult> DownloadCloudAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<CloudBackupFile>> ListCloudFilesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CloudBackupFile>>(
        UseMutableBackups ? _files.ToArray() : SimulateConnected ? (CloudBackupFile[])[new CloudBackupFile("profile.json", "Backup principal", DateTime.UtcNow, 2, 3, true, false, true, "Essenciais, Trabalho",
                (CloudBackupCollection[])[new CloudBackupCollection("Essenciais", (CloudBackupApp[])[new CloudBackupApp("7-Zip", "7zip.7zip", "WinGet"), new CloudBackupApp("VLC", "VideoLAN.VLC", "WinGet")]),
                    new CloudBackupCollection("Trabalho", (CloudBackupApp[])[new CloudBackupApp("Visual Studio Code", "Microsoft.VisualStudioCode", "WinGet")])]),
            new CloudBackupFile("outro-perfil.json", "Arquivo JSON do Gist", null, 1, 1, false, false, true, "Notebook",
                (CloudBackupCollection[])[new CloudBackupCollection("Notebook", (CloudBackupApp[])[new CloudBackupApp("PowerToys", "Microsoft.PowerToys", "WinGet")])]),
            new CloudBackupFile("invalido.json", "JSON incompatível ou indisponível", null, 0, 0, false, false, false)] : (CloudBackupFile[])[]);
    public Task<CloudBackupDownloadResult> DownloadCloudFileAsync(string fileName, CancellationToken cancellationToken, string? gistId = null) => throw new NotSupportedException();
    public Task<(bool Success, string? Error)> DeleteCloudFileAsync(string fileName, CancellationToken cancellationToken, string? gistId = null) => throw new NotSupportedException();
    public Task<(int DeletedCount, int TotalCount, string? Error)> DeleteAllAppGistsAsync(CancellationToken cancellationToken)
    {
        if (!UseMutableBackups) throw new NotSupportedException();
        int count = _files.Count;
        _files.Clear();
        return Task.FromResult<(int, int, string?)>((count, count, null));
    }
}
internal sealed partial class SmokePackageBackend : IPackageBackend
{
    public Func<AppEntry, Task<WingetExecutionResult>>? InstallHandler { get; set; }
    public Func<UpgradablePackage, PackageInstallOptions?, Task<WingetExecutionResult>>? UpdateHandler { get; set; }
    public Func<AppEntry, bool, bool, PackageInstallOptions?, Task<WingetExecutionResult>>? UninstallHandler { get; set; }
    public Func<string, string?, string, CancellationToken, Task<WingetExecutionResult>>? DownloadHandler { get; set; }
    public PackageInstallOptions? LastUpdateOptions { get; private set; }
    public UpgradablePackage? LastUpdatePackage { get; private set; }
    public PackageInstallOptions? LastUninstallOptions { get; private set; }
    public bool LastUninstallInteractive { get; private set; }
    public bool LastUninstallRequiresElevation { get; private set; }
    public string? LastDownloadId { get; private set; }
    public string? LastDownloadSource { get; private set; }
    public string? LastDownloadDirectory { get; private set; }
    public int UpdateCount { get; private set; }
    public int UninstallCount { get; private set; }
    public int DownloadCount { get; private set; }
    public int ScanCount { get; private set; }
    public int InstallCount { get; set; }
    public Func<CancellationToken, Func<UpdateSourceScanResult, Task>?, Task<List<UpgradablePackage>>>? ScanHandler { get; set; }
    public Task ShutdownAsync() => Task.CompletedTask;
    public void CancelAll() { }
    public Task<WingetExecutionResult> InstallAsync(AppEntry app, PackageInstallOptions? options = null)
    {
        InstallCount++;
        return InstallHandler?.Invoke(app) ?? throw new NotSupportedException();
    }
    public Task<WingetExecutionResult> UpdateAsync(UpgradablePackage package, PackageInstallOptions? options = null)
    {
        UpdateCount++;
        LastUpdatePackage = package;
        LastUpdateOptions = options;
        return UpdateHandler?.Invoke(package, options) ?? throw new NotSupportedException();
    }
    public Task<WingetExecutionResult> UninstallAsync(AppEntry app, bool interactive = false, bool requiresElevation = false, PackageInstallOptions? options = null)
    {
        UninstallCount++;
        LastUninstallInteractive = interactive;
        LastUninstallRequiresElevation = requiresElevation;
        LastUninstallOptions = options;
        return UninstallHandler?.Invoke(app, interactive, requiresElevation, options) ?? throw new NotSupportedException();
    }
    public Task<WingetExecutionResult> DownloadAsync(string id, string? source, string directory, CancellationToken token, string? version = null)
    {
        DownloadCount++;
        LastDownloadId = id;
        LastDownloadSource = source;
        LastDownloadDirectory = directory;
        return DownloadHandler?.Invoke(id, source, directory, token) ?? throw new NotSupportedException();
    }
    public async Task<List<UpgradablePackage>> ScanUpdatesAsync(CancellationToken token,
        Func<UpdateSourceScanResult, Task>? onSourceCompleted = null)
    {
        token.ThrowIfCancellationRequested();
        ScanCount++;
        if (ScanHandler is not null)
        {
            List<UpgradablePackage> handled = await ScanHandler(token, onSourceCompleted);
            return handled;
        }
        List<UpgradablePackage> packages = new()
        {
            new() { Id = "Google.Chrome", Name = "Google Chrome", CurrentVersion = "1.0", AvailableVersion = "2.0", Source = "winget" },
            new() { Id = "demo.package", Name = "Demo Package", CurrentVersion = "1.2.3", AvailableVersion = "2.0.0-rc.1", Source = "chocolatey" }
        };
        if (onSourceCompleted is not null)
        {
            await onSourceCompleted(new UpdateSourceScanResult("WinGet", packages.Where(package => package.Source == "winget").ToArray()));
            await onSourceCompleted(new UpdateSourceScanResult("Chocolatey", packages.Where(package => package.Source == "chocolatey").ToArray()));
        }
        return packages;
    }
    internal sealed partial class SmokeOfficeCollectionInstaller : IOfficeCollectionInstaller
    {
        public Task<bool> InstallAsync(OfficeInstallRequest request, CancellationToken token) => throw new NotSupportedException();
    }
    internal sealed partial class SmokeInstalledPackagesService : IInstalledPackagesService
    {
        public int ListCount { get; private set; }
        public Task<IReadOnlyList<InstalledPackageInfo>> ListAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            ListCount++;
            return Task.FromResult<IReadOnlyList<InstalledPackageInfo>>((InstalledPackageInfo[])[new("Aplicativo de teste", "Test.Package", "1", "winget", "", "", "", "", false)]);
        }
    }
}
