using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

/// <summary>
/// Opções de serialização/desserialização de JSON usadas por TODOS os serviços
/// do app (profiles, manifestos de provisionamento, backup local/Gist, configuração
/// da conta GitHub etc.). Ter um ponto centralizado garante:
///
/// <list type="bullet">
/// <item><description>O app SEMPRE lê o que escreveu — nenhum serviço usa opções
///   diferentes sem sabermos (era uma das causas do Bootstrap puxar arquivos
///   antigos: inconsistências entre ler com uma policy e gravar com outra).</description></item>
/// <item><description>Round-trip idempotente: serializar → desserializar → serializar
///   de novo produz a mesma estrutura (campos em camelCase, indentações consistentes,
///   enums como string, datas em ISO-8601).</description></item>
/// <item><description>Leitura tolerante (allow trailing commas, ignora comments,
///   case-insensitive match nos nomes de propriedade) — evita rejeitar perfis
///   salvos por versões ligeiramente mais antigas do app, ou editados manualmente.</description></item>
/// <item><description>Enums serializados como <b>string</b> em vez de int: no .json
///   salvo por app v1 aparecia <c>"theme": 2</c>; se alguém reordenar os membros
///   de <c>SystemThemeMode</c>, tudo fica errado. Como string (<c>"theme": "Escuro"</c>)
///   esse risco some. Desserialização ainda aceita tanto o int antigo (compatibilidade
///   retroativa) quanto a string nova.</description></item>
/// <item><description>Perfis exportados usam <see cref="Profile"/>, que mantém todos os
///   campos do contrato e escreve <c>null</c> explicitamente. Isso torna perfis completos
///   e parciais estruturalmente idênticos; caches internos continuam usando <see cref="Default"/>
///   e podem omitir valores nulos.</description></item>
/// </list>
///
/// Leitores e escritores de perfis, backups e caches devem compartilhar estas políticas.
/// O catálogo produzido pelo Indexer externo segue o mesmo contrato de leitura.
/// </summary>
public static class WinProvisionJsonOptions
{
    /// <summary>Opção usada para gravar caches e configurações internas.</summary>
    public static readonly JsonSerializerOptions Default = Build(indented: true);

    /// <summary>
    /// Contrato usado por perfis exportados e pelo visualizador JSON.
    /// Valores nulos são omitidos para manter perfis parciais limpos e legíveis;
    /// a ausência de um campo continua significando "não alterar".
    /// </summary>
    public static readonly JsonSerializerOptions Profile = Build(indented: true, omitNullValues: true);

    /// <summary>Variante compacta (sem indentação) — útil para payloads de API pequenos
    /// (ex.: metadados internos). Não substitui a Default em disco; é só um atalho.</summary>
    public static readonly JsonSerializerOptions Compact = Build(indented: false);

    private static JsonSerializerOptions Build(bool indented, bool omitNullValues = true)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = omitNullValues
                ? JsonIgnoreCondition.WhenWritingNull
                : JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        // 1. Enum como string, mas aceitando tanto a string quanto o número antigo.
        //    Ordem importa: o JsonStringEnumConverter é colocado por último, então
        //    os conversores built-in pegam primeiro no número se for numérico.
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.InstallProgressPhase>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.OperationKind>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.OperationState>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.WingetMethod>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.PackageInstallMethod>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Office.OfficeEditionCategory>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Office.OfficeDisplayLevel>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Office.OfficeUninstallOutcome>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Provisioning.SystemThemeMode>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Provisioning.AccentColorMode>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Provisioning.TaskbarAlignmentMode>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Provisioning.TaskbarSearchBoxMode>(namingPolicy: null, allowIntegerValues: true));
        opts.Converters.Add(new JsonStringEnumConverter<WinProvision.Core.Models.Provisioning.PowerPlanMode>(namingPolicy: null, allowIntegerValues: true));

        return opts;
    }
}
