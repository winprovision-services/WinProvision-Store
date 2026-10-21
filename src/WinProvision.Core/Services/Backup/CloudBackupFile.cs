namespace WinProvision.Core.Services.Backup;

public sealed record CloudBackupFile(
    string FileName,
    string Description,
    DateTime? CreatedAt,
    int CollectionCount,
    int AppCount,
    bool IsPrimary,
    bool IsCurrentDevice,
    bool CanImport,
    string? Contents = null,
    IReadOnlyList<CloudBackupCollection>? Collections = null);

public sealed record CloudBackupCollection(string Name, IReadOnlyList<CloudBackupApp> Apps);

public sealed record CloudBackupApp(string Name, string Id, string Source);
