using System;
using System.Threading;
using WinProvision.Providers.WinGet.Cli.Discovery;

namespace WinProvision.Core.Services;

/// <summary>
/// Caminho do cliente de pacotes usado por TODO o app (CLI, elevação, listagens). Por padrão é o
/// alias "cliente de pacotes" do PATH. Só muda quando o bootstrap (ver <see cref="WingetBootstrapper"/>)
/// confirma que o alias não existe mas o pacote App Installer está instalado em
/// "Program Files\WindowsApps": caso típico da conta SYSTEM, que não tem o alias por usuário
/// em %LocalAppData%\plataforma do sistema\WindowsApps, e de sessões em que o alias ainda não foi criado.
/// </summary>
public static class WingetLocator
{
    public const string DefaultExecutable = WinGetCliDiscovery.DefaultExecutableName;

    private static string _executablePath = DefaultExecutable;

    /// <summary>Executável a usar em qualquer chamada ao gerenciador de pacotes (alias ou caminho completo).</summary>
    public static string ExecutablePath => Volatile.Read(ref _executablePath);

    internal static void UsePackagedPath(string path) => Volatile.Write(ref _executablePath, path);

    /// <summary>
    /// Procura o cliente de pacotes do App Installer já instalado em "Program Files\WindowsApps"
    /// (pasta "plataforma do sistema.DesktopAppInstaller_{versão}_{arq}__8wekyb3d8bbwe"), escolhendo a
    /// maior versão. Devolve null se não achar ou se a pasta não for legível (contas sem
    /// permissão em WindowsApps; SYSTEM lê).
    /// </summary>
    public static string? FindPackagedExecutable() => new WinGetCliDiscovery().FindPackaged();
}
