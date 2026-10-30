using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services.Backup;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class LocalBackupServiceTests
{
    [Fact]
    public async Task SavesAndLoadsLatestBackupAndKeepsSnapshot()
    {
        string folder = CreateFolder();
        try
        {
            var service = new LocalBackupService(folder);
            var backup = new ProfileBackupSet
            {
                Tabs =
                [
                    new ProfileManifest
                    {
                        Name = "Essenciais",
                        Apps = [new ProfileAppRef { Id = "7zip.7zip", Source = "winget" }]
                    }
                ]
            };

            await service.SaveAsync(backup);
            ProfileBackupSet? loaded = await service.TryLoadLatestAsync();

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Tabs);
            Assert.Equal("Essenciais", loaded.Tabs[0].Name);
            Assert.True(File.Exists(service.LatestPath));
            Assert.Single(Directory.GetFiles(folder, "profile-????????-??????.json"));
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    [Fact]
    public async Task InvalidLatestBackupIsIgnoredWithoutThrowing()
    {
        string folder = CreateFolder();
        try
        {
            string path = Path.Combine(folder, "profile-latest.json");
            await File.WriteAllTextAsync(path, "{ inválido");

            var service = new LocalBackupService(folder);

            Assert.Null(await service.TryLoadLatestAsync());
        }
        finally
        {
            DeleteFolder(folder);
        }
    }

    private static string CreateFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "WinProvision-local-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
