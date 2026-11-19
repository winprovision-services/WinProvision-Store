namespace WinProvision.Core.Models;

/// <summary>Resultado em tempo de execução de uma verificação de gerenciador.</summary>
public sealed record PackageManagerProbeState(
    bool IsChecking,
    bool HasChecked,
    bool IsFound,
    bool IsReady,
    bool HasError = false,
    string? Version = null);

public sealed record PackageManagerRowStatus(string Status, string Version, bool IsEffectivelyEnabled);

/// <summary>Combina a preferência do usuário e o resultado do probe; não lê dados estáticos da interface.</summary>
public static class PackageManagerStatusResolver
{
    public static PackageManagerRowStatus Resolve(
        PackageManagerDescriptor definition,
        bool enabled,
        PackageManagerProbeState? probe,
        bool parentEnabled = true,
        PackageManagerProbeState? parentProbe = null)
    {
        if (!enabled || (definition.ParentId is not null && !parentEnabled))
            return new PackageManagerRowStatus("disabled", NormalizeVersion(probe?.Version), false);

        if (string.Equals(definition.Check?.Kind, "inherit", StringComparison.OrdinalIgnoreCase))
        {
            if (parentProbe is null || parentProbe.IsChecking || !parentProbe.HasChecked)
                return new PackageManagerRowStatus("checking", NormalizeVersion(parentProbe?.Version), false);
            if (parentProbe.HasError)
                return new PackageManagerRowStatus("error", NormalizeVersion(parentProbe.Version), false);
            if (!parentProbe.IsFound)
                return new PackageManagerRowStatus("notFound", NormalizeVersion(parentProbe.Version), false);
            if (!parentProbe.IsReady)
                return new PackageManagerRowStatus("error", NormalizeVersion(parentProbe.Version), false);
            return new PackageManagerRowStatus("ready", NormalizeVersion(parentProbe.Version), true);
        }

        if (string.Equals(definition.Check?.Kind, "none", StringComparison.OrdinalIgnoreCase) && probe is null)
            return new PackageManagerRowStatus("notChecked", string.Empty, enabled && parentEnabled);

        if (probe is null || probe.IsChecking || !probe.HasChecked)
            return new PackageManagerRowStatus("checking", NormalizeVersion(probe?.Version), false);
        if (probe.HasError)
            return new PackageManagerRowStatus("error", NormalizeVersion(probe.Version), false);
        if (!probe.IsFound)
            return new PackageManagerRowStatus("notFound", NormalizeVersion(probe.Version), false);
        return probe.IsReady
            ? new PackageManagerRowStatus("ready", NormalizeVersion(probe.Version), true)
            : new PackageManagerRowStatus("error", NormalizeVersion(probe.Version), false);
    }

    private static string NormalizeVersion(string? version) => string.IsNullOrWhiteSpace(version) || version == "—"
        ? string.Empty : version.Trim();
}
