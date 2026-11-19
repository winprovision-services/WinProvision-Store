namespace WinProvision.Core.Models;

/// <summary>Preferências de instalação reutilizáveis associadas a um pacote do gerenciador de pacotes.</summary>
public sealed record PackageInstallOptions(
    bool Interactive = false,
    string? Scope = null,
    string? Architecture = null,
    bool IgnoreSecurityHash = false,
    string? InstallLocation = null,
    bool RequiresElevation = false,
    string? Version = null,
    string? CustomArguments = null,
    string? OverrideArguments = null,
    bool AutoUpdate = false,
    int? SkipMinorVersionSegments = null,
    string? PreInstallScript = null,
    string? PostInstallScript = null,
    bool RunInstallHooks = false,
    string? UpdateArguments = null,
    string? UninstallArguments = null,
    string? PreUpdateScript = null,
    string? PostUpdateScript = null,
    bool RunUpdateHooks = false,
    bool AbortUpdateOnPreScriptFailure = true,
    string? PreUninstallScript = null,
    string? PostUninstallScript = null,
    bool RunUninstallHooks = false,
    bool AbortUninstallOnPreScriptFailure = true,
    bool AbortInstallOnPreScriptFailure = true,
    string[]? ProcessesToClose = null,
    bool ForceCloseProcesses = false,
    bool OverridesGlobalOptions = false,
    bool UninstallPrevious = false,
    bool AllowPrerelease = false,
    string? InstallerType = null,
    string? Locale = null)
{
    public bool RequiresWingetCli => Interactive
        || RequiresElevation
        || !string.IsNullOrWhiteSpace(InstallLocation)
        || !string.IsNullOrWhiteSpace(Version)
        || !string.IsNullOrWhiteSpace(CustomArguments)
        || !string.IsNullOrWhiteSpace(OverrideArguments)
        || !string.IsNullOrWhiteSpace(UpdateArguments)
        || !string.IsNullOrWhiteSpace(UninstallArguments)
        || UninstallPrevious
        || !string.IsNullOrWhiteSpace(Scope)
        || !string.IsNullOrWhiteSpace(Architecture);
}
