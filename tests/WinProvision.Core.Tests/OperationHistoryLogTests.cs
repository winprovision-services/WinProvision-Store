using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class OperationHistoryLogTests
{
    [Fact]
    public void SearchMatchesMultipleTermsAcrossOperationMetadataAndOutput()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinProvision-history-search-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var history = new OperationHistoryService(path);
            var install = new OperationItem("7-Zip", OperationKind.Install)
            {
                State = OperationState.Running,
                Method = WingetMethod.ComApi
            };
            install.AppendLog("Package located in catalog");
            install.State = OperationState.Completed;
            history.Record(install);

            var failedUpdate = new OperationItem("Firefox", OperationKind.Update)
            {
                State = OperationState.Running,
                Method = WingetMethod.WingetExe
            };
            failedUpdate.AppendLog("Network timeout");
            failedUpdate.State = OperationState.Failed;
            history.Record(failedUpdate);

            Assert.Equal("7-Zip", Assert.Single(history.Search("api winget package located")).AppName);
            Assert.Equal("Firefox", Assert.Single(history.Search("FIREFOX falhou timeout")).AppName);
            Assert.Empty(history.Search("7-Zip falhou"));
            Assert.Equal(2, history.Search(" ").Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string logPath = Path.ChangeExtension(path, ".log");
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public void FinishedOperationRetainsTimingAndOutputAcrossReload()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinProvision-history-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var item = new OperationItem("7-Zip", OperationKind.Install);
            item.State = OperationState.Running;
            item.Method = WingetMethod.ComApi;
            item.AppendLog("Pacote localizado no catálogo WinGet.");
            item.State = OperationState.Completed;
            new OperationHistoryService(path).Record(item);

            OperationHistoryEntry entry = Assert.Single(new OperationHistoryService(path).Entries);
            Assert.NotNull(entry.StartedAt);
            Assert.Contains("Pacote localizado", entry.LogDetails);
            Assert.Contains("API do WinGet", entry.MethodLine);
            var history = new OperationHistoryService(path);
            string taskLog = File.ReadAllText(history.TaskLogPath);
            Assert.Contains("Início:", taskLog);
            Assert.Contains("-- Saída da operação", taskLog);
            Assert.Contains("Instalação · 7-Zip — Concluída", taskLog);
            history.Clear();
            Assert.False(File.Exists(history.TaskLogPath));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            string logPath = Path.ChangeExtension(path, ".log");
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }
}
