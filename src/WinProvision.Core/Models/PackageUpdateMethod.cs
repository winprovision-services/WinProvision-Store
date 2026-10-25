namespace WinProvision.Core.Models;

/// <summary>Rota preferencial independente para atualizar pacotes instalados.</summary>
public enum PackageUpdateMethod
{
    Automatic,
    ComApi,
    WinProvisionApi,
    WingetCli
}
