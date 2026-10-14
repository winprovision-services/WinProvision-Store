using System.Diagnostics;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed partial class WingetPackageDetailsService
{
    public async Task<ExtendedPackageInfo> GetAsync(AppEntry app, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = WingetLocator.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[]
        {
            "show", "--id", app.Id, "--exact", "--source", app.Source,
            "--accept-source-agreements", "--disable-interactivity",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                WinProvision.Core.Services.CoreLog.Warning(exception, "WingetPackageDetailsService.StopProcess");
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string error = await errorTask.ConfigureAwait(false);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"O WinGet falhou ao consultar os detalhes do pacote (código {process.ExitCode})."
                    : error.Trim());
        }

        return ParseWingetShowOutput(await outputTask.ConfigureAwait(false));
    }
    public sealed partial class ExtendedPackageInfo
    {
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string? Homepage { get; set; }
        public string? License { get; set; }
        public string? LicenseUrl { get; set; }
        public string? ReleaseDate { get; set; }
        public string? InstallerType { get; set; }
        public string? InstallerUrl { get; set; }
        public string? Sha256 { get; set; }
        public string? Dependencies { get; set; }
        public string? ReleaseNotes { get; set; }
        public string? ReleaseNotesUrl { get; set; }
        public List<string> Tags { get; } = [];
    }

    private static ExtendedPackageInfo ParseWingetShowOutput(string output)
    {
        var info = new ExtendedPackageInfo();
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string currentSection = "";

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Seções pai (ex.: Marcas:, Instaladora:, Documentação:)
            if (line.EndsWith(':') && !line.Contains("://", StringComparison.OrdinalIgnoreCase))
            {
                currentSection = line.TrimEnd(':').Trim();
                continue;
            }

            int colonIdx = line.IndexOf(':');
            if (colonIdx > 0 && !line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                string key = line[..colonIdx].Trim();
                string val = line[(colonIdx + 1)..].Trim();

                if (key.Equals("Versão", StringComparison.OrdinalIgnoreCase) || key.Equals("Version", StringComparison.OrdinalIgnoreCase))
                    info.Version = val;
                else if (key.Equals("Fornecedor", StringComparison.OrdinalIgnoreCase) || key.Equals("Publisher", StringComparison.OrdinalIgnoreCase))
                    info.Publisher = val;
                else if (key.Equals("Autor", StringComparison.OrdinalIgnoreCase) || key.Equals("Author", StringComparison.OrdinalIgnoreCase))
                    info.Author = val;
                else if (key.Equals("Descrição", StringComparison.OrdinalIgnoreCase) || key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    info.Description = val;
                else if (key.Equals("Página inicial", StringComparison.OrdinalIgnoreCase) || key.Equals("Homepage", StringComparison.OrdinalIgnoreCase))
                    info.Homepage = val;
                else if (key.Equals("Licença", StringComparison.OrdinalIgnoreCase) || key.Equals("License", StringComparison.OrdinalIgnoreCase))
                    info.License = val;
                else if (key.Equals("URL da licença", StringComparison.OrdinalIgnoreCase) || key.Equals("License Url", StringComparison.OrdinalIgnoreCase))
                    info.LicenseUrl = val;
                else if (key.Equals("Tipo de instalador", StringComparison.OrdinalIgnoreCase) || key.Equals("Installer Type", StringComparison.OrdinalIgnoreCase))
                    info.InstallerType = val;
                else if (key.Contains("URL do instalador", StringComparison.OrdinalIgnoreCase) || key.Contains("Installer Url", StringComparison.OrdinalIgnoreCase))
                    info.InstallerUrl = val;
                else if (key.Contains("SHA256", StringComparison.OrdinalIgnoreCase))
                    info.Sha256 = val;
                else if (key.Contains("Lançamento", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Date", StringComparison.OrdinalIgnoreCase))
                    info.ReleaseDate = val;
                else if (key.Contains("Dependência", StringComparison.OrdinalIgnoreCase) || key.Contains("Dependencies", StringComparison.OrdinalIgnoreCase))
                    info.Dependencies = val;
                else if (key.Contains("Notas de lançamento", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Notes", StringComparison.OrdinalIgnoreCase))
                    info.ReleaseNotes = val;
                else if (key.Contains("URL das notas", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Notes Url", StringComparison.OrdinalIgnoreCase))
                    info.ReleaseNotesUrl = val;
            }
            else if (currentSection.Equals("Marcas", StringComparison.OrdinalIgnoreCase) || currentSection.Equals("Tags", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    info.Tags.Add(line);
            }
        }

        return info;
    }
}
