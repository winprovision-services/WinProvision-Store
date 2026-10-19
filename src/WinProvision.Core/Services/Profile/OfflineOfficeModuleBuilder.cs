using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;

namespace WinProvision.Core.Services.Profile;

public static class OfflineOfficeModuleBuilder
{
    public static bool IsOffice(OfflineCollectionRequest request) => request.Office is not null || request.IsOffice ||
        request.Id.StartsWith("office.", StringComparison.OrdinalIgnoreCase);

    public static XDocument BuildConfiguration(IReadOnlyList<OfflineCollectionRequest> requests)
    {
        if (requests.Count == 0) throw new ArgumentException("Selecione um produto Office.", nameof(requests));
        if (requests.Select(request => request.Office?.DownloadBeforeInstall).Distinct().Count() > 1)
            throw new InvalidDataException("Os produtos Office do ZIP precisam usar o mesmo modo online ou offline.");
        var documents = new List<XDocument>();
        foreach (OfflineCollectionRequest request in requests)
        {
            OfficeInstallOptions options = request.Office ?? throw new InvalidDataException("O pacote Office não possui configuração salva pela aba Office.");
            if (options.ExcludedApps is null || options.AdditionalLanguageIds is null || options.AdditionalProductIds is null)
                throw new InvalidDataException("A configuração Office contém listas inválidas. Salve novamente pela aba Office.");
            OfficePlan plan = OfficePlanCatalog.ByProductId(options.ProductId) ?? throw new InvalidDataException("Produto Office desconhecido.");
            OfficePlan[] additional = options.AdditionalProductIds.Select(id => OfficePlanCatalog.ByProductId(id)
                ?? throw new InvalidDataException("Produto adicional Office desconhecido.")).ToArray();
            documents.Add(OfficeConfigXmlBuilder.Build(new(plan, options.Architecture, options.LanguageId, options.ExcludedApps,
                DisplayNone: options.Silent, AdditionalLanguageIds: options.AdditionalLanguageIds,
                DisplayLevel: options.Silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
                ChannelOverride: options.ChannelOverride, AutoUpdatesEnabled: options.AutoUpdatesEnabled, AdditionalProducts: additional,
                SourcePath: options.DownloadBeforeInstall ? "." : null, DownloadBeforeInstall: options.DownloadBeforeInstall)));
        }
        // The same builder as the Office tab defines each product. Merge only compatible global settings.
        XDocument combined = documents[0];
        XElement add = combined.Root!.Element("Add")!;
        foreach (XDocument document in documents.Skip(1))
        {
            XElement candidate = document.Root!.Element("Add")!;
            if ((string?)add.Attribute("OfficeClientEdition") != (string?)candidate.Attribute("OfficeClientEdition") ||
                (string?)add.Attribute("Channel") != (string?)candidate.Attribute("Channel") ||
                !XNode.DeepEquals(combined.Root.Element("Display"), document.Root.Element("Display")) ||
                !XNode.DeepEquals(combined.Root.Element("Updates"), document.Root.Element("Updates")))
                throw new InvalidDataException("Os produtos Office precisam usar a mesma arquitetura, canal, modo de exibição e política de atualizações. Ajuste-os na aba Office.");
            foreach (XElement product in candidate.Elements("Product"))
            {
                XElement? existing = add.Elements("Product").SingleOrDefault(item => (string?)item.Attribute("ID") == (string?)product.Attribute("ID"));
                if (existing is not null && !XNode.DeepEquals(existing, product))
                    throw new InvalidDataException("O mesmo produto Office possui configurações diferentes na coleção.");
                if (existing is null) add.Add(new XElement(product));
            }
        }
        OfficePlan[] plans = add.Elements("Product").Select(item => OfficePlanCatalog.ByProductId((string)item.Attribute("ID")!)!).ToArray();
        if (plans.Count(plan => plan.Category != OfficeEditionCategory.VisioProject) > 1 ||
            plans.Count(plan => plan.ProductId.StartsWith("Visio", StringComparison.OrdinalIgnoreCase)) > 1 ||
            plans.Count(plan => plan.ProductId.StartsWith("Project", StringComparison.OrdinalIgnoreCase)) > 1)
            throw new InvalidDataException("Escolha apenas uma suíte Office, um Visio e um Project para o XML único.");
        return combined;
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<OfflineOfficeModule> PrepareAsync(string staging, IReadOnlyList<OfflineCollectionRequest> requests,
        IOfflineOfficeSource source, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
    {
        XDocument configuration = BuildConfiguration(requests);
        string folder = Path.Combine(staging, OfflineOfficeConfiguration.Folder);
        Directory.CreateDirectory(folder);
        string xml = configuration.ToString(SaveOptions.None);
        using (XmlReader reader = XmlReader.Create(new StringReader(xml), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            _ = XDocument.Load(reader);
        string configurationPath = Path.Combine(folder, OfflineOfficeConfiguration.ConfigurationFile);
        await File.WriteAllTextAsync(configurationPath, xml, new UTF8Encoding(false), token).ConfigureAwait(false);
        string setup = Path.Combine(folder, OfflineOfficeConfiguration.SetupFile);
        string expected = await source.CopySetupAsync(setup, progress, token).ConfigureAwait(false);
        string actual = await HashAsync(setup, token).ConfigureAwait(false);
        if (expected.Length != 64 || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 do ODT divergente.");
        bool offline = requests[0].Office!.DownloadBeforeInstall;
        if (offline)
        {
            // The download destination is absolute; the shipped XML remains portable.
            var download = new XDocument(configuration);
            download.Root!.Element("Add")!.SetAttributeValue("SourcePath", folder);
            string downloadPath = Path.Combine(folder, "download-configuration.xml");
            try
            {
                await File.WriteAllTextAsync(downloadPath, download.ToString(SaveOptions.None), new UTF8Encoding(false), token).ConfigureAwait(false);
                await source.DownloadPayloadAsync(setup, downloadPath, progress, token).ConfigureAwait(false);
                var add = configuration.Root!.Element("Add")!;
                add.SetAttributeValue("Version", OfficeOfflinePayload.GetVersion(folder,
                    (int)add.Attribute("OfficeClientEdition")!, add.Elements("Product").Elements("Language").Select(language => (string)language.Attribute("ID")!)));
                await File.WriteAllTextAsync(configurationPath, configuration.ToString(SaveOptions.None), new UTF8Encoding(false), token).ConfigureAwait(false);
                progress?.Report(new("Verificando a integridade dos arquivos completos do Office..."));
                await OfficeOfflinePayload.WriteIndexAsync(folder, token).ConfigureAwait(false);
            }
            finally { if (File.Exists(downloadPath)) File.Delete(downloadPath); }
        }
        var files = new List<OfflineCollectionFile>
        {
            new(OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.ConfigurationFile), await HashAsync(configurationPath, token).ConfigureAwait(false)),
            new(OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.SetupFile), actual)
        };
        if (offline)
        {
            foreach (string file in OfficeOfflinePayload.GetFiles(folder, token))
                files.Add(new(OfflineOfficeConfiguration.Relative(Path.GetRelativePath(folder, file).Replace('\\', '/')), await HashAsync(file, token).ConfigureAwait(false)));
            files.Add(new(OfflineOfficeConfiguration.Relative(OfficeOfflinePayload.IndexFileName), await HashAsync(Path.Combine(folder, OfficeOfflinePayload.IndexFileName), token).ConfigureAwait(false)));
        }
        var module = new OfflineOfficeModule(1, configuration.Root!.Element("Add")!.Elements("Product").Select(item => (string)item.Attribute("ID")!).ToArray(), files, offline);
        await File.WriteAllTextAsync(Path.Combine(folder, OfflineOfficeConfiguration.ScriptFile), GetInstallationScript(module), new UTF8Encoding(true), token).ConfigureAwait(false);
        files.Add(new(OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.ScriptFile), await HashAsync(Path.Combine(folder, OfflineOfficeConfiguration.ScriptFile), token).ConfigureAwait(false)));
        return module;
    }

    public static string GetInstallationScript(OfflineOfficeModule module) => ReadScript("InstallOffice.ps1")
        .Replace("__SETUP_SHA256__", module.Files.Single(file => file.Path == OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.SetupFile)).Sha256, StringComparison.Ordinal)
        .Replace("__XML_SHA256__", module.Files.Single(file => file.Path == OfflineOfficeConfiguration.Relative(OfflineOfficeConfiguration.ConfigurationFile)).Sha256, StringComparison.Ordinal)
        .Replace("__PAYLOAD_INDEX_SHA256__", module.Files.SingleOrDefault(file => file.Path == OfflineOfficeConfiguration.Relative(OfficeOfflinePayload.IndexFileName))?.Sha256 ?? "", StringComparison.Ordinal)
        .Replace("__CONFIGURATION_FILE__", OfflineOfficeConfiguration.ConfigurationFile, StringComparison.Ordinal)
        .Replace("__SETUP_FILE__", OfflineOfficeConfiguration.SetupFile, StringComparison.Ordinal);

    internal static string ScriptHash(string script) => Convert.ToHexString(SHA256.HashData(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(script)).ToArray()));
    private static string ReadScript(string name)
    {
        using Stream stream = typeof(OfflineOfficeModuleBuilder).Assembly.GetManifestResourceStream("WinProvision.Core." + name) ?? throw new InvalidOperationException("Script Office não encontrado.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
