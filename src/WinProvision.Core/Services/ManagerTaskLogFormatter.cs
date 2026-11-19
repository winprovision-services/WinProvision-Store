using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Formata tarefas persistidas no formato textual da tela de registros.</summary>
public static class ManagerTaskLogFormatter
{
    public const int DefaultMaximumCharacters = 2 * 1024 * 1024;
    private const int MaximumOutputCharacters = 50 * 1024;
    private const int SeparatorLength = 76;

    public static string Format(IReadOnlyList<ManagerTaskLogRecord> tasks, int maximumCharacters = DefaultMaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var blocks = new List<string>();
        int length = 0;
        foreach (ManagerTaskLogRecord task in tasks)
        {
            string block = FormatTask(task);
            if (length + block.Length + 2 > maximumCharacters)
            {
                blocks.Add("[Exibição limitada a 2 MB; exporte o arquivo para incluir o restante.]");
                break;
            }
            blocks.Add(block);
            length += block.Length + 2;
        }
        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static string FormatTask(ManagerTaskLogRecord task)
    {
        string Clip(string value) => value.Length > MaximumOutputCharacters
            ? value[..MaximumOutputCharacters] + Environment.NewLine + "[saída truncada — excedeu 50 KB]"
            : value;
        string start = task.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        string end = task.FinishedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "EM EXECUÇÃO";
        var lines = new List<string> { new('_', SeparatorLength) };
        if (task.Kind.Equals("Native", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Tarefa nativa registrada no gerenciador {task.Manager}. Tipo da tarefa: {task.TaskType}");
            lines.Add($"Início do processo: {start}");
            lines.Add($"Fim do processo:    {end}");
            lines.Add(string.Empty);
            lines.Add("-- Informações da tarefa");
            lines.AddRange((string.IsNullOrWhiteSpace(task.TaskInformation) ? " ..." : Clip(task.TaskInformation))
                .Split('\n').Select(line => "  " + line));
            if (!string.IsNullOrWhiteSpace(task.StandardError))
            {
                lines.Add(string.Empty);
                lines.Add("-- Erros da tarefa");
                lines.AddRange(Clip(task.StandardError).Split('\n').Select(line => "  " + line));
            }
            lines.Add(task.Result == "success" ? "A tarefa foi concluída com sucesso"
                : task.Result == "error" ? "A tarefa falhou" : "A tarefa terminou com avisos");
        }
        else
        {
            lines.Add($"Tarefa por subprocesso registrada no gerenciador {task.Manager}. Tipo: {task.TaskType}");
            lines.Add($"Executável: \"{task.Executable}\"");
            lines.Add($"Argumentos: \"{task.Arguments}\"");
            lines.Add($"Início do processo: {start}");
            lines.Add($"Fim do processo:    {end}");
            lines.Add(string.Empty);
            lines.Add("-- Saída do processo (STDOUT)");
            lines.AddRange(Clip(task.StandardOutput).Split('\n').Select(line => "  " + line));
            if (!string.IsNullOrWhiteSpace(task.StandardError))
            {
                lines.Add(string.Empty);
                lines.Add("-- Erro do processo (STDERR)");
                lines.AddRange(Clip(task.StandardError).Split('\n').Select(line => "  " + line));
            }
            lines.Add(task.ExitCode is null ? "Código de retorno: não informado"
                : task.ExitCode == 0 ? "Código de retorno: SUCESSO (0)"
                : $"Código de retorno: FALHA (0x{(uint)task.ExitCode.Value:X}, {task.ExitCode})");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
