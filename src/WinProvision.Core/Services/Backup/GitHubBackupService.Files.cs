using System.Net.Http.Json;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services.Backup;

public partial class GitHubBackupService
{
    public async Task<(int DeletedCount, int TotalCount, string? Error)> DeleteAllAppGistsAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected) return (0, 0, "Conecte o GitHub novamente para excluir os Gists.");
            var ids = new List<string>();
            for (int page = 1; page <= 10; page++)
            {
                using var response = await SendWithTransientRetryAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiBase}/gists?per_page=100&page={page}"), ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return (0, 0, await FormatGitHubErrorAsync(response, "listar os Gists", ct).ConfigureAwait(false));
                var gists = await response.Content.ReadFromJsonAsync(AotJson.TypeInfo<List<GitHubGistResponse>>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken: ct).ConfigureAwait(false);
                if (gists is null) return (0, 0, "O GitHub retornou uma lista de Gists inválida.");
                ids.AddRange(gists.Where(IsAppGist).Select(gist => gist.Id!).Distinct(StringComparer.OrdinalIgnoreCase));
                if (gists.Count < 100) break;
                if (page == 10) return (0, 0, "Há mais de 1.000 Gists na conta. A exclusão em massa foi interrompida para evitar uma lista incompleta.");
            }

            int deleted = 0;
            foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                using var verify = await SendWithTransientRetryAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    $"{ApiBase}/gists/{Uri.EscapeDataString(id)}"), ct).ConfigureAwait(false);
                if (!verify.IsSuccessStatusCode)
                    return (deleted, ids.Count, await FormatGitHubErrorAsync(verify, "verificar um Gist antes de excluir", ct).ConfigureAwait(false));
                var gist = await verify.Content.ReadFromJsonAsync(AotJson.TypeInfo<GitHubGistResponse>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken: ct).ConfigureAwait(false);
                if (!IsAppGist(gist) || !string.Equals(gist?.Id, id, StringComparison.OrdinalIgnoreCase))
                    return (deleted, ids.Count, "Um Gist mudou desde a listagem. Atualize e tente novamente.");
                using var response = await SendWithTransientRetryAsync(() => new HttpRequestMessage(HttpMethod.Delete,
                    $"{ApiBase}/gists/{Uri.EscapeDataString(id)}"), ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return (deleted, ids.Count, await FormatGitHubErrorAsync(response, "excluir um Gist", ct).ConfigureAwait(false));
                deleted++;
                if (string.Equals(_account.GistId, id, StringComparison.OrdinalIgnoreCase)) _account.GistId = null;
                if (string.Equals(_account.BootstrapGistId, id, StringComparison.OrdinalIgnoreCase)) _account.BootstrapGistId = null;
                _account.LastSyncUtc = null;
                SaveGistIdToRegistry(_account.GistId);
                PersistAccountInfo();
            }
            _account.GistId = null;
            _account.BootstrapGistId = null;
            _account.LastSyncUtc = null;
            SaveGistIdToRegistry(null);
            PersistAccountInfo();
            return (deleted, ids.Count, null);
        }
        finally { _syncLock.Release(); }
    }

    private static bool IsAppGist(GitHubGistResponse? gist) => gist?.Id is { Length: > 0 }
        && ((string.Equals(gist.Description, GistDescription, StringComparison.Ordinal)
                && (gist.Files?.ContainsKey(BackupFileName) == true || gist.Files?.ContainsKey(LegacyBackupFileName) == true
                    || gist.Files?.Keys.Any(name => name.StartsWith("profile-device-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) == true))
            || (string.Equals(gist.Description, BootstrapGistDescription, StringComparison.Ordinal)
                && (gist.Files?.ContainsKey(BootstrapScriptFileName) == true || gist.Files?.ContainsKey(BackupFileName) == true)));

    public async Task<IReadOnlyList<CloudBackupFile>> ListBackupFilesAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gist = await GetOwnedBackupGistAsync(ct).ConfigureAwait(false);
            if (gist?.Files is null) return [];
            var result = new List<CloudBackupFile>();
            foreach (var (name, file) in gist.Files.OrderBy(entry => IsPrimaryBackupFile(entry.Key) ? 0 :
                entry.Key.Equals(BuildDeviceBackupFileName(), StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsBackupJsonFileName(name)) continue;
                ct.ThrowIfCancellationRequested();
                try
                {
                    string content = await ReadGistFileContentAsync(file, ct).ConfigureAwait(false);
                    ProfileBackupSet backup = ProfileDocumentService.Parse(content, Path.GetFileNameWithoutExtension(name));
                    int apps = backup.Tabs.Sum(tab => tab.Apps.Count);
                    using JsonDocument document = JsonDocument.Parse(content);
                    DateTime? createdAt = document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("createdAt", out var createdAtElement)
                        && createdAtElement.ValueKind == JsonValueKind.String
                        ? backup.CreatedAt : null;
                    string collectionNames = string.Join(", ", backup.Tabs.Take(5).Select(tab => tab.Name));
                    if (backup.Tabs.Count > 5) collectionNames += $" e mais {backup.Tabs.Count - 5}";
                    CloudBackupCollection[] collections = backup.Tabs.Select(tab => new CloudBackupCollection(
                        string.IsNullOrWhiteSpace(tab.Name) ? "Coleção sem nome" : tab.Name.Trim(),
                        tab.Apps.Select(app => new CloudBackupApp(
                            string.IsNullOrWhiteSpace(app.Name) ? app.Id : app.Name.Trim(), app.Id,
                            string.IsNullOrWhiteSpace(app.Source) ? "WinGet" : app.Source)).ToArray())).ToArray();
                    result.Add(new CloudBackupFile(name, DescribeBackupFile(name), createdAt,
                        backup.Tabs.Count, apps, IsPrimaryBackupFile(name),
                        name.Equals(BuildDeviceBackupFileName(), StringComparison.OrdinalIgnoreCase), true, collectionNames, collections));
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or HttpRequestException)
                {
                    result.Add(new CloudBackupFile(name, "JSON incompatível ou indisponível", null, 0, 0,
                        IsPrimaryBackupFile(name), name.Equals(BuildDeviceBackupFileName(), StringComparison.OrdinalIgnoreCase), false));
                }
            }
            return result;
        }
        finally { _syncLock.Release(); }
    }

    public async Task<CloudBackupDownloadResult> DownloadBackupFileAsync(string fileName, CancellationToken ct = default)
    {
        if (!IsBackupJsonFileName(fileName))
            return CloudBackupDownloadResult.Fail("Nome de arquivo JSON inválido.");
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gist = await GetOwnedBackupGistAsync(ct).ConfigureAwait(false);
            if (gist?.Files?.TryGetValue(fileName, out var file) != true)
                return CloudBackupDownloadResult.Fail("O arquivo selecionado não está mais no Gist.");
            string content = await ReadGistFileContentAsync(file!, ct).ConfigureAwait(false);
            return CloudBackupDownloadResult.Ok(ProfileDocumentService.Parse(content, Path.GetFileNameWithoutExtension(fileName)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or HttpRequestException)
        {
            return CloudBackupDownloadResult.Fail($"Não foi possível ler o JSON selecionado: {exception.Message}");
        }
        finally { _syncLock.Release(); }
    }

    public async Task<(bool Success, string? Error)> DeleteBackupFileAsync(string fileName, CancellationToken ct = default)
    {
        if (!IsBackupJsonFileName(fileName)) return (false, "Nome de arquivo JSON inválido.");
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var gist = await GetOwnedBackupGistAsync(ct).ConfigureAwait(false);
            if (gist?.Id is null || gist.Files?.ContainsKey(fileName) != true)
                return (false, "O arquivo selecionado não está mais no Gist.");
            var payload = new Dictionary<string, object?>
            {
                ["files"] = new Dictionary<string, object?> { [fileName] = null }
            };
            using var response = await SendWithTransientRetryAsync(() => new HttpRequestMessage(HttpMethod.Patch,
                $"{ApiBase}/gists/{Uri.EscapeDataString(gist.Id)}")
            {
                Content = new StringContent(AotJson.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
            }, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, await FormatGitHubErrorAsync(response, "excluir o arquivo do Gist", ct).ConfigureAwait(false));
            if (IsPrimaryBackupFile(fileName))
            {
                _account.LastSyncUtc = null;
                PersistAccountInfo();
            }
            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException exception) { return (false, exception.Message); }
        finally { _syncLock.Release(); }
    }

    private async Task<GitHubGistResponse?> GetOwnedBackupGistAsync(CancellationToken ct)
    {
        if (!IsConnected) throw new InvalidOperationException("Conecte o GitHub antes de acessar os arquivos da nuvem.");
        var lookup = await ResolveOwnedGistIdAsync(_account.GistId ?? LoadGistIdFromRegistry(), ct).ConfigureAwait(false);
        if (lookup.Error is not null) throw new HttpRequestException(lookup.Error);
        if (lookup.Id is null) return null;
        _account.GistId = lookup.Id;
        using var response = await SendWithTransientRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/gists/{Uri.EscapeDataString(lookup.Id)}"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await FormatGitHubErrorAsync(response, "listar os arquivos do Gist", ct).ConfigureAwait(false));
        var gist = await response.Content.ReadFromJsonAsync(AotJson.TypeInfo<GitHubGistResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), cancellationToken: ct).ConfigureAwait(false);
        if (!IsWinProvisionGist(gist)) throw new InvalidDataException("O Gist encontrado não pertence ao backup do WinProvision Store.");
        return gist;
    }

    private async Task<string> ReadGistFileContentAsync(GitHubGistFile file, CancellationToken ct)
    {
        if (!file.Truncated && !string.IsNullOrEmpty(file.Content)) return file.Content;
        if (!Uri.TryCreate(file.RawUrl, UriKind.Absolute, out var uri)
            || !IsAllowedGitHubDestination(uri, "gist.githubusercontent.com"))
            throw new InvalidDataException("O Gist não forneceu uma URL raw válida.");
        using var response = await SendWithTransientRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, uri), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await FormatGitHubErrorAsync(response, "ler o arquivo do Gist", ct).ConfigureAwait(false));
        if (response.Content.Headers.ContentLength > ProfileDocumentService.MaximumBytes)
            throw new InvalidDataException("O arquivo excede 16 MiB.");
        string content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (System.Text.Encoding.UTF8.GetByteCount(content) > ProfileDocumentService.MaximumBytes)
            throw new InvalidDataException("O arquivo excede 16 MiB.");
        return content;
    }

    private static bool IsBackupJsonFileName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 255
        && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['/', '\\', '\r', '\n']) < 0;

    private static bool IsPrimaryBackupFile(string name) =>
        name.Equals(BackupFileName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(LegacyBackupFileName, StringComparison.OrdinalIgnoreCase);

    private static string DescribeBackupFile(string name) => name.Equals(BackupFileName, StringComparison.OrdinalIgnoreCase)
        ? "Backup principal (sincronizado automaticamente)"
        : name.Equals(BuildDeviceBackupFileName(), StringComparison.OrdinalIgnoreCase)
            ? "Cópia deste computador"
            : name.StartsWith("profile-device-", StringComparison.OrdinalIgnoreCase)
                ? "Cópia de outro computador"
                : "Arquivo JSON do Gist";
}
