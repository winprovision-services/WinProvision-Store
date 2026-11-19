using System.Runtime.Versioning;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Provisioning;
using WinProvision.Elevation;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Serviço especializado para desinstalação completa do Office usando métodos
/// padrão e agressivos (GetHelpCmd) quando necessário, sempre com privilégios
/// administrativos via ElevatedProcessRunner.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeUninstallService
{
    private const string GetHelpCmdDownloadUrl = NetworkEndpoints.HelpRecoveryAssistantUrl;
    // A GetHelpCmd build expires 90 days after its creation date. Refresh at 60 days
    // so a cached copy does not reach plataforma do sistema's expiration window.
    private static readonly TimeSpan GetHelpCmdCacheLifetime = TimeSpan.FromDays(60);

    private static readonly string GetHelpCmdRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvision", "GetHelpCmd");

    private static readonly string[] UninstallRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public IReadOnlyList<OfficeInstallation> DetectInstalledOffice()
    {
        var results = new List<OfficeInstallation>();

        foreach (var root in UninstallRoots)
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key is null) continue;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey?.GetValue("UninstallString") is not string uninstallString) continue;
                if (!uninstallString.Contains("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase)) continue;

                var displayName = subKey.GetValue("DisplayName") as string ?? "Microsoft Office";
                results.Add(new OfficeInstallation(displayName, uninstallString));
            }
        }

        return results;
    }

    public async Task<OfficeUninstallOutcome> UninstallAsync(Action<string>? onStatus = null, CancellationToken ct = default,
        bool recoverWhenNoClickToRun = false)
    {
        var installations = DetectInstalledOffice();
        if (installations.Count == 0 && !recoverWhenNoClickToRun)
        {
            onStatus?.Invoke("Office não está instalado.");
            return OfficeUninstallOutcome.NotInstalled;
        }

        onStatus?.Invoke($"Detectado {installations.Count} instalação(ões) Click-to-Run do Office.");

        if (installations.Count > 0 && await RunStandardAsync(installations, onStatus, ct)
            && DetectInstalledOffice().Count == 0 && !recoverWhenNoClickToRun)
        {
            onStatus?.Invoke("Office removido com sucesso (método padrão).");
            return OfficeUninstallOutcome.RemovedByStandardMethod;
        }

        onStatus?.Invoke("Método padrão falhou. Tentando método agressivo (GetHelpCmd)...");

        var getHelpCmdPath = await EnsureGetHelpCmdAsync(onStatus, ct);
        if (getHelpCmdPath is null)
        {
            onStatus?.Invoke("Falha ao baixar GetHelpCmd.");
            return OfficeUninstallOutcome.Failed;
        }

        if (await RunAggressiveAsync(getHelpCmdPath, onStatus, ct) && DetectInstalledOffice().Count == 0)
        {
            onStatus?.Invoke("Office removido com sucesso (método agressivo).");
            return OfficeUninstallOutcome.RemovedByAggressiveMethod;
        }

        onStatus?.Invoke("Falha na desinstalação do Office.");
        return OfficeUninstallOutcome.Failed;
    }

    private static async Task<string?> EnsureGetHelpCmdAsync(Action<string>? onStatus, CancellationToken ct)
    {
        string parent = Path.GetDirectoryName(GetHelpCmdRoot)!;
        Directory.CreateDirectory(parent);
        OfficeTemporaryDirectory.EnsurePrivate(GetHelpCmdRoot);
        var cached = FindExecutable(GetHelpCmdRoot);
        if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) < GetHelpCmdCacheLifetime
            && IsMicrosoftSigned(cached))
        {
            onStatus?.Invoke("Usando GetHelpCmd em cache.");
            return cached;
        }

        onStatus?.Invoke("Baixando a versão atual do GetHelpCmd do site oficial da Microsoft...");
        using OfficeTemporaryDirectory temporary = OfficeTemporaryDirectory.Create(parent, "gethelpcmd");
        string zipPath = Path.Combine(temporary.Path, "GetHelpCmd.zip");
        string extractFolder = Path.Combine(temporary.Path, "extract");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await http.GetAsync(GetHelpCmdDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
                !finalUri.Host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O download do GetHelpCmd não terminou no domínio oficial download.microsoft.com.");
            }

            const long maximumArchiveBytes = 512L * 1024 * 1024;
            if (response.Content.Headers.ContentLength is <= 0 or > maximumArchiveBytes)
                throw new InvalidDataException("Tamanho inválido para o pacote de recuperação do Office.");
            await using (var remoteStream = await response.Content.ReadAsStreamAsync(ct))
                await AtomicFile.WriteAsync(zipPath, (output, token) => remoteStream.CopyToAsync(output, token), ct);

            onStatus?.Invoke("Extraindo GetHelpCmd...");
            Directory.CreateDirectory(extractFolder);
            ZipFile.ExtractToDirectory(zipPath, extractFolder, overwriteFiles: true);
            string? executable = FindExecutable(extractFolder);
            if (executable is null || !IsMicrosoftSigned(executable))
                throw new InvalidDataException("A ferramenta de recuperação não passou na validação Authenticode da Microsoft.");

            OfficeTemporaryDirectory.Delete(GetHelpCmdRoot);
            Directory.Move(extractFolder, GetHelpCmdRoot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            onStatus?.Invoke("Não foi possível validar ou preparar a ferramenta oficial de recuperação do Office.");
            return null;
        }

        return FindExecutable(GetHelpCmdRoot);
    }

    private static bool IsMicrosoftSigned(string path)
    {
        try { MicrosoftSignedToolVerifier.Verify(path); return true; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { return false; }
    }

    private static string? FindExecutable(string root)
    {
        if (!Directory.Exists(root)) return null;

        return Directory
            .EnumerateFiles(root, "GetHelpCmd.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
    }

    private static async Task<bool> RunStandardAsync(IEnumerable<OfficeInstallation> installations, Action<string>? onStatus, CancellationToken ct)
    {
        foreach (var install in installations)
        {
            onStatus?.Invoke($"Desinstalando {install.DisplayName} (método padrão com elevação)...");

            var (exePath, arguments) = SplitCommandLine(install.UninstallString);
            if (exePath is null || !File.Exists(exePath))
            {
                onStatus?.Invoke($"Caminho de desinstalação inválido: {install.UninstallString}");
                return false;
            }

            if (!IsMicrosoftSigned(exePath))
            {
                onStatus?.Invoke("A ferramenta registrada para desinstalação não possui assinatura Microsoft válida.");
                return false;
            }

            // Usa ElevatedProcessRunner para executar com privilégios administrativos
            var result = await ElevatedProcessRunner.RunElevatedAsync(
                exePath,
                WindowsArgumentParser.Parse($"{arguments} DisplayLevel=False"), ct, ElevationCategory.Uninstall);

            if (!result.Success)
            {
                onStatus?.Invoke($"Falha na desinstalação padrão (código {result.ExitCode}): {result.Output}");
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> RunAggressiveAsync(string getHelpCmdPath, Action<string>? onStatus, CancellationToken ct)
    {
        if (!File.Exists(getHelpCmdPath))
        {
            onStatus?.Invoke("GetHelpCmd.exe não encontrado.");
            return false;
        }

        onStatus?.Invoke("Executando desinstalação agressiva (OfficeScrubScenario) com elevação...");

        // Usa ElevatedProcessRunner para executar GetHelpCmd com privilégios administrativos
        var result = await ElevatedProcessRunner.RunElevatedAsync(
            getHelpCmdPath,
            WindowsArgumentParser.Parse("-S OfficeScrubScenario -AcceptEula -OfficeVersion All"),
            ct, ElevationCategory.Uninstall);

        bool success = result.Success;

        onStatus?.Invoke(success
            ? "Desinstalação agressiva concluída."
            : $"Desinstalação agressiva falhou (código {result.ExitCode}): {result.Output}");

        return success;
    }

    private static (string? ExePath, string Arguments) SplitCommandLine(string commandLine)
    {
        if (!commandLine.StartsWith('"')) return (null, string.Empty);

        var closingQuote = commandLine.IndexOf('"', 1);
        if (closingQuote < 0) return (null, string.Empty);

        var exePath = commandLine[1..closingQuote];
        var arguments = commandLine[(closingQuote + 1)..].Trim();
        return (exePath, arguments);
    }
}
