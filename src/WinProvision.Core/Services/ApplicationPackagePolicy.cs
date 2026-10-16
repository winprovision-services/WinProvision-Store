namespace WinProvision.Core.Services;

public static class ApplicationPackagePolicy
{
    public static bool IsSelf(string? id, string? name = null) =>
        string.Equals(id, "WinProvision.Store", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name?.Trim(), "WinProvision Store", StringComparison.OrdinalIgnoreCase);

    public const string SelfManagementMessage = "O WinProvision Store não pode instalar, atualizar ou remover a si mesmo enquanto está aberto. Use o instalador externo.";
}
