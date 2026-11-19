namespace WinProvision.WinGet.Contracts;

/// <summary>
/// Shared selection envelope for WinGet metadata preflight and execution. The package manager does not
/// return an installer identity, so VariantConfirmed is always false.
/// </summary>
public sealed class WinGetVariantSelectionOptions
{
    private readonly HashSet<string> _unfixedFields = new(StringComparer.Ordinal);

    private WinGetVariantSelectionOptions(string? version, string? requestedScope, string? scope,
        string? architecture, string? installerType, string? locale, string? installedScope, string? installedInstallerType)
    {
        Version = version;
        RequestedScope = NormalizeScope(requestedScope);
        Scope = NormalizeScope(scope);
        Architecture = architecture;
        InstallerType = installerType;
        Locale = locale;
        InstalledScope = NormalizeScope(installedScope);
        InstalledInstallerType = installedInstallerType;
    }

    public string? Version { get; set; }
    public string? RequestedScope { get; }
    public string? Scope { get; set; }
    public string? Architecture { get; set; }
    public string? InstallerType { get; set; }
    public string? Locale { get; }
    public string? InstalledScope { get; }
    public string? InstalledInstallerType { get; }
    public bool VariantConfirmed => false;
    public bool VariantExamined { get; private set; }
    public bool HasRequestedInstallScopeMismatch => VariantExamined && InstalledScope is null
        && RequestedScope is { } requested && Scope is { } reported
        && !requested.Equals(reported, StringComparison.OrdinalIgnoreCase);

    public static WinGetVariantSelectionOptions Create(string? version, string? requestedScope,
        string? architecture, string? installerType, string? locale,
        string? installedScope = null, string? installedInstallerType = null) =>
        new(version, requestedScope, NormalizeScope(installedScope) ?? NormalizeScope(requestedScope),
            architecture, installerType ?? installedInstallerType, locale, installedScope, installedInstallerType);

    public IReadOnlyList<string> FixedFields => new[] { "version", "scope", "architecture", "installerType", "locale" }
        .Where(name => !_unfixedFields.Contains(name) && name switch
        {
            "version" => !string.IsNullOrWhiteSpace(Version),
            "scope" => !string.IsNullOrWhiteSpace(Scope),
            "architecture" => !string.IsNullOrWhiteSpace(Architecture),
            "installerType" => !string.IsNullOrWhiteSpace(InstallerType),
            "locale" => !string.IsNullOrWhiteSpace(Locale),
            _ => false
        }).ToArray();

    public IReadOnlyList<string> UnfixedFields => new[] { "installerId", "hash", "locale" }
        .Where(name => name is not "locale" || string.IsNullOrWhiteSpace(Locale))
        .Concat(_unfixedFields).Distinct(StringComparer.Ordinal).ToArray();

    public void MarkUnfixed(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (string name in fields)
            if (!string.IsNullOrWhiteSpace(name)) _unfixedFields.Add(name);
    }

    public void MarkFixed(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (string name in fields) _unfixedFields.Remove(name);
    }

    public void MarkExamined() => VariantExamined = true;

    private static string? NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    { "user" => "user", "machine" or "system" => "machine", _ => null };
}
