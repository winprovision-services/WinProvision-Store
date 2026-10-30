using System.Runtime.InteropServices;
using System.Security.Principal;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;

internal static partial class Program
{
    private static int Main()
    {
        // This ambiente de validação removes/reinstalls 7-Zip. It must never run on a computador de uso cotidiano.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted") return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Execute o teste com integridade média.");
        Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            return RunAsync().GetAwaiter().GetResult();
        }
        finally { CoUninitialize(); }
    }

    private static async Task<int> RunAsync()
    {
        PackageDiagnostics.Configure(Console.WriteLine, () => { });
        var executor = new WingetExecutor();
        await using var api = new WinProvisionApiService();
        var service = new WinGetService(executor, api, new());
        string binary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        foreach (var method in new[] { PackageInstallMethod.ComApi, PackageInstallMethod.WingetCli, PackageInstallMethod.WinProvisionApi })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            var removed = await executor.UninstallAppAsync("7zip.7zip", Console.WriteLine, timeout.Token,
                source: "winget", requiresElevation: true);
            // Some uninstallers return before their cleanup process removes the files.
            // Keep the binary check as a hard prerequisite, but allow cleanup to finish.
            var cleanupDeadline = DateTime.UtcNow.AddSeconds(30);
            while (File.Exists(binary) && DateTime.UtcNow < cleanupDeadline)
                await Task.Delay(250, timeout.Token);
            if (File.Exists(binary)) throw new InvalidOperationException($"7-Zip ainda instalado antes de testar {method}: {removed.Output}");
            WingetMethod actualMethod = WingetMethod.Unknown;
            bool inProcessObserved = false;
            void Log(string line) { if (line.StartsWith("COM INPROC ", StringComparison.Ordinal)) inProcessObserved = true; Console.WriteLine(line); }
            var result = await service.InstallWithMethodAsync("7zip.7zip", method, Log, timeout.Token,
                onProgress: update => { actualMethod = update.Method; Console.WriteLine($"PROGRESS {method} {update.Method} {update.Phase} {update.Percent}"); });
            if (!result.Success || !File.Exists(binary))
                throw new InvalidOperationException($"Instalação real {method} falhou: {result.ExitCode} {result.Output}");
            var expectedMethod = method switch
            {
                PackageInstallMethod.ComApi => WingetMethod.ComApi,
                PackageInstallMethod.WingetCli => WingetMethod.WingetExe,
                _ => WingetMethod.OwnApi
            };
            if (actualMethod != expectedMethod) throw new InvalidOperationException($"O teste de {method} mudou para {actualMethod}.");
            if (method != PackageInstallMethod.WinProvisionApi && !result.WasElevated)
                throw new InvalidOperationException($"O método {method} não confirmou elevação pelo helper.");
            if (method == PackageInstallMethod.ComApi && !inProcessObserved)
                throw new InvalidOperationException("A instalação COM não comprovou o uso do SDK no processo elevado.");
            Console.WriteLine($"REAL INSTALL PASSED: selected={method}; executed={actualMethod}; exit={result.ExitCode}; binary={binary}");
        }
        return 0;
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint apartment);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
}
