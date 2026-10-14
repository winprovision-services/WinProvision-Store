namespace WinProvision.Core.Services.Profile;

public static class OfflineOfficeConfiguration
{
    public const string Folder = "Office";
    public const string ConfigurationFile = "configuration.xml";
    public const string SetupFile = "setup.exe";
    public const string ScriptFile = "Install-Office.ps1";
    public const string TrustedOfficeFolder = "OfficeScripts";
    public static string Relative(string name) => Folder + "/" + name;
}
