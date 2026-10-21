using System.Diagnostics;
using System.Text;
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
        var result = await RunAsync(["source", "add", "--name", name.Trim(), "--arg", parsed.AbsoluteUri, "--accept-source-agreements", "--disable-interactivity"], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
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
}
