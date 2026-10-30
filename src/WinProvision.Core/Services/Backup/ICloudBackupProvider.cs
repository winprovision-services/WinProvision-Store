using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Backup;

public interface ICloudBackupProvider : IDisposable
{
    bool IsConnected { get; }
    string? ConnectedLogin { get; }
    string? ConnectedAvatarUrl { get; }
    DateTime? LastSyncUtc { get; }

    Task<CloudConnectResult> ConnectAsync(
        string token,
        CancellationToken cancellationToken = default,
        string? refreshToken = null);

    void Disconnect();

    Task<CloudBackupUploadResult> UploadProfileAsync(
        ProfileBackupSet backupSet,
        CancellationToken cancellationToken = default);

    Task<CloudBackupDownloadResult> DownloadProfileAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudBackupFile>> ListBackupFilesAsync(
        CancellationToken cancellationToken = default);

    Task<CloudBackupDownloadResult> DownloadBackupFileAsync(
        string fileName,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string? Error)> DeleteBackupFileAsync(
        string fileName,
        CancellationToken cancellationToken = default);

    Task<(int DeletedCount, int TotalCount, string? Error)> DeleteAllAppGistsAsync(
        CancellationToken cancellationToken = default);
}
