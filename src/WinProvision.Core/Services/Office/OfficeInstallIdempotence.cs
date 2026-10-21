using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Services.Office;

/// <summary>Decide se todos os produtos pedidos já constam na instalação Click-to-Run.</summary>
public static class OfficeInstallIdempotence
{
    public static bool IsAlreadyInstalled(OfficeInstallRequest request, IReadOnlyList<OfficeInstalledProduct> installed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installed);
        var requested = new[] { request.Plan }.Concat(request.AdditionalProducts ?? []).ToArray();
        return requested.Length > 0 && requested.All(plan => installed.Any(product =>
            product.ProductId.Equals(plan.ProductId, StringComparison.OrdinalIgnoreCase)));
    }
}
