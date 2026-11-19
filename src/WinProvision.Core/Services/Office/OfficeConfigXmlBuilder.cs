using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Monta o configuration.xml exigido pelo ODT (setup.exe /configure), seguindo o
/// schema oficial: Configuration > Add > Product > Language / ExcludeApp, mais
/// Display e Updates. Nenhum elemento aqui é inventado.
/// </summary>
public static class OfficeConfigXmlBuilder
{
    public static XDocument Build(OfficeInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        var products = new[] { request.Plan }.Concat(request.AdditionalProducts ?? []).ToArray();
        var validationError = ValidateRequest(request, products);
        if (validationError is not null)
            throw new ArgumentException(validationError, nameof(request));

        return WriteDocument(writer =>
        {
            writer.WriteStartElement("Configuration");
            writer.WriteStartElement("Add");
            writer.WriteAttributeString("OfficeClientEdition", request.Architecture.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(request.SourcePath)) writer.WriteAttributeString("SourcePath", request.SourcePath);
            if (request.DownloadBeforeInstall || !string.IsNullOrWhiteSpace(request.SourcePath)) writer.WriteAttributeString("AllowCdnFallback", "FALSE");
            string? channel = request.ChannelOverride is { Length: > 0 } overrideValue ? overrideValue : request.Plan.Channel;
            if (!string.IsNullOrWhiteSpace(channel)) writer.WriteAttributeString("Channel", channel);
            foreach (OfficePlan plan in products) WriteProduct(writer, plan, request, plan == request.Plan);
            writer.WriteEndElement();
            WriteDisplay(writer, request.DisplayLevel, request.AcceptEula);
            writer.WriteStartElement("Updates");
            writer.WriteAttributeString("Enabled", request.AutoUpdatesEnabled ? "TRUE" : "FALSE");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    /// <summary>Valida os produtos antes de gerar ou executar a configuração.</summary>
    public static string? ValidateRequest(OfficeInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        return ValidateRequest(request, new[] { request.Plan }.Concat(request.AdditionalProducts ?? []).ToArray());
    }

    private static string? ValidateRequest(OfficeInstallRequest request, IReadOnlyList<OfficePlan> products)
    {
        if (request.Architecture is not (32 or 64)) return "A arquitetura deve ser 32 ou 64 bits.";
        if (!IsSafeToken(request.LanguageId)) return "O idioma principal é inválido.";
        if (products.Count == 0 || products.Any(p => p is null || !IsSafeToken(p.ProductId))) return "Há um Product ID inválido.";
        if (products.Select(p => p.ProductId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != products.Count)
            return "O mesmo produto não pode ser adicionado mais de uma vez.";
        if (products.Any(p => OfficePlanCatalog.ByProductId(p.ProductId) is null))
            return "O Product ID não pertence ao catálogo ODT suportado. Office Home 2024 usa Home2024Retail.";
        if (products.Skip(1).Any(p => p.Category != OfficeEditionCategory.VisioProject))
            return "Produtos adicionais precisam ser Visio ou Project.";
        if (products.Count > 3)
            return "Selecione no máximo um plano principal, um Visio e um Project.";
        if (products.Skip(1).Any(p => p.Category == request.Plan.Category && p.ProductId == request.Plan.ProductId))
            return "Selecione produtos adicionais diferentes do plano principal.";

        var effectiveChannel = request.ChannelOverride ?? request.Plan.Channel;
        if (effectiveChannel is { Length: > 0 } && !IsSafeToken(effectiveChannel))
            return "O canal de atualização é inválido.";
        if (request.ChannelOverride is { Length: > 0 } && !request.Plan.SupportsSelectableChannel)
            return "O canal deste plano é fixo e não pode ser substituído.";
        if (request.ChannelOverride is { Length: > 0 } channelOverride &&
            !OfficeChannelCatalog.SubscriptionChannels.Any(c => string.Equals(c.Id, channelOverride, StringComparison.OrdinalIgnoreCase)))
            return "O canal informado não é compatível com o ODT.";
        bool mainUsesSubscriptionChannel = OfficeChannelCatalog.SubscriptionChannels.Any(c =>
            string.Equals(c.Id, effectiveChannel, StringComparison.OrdinalIgnoreCase));
        if (products.Skip(1).Any(p =>
                !string.Equals(p.Channel, effectiveChannel, StringComparison.OrdinalIgnoreCase) &&
                !(mainUsesSubscriptionChannel && p.SupportsSelectableChannel)))
            return "Os produtos selecionados usam canais ODT diferentes. Escolha versões compatíveis para gerar um único XML.";
        if (request.AdditionalLanguageIds?.Any(id => !IsSafeToken(id)) == true)
            return "Há um idioma adicional inválido.";
        if (request.ExcludedApps is null || request.ExcludedApps.Any(id => !string.Equals(id, "Bing", StringComparison.OrdinalIgnoreCase) && !IsSafeToken(id)))
            return "Há um aplicativo excluído com identificador inválido.";
        string[] supportedExclusions = OfficeAppCatalog.CoreApps.Select(app => app.Id)
            .Concat(OfficeAppCatalog.AdvancedApps.Select(app => app.Id))
            .Append("OneDrive").Append("OutlookForWindows").ToArray();
        if (request.ExcludedApps.Any(id => !string.Equals(id, "Bing", StringComparison.OrdinalIgnoreCase) && !supportedExclusions.Contains(id, StringComparer.OrdinalIgnoreCase)))
            return "Há um ExcludeApp não suportado pelo ODT.";
        return null;
    }

    private static void WriteProduct(XmlWriter writer, OfficePlan plan, OfficeInstallRequest request, bool isMainProduct)
    {
        writer.WriteStartElement("Product");
        writer.WriteAttributeString("ID", plan.ProductId);
        WriteLanguage(writer, request.LanguageId);
        if (request.AdditionalLanguageIds is { Count: > 0 })
        {
            foreach (var languageId in request.AdditionalLanguageIds.Where(id => !string.Equals(id, request.LanguageId, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
                WriteLanguage(writer, languageId);
        }
        if (isMainProduct)
            foreach (var app in request.ExcludedApps.Where(id => !string.Equals(id, "Bing", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartElement("ExcludeApp");
                writer.WriteAttributeString("ID", app);
                writer.WriteEndElement();
            }
        writer.WriteEndElement();
    }

    private static void WriteLanguage(XmlWriter writer, string id)
    {
        writer.WriteStartElement("Language");
        writer.WriteAttributeString("ID", id);
        writer.WriteEndElement();
    }

    private static void WriteDisplay(XmlWriter writer, OfficeDisplayLevel level, bool acceptEula)
    {
        writer.WriteStartElement("Display");
        writer.WriteAttributeString("Level", level == OfficeDisplayLevel.Silent ? "None" : "Full");
        writer.WriteAttributeString("AcceptEULA", acceptEula ? "TRUE" : "FALSE");
        writer.WriteEndElement();
    }

    private static XDocument WriteDocument(Action<XmlWriter> write)
    {
        using var stream = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = false,
            CloseOutput = false
        }))
        {
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }
        stream.Position = 0;
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static bool IsSafeToken(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Monta um configuration.xml contendo só o elemento &lt;Updates&gt; — permite
    /// ligar/desligar a atualização automática do Office sem reinstalar/reparar nada,
    /// aplicando via setup.exe /configure (mecanismo oficial e documentado do ODT).
    /// </summary>
    public static XDocument BuildUpdatesOnly(bool enabled)
    {
        return WriteDocument(writer =>
        {
            writer.WriteStartElement("Configuration");
            writer.WriteStartElement("Updates");
            writer.WriteAttributeString("Enabled", enabled ? "TRUE" : "FALSE");
            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    public static async Task<string> WriteUpdatesOnlyToFolderAsync(bool enabled, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "configuration-updates.xml");

        var doc = BuildUpdatesOnly(enabled);
        await AtomicFile.WriteAsync(path, (stream, token) => doc.SaveAsync(stream, SaveOptions.None, token), cancellationToken);

        return path;
    }

    /// <summary>
    /// Monta o configuration.xml para uma remoção (setup.exe /configure), usando o
    /// elemento oficial &lt;Remove&gt; do schema do ODT. Com <see cref="OfficeRemoveRequest.RemoveAll"/>
    /// definido, gera &lt;Remove All="TRUE"/&gt;, que remove todos os produtos
    /// Click-to-Run instalados na máquina — não é necessário informar cada SKU.
    /// </summary>
    public static XDocument BuildRemove(OfficeRemoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.RemoveAll && (request.ProductIds is not { Count: > 0 } ||
            request.ProductIds.Any(id => !IsSafeToken(id))))
            throw new ArgumentException("Informe os produtos a remover ou selecione RemoveAll.", nameof(request));
        return WriteDocument(writer =>
        {
            writer.WriteStartElement("Configuration");
            writer.WriteStartElement("Remove");
            if (request.RemoveAll) writer.WriteAttributeString("All", "TRUE");
            else foreach (string productId in request.ProductIds ?? Array.Empty<string>())
            {
                writer.WriteStartElement("Product");
                writer.WriteAttributeString("ID", productId);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            WriteDisplay(writer, request.DisplayLevel, acceptEula: true);
            writer.WriteEndElement();
        });
    }

    public static async Task<string> WriteToFolderAsync(OfficeInstallRequest request, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "configuration.xml");

        var doc = Build(request);
        if (request.DownloadBeforeInstall || !string.IsNullOrWhiteSpace(request.SourcePath))
            doc.Root!.Element("Add")!.SetAttributeValue("Version", OfficeOfflinePayload.GetVersion(request.SourcePath ?? folder,
                request.Architecture, new[] { request.LanguageId }.Concat(request.AdditionalLanguageIds ?? [])));
        await AtomicFile.WriteAsync(path, (stream, token) => doc.SaveAsync(stream, SaveOptions.None, token), cancellationToken);

        return path;
    }

    public static async Task<string> WriteRemoveToFolderAsync(OfficeRemoveRequest request, string folder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "configuration-remove.xml");

        var doc = BuildRemove(request);
        await AtomicFile.WriteAsync(path, (stream, token) => doc.SaveAsync(stream, SaveOptions.None, token), cancellationToken);

        return path;
    }
}
