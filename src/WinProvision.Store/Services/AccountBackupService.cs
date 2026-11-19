using WinProvision.Core.Models;
using WinProvision.Core.Services.Backup;

namespace WinProvision.Store.Services;

public interface IAccountBackupService
{
    bool IsConnected { get; }
    string? ConnectedLogin { get; }
    string? AvatarUrl { get; }
    DateTime? LastSyncUtc { get; }
    DateTime? LastBackupUtc { get; }
    Task<CloudConnectResult> ConnectAsync(string token, CancellationToken cancellationToken, string? refreshToken = null);
    void Disconnect();
    Task SaveLocalAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken);
    Task<ProfileBackupSet?> LoadLatestLocalAsync(CancellationToken cancellationToken);
    Task<CloudBackupUploadResult> UploadCloudAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken);
    Task<CloudBackupDownloadResult> DownloadCloudAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudBackupFile>> ListCloudFilesAsync(CancellationToken cancellationToken);
    Task<CloudBackupDownloadResult> DownloadCloudFileAsync(string fileName, CancellationToken cancellationToken, string? gistId = null);
    Task<(bool Success, string? Error)> DeleteCloudFileAsync(string fileName, CancellationToken cancellationToken, string? gistId = null);
    Task<(int DeletedCount, int TotalCount, string? Error)> DeleteAllAppGistsAsync(CancellationToken cancellationToken);
}

public sealed partial class AccountBackupService(
    LocalBackupService localBackup,
    ICloudBackupProvider cloudBackup) : IAccountBackupService
{
    public bool IsConnected => cloudBackup.IsConnected;
    public string? ConnectedLogin => cloudBackup.ConnectedLogin;
    public string? AvatarUrl => cloudBackup.ConnectedAvatarUrl;
    public DateTime? LastSyncUtc => cloudBackup.LastSyncUtc;
    public DateTime? LastBackupUtc => localBackup.LastBackupUtc;

    public Task<CloudConnectResult> ConnectAsync(string token, CancellationToken cancellationToken, string? refreshToken = null) =>
        cloudBackup.ConnectAsync(token, cancellationToken, refreshToken);

    public void Disconnect() => cloudBackup.Disconnect();

    public Task SaveLocalAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken) =>
        localBackup.SaveAsync(backupSet, cancellationToken);

    public Task<ProfileBackupSet?> LoadLatestLocalAsync(CancellationToken cancellationToken) =>
        localBackup.TryLoadLatestAsync(cancellationToken);

    public Task<CloudBackupUploadResult> UploadCloudAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken) =>
        cloudBackup.UploadProfileAsync(backupSet, cancellationToken);

    public Task<CloudBackupDownloadResult> DownloadCloudAsync(CancellationToken cancellationToken) =>
        cloudBackup.DownloadProfileAsync(cancellationToken);

    public Task<IReadOnlyList<CloudBackupFile>> ListCloudFilesAsync(CancellationToken cancellationToken) =>
        cloudBackup.ListBackupFilesAsync(cancellationToken);

    public Task<CloudBackupDownloadResult> DownloadCloudFileAsync(string fileName, CancellationToken cancellationToken, string? gistId = null) =>
        cloudBackup.DownloadBackupFileAsync(fileName, cancellationToken, gistId);

    public Task<(bool Success, string? Error)> DeleteCloudFileAsync(string fileName, CancellationToken cancellationToken, string? gistId = null) =>
        cloudBackup.DeleteBackupFileAsync(fileName, cancellationToken, gistId);

    public Task<(int DeletedCount, int TotalCount, string? Error)> DeleteAllAppGistsAsync(CancellationToken cancellationToken) =>
        cloudBackup.DeleteAllAppGistsAsync(cancellationToken);
}
