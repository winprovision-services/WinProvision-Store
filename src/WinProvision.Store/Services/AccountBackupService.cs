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
    Task<GitHubConnectResult> ConnectAsync(string token, CancellationToken cancellationToken);
    void Disconnect();
    Task SaveLocalAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken);
    Task<ProfileBackupSet?> LoadLatestLocalAsync(CancellationToken cancellationToken);
    Task<GitHubBackupUploadResult> UploadCloudAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken);
    Task<GitHubBackupDownloadResult> DownloadCloudAsync(CancellationToken cancellationToken);
}

public sealed partial class AccountBackupService(
    LocalBackupService localBackup,
    GitHubBackupService githubBackup) : IAccountBackupService
{
    public bool IsConnected => githubBackup.IsConnected;
    public string? ConnectedLogin => githubBackup.ConnectedLogin;
    public string? AvatarUrl => githubBackup.ConnectedAvatarUrl;
    public DateTime? LastSyncUtc => githubBackup.LastSyncUtc;
    public DateTime? LastBackupUtc => localBackup.LastBackupUtc;

    public Task<GitHubConnectResult> ConnectAsync(string token, CancellationToken cancellationToken) =>
        githubBackup.ConnectAsync(token, cancellationToken);

    public void Disconnect() => githubBackup.Disconnect();

    public Task SaveLocalAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken) =>
        localBackup.SaveAsync(backupSet, cancellationToken);

    public Task<ProfileBackupSet?> LoadLatestLocalAsync(CancellationToken cancellationToken) =>
        localBackup.TryLoadLatestAsync(cancellationToken);

    public Task<GitHubBackupUploadResult> UploadCloudAsync(ProfileBackupSet backupSet, CancellationToken cancellationToken) =>
        githubBackup.UploadProfileAsync(backupSet, cancellationToken);

    public Task<GitHubBackupDownloadResult> DownloadCloudAsync(CancellationToken cancellationToken) =>
        githubBackup.DownloadProfileAsync(cancellationToken);
}
