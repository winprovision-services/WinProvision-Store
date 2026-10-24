namespace WinProvision.Core.Models;

public enum PackageInstallMethod
{
    Automatic,
    ComApi,
    WinProvisionApi,
    WingetCli,
    // Compatibilidade com preferências antigas; normalizado para Automatic.
    WinGetPkgsViper
}
