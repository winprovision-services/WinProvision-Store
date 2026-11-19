using System.Reflection;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationContractCompatibilityTests
{
    [Fact]
    public void PreRefactorHistoryFixtureStillLoadsSnapshotAndRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), "winprovision-history-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "operation-history.jsonl");
        try
        {
            File.WriteAllText(path, Resource("OperationContractTests.operation-history-pre-refactor.jsonl"));
            OperationHistoryRecord record = Assert.Single(new OperationHistoryService(path).Records);
            Assert.Equal(3, record.SchemaVersion);
            Assert.Equal("install", record.Type.ToValue());
            Assert.Equal("Vendor.Fixture", record.Snapshot!.PackageId);
            Assert.Equal(0, (int)record.Snapshot.Kind);
            Assert.Equal("1.2.3", record.Snapshot.VersionTo);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void PreRefactorOperationPreferencesFixtureKeepsNamesAndValues()
    {
        string root = Path.Combine(Path.GetTempPath(), "winprovision-preferences-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "operation-preferences.json");
        try
        {
            File.WriteAllText(path, Resource("OperationContractTests.operation-preferences-pre-refactor.json"));
            var service = new OperationPreferencesService(path);
            OperationPreferences preferences = service.Load();
            Assert.Equal(2, preferences.ParallelOperations);
            Assert.Equal(4, preferences.ParallelDownloads);
            Assert.Equal(InstallerNameStyle.PackageVersionArchitecture, preferences.InstallerNames);
            service.Save(preferences);
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.True(saved.RootElement.TryGetProperty("ParallelOperations", out JsonElement parallel));
            Assert.Equal(2, parallel.GetInt32());
            Assert.False(saved.RootElement.TryGetProperty("parallelOperations", out _));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    private static string Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
