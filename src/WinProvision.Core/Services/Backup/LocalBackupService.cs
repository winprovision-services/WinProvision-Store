using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Backup;

/// <summary>
/// Armazena o backup atual e até dez snapshots locais para restauração de coleções.
/// </summary>
public class LocalBackupService
{
    private const int MaxSnapshots = 10;

    private readonly string _backupDir;
    private readonly string _latestPath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public LocalBackupService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvision", "Backup"))
    {
    }

    /// <summary>Construtor interno com diretório explícito — facilita testes sem tocar no LocalAppData real.</summary>
    internal LocalBackupService(string backupDir)
    {
        _backupDir = backupDir;
        _latestPath = Path.Combine(_backupDir, "profile-latest.json");
    }

    public string LatestPath => _latestPath;

    /// <summary>Data/hora (UTC) do último backup local, ou null se nenhum foi feito ainda.</summary>
    public DateTime? LastBackupUtc => File.Exists(_latestPath)
        ? File.GetLastWriteTimeUtc(_latestPath)
        : null;

    public async Task SaveAsync(ProfileBackupSet backupSet, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(_backupDir);

            string json = WinProvision.Core.Services.AotJson.Serialize(backupSet, WinProvisionJsonOptions.Profile);
            await WriteAllTextUtf8NoBomAsync(_latestPath, json, ct);

            string snapshotPath = Path.Combine(_backupDir, $"profile-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await WriteAllTextUtf8NoBomAsync(snapshotPath, json, ct);

            PruneOldSnapshots();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>Lê a última versão salva. Retorna null se nunca houve backup ou se o arquivo estiver corrompido.</summary>
    public async Task<ProfileBackupSet?> TryLoadLatestAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_latestPath))
            return null;

        try
        {
            string json = await File.ReadAllTextAsync(_latestPath, ct);
            return WinProvision.Core.Services.AotJson.Deserialize<ProfileBackupSet>(json, WinProvisionJsonOptions.Profile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Mantém só os N snapshots mais recentes — evita crescer sem limite em máquinas usadas por muito tempo.</summary>
    private void PruneOldSnapshots()
    {
        var snapshots = new DirectoryInfo(_backupDir)
            .GetFiles("profile-????????-??????.json")
            .OrderByDescending(f => f.CreationTimeUtc)
            .Skip(MaxSnapshots);

        foreach (var file in snapshots)
        {
            try { file.Delete(); } catch (IOException) { /* melhor esforço — não é crítico */ }
        }
    }

    private static Task WriteAllTextUtf8NoBomAsync(string path, string json, CancellationToken ct) =>
        AtomicFile.WriteAllTextAsync(path, json, ct);
}
