using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ManagerTaskLogViewerTests
{
    [Fact]
    public async Task WritesReadsAndFormatsNativeAndSubprocessTasksAsThreeVisibleBlocks()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-ManagerLogs", Guid.NewGuid().ToString("N"));
        string path = ManagerTaskLogStore.GetPath(directory);
        var store = new ManagerTaskLogStore(null!, path);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ManagerTaskLogRecord[] tasks =
        [
            new()
            {
                TaskId = Guid.NewGuid(), Manager = "winget", TaskType = "Listar fontes", Kind = "Native",
                StartedAt = now, FinishedAt = now.AddSeconds(1), TaskInformation = "Fontes carregadas", Result = "success"
            },
            new()
            {
                TaskId = Guid.NewGuid(), Manager = "winget", TaskType = "Buscar", Kind = "Subprocess",
                Executable = "winget.exe", Arguments = "search Example", StartedAt = now, FinishedAt = now.AddSeconds(2),
                StandardOutput = "Example.App 1.0", ExitCode = 0, Result = "success"
            },
            new()
            {
                TaskId = Guid.NewGuid(), Manager = "winget", TaskType = "Instalar", Kind = "Subprocess",
                Executable = "winget.exe", Arguments = "install Broken.App", StartedAt = now, FinishedAt = now.AddSeconds(3),
                StandardError = "Erro de exemplo do processo", ExitCode = 1, Result = "error"
            }
        ];

        try
        {
            foreach (ManagerTaskLogRecord task in tasks)
                await store.AppendAsync(task);

            Assert.Equal(path, store.FilePath);
            Assert.True(File.Exists(path));
            ManagerTaskLogRecord[] read = (await store.ReadAsync("winget", maximumTasks: 100)).ToArray();
            string text = ManagerTaskLogFormatter.Format(read);

            Assert.Equal(3, read.Length);
            Assert.Equal(3, text.Split(new string('_', 76), StringSplitOptions.None).Length - 1);
            Assert.Contains("Tarefa nativa registrada no gerenciador winget", text, StringComparison.Ordinal);
            Assert.Contains("-- Informações da tarefa", text, StringComparison.Ordinal);
            Assert.Contains("Example.App 1.0", text, StringComparison.Ordinal);
            Assert.Contains("-- Erro do processo (STDERR)", text, StringComparison.Ordinal);
            Assert.Contains("Erro de exemplo do processo", text, StringComparison.Ordinal);
            Assert.Contains("Código de retorno: FALHA", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
