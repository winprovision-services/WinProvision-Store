using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Cli;

public sealed partial class CliApplication
{
    private int Report(WingetExecutionResult result, string success = "Concluído", string? name = null)
    {
        if (!result.Success)
        {
            Log($"Falha (código {result.ExitCode}): {result.Output}");
            _console.Result(CliFailure.Message(CliFailure.Describe(result), name), false);
            return 1;
        }
        _console.Result(success, true, result.RebootRequired);
        return 0;
    }

    private async Task<int> ExportAsync(InteractiveCliRequest request, CancellationToken token)
    {
        string destination = Path.GetFullPath(request.Output ?? "winprovision-apps.json");
        if (File.Exists(destination)) throw new IOException($"O arquivo já existe: {destination}. Escolha outro --output.", unchecked((int)0x80070050));
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".winprovision-{Guid.NewGuid():N}.json");
        try
        {
            var result = await _console.RunAsync("Exportando aplicativos", () => backend.ExportAsync(temporary, Log, token), token);
            if (!result.Success) return Report(result);
            // Validate the complete manifest before publishing an export.
            using var reader = new ProfileDocumentReader();
            _ = ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(await reader.ReadAsync(temporary, token)));
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            _console.Result("Lista exportada", true);
            _console.Section("Exportação", $"Destino: {destination}");
            return 0;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<int> ImportAsync(InteractiveCliRequest request, CancellationToken token)
    {
        using var reader = new ProfileDocumentReader();
        var packages = ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(await reader.ReadAsync(request.Input!, token)));
        if (packages.Count == 0) { _console.Section("Importação", "A lista não contém pacotes."); return 0; }
        string action = request.Download ? "baixar" : "instalar";
        _console.Heading("Importação");
        _console.Note($"{packages.Count} pacote(s) para {action}:");
        int succeeded = 0, failed = 0, reboot = 0;
        for (int i = 0; i < packages.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var package = packages[i];
            try
            {
                var result = await _console.RunAsync($"{(request.Download ? "Baixando" : "Instalando")} {package.Name ?? package.Id}",
                    () => request.Download
                        ? backend.DownloadProfileAsync(package, request.Output, Log, token)
                        : backend.ExecuteProfileAsync(package, Log, token), token, i, packages.Count);
                if (Report(result, $"{package.Name ?? package.Id} — {(request.Download ? "download concluído" : "instalado")}", package.Name ?? package.Id) == 0) succeeded++; else failed++;
                if (result.RebootRequired) reboot++;
                if (result.RebootInitiated) break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { failed++; Log(exception.ToString()); _console.Result(CliFailure.Message(CliFailure.Describe(exception), package.Name ?? package.Id), false); }
        }
        _console.Summary(succeeded, failed, reboot);
        return failed == 0 ? 0 : 1;
    }

}
