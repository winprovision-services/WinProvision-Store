using System.Reflection;
using System.Text;
using WinProvision.Cli;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    string version = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
    return await new CliApplication(new WingetCliBackend(), Console.In, Console.Out, Console.Error, version)
        .RunAsync(args, cancellation.Token);
}
catch (OperationCanceledException) { Console.Error.WriteLine("Operação cancelada."); return 130; }
catch (Exception exception)
{
    Console.Error.WriteLine("Erro: " + CliApplication.Clean(exception.Message));
    return exception is ArgumentException ? 2 : 1;
}
