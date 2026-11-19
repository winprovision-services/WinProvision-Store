using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

public sealed class WingetPackageDetailsService
{
    public Task<ExtendedPackageInfo> GetAsync(AppEntry app, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O teste usa diretamente o mapeamento síncrono do adaptador.");

    public Task<string[]> GetVersionsAsync(AppEntry app, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Este teste não consulta versões do WinGet.");

    public sealed class ExtendedPackageInfo
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
}
