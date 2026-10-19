using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Services.Office;

public static class OfficeProfileConfiguration
{
    public static OfficeInstallRequest Build(OfficeInstallOptions options)
    {
        OfficePlan plan = OfficePlanCatalog.ByProductId(options.ProductId)
            ?? throw new InvalidDataException($"Plano Office desconhecido: {options.ProductId}.");
        var additional = (options.AdditionalProductIds ?? []).Select(id => OfficePlanCatalog.ByProductId(id)
            ?? throw new InvalidDataException($"Produto Office adicional desconhecido: {id}.")).ToArray();
        var request = new OfficeInstallRequest(plan, options.Architecture, options.LanguageId, options.ExcludedApps ?? [],
            DisplayNone: options.Silent, AdditionalLanguageIds: options.AdditionalLanguageIds ?? [],
            DisplayLevel: options.Silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
            ChannelOverride: options.ChannelOverride, AutoUpdatesEnabled: options.AutoUpdatesEnabled,
            AdditionalProducts: additional, DownloadBeforeInstall: options.DownloadBeforeInstall,
            UseViperDownload: options.UseViperDownload);
        string? error = OfficeConfigXmlBuilder.ValidateRequest(request);
        if (error is not null) throw new InvalidDataException(error);
        return request;
    }
}
