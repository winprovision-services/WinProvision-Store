namespace WinProvision.Core.Models;

/// <summary>Motor usado para as operações do catálogo WinGet.</summary>
public enum WinGetEnginePreference
{
    Automatic,
    ComApi,
    WingetCli
}

public static class WinGetEnginePreferenceExtensions
{
    public static PackageInstallMethod ToInstallMethod(this WinGetEnginePreference preference) => preference switch
    {
        WinGetEnginePreference.ComApi => PackageInstallMethod.ComApi,
        WinGetEnginePreference.WingetCli => PackageInstallMethod.WingetCli,
        _ => PackageInstallMethod.Automatic
    };

    public static PackageUpdateMethod ToUpdateMethod(this WinGetEnginePreference preference) => preference switch
    {
        WinGetEnginePreference.ComApi => PackageUpdateMethod.ComApi,
        WinGetEnginePreference.WingetCli => PackageUpdateMethod.WingetCli,
        _ => PackageUpdateMethod.Automatic
    };
}
