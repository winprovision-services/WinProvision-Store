using WinProvision.Core.Models;

namespace WinProvision.Core.Services.Backup;

/// <summary>Resultado da conexão com o provedor de backup remoto.</summary>
public record CloudConnectResult(bool Success, string? ErrorMessage = null, string? Login = null)
{
    public static CloudConnectResult Ok(string login) => new(true, null, login);
    public static CloudConnectResult Fail(string message) => new(false, message);
}

/// <summary>Resultado da publicação do bootstrap e do perfil no armazenamento remoto.</summary>
public record CloudBootstrapPublishResult(bool Success, string? RawUrl = null, string? ErrorMessage = null)
{
    public static CloudBootstrapPublishResult Ok(string rawUrl) => new(true, rawUrl);
    public static CloudBootstrapPublishResult Fail(string message) => new(false, null, message);
}

public enum BootstrapDisplayMode
{
    UserInterface,
    Terminal
}

public enum BootstrapLogMode
{
    Local,
    Cloud
}

public record CloudBackupUploadResult(bool Success, string? ErrorMessage = null)
{
    public static CloudBackupUploadResult Ok() => new(true);
    public static CloudBackupUploadResult Fail(string message) => new(false, message);
}

public record CloudBackupDownloadResult(ProfileBackupSet? BackupSet, string? ErrorMessage = null)
{
    public bool Success => BackupSet is not null;
    public static CloudBackupDownloadResult Ok(ProfileBackupSet backupSet) => new(backupSet);
    public static CloudBackupDownloadResult NotFound() => new(null!);
    public static CloudBackupDownloadResult Fail(string message) => new(null!, message);
}

/// <summary>
/// Metadados persistidos em disco (texto puro, não sensível) sobre a conexão com o
/// serviço remoto: identidade, identificador do arquivo privado usado como backup e a última sincronização
/// bem-sucedida. O Personal Access Token em si NUNCA fica aqui — ele vive
/// separadamente, protegido via <see cref="SecureTokenStore"/> (DPAPI).
/// </summary>
internal class BackupAccountInfo
{
    public string? Login { get; set; }
    public string? AvatarUrl { get; set; }
    public string? GistId { get; set; }
    public string? BootstrapGistId { get; set; }
    public DateTime? LastSyncUtc { get; set; }
}
