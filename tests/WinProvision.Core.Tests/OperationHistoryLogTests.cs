using WinProvision.Core.Models;
using WinProvision.Core.Services;
using System.Text.Json;
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

            var history = new OperationHistoryService(path);
            OperationHistoryEntry entry = Assert.Single(history.Entries);
            Assert.NotNull(entry.StartedAt);
            Assert.Contains("Pacote localizado", entry.LogDetails);
            Assert.Contains("API do WinGet", entry.MethodLine);
            OperationHistoryRecord saved = Assert.Single(history.Records);
            Assert.Equal("winget", saved.Manager);
            Assert.Equal("winget", saved.SourceName);
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

    [Fact]
    public void HistoryPersistsTheOperationSnapshotForFaithfulReplay()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinProvision-history-snapshot-" + Guid.NewGuid().ToString("N") + ".json");
        Guid? operationId = null;
        try
        {
            var options = new PackageInstallOptions(Interactive: true, Scope: "user", Version: "2.4.1",
                CustomArguments: "--custom-flag", RequiresElevation: true, AllowPrerelease: true);
            var item = new OperationItem("Exemplo", OperationKind.Install)
            {
                State = OperationState.Running,
                PackageId = "Vendor.Example",
                Manager = "winget",
                SourceName = "winget",
                VersionTo = "2.4.1",
                Snapshot = new OperationSnapshot(1, "winget", "winget", "Vendor.Example", "Exemplo",
                    OperationKind.Install, null, "2.4.1", options)
            };
            item.State = OperationState.Completed;
            new OperationHistoryService(path).Record(item);

            OperationHistoryRecord record = Assert.Single(new OperationHistoryService(path).Records);
            operationId = record.Id;
            Assert.Equal(3, record.SchemaVersion);
            Assert.NotNull(record.Snapshot);
            Assert.Equal("Vendor.Example", record.Snapshot.PackageId);
            Assert.Equal("2.4.1", record.Snapshot.Options.Version);
            Assert.Equal("--custom-flag", record.Snapshot.Options.CustomArguments);
            Assert.True(record.Snapshot.Options.Interactive);
            Assert.True(record.Snapshot.Options.RequiresElevation);
            Assert.True(record.Snapshot.Options.AllowPrerelease);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (operationId is Guid id)
            {
                string outputPath = Path.Combine(Path.GetDirectoryName(path)!, "logs", "operations", id.ToString("D") + ".log");
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public void HistoryUsesJsonLinesAndMigratesThePreviousJsonArray()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-history-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string jsonl = Path.Combine(directory, "operation-history.jsonl");
        string legacy = Path.Combine(directory, "operation-history.json");
        try
        {
            var old = new OperationHistoryEntry(Guid.NewGuid(), "7-Zip", OperationKind.Install,
                OperationState.Completed, DateTimeOffset.UtcNow, WingetMethod.WingetExe,
                DateTimeOffset.UtcNow.AddSeconds(-5), "Instalação concluída.");
            File.WriteAllText(legacy, AotJson.Serialize(new[] { old }, WinProvisionJsonOptions.Compact));

            var history = new HistoryStore(jsonl);
            OperationHistoryRecord migrated = Assert.Single(history.Query("7-Zip"));
            Assert.Equal(OperationHistoryType.Install, migrated.Type);
            Assert.Equal(OperationHistoryStatus.Succeeded, migrated.Status);
            Assert.True(File.Exists(history.GetOutputPath(migrated)));
            Assert.Contains("Instalação concluída", history.ReadOutput(migrated));
            Assert.True(File.Exists(legacy), "O JSON antigo é preservado após migração.");

            string[] lines = File.ReadAllLines(jsonl);
            Assert.Single(lines);
            Assert.Contains("\"schemaVersion\":2", lines[0]);
            Assert.Contains("\"type\":\"install\"", lines[0]);
            Assert.Contains("\"status\":\"succeeded\"", lines[0]);
            Assert.Contains("\"packageId\":null", lines[0]);
            Assert.Contains("\"manager\":\"winget\"", lines[0]);
            Assert.Contains("\"sourceName\":\"winget\"", lines[0]);
            Assert.Contains("\"versionFrom\":null", lines[0]);
            Assert.DoesNotContain("Instalação concluída", lines[0]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JsonLinesSchemaOneIsUpgradedAndLegacyManagerIsRetained()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-history-v1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "operation-history.jsonl");
        try
        {
            Guid id = Guid.NewGuid();
            string finished = DateTimeOffset.UtcNow.ToString("O");
            File.WriteAllText(path, $$"""{"schemaVersion":1,"id":"{{id:D}}","type":"install","packageName":"Legacy App","packageId":"Legacy App","manager":"winget","sourceName":"winget","versionFrom":"","versionTo":"","status":"succeeded","startedAt":"{{finished}}","finishedAt":"{{finished}}","exitCode":null,"outputFile":"","extraField":"ignored"}""");

            var history = new HistoryStore(path);
            OperationHistoryRecord migrated = Assert.Single(history.Records);
            Assert.Equal(2, migrated.SchemaVersion);
            Assert.Equal("Legacy App", migrated.PackageName);
            Assert.Null(migrated.PackageId);
            Assert.Equal("winget", migrated.Manager);
            Assert.Equal("winget", migrated.SourceName);
            Assert.Null(migrated.VersionFrom);
            Assert.Null(migrated.VersionTo);
            Assert.Contains("\"schemaVersion\":2", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExistingLocalHistoryMigratesWithoutChangingItsRecordCountOrOriginalFiles()
    {
        string localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore");
        string localJsonl = Path.Combine(localRoot, "operation-history.jsonl");
        if (!File.Exists(localJsonl)) return;

        byte[] original = File.ReadAllBytes(localJsonl);
        string[] lines = File.ReadAllLines(localJsonl).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var expected = new Dictionary<Guid, (int SchemaVersion, string? VersionFrom, string? VersionTo)>();
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement json = document.RootElement;
            expected[json.GetProperty("id").GetGuid()] = (
                json.TryGetProperty("schemaVersion", out JsonElement schema) ? schema.GetInt32() : 1,
                json.TryGetProperty("versionFrom", out JsonElement from) && from.ValueKind == JsonValueKind.String ? from.GetString() : null,
                json.TryGetProperty("versionTo", out JsonElement to) && to.ValueKind == JsonValueKind.String ? to.GetString() : null);
        }
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-history-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string copiedJsonl = Path.Combine(directory, "operation-history.jsonl");
            File.Copy(localJsonl, copiedJsonl);
            string legacyJson = Path.Combine(localRoot, "operation-history.json");
            if (File.Exists(legacyJson)) File.Copy(legacyJson, Path.Combine(directory, "operation-history.json"));
            int copiedOutputCount = 0;
            foreach (string line in lines)
            {
                using JsonDocument json = JsonDocument.Parse(line);
                if (!json.RootElement.TryGetProperty("outputFile", out JsonElement output)
                    || output.ValueKind != JsonValueKind.String) continue;
                string relative = output.GetString()!.Replace('/', Path.DirectorySeparatorChar);
                string source = Path.GetFullPath(Path.Combine(localRoot, relative));
                string destination = Path.GetFullPath(Path.Combine(directory, relative));
                if (!source.StartsWith(Path.GetFullPath(localRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(source)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
                copiedOutputCount++;
            }

            var history = new HistoryStore(copiedJsonl);
            Assert.Equal(lines.Length, history.Records.Count);
            Assert.Equal(lines.Length, copiedOutputCount);
            Assert.All(history.Records, record =>
            {
                Assert.Equal(expected[record.Id].SchemaVersion, record.SchemaVersion);
                Assert.True(File.Exists(history.GetOutputPath(record)));
                Assert.Equal(expected[record.Id].VersionFrom, record.VersionFrom);
                Assert.Equal(expected[record.Id].VersionTo, record.VersionTo);
            });
            Assert.Equal(expected.Keys.Order().ToArray(), history.Records.Select(record => record.Id).Order().ToArray());
            Assert.Equal(original, File.ReadAllBytes(localJsonl));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptLinesAreSkippedAndRemoveDeletesTheSeparateOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-history-jsonl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "operation-history.jsonl");
        try
        {
            var history = new HistoryStore(path);
            var item = new OperationItem("Firefox", OperationKind.Update)
            {
                State = OperationState.Running,
                PackageId = "Mozilla.Firefox",
                ManagerOverride = "chocolatey",
                SourceNameOverride = "community",
                VersionFrom = "1.0",
                VersionTo = "2.0"
            };
            item.AppendLog("Atualização concluída.");
            item.State = OperationState.Completed;
            history.Add(item);
            OperationHistoryRecord record = Assert.Single(history.Records);
            string output = history.GetOutputPath(record);
            Assert.True(File.Exists(output));
            Assert.Equal("chocolatey", record.Manager);
            Assert.Equal("community", record.SourceName);
            Assert.Equal("1.0", record.VersionFrom);
            Assert.Equal("2.0", record.VersionTo);
            Assert.Equal(3, record.SchemaVersion);
            File.AppendAllText(path, "{linha inválida" + Environment.NewLine);

            var reloaded = new HistoryStore(path);
            Assert.Single(reloaded.Records);
            Assert.Equal("Mozilla.Firefox", Assert.Single(reloaded.Query(manager: "chocolatey")).PackageId);
            reloaded.Remove(record.Id);
            Assert.Empty(reloaded.Records);
            Assert.False(File.Exists(output));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
