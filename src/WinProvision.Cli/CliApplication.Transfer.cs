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
            using var reader = new ProfileDocumentReader();
            _ = ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(await reader.ReadAsync(temporary, token)));
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
            Write($"Lista exportada: {destination}");
            return 0;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<int> ImportAsync(InteractiveCliRequest request, CancellationToken token)
    {
        using var reader = new ProfileDocumentReader();
        var packages = ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(await reader.ReadAsync(request.Input!, token)));
        if (packages.Count == 0) { Write("A lista não contém pacotes."); return 0; }
        string action = request.Download ? "baixar" : "instalar";
        Write($"{packages.Count} pacote(s) para {action}:");
        foreach (var package in packages) Write($"{package.Name ?? package.Id} | {package.Id} | {package.Source} | {package.PinnedVersion ?? "atual"}");
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
                    ? await backend.DownloadProfileAsync(package, request.Output, Log, token)
                    : await backend.ExecuteProfileAsync(package, Log, token);
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

}
