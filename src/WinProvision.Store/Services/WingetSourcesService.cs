using System.Diagnostics;
using System.Text;
using WinProvision.PackageManagement;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record WingetSource(string Name, string Argument);

public sealed class WingetSourcesService
{
    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken token = default)
    {
        var result = await RunAsync(["source", "list", "--disable-interactivity"], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("Source", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task AddAsync(string name, string uri, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo))
            throw new ArgumentException("Informe um nome e uma URL HTTPS válida para a fonte.");
        string normalizedName = name.Trim();
        Exception? lastFailure = null;
        for (int index = 0; index < WingetSourceTypeRetry.Types.Count; index++)
        {
            var result = await RunAsync(["source", "add", "--name", normalizedName, "--arg", parsed.AbsoluteUri,
                "--accept-source-agreements", "--disable-interactivity", "--type", WingetSourceTypeRetry.Types[index]], token);
            if (result.ExitCode == 0) return;

            lastFailure = new InvalidOperationException(FormatError(result));
            IReadOnlyList<string> sources;
            try
            {
                sources = await ListAsync(token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception verificationFailure)
            {
                throw new InvalidOperationException(
                    $"A tentativa de adicionar a fonte falhou e não foi possível confirmar se ela foi criada; nenhuma nova tentativa foi feita. {lastFailure.Message}",
                    new AggregateException(lastFailure, verificationFailure));
            }

            // A falha do processo pode ocorrer depois de o gerenciador de pacotes registrar a fonte.
            // Só tentamos o formato alternativo após confirmar que a fonte não existe.
            if (WingetSourceTypeRetry.IsListed(sources, normalizedName)) return;
        }

        throw lastFailure ?? new InvalidOperationException("Não foi possível adicionar a fonte WinGet.");
    }

    public async Task RemoveAsync(string name, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Informe o nome da fonte.");
        if (name.Trim().Equals("winget", StringComparison.OrdinalIgnoreCase)
            || name.Trim().Equals("msstore", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("As fontes padrão winget e msstore não podem ser removidas nesta página.");
        var result = await RunAsync(["source", "remove", "--name", name.Trim(), "--disable-interactivity"], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, CancellationToken token)
    {
        var start = new ProcessStartInfo { FileName = WingetLocator.ExecutablePath, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Não foi possível iniciar o WinGet.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(token), error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return (process.ExitCode, await output, await error);
    }

    private static string FormatError((int ExitCode, string Output, string Error) result)
    {
        string detail = string.Join(Environment.NewLine,
            new[] { result.Error.Trim(), result.Output.Trim() }.Where(value => value.Length > 0));
        return detail.Length == 0
            ? $"O WinGet falhou ao adicionar a fonte (código {result.ExitCode})."
            : detail;
    }
}
