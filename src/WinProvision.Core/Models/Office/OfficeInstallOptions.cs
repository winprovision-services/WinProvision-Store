using System.Text.Json.Serialization;

namespace WinProvision.Core.Models.Office;

/// <summary>
/// Snapshot serializável de tudo que é preciso para refazer um <see cref="OfficeInstallRequest"/>
/// mais tarde — anexado a um <see cref="AppEntry"/> (via <see cref="AppEntry.Office"/>) quando o
/// item representa um plano de Office em vez de um pacote do gerenciador de pacotes comum.
///
/// Não referencia <see cref="OfficePlan"/> diretamente (o record tem membros não
/// triviais de serializar/round-tripar) — guarda só o <see cref="ProductId"/> estável,
/// resolvido de volta via <see cref="OfficePlanCatalog.ByProductId"/> no momento de
/// instalar. Isso é o que permite o mesmo AppEntry (e o mesmo .json de perfil) valer
/// tanto pra um aplicativo comum do gerenciador de pacotes (Office == null) quanto pra um plano de Office
/// (Office preenchido), sem depender do catálogo remoto de apps pra reconstruir nada.
/// </summary>
public class OfficeInstallOptions
{
    [JsonPropertyName("downloadBeforeInstall")]
    public bool DownloadBeforeInstall { get; set; }

    /// <summary>Opcional; perfis antigos seguem usando o download oficial do ODT.</summary>
    [JsonPropertyName("useViperDownload")]
    public bool UseViperDownload { get; set; }
    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("architecture")]
    public int Architecture { get; set; } = 64;

    [JsonPropertyName("languageId")]
    public string LanguageId { get; set; } = "pt-br";

    [JsonPropertyName("additionalLanguageIds")]
    public List<string> AdditionalLanguageIds { get; set; } = new();

    [JsonPropertyName("excludedApps")]
    public List<string> ExcludedApps { get; set; } = new();

    [JsonPropertyName("silent")]
    public bool Silent { get; set; } = true;

    /// <summary>Só relevante pra planos de assinatura (Corporate365) — null = usa o canal padrão do plano.</summary>
    [JsonPropertyName("channelOverride")]
    public string? ChannelOverride { get; set; }

    [JsonPropertyName("autoUpdatesEnabled")]
    public bool AutoUpdatesEnabled { get; set; } = true;

    /// <summary>IDs ODT de complementos Visio/Project compatíveis com o produto principal.</summary>
    [JsonPropertyName("additionalProductIds")]
    public List<string> AdditionalProductIds { get; set; } = new();
}
