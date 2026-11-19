using System;
using System.Collections.Generic;
using System.Linq;

namespace WinProvision.Core.Models.Office;

public enum OfficeEditionCategory
{
    Corporate365,
    Ltsc,
    Personal,
    VisioProject
}

/// <summary>
/// Um plano instalável de Office. ProductId e Channel seguem exatamente os valores
/// documentados em documentação do instalador — nunca inventados.
/// </summary>
#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public partial record OfficePlan(
    string DisplayName,
    OfficeEditionCategory Category,
    string ProductId,
    string? Channel,
    bool IsVolumeLicensed)
{
    /// <summary>
    /// Produtos Retail/assinatura aceitam canais de manutenção plataforma do sistema 365;
    /// Office LTSC mantém o canal PerpetualVL obrigatório.
    /// </summary>
    public bool? SelectableChannelOverride { get; init; }
    public bool SupportsSelectableChannel => SelectableChannelOverride ?? (Category != OfficeEditionCategory.Ltsc &&
        (!IsVolumeLicensed || (Category == OfficeEditionCategory.VisioProject && ProductId.Contains("2024", StringComparison.OrdinalIgnoreCase))));

    /// <summary>Aplicativos que este Product ID realmente instala; adicionais ficam fora da suíte principal.</summary>
    public IReadOnlyList<string> IncludedApplicationIds => OfficeProductApplicationCatalog.Get(ProductId);
    public string Group { get; init; } = string.Empty;
    public int Order { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<OfficeCatalogAddOn> AddOns { get; init; } = Array.Empty<OfficeCatalogAddOn>();
    /// <summary>Aplicativos do SKU cuja instalação depende do plano de serviço atribuído.</summary>
    public IReadOnlyList<string> ConditionalApplicationIds => OfficeProductApplicationCatalog.GetConditional(ProductId);

    public string? Description { get; init; }
    public string? IconUrl { get; init; }
    public string? BannerUrl { get; init; }
    public IReadOnlyList<string> Screenshots { get; init; } = Array.Empty<string>();

    public static readonly OfficePlan Microsoft365Enterprise =
        new("Microsoft 365 Apps for enterprise", OfficeEditionCategory.Corporate365, "O365ProPlusRetail", "Current", false);

    public static readonly OfficePlan Microsoft365Business =
        new("Microsoft 365 Apps for business", OfficeEditionCategory.Corporate365, "O365BusinessRetail", "Current", false)
        {
            Description = "Microsoft 365 Apps para empresas. A oferta da Store não substitui o Product ID exigido pelo ODT.",
            IconUrl = OfficeAppCatalog.Microsoft365IconUrl,
        };

    public static readonly OfficePlan LtscProPlus2024 =
        new("Office LTSC Professional Plus 2024", OfficeEditionCategory.Ltsc, "ProPlus2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan LtscProPlus2021 =
        new("Office LTSC Professional Plus 2021", OfficeEditionCategory.Ltsc, "ProPlus2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan LtscProPlus2019 =
        new("Office LTSC Professional Plus 2019", OfficeEditionCategory.Ltsc, "ProPlus2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan LtscStandard2024 =
        new("Office LTSC Standard 2024", OfficeEditionCategory.Ltsc, "Standard2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan LtscStandard2021 =
        new("Office LTSC Standard 2021", OfficeEditionCategory.Ltsc, "Standard2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan LtscStandard2019 =
        new("Office LTSC Standard 2019", OfficeEditionCategory.Ltsc, "Standard2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan Family =
        new("Microsoft 365 Family", OfficeEditionCategory.Personal, "O365HomePremRetail", "Current", false)
        {
            Description = "Configuração Microsoft 365 para uso pessoal e familiar. Personal, Family e Premium usam este Product ID do ODT; a assinatura e seus benefícios são vinculados à conta Microsoft.",
            IconUrl = "pack://application:,,,/Assets/Office/Microsoft365FamilyIcon.jpg",
            BannerUrl = "pack://application:,,,/Assets/Office/Microsoft365FamilyBanner.jpg",
        };

    public static readonly OfficePlan HomeStudent2021 =
        new("Office Home & Student 2021", OfficeEditionCategory.Personal, "HomeStudent2021Retail", "Current", false);

    public static readonly OfficePlan HomeBusiness2024 =
        new("Office Home & Business 2024", OfficeEditionCategory.Personal, "HomeBusiness2024Retail", "Current", false);

    public static readonly OfficePlan HomeBusiness2021 =
        new("Office Home & Business 2021", OfficeEditionCategory.Personal, "HomeBusiness2021Retail", "Current", false);

    public static readonly OfficePlan Home2024 =
        new("Office Home 2024", OfficeEditionCategory.Personal, "Home2024Retail", "Current", false);

    // --- Visio ---
    // IDs Retail para assinaturas plataforma do sistema 365 e licenças de varejo, conforme a lista oficial do ODT.
    public static readonly OfficePlan VisioProSubscription =
        new("Visio Professional (Microsoft 365)", OfficeEditionCategory.VisioProject, "VisioProRetail", "Current", false);

    public static readonly OfficePlan VisioStandardRetail =
        new("Visio Standard", OfficeEditionCategory.VisioProject, "VisioStdRetail", "Current", false);

    public static readonly OfficePlan VisioPro2024Retail =
        new("Visio Professional 2024 (Retail)", OfficeEditionCategory.VisioProject, "VisioPro2024Retail", "Current", false);

    public static readonly OfficePlan VisioStandard2024Retail =
        new("Visio Standard 2024 (Retail)", OfficeEditionCategory.VisioProject, "VisioStd2024Retail", "Current", false);

    public static readonly OfficePlan VisioPro2021Retail =
        new("Visio Professional 2021 (Retail)", OfficeEditionCategory.VisioProject, "VisioPro2021Retail", "Current", false);

    public static readonly OfficePlan VisioStandard2021Retail =
        new("Visio Standard 2021 (Retail)", OfficeEditionCategory.VisioProject, "VisioStd2021Retail", "Current", false);

    public static readonly OfficePlan VisioPro2019Retail =
        new("Visio Professional 2019 (Retail)", OfficeEditionCategory.VisioProject, "VisioPro2019Retail", "Current", false);

    public static readonly OfficePlan VisioStandard2019Retail =
        new("Visio Standard 2019 (Retail)", OfficeEditionCategory.VisioProject, "VisioStd2019Retail", "Current", false);

    public static readonly OfficePlan VisioPro2024 =
        new("Visio LTSC Professional 2024", OfficeEditionCategory.VisioProject, "VisioPro2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan VisioPro2021 =
        new("Visio LTSC Professional 2021", OfficeEditionCategory.VisioProject, "VisioPro2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan VisioPro2019 =
        new("Visio Professional 2019", OfficeEditionCategory.VisioProject, "VisioPro2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan VisioStd2021 =
        new("Visio LTSC Standard 2021", OfficeEditionCategory.VisioProject, "VisioStd2021Volume", "PerpetualVL2021", true);

    // --- Project ---
    public static readonly OfficePlan ProjectProSubscription =
        new("Project Professional (Microsoft 365)", OfficeEditionCategory.VisioProject, "ProjectProRetail", "Current", false);

    public static readonly OfficePlan ProjectStandardRetail =
        new("Project Standard", OfficeEditionCategory.VisioProject, "ProjectStdRetail", "Current", false);

    public static readonly OfficePlan ProjectPro2024Retail =
        new("Project Professional 2024 (Retail)", OfficeEditionCategory.VisioProject, "ProjectPro2024Retail", "Current", false);

    public static readonly OfficePlan ProjectStandard2024Retail =
        new("Project Standard 2024 (Retail)", OfficeEditionCategory.VisioProject, "ProjectStd2024Retail", "Current", false);

    public static readonly OfficePlan ProjectPro2021Retail =
        new("Project Professional 2021 (Retail)", OfficeEditionCategory.VisioProject, "ProjectPro2021Retail", "Current", false);

    public static readonly OfficePlan ProjectStandard2021Retail =
        new("Project Standard 2021 (Retail)", OfficeEditionCategory.VisioProject, "ProjectStd2021Retail", "Current", false);

    public static readonly OfficePlan ProjectPro2019Retail =
        new("Project Professional 2019 (Retail)", OfficeEditionCategory.VisioProject, "ProjectPro2019Retail", "Current", false);

    public static readonly OfficePlan ProjectStandard2019Retail =
        new("Project Standard 2019 (Retail)", OfficeEditionCategory.VisioProject, "ProjectStd2019Retail", "Current", false);

    public static readonly OfficePlan ProjectPro2024 =
        new("Project LTSC Professional 2024", OfficeEditionCategory.VisioProject, "ProjectPro2024Volume", "PerpetualVL2024", true);

    public static readonly OfficePlan ProjectPro2021 =
        new("Project LTSC Professional 2021", OfficeEditionCategory.VisioProject, "ProjectPro2021Volume", "PerpetualVL2021", true);

    public static readonly OfficePlan ProjectPro2019 =
        new("Project Professional 2019", OfficeEditionCategory.VisioProject, "ProjectPro2019Volume", "PerpetualVL2019", true);

    public static readonly OfficePlan ProjectStd2021 =
        new("Project LTSC Standard 2021", OfficeEditionCategory.VisioProject, "ProjectStd2021Volume", "PerpetualVL2021", true);
}

/// <summary>Dados de uma oferta comercial usados para abrir os detalhes do produto na Store.</summary>
public sealed record OfficeStoreOffer(string StoreProductId, string DisplayName, string OdtProductId,
    string? Description = null, string? IconUrl = null, string? BannerUrl = null,
    IReadOnlyList<string>? Screenshots = null);

public static class OfficePlanCatalog
{
    public static IReadOnlyList<OfficePlan> All => Array.AsReadOnly(OfficeCatalogRuntime.Current.Products
        .Select(product => new OfficePlan(product.Name, product.Group switch
        {
            "ltsc" => OfficeEditionCategory.Ltsc,
            "visio" or "project" => OfficeEditionCategory.VisioProject,
            "empresarial" => OfficeEditionCategory.Corporate365,
            _ => OfficeEditionCategory.Personal
        }, product.ProductId, product.Channel, product.VolumeLicensed)
        {
            Group = product.Group, Order = product.Order, IsDefault = product.IsDefault,
            Aliases = product.Aliases, AddOns = product.AddOns,
            SelectableChannelOverride = product.SupportsSelectableChannel
        }).ToArray());

    public static IEnumerable<OfficePlan> ByCategory(OfficeEditionCategory category) => All.Where(p => p.Category == category);
    public static OfficePlan? ByProductId(string productId) => All.FirstOrDefault(plan =>
        string.Equals(plan.ProductId, productId, StringComparison.OrdinalIgnoreCase)
        || plan.Aliases.Contains(productId, StringComparer.OrdinalIgnoreCase));
}

/// <summary>Aplicabilidade, complementos e metadados de apps vêm do catálogo Office versionado.</summary>
public static class OfficeProductApplicationCatalog
{
    public static IReadOnlyList<string> Get(string productId) => OfficeCatalogRuntime.Current.Products
        .FirstOrDefault(product => string.Equals(product.ProductId, productId, StringComparison.OrdinalIgnoreCase))?.Apps
        ?? Array.Empty<string>();
    public static IReadOnlyList<string> GetConditional(string productId) => OfficeCatalogRuntime.Current.Products
        .FirstOrDefault(product => string.Equals(product.ProductId, productId, StringComparison.OrdinalIgnoreCase))?.ConditionalApps
        ?? Array.Empty<string>();
}

/// <summary>Metadados de aplicativos, origem de ícones, detecção e estados declarados no catálogo Office.</summary>
public static class OfficeAppCatalog
{
    public const string IconBaseUrl = WinProvision.Core.Services.CatalogEndpoints.OfficeIconBaseUrl;
    public static string Microsoft365IconUrl => $"{IconBaseUrl}/{OfficeCatalogRuntime.Current.SourceIcon}";
    private static string Icon(OfficeCatalogApp app) => $"{IconBaseUrl}/{app.Icon}";
    public static IReadOnlyList<(string Id, string DisplayName, string IconUrl)> CoreApps => OfficeCatalogRuntime.Current.Apps
        .Where(app => app.Kind == "exclude")
        .Select(app => (app.Id, app.Name, Icon(app))).ToArray();
    public static IReadOnlyList<(string Id, string DisplayName, string IconUrl)> AdditionalProducts => OfficeCatalogRuntime.Current.Apps
        .Where(app => app.Kind == "addOn")
        .Select(app => (app.Id, app.Name, Icon(app))).ToArray();
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DetectionExecutables => OfficeCatalogRuntime.Current.Apps
        .GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.SelectMany(app => app.Executables).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DetectionPackages => OfficeCatalogRuntime.Current.Apps
        .Where(app => app.PackageNames.Count > 0)
        .ToDictionary(app => app.Id, app => app.PackageNames, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DetectionPaths => OfficeCatalogRuntime.Current.Apps
        .Where(app => app.DetectionPaths.Count > 0)
        .ToDictionary(app => app.Id, app => app.DetectionPaths, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<(string Id, string DisplayName)> AdvancedApps => OfficeCatalogRuntime.Current.Apps
        .Where(app => app.Kind == "advanced").Select(app => (app.Id, app.Name)).ToArray();
}

/// <summary>
/// Nomes aceitos pelo ODT para canais de atualização do plataforma do sistema 365.
/// Office LTSC mantém o canal PerpetualVLxxxx exigido pela edição.
/// </summary>
public static class OfficeChannelCatalog
{
    public static readonly IReadOnlyList<(string Id, string DisplayName)> SubscriptionChannels =
    [
        ("Current", "Canal Atual"),
        ("MonthlyEnterprise", "Enterprise Mensal"),
        ("SemiAnnual", "Semestral (Corrente)"),
        ("SemiAnnualPreview", "Semestral (Prévia)"),
        ("CurrentPreview", "Canal Atual (Prévia)"),
        ("BetaChannel", "Beta / Insiders"),
    ];

    // Official Click-to-Run channel IDs observed in UpdateChannel registry URLs.
    public static readonly IReadOnlyList<(string DetectionId, string ChannelId)> DetectedIds =
    [
        ("492350f6-3a01-4f97-b9c0-c7c6ddf67d60", "Current"),
        ("55336b82-a18d-4dd6-b5f6-9e5095c314a6", "MonthlyEnterprise"),
        ("7ffbc6bf-bc32-4f92-8982-f9dd17fd3114", "SemiAnnual"),
        ("b8f9b850-328d-4355-9145-c59439a0c4cf", "SemiAnnualPreview"),
        ("64256afe-f5d9-4f86-8936-8840a6a4f5be", "CurrentPreview"),
        ("5440fd1f-7ecb-4221-8110-145efaa6372f", "BetaChannel")
    ];

    public static string ResolveDetectedName(string value)
    {
        string id = Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.Segments.Last().TrimEnd('/')
            : value.Trim().TrimEnd('/');
        string channelId = DetectedIds.FirstOrDefault(item => item.DetectionId.Equals(id, StringComparison.OrdinalIgnoreCase)).ChannelId;
        if (string.IsNullOrEmpty(channelId)) channelId = id;
        return SubscriptionChannels.FirstOrDefault(item => item.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase)).DisplayName
            ?? "Canal personalizado";
    }
}

/// <summary>Nível de interface do setup.exe do ODT durante /configure (atributo Display/Level).</summary>
public enum OfficeDisplayLevel
{
    /// <summary>Level="None" — nenhuma UI, nenhuma barra de progresso do instalador nativo.</summary>
    Silent,
    /// <summary>Level="Full" — UI completa do instalador da plataforma do sistema.</summary>
    Visible,
}

public record OfficeInstallRequest(
    OfficePlan Plan,
    int Architecture,                 // 32 ou 64
    string LanguageId,                // idioma principal, ex: "pt-br"
    IReadOnlyList<string> ExcludedApps,
    bool DisplayNone = true,
    bool AcceptEula = true,
    IReadOnlyList<string>? AdditionalLanguageIds = null,
    OfficeDisplayLevel DisplayLevel = OfficeDisplayLevel.Silent,
    /// <summary>
    /// Sobrescreve o Channel padrão dos planos que aceitam seleção de canal.
    /// Office LTSC preserva o canal PerpetualVLxxxx obrigatório.
    /// </summary>
    string? ChannelOverride = null,
    /// <summary>Gera o elemento &lt;Updates Enabled="TRUE|FALSE"/&gt; do ODT, controlando a política de atualização automática do Office nesta máquina.</summary>
    bool AutoUpdatesEnabled = true,
    IReadOnlyList<OfficePlan>? AdditionalProducts = null,
    /// <summary>Origem local opcional para reutilizar conteúdo já baixado pelo ODT.</summary>
    string? SourcePath = null,
    bool DownloadBeforeInstall = false,
    /// <summary>Obtém a mídia C2R da CDN com transferências paralelas do Viper.</summary>
    bool UseViperDownload = false);

/// <summary>
/// Um produto Click-to-Run detectado no registro (ver Configuration\ProductReleaseIds),
/// já mapeado para o OfficePlan correspondente quando reconhecido pelo catálogo.
/// </summary>
public record OfficeInstalledProduct(
    string ProductId,
    OfficePlan? KnownPlan,
    string? VersionToReport,
    string? Platform,
    string? ClientCulture,
    IReadOnlyList<string> ExcludedApps,
    string? UpdateChannel = null,
    string? InstallationPath = null)
{
    public string DisplayName => KnownPlan?.DisplayName ?? ProductId;
}

/// <summary>
/// Pedido de remoção via setup.exe /configure com o elemento &lt;Remove&gt;. Use
/// <see cref="RemoveAll"/> para a tag RemoveAll (equivalente a &lt;Remove All="TRUE"/&gt;,
/// removendo todos os produtos Click-to-Run da máquina de uma vez), ou informe
/// <see cref="ProductIds"/> para remover produtos específicos previamente detectados.
/// </summary>
public record OfficeRemoveRequest(
    bool RemoveAll,
    IReadOnlyList<string>? ProductIds = null,
    OfficeDisplayLevel DisplayLevel = OfficeDisplayLevel.Silent,
    /// <summary>Também remove a edição do catálogo visual (pacote AppX) do Office, se presente.</summary>
    bool CleanStoreEdition = true,
    /// <summary>Usa o método de desinstalação agressivo (GetHelpCmd) se o método padrão falhar.</summary>
    bool UseAggressiveUninstall = true);

/// <summary>Representa uma instalação do Office detectada no sistema.</summary>
public record OfficeInstallation(string DisplayName, string UninstallString);

/// <summary>Resultado de uma operação de desinstalação do Office.</summary>
public enum OfficeUninstallOutcome
{
    /// <summary>Office não está instalado.</summary>
    NotInstalled,
    /// <summary>Removido com sucesso usando o método padrão.</summary>
    RemovedByStandardMethod,
    /// <summary>Removido com sucesso usando o método agressivo (GetHelpCmd).</summary>
    RemovedByAggressiveMethod,
    /// <summary>Falha na desinstalação.</summary>
    Failed
}
