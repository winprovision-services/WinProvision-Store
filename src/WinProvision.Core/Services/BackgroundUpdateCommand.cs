namespace WinProvision.Core.Services;

/// <summary>Command shared by logon registration and the legacy background host.</summary>
public static class BackgroundUpdateCommand
{
    public static string CliPath(string applicationDirectory) =>
        Path.Combine(applicationDirectory, "cli", "winprovision.exe");

    public static string ForDirectory(string applicationDirectory) =>
        $"\"{CliPath(applicationDirectory)}\" --background-update";

    public static string StorePath(string applicationDirectory) =>
        Path.Combine(applicationDirectory, "WinProvision.Store.exe");

    public static string StartupForDirectory(string applicationDirectory) =>
        $"\"{StorePath(applicationDirectory)}\" --startup-minimized";
}
