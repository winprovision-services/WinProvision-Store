using WinProvision.Core.Services.Backup;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CloudBackupFileRow(CloudBackupFile file)
{
    public string FileName => file.FileName;
    public string Title => file.IsPrimary ? "Backup principal" : file.IsCurrentDevice ? "Este computador"
        : file.FileName.StartsWith("profile-device-", StringComparison.OrdinalIgnoreCase) ? "Outro computador" : "Arquivo adicional";
    public string Description => file.CanImport ? file.Description : "JSON incompatível ou indisponível";
    public bool IsPrimary => file.IsPrimary;
    public bool CanImport => file.CanImport;
    public IReadOnlyList<CloudBackupCollectionRow> Collections { get; } =
        (file.Collections ?? []).Select(collection => new CloudBackupCollectionRow(collection)).ToArray();
    public string Details => file.CanImport
        ? $"{file.CollectionCount} coleção(ões) · {file.AppCount} aplicativo(s) · {(file.CreatedAt is { } date ? date.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "data desconhecida")}"
        : "Este arquivo não contém um perfil compatível para importação.";
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CloudBackupCollectionRow(CloudBackupCollection collection)
{
    public string Name => collection.Name;
    public string AppCount => $"{collection.Apps.Count} aplicativo(s)";
    public IReadOnlyList<CloudBackupAppRow> Apps { get; } = collection.Apps.Select(app => new CloudBackupAppRow(app)).ToArray();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class CloudBackupAppRow(CloudBackupApp app)
{
    public string Name => app.Name;
    public string Details => $"{app.Id} · {app.Source}";
}
