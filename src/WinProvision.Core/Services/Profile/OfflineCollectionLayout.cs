namespace WinProvision.Core.Services.Profile;

public static class OfflineCollectionLayout
{
    public const string ScriptFile = "Install.ps1";
    public const string ReadmeFile = "Readme.txt";
    public const string InstallersFolder = "Instaladores";
    public const string ManifestFile = "colecao.json";
    public static string ManifestPath(bool hasInstallers) => (hasInstallers ? InstallersFolder : OfflineOfficeConfiguration.Folder) + "/" + ManifestFile;
    public static IReadOnlyList<string> ManifestPaths { get; } = [InstallersFolder + "/" + ManifestFile, OfflineOfficeConfiguration.Folder + "/" + ManifestFile, ManifestFile];
}
