using WinProvision.Core.Services;
using WinProvision.Providers.WinGet.Cli.Contracts;
using WinProvision.Providers.WinGet.Cli.Parsing;

namespace WinProvision.PackageManagement;

/// <summary>Core adapter from provider search DTOs to the existing package domain model.</summary>
internal static class WinGetCliSearchService
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(2);

    internal static async Task<IReadOnlyList<WinGetService.WinGetPackageMatch>> SearchAsync(
        string query, CancellationToken cancellationToken, WingetExecutor executor, string? source = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string> { "search", "--query", query, "--accept-source-agreements", "--disable-interactivity" };
        if (!string.IsNullOrWhiteSpace(source)) args.AddRange(["--source", source]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);
        WingetExecutionResult result = await executor.ExecuteCliAsync(args, null, timeout.Token).ConfigureAwait(false);
        if (unchecked((uint)result.ExitCode) == 0x8A150014) return [];
        if (!result.Success)
        {
            PackageDiagnostics.Write($"WinGet search failed: {result.StandardError}");
            throw new InvalidOperationException($"A busca pelo WinGet CLI falhou (0x{result.ExitCode:X8}).");
        }
        return ParseOutput(result.StandardOutput, source);
    }

    internal static IReadOnlyList<WinGetService.WinGetPackageMatch> ParseOutput(string output, string? source = null) =>
        WinGetCliTableParser.ParseSearch(output, source)
            .Select(package => new WinGetService.WinGetPackageMatch(package.Id, package.Name,
                package.Version, package.Match, package.Source)).ToArray();
}
