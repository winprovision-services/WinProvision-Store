namespace WinProvision.Core.Models;

/// <summary>Preferências de instalação reutilizáveis associadas a um pacote WinGet.</summary>
public sealed record PackageInstallOptions(
    bool Interactive = false,
    string? Scope = null,
    string? Architecture = null,
    bool IgnoreSecurityHash = false,
    string? InstallLocation = null,
    bool RequiresElevation = false,
    string? Version = null,
    string? CustomArguments = null,
    bool AutoUpdate = false,
    int? SkipMinorVersionSegments = null,
    string? PreInstallScript = null,
    string? PostInstallScript = null,
    bool RunInstallHooks = false)
{
    public bool RequiresWingetCli => Interactive
        || RequiresElevation
        || !string.IsNullOrWhiteSpace(InstallLocation)
        || !string.IsNullOrWhiteSpace(Version)
        || !string.IsNullOrWhiteSpace(CustomArguments)
        || !string.IsNullOrWhiteSpace(Scope)
        || !string.IsNullOrWhiteSpace(Architecture);
}
