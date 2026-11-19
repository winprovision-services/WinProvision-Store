using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using WinProvision.Providers.WinGet.Cli.Parsing;

namespace WinProvision.Store.Services;

public sealed record WingetSource(string Name, string Argument);

public sealed class WingetSourcesService
{
    private readonly WingetExecutor _executor;
    public WingetSourcesService(WingetExecutor? executor = null) => _executor = executor ?? new WingetExecutor();

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken token = default)
    {
        var result = await RunAsync(["source", "list", "--disable-interactivity"], token);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
        return ParseListOutput(result.Output);
    }

    internal static IReadOnlyList<string> ParseListOutput(string output) => WinGetCliTableParser.ParseSourceNames(output);

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

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        WingetExecutionResult result = await _executor.ExecuteCliAsync(args, null, timeout.Token).ConfigureAwait(false);
        return (result.ExitCode, result.StandardOutput, result.StandardError);
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
