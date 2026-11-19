using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Providers.WinGet.Cli.Parsing;

namespace WinProvision.Store.Services;

public sealed partial class WingetPackageDetailsService
{
    private readonly WingetExecutor _executor;
    public WingetPackageDetailsService(WingetExecutor? executor = null) => _executor = executor ?? new WingetExecutor();

    public async Task<string[]> GetVersionsAsync(AppEntry app, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WingetExecutionResult result = await _executor.ExecuteCliAsync((string[])["show", "--id", app.Id, "--exact", "--versions", "--source", app.Source,
            "--accept-source-agreements", "--disable-interactivity"], null, cancellationToken).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StandardError)
            ? $"O WinGet falhou ao consultar versões (código {result.ExitCode})." : result.StandardError.Trim());
        return ParseVersions(result.StandardOutput);
    }

    internal static string[] ParseVersions(string output)
        => WinGetCliTableParser.ParseVersions(output);

    public Task<ExtendedPackageInfo> GetAsync(AppEntry app, CancellationToken cancellationToken) => GetAsync(app, null, cancellationToken);

    public async Task<ExtendedPackageInfo> GetAsync(AppEntry app, string? version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var arguments = new List<string> { "show", "--id", app.Id, "--exact", "--source", app.Source };
        if (!string.IsNullOrWhiteSpace(version)) { arguments.Add("--version"); arguments.Add(version); }
        arguments.AddRange((string[])["--accept-source-agreements", "--disable-interactivity"]);
        WingetExecutionResult result = await _executor.ExecuteCliAsync(arguments, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"O WinGet falhou ao consultar os detalhes do pacote (código {result.ExitCode})."
                    : result.StandardError.Trim());
        }

        return FromCliDetails(WinGetCliTableParser.ParsePackageDetails(result.StandardOutput));
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

    private static ExtendedPackageInfo FromCliDetails(WinProvision.Providers.WinGet.Cli.Contracts.WinGetCliPackageDetails parsed)
    {
        var info = new ExtendedPackageInfo
        {
            Version = parsed.Version, Publisher = parsed.Publisher, Author = parsed.Author, Description = parsed.Description,
            Homepage = parsed.Homepage, License = parsed.License, LicenseUrl = parsed.LicenseUrl,
            ReleaseDate = parsed.ReleaseDate, InstallerType = parsed.InstallerType, InstallerUrl = parsed.InstallerUrl,
            Sha256 = parsed.Sha256, Dependencies = parsed.Dependencies, ReleaseNotes = parsed.ReleaseNotes,
            ReleaseNotesUrl = parsed.ReleaseNotesUrl
        };
        info.Tags.AddRange(parsed.Tags);
        return info;
    }
}
