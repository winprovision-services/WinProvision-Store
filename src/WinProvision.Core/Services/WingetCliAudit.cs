using System.Diagnostics;
using System.Linq;

namespace WinProvision.Core.Services;

/// <summary>
/// Registro central de TODA execução do winget.exe feita pelo app. A Store liga o <see cref="Sink"/>
/// ao log de diagnóstico; cada linha traz os argumentos e a cadeia de chamadores, o que responde
/// "quem abriu esse winget.exe?" (instalação, listagem, atualização, checagem de versão...).
/// </summary>
public static class WingetCliAudit
{
    public static Action<string>? Sink { get; set; }

    /// <summary>
    /// Registra o início da execução e devolve um cronômetro já rodando — passe-o pra
    /// <see cref="Result"/> quando o processo terminar, pra fechar a mesma linha com exit
    /// code e tempo decorrido. Chamadores que não precisam do resultado (ex.: buscas) podem
    /// ignorar o retorno normalmente.
    /// </summary>
    public static Stopwatch Launch(string fileName, string arguments)
    {
        var stopwatch = Stopwatch.StartNew();
        var sink = Sink;
        if (sink is null)
        {
            return stopwatch;
        }

        try
        {
            var chain = string.Join(
                " <- ",
                new StackTrace(1, false).GetFrames()
                    .Select(DiagnosticMethodInfo.Create)
                    .Where(method => method is not null && method.DeclaringTypeName != typeof(WingetCliAudit).FullName)
                    .Select(method => $"{method!.DeclaringTypeName}.{method.Name}")
                    .Distinct()
                    .Take(6));
            sink($"WINGET.EXE LAUNCH file={fileName} args=\"{arguments}\" origem={chain}");
        }
        catch (Exception recoveryException)
        {
            WinProvision.Core.Services.CoreLog.Warning(recoveryException, "WingetCliAudit.Launch");

            // Auditoria nunca pode alterar o comportamento da execução.
        }

        return stopwatch;
    }

    /// <summary>
    /// Fecha a linha aberta por <see cref="Launch"/>: sem isso, um "WINGET.EXE LAUNCH" sem
    /// resultado correspondente no log não deixa saber se aquele processo terminou bem ou
    /// mal — quem lê o log precisava ficar de olho na UI pra descobrir.
    /// </summary>
    public static void Result(string fileName, int exitCode, bool success, Stopwatch stopwatch, string? arguments = null)
    {
        var sink = Sink;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink($"WINGET.EXE RESULT file={fileName} args={arguments ?? "(indisponível)"} exitCode={exitCode} success={success} elapsed={stopwatch.Elapsed}");
        }
        catch (Exception recoveryException)
        {
            WinProvision.Core.Services.CoreLog.Warning(recoveryException, "WingetCliAudit.Result");

            // Auditoria nunca pode alterar o comportamento da execução.
        }
    }

}
