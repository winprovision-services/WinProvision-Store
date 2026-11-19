namespace WinProvision.Core.Services;

public static class LanWorkerStartupFailurePolicy
{
    public static LanInstallBatchResult CreateResult(
        IReadOnlyList<LanInstallerInstallRequest> installers,
        int exitCode,
        string workerPath,
        string standardOutput,
        string standardError)
    {
        ArgumentNullException.ThrowIfNull(installers);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);

        string hexCode = $"0x{unchecked((uint)exitCode):X8}";
        string cause = DescribeHostFailure(exitCode);
        string message = $"O componente de instalação não pôde iniciar (código {hexCode}). {cause} Etapa: início do worker. Caminho: {workerPath}";
        string details =
            $"Worker startup failed; exitCode={hexCode}; path={workerPath}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{standardOutput}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{standardError}";
        details = SensitiveDataRedactor.Redact(details);

        return new LanInstallBatchResult(installers.Select(installer => new LanInstallItemResult(
            installer.File,
            exitCode,
            LanInstallerExecutionStage.StartingWorker,
            LanInstallerOutcome.Failed,
            message,
            details)).ToArray(), message);
    }

    public static string DescribeHostFailure(int exitCode) => unchecked((uint)exitCode) switch
    {
        0x80008081 => "O runtime .NET necessário não foi encontrado. Reinstale o WinProvision Store ou instale o .NET 10.",
        0x80008082 => "A versão do runtime .NET solicitada não é compatível com este worker.",
        0x80008083 => "A biblioteca hostfxr não foi encontrada. Reinstale o WinProvision Store ou instale o .NET 10.",
        0x80008084 => "O hostfxr não conseguiu inicializar o runtime .NET.",
        0x80008096 => "A biblioteca hostpolicy não foi encontrada. Reinstale o WinProvision Store ou instale o .NET 10.",
        0x800700C1 => "A arquitetura do worker é incompatível com o Windows. Reinstale o WinProvision Store da arquitetura correta.",
        193 => "A arquitetura do worker é incompatível com o Windows. Reinstale o WinProvision Store da arquitetura correta.",
        2 => "O executável do worker não foi encontrado. Reinstale o WinProvision Store.",
        5 => "O Windows negou acesso ao worker. Reinstale o WinProvision Store ou verifique o antivírus.",
        1460 => "O teste de inicialização do worker excedeu o tempo limite. Verifique se o antivírus está bloqueando o executável.",
        _ => "O worker encerrou antes de gravar o resultado. Consulte os logs para obter a saída do processo."
    };
}
