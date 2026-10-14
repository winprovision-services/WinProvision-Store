using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Cli;

public sealed partial class CliApplication
{
    private int Report(WingetExecutionResult result)
    {
        if (!result.Success) { Log($"Falha (código {result.ExitCode}): {result.Output}"); return 1; }
        Write(result.RebootRequired ? "Concluído. Reinicialização necessária." : "Concluído.");
        return 0;
    }

    private async Task<int> ExportAsync(InteractiveCliRequest request, CancellationToken token)
    {
        string destination = Path.GetFullPath(request.Output ?? "winprovision-apps.json");
        if (File.Exists(destination)) throw new IOException($"O arquivo já existe: {destination}. Escolha outro --output.");
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".winprovision-{Guid.NewGuid():N}.json");
        try
        {
            var result = await backend.ExportAsync(temporary, Log, token);
            if (!result.Success) return Report(result);
            // Validate the complete manifest before publishing an export.
            ParseManifest(await ReadManifestAsync(temporary, token));
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            Write($"Lista exportada: {destination}");
            return 0;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<int> ImportAsync(InteractiveCliRequest request, CancellationToken token)
    {
        var packages = ParseManifest(await ReadManifestAsync(request.Input!, token));
        if (packages.Count == 0) { Write("A lista não contém pacotes."); return 0; }
        string action = request.Download ? "baixar" : "instalar";
        Write($"{packages.Count} pacote(s) para {action}:");
        foreach (var package in packages) Write(Describe(package, "install"));
        Write("[1] Confirmar\n[0] Sair");
        if (await ReadConfirmation(token) == 0) return 0;
        int succeeded = 0, failed = 0, reboot = 0;
        for (int i = 0; i < packages.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var package = packages[i];
            Write($"\n{i + 1}/{packages.Count} — {action} {package.Id}");
            try
            {
                var result = request.Download
                    ? await backend.DownloadAsync(package, request.Output, Log, token)
                    : await backend.ExecuteAsync(true, package, Log, token);
                if (Report(result) == 0) succeeded++; else failed++;
                if (result.RebootRequired) reboot++;
                if (result.RebootInitiated) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { failed++; Log($"Falha em {package.Id}: {exception.Message}"); }
        }
        Write($"\nResumo: {succeeded} concluído(s), {failed} falha(s), {reboot} exige(m) reinicialização.");
        return failed == 0 ? 0 : 1;
    }

    internal static List<UpgradablePackage> ParseManifest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var packages = new List<UpgradablePackage>();
        void Add(JsonElement entries, string? source, bool winget)
        {
            if (entries.ValueKind != JsonValueKind.Array) throw new ArgumentException("A lista de pacotes deve ser um array.");
            foreach (var entry in entries.EnumerateArray())
            {
                if (packages.Count >= 1000) throw new ArgumentException("Limite de 1000 pacotes por lista.");
                string? id = entry.GetProperty(winget ? "PackageIdentifier" : "id").GetString();
                string? packageSource = winget ? source : entry.TryGetProperty("source", out var value) ? value.GetString() : "winget";
                // Apply exactly the same identifier/source contract as an explicit install.
                var parsed = InteractiveCliInput.ParseRequest(["install", "--id", id ?? "", "--source", packageSource ?? ""]);
                packages.Add(new() { Id = parsed.Id!, Name = parsed.Id!, Source = parsed.Source! });
            }
        }
        if (root.ValueKind == JsonValueKind.Array) Add(root, null, false);
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in sources.EnumerateArray())
                Add(source.GetProperty("Packages"), source.GetProperty("SourceDetails").GetProperty("Name").GetString(), true);
        }
        else throw new ArgumentException("JSON inválido. Use um export WinGet ou um array com id e source.");
        return packages.DistinctBy(package => (package.Source, package.Id.ToLowerInvariant())).ToList();
    }

    private static async Task<byte[]> ReadManifestAsync(string location, CancellationToken token)
    {
        const int limit = 2 * 1024 * 1024;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        using var response = Uri.TryCreate(location, UriKind.Absolute, out var uri) && !uri.IsFile
            ? uri.Scheme == Uri.UriSchemeHttps
                ? await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                : throw new ArgumentException("A URL da lista deve usar HTTPS.")
            : null;
        response?.EnsureSuccessStatusCode();
        await using Stream stream = response is null
            ? File.OpenRead(uri?.IsFile == true ? uri.LocalPath : location)
            : await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > limit) throw new ArgumentException("A lista JSON excede 2 MiB.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
