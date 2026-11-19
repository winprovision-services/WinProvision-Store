using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using Microsoft.Data.Sqlite;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class CatalogV2ClientTests
{
    private static byte[] Fixture(string name)
    {
        string resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(resource => resource.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(name);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void PublishedStateSignatureIsValidAndAlteredStateIsRejected()
    {
        CatalogState state = CatalogV2Client.ParseState(Fixture("state.json"), Fixture("state.sig"));
        Assert.Equal(9, state.Generation);
        byte[] altered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Fixture("state.json")).Replace("\"generation\":9", "\"generation\":10", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => CatalogV2Client.ParseState(altered, Fixture("state.sig")));
    }

    [Fact]
    public void OlderGenerationAndCatalogHashAreRejected()
    {
        CatalogState state = CatalogV2Client.ParseState(Fixture("state.json"), Fixture("state.sig"));
        Assert.Throws<InvalidDataException>(() => CatalogV2Client.EnsureNotDowngrade(state with { Generation = 8 }, 9));
        Assert.False(CatalogV2Client.HashEquals(Fixture("catalog.cc617606.db.br"), new string('0', 64)));
        Assert.True(CatalogV2Client.HashEquals(Fixture("catalog.cc617606.db.br"), state.CatalogSha256));
    }

    [Fact]
    public async Task PublishedDatabaseMetadataMatchesAndRejectsWrongGeneration()
    {
        CatalogState state = CatalogV2Client.ParseState(Fixture("state.json"), Fixture("state.sig"));
        string directory = Path.Combine(Path.GetTempPath(), "wps-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "catalog.db");
        try
        {
            await using (var source = new MemoryStream(Fixture("catalog.cc617606.db.br")))
            await using (var brotli = new BrotliStream(source, CompressionMode.Decompress))
            await using (var output = File.Create(database)) await brotli.CopyToAsync(output);
            await CatalogV2Client.ValidateDatabaseAsync(database, state, default);
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE meta SET value='7' WHERE key='generation'";
                await command.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => CatalogV2Client.ValidateDatabaseAsync(database, state, default));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("7zip.7zip.app.json", "7zip.7zip", "f065796a852322cf16a8923eb9d0003283e17d0e393e0164f6776a50c5b72559")]
    [InlineData("Google.Chrome.EXE.app.json", "Google.Chrome.EXE", "1a0ca580a1f0e30d7f8241c3e59c50049cf3b30621bfe9916eda2e4b3871d7e1")]
    [InlineData("Microsoft.PowerShell.app.json", "Microsoft.PowerShell", "51ee6117ff40710aab9a5f054591867ff225978bfe36959282e6cdf85b5c04ab")]
    public void RealAppJsonMatchesCatalogHash(string file, string id, string hash)
    {
        byte[] bytes = Fixture(file);
        CatalogV2Client.ValidateAppJson(bytes, id, hash);
        Assert.Throws<InvalidDataException>(() => CatalogV2Client.ValidateAppJson(bytes, id, new string('0', 64)));
    }

    [Fact]
    public void PublishedTruncatedAppHashMatchesTheSha256Prefix()
    {
        byte[] bytes = Fixture("GnuPG.Gpg4win.app.json");
        const string hash = "4d15657e96c9a96b8f5c17ac0f30f904";
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..32]);
        CatalogV2Client.ValidateAppJson(bytes, "GnuPG.Gpg4win", hash);
        Assert.False(CatalogV2Client.AppHashEquals(bytes, new string('0', 32)));
    }

    [Fact]
    public async Task PublishedDatabaseSearchHandlesEveryPackageIncludingTruncatedAppHashes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wps-catalog-full-search", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using (var source = new MemoryStream(Fixture("catalog.cc617606.db.br")))
            await using (var brotli = new BrotliStream(source, CompressionMode.Decompress))
            await using (var output = File.Create(Path.Combine(directory, "catalog.db"))) await brotli.CopyToAsync(output);
            using var client = new CatalogV2Client(new HttpClient(new HttpClientHandler()), directory);
            IReadOnlyList<AppEntry> allPackages = await client.SearchAsync(null, 50000);
            Assert.Equal(16397, allPackages.Count);
            AppEntry gpg = Assert.Single(allPackages, app => app.Id == "GnuPG.Gpg4win");
            Assert.Equal("4d15657e96c9a96b8f5c17ac0f30f904", gpg.CatalogDetailSha256);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RealInstallerDetailsValidateAndRejectTamperedHash()
    {
        byte[] bytes = Fixture("7zip.7zip-26.06.detail.json");
        using JsonDocument app = JsonDocument.Parse(Fixture("7zip.7zip.app.json"));
        string hash = app.RootElement.GetProperty("versions").EnumerateArray()
            .Single(version => version.GetProperty("version").GetString() == "26.06").GetProperty("detailSha256").GetString()!;
        var parsed = CatalogV2Client.ValidateVersionDetail(bytes, "7zip.7zip", "26.06", hash);
        Assert.NotEmpty(parsed.Installers);
        Assert.Throws<InvalidDataException>(() => CatalogV2Client.ValidateVersionDetail(bytes, "7zip.7zip", "26.06", new string('0', 64)));
        Assert.All(parsed.Installers, installer => Assert.Matches("^[A-Fa-f0-9]{64}$", installer.Sha256!));
        Assert.False(CatalogV2Client.HashEquals(new byte[] { 1, 2, 3 }, parsed.Installers[0].Sha256!));
    }

    [Fact]
    public async Task PublishedPackageArchitectureWithoutDetailedInstallerFailsClearly()
    {
        using JsonDocument app = JsonDocument.Parse(Fixture("Microsoft.PowerShell.app.json"));
        Assert.Contains("arm", app.RootElement.GetProperty("architectures").EnumerateArray()
            .Select(value => value.GetString()), StringComparer.OrdinalIgnoreCase);

        JsonElement latestDetailedVersion = app.RootElement.GetProperty("versions").EnumerateArray()
            .First(version => version.GetProperty("detailPath").ValueKind == JsonValueKind.String);
        string versionText = latestDetailedVersion.GetProperty("version").GetString()!;
        string detailFile = $"Microsoft.PowerShell-{versionText}.detail.json";
        PackageManifest manifest = CatalogV2Client.ValidateVersionDetail(
            Fixture(detailFile), "Microsoft.PowerShell", versionText,
            latestDetailedVersion.GetProperty("detailSha256").GetString()!);
        Assert.DoesNotContain(manifest.Installers, installer =>
            string.Equals(installer.Architecture, "arm", StringComparison.OrdinalIgnoreCase));

        await using var service = new WinProvisionApiService(cacheDir:
            Path.Combine(Path.GetTempPath(), "wps-unsupported-arch", Guid.NewGuid().ToString("N")));
        WinProvisionInstallResult result = await service.TryInstallAsync(
            "Microsoft.PowerShell", preferredArchitecture: "arm", suppliedManifest: manifest,
            recordInstall: false);

        Assert.Equal(WinProvisionInstallOutcome.NoCompatibleInstaller, result.Outcome);
        Assert.Contains("Nenhum installer compatível", result.Message, StringComparison.Ordinal);
        Assert.Contains("Microsoft.PowerShell", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerDefaultsMergeAndInstallerValuesWin()
    {
        const string fixture = """{"schemaVersion":2,"id":"demo.package","version":"1","defaults":{"scope":"machine","silent":{"arguments":"/default","source":"default"}},"installers":[{"architecture":"x64","type":"exe","scope":"user","silent":{"arguments":"/manifest","source":"manifest"},"url":"https://example.test/setup.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
        var parsed = CatalogV2Client.ParseDetailDocument(Encoding.UTF8.GetBytes(fixture));
        Assert.Single(parsed.Installers);
        Assert.Equal("user", parsed.Installers[0].Scope);
        Assert.Equal("/manifest", parsed.Installers[0].SilentArgs);
        Assert.Equal("machine", CatalogV2Client.ParseDetailDocument(Encoding.UTF8.GetBytes(fixture.Replace("\"scope\":\"user\",", "", StringComparison.Ordinal))).Installers[0].Scope);
        Assert.Equal("/default", CatalogV2Client.ParseDetailDocument(Encoding.UTF8.GetBytes(fixture.Replace("\"arguments\":\"/manifest\",\"source\":\"manifest\"", "\"arguments\":\"/default\",\"source\":\"default\"", StringComparison.Ordinal))).Installers[0].SilentArgs);
    }

    [Fact]
    public void CatalogDetailV3ConsumesResolvedElevationAndRejectsUnknownSchema()
    {
        const string detail = """{"schemaVersion":3,"id":"demo.package","version":"1","defaults":{"scope":"machine"},"installers":[{"id":"a","type":"wix","elevation":{"decision":"required","source":"scope","requirement":null,"prohibited":true},"url":"https://example.test/a.msi","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
        CatalogInstaller installer = Assert.Single(CatalogV2Client.ParseDetailDocument(Encoding.UTF8.GetBytes(detail)).Installers);
        Assert.Equal("required", installer.Elevation?.Decision);
        Assert.Equal("scope", installer.Elevation?.Source);
        Assert.True(installer.Elevation?.Prohibited);
        Assert.Throws<InvalidDataException>(() => CatalogV2Client.ParseDetailDocument(Encoding.UTF8.GetBytes(detail.Replace("\"schemaVersion\":3", "\"schemaVersion\":4", StringComparison.Ordinal))));
    }

    [Fact]
    public void CatalogDetailV3AcceptsTypeSourceAndReadsZipNestedType()
    {
        const string detail = """{"schemaVersion":3,"id":"demo.package","version":"1","installers":[{"id":"a","type":"zip","nestedType":"portable","elevation":{"decision":"not-required","source":"type","requirement":null,"prohibited":false},"url":"https://example.test/a.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""";
        CatalogInstaller installer = Assert.Single(CatalogV2Client.ParseDetailDocument(Encoding.UTF8.GetBytes(detail)).Installers);
        Assert.Equal("portable", installer.NestedType);
        ResolvedInstallerElevation state = InstallerElevationResolver.Resolve(installer, useResolvedState: true);
        Assert.Equal("not-required", state.Decision);
        Assert.Equal("type", state.Source);
    }

    [Fact]
    public void UserScopePrefersUserVariantAndNeverFallsBackToMachine()
    {
        var manifest = new PackageManifest { Installers =
        [
            new CatalogInstaller { Id = "machine", Architecture = "x64", Scope = "machine", Type = "msi" },
            new CatalogInstaller { Id = "user", Architecture = "x64", Scope = "user", Type = "exe" }
        ] };
        Assert.Equal("user", WinProvisionApiService.PickInstaller(manifest, System.Runtime.InteropServices.Architecture.X64, "user")?.Id);
        manifest.Installers.RemoveAt(1);
        Assert.Null(WinProvisionApiService.PickInstaller(manifest, System.Runtime.InteropServices.Architecture.X64, "user"));
    }

    [Fact]
    public void CatalogSilentAndSilentWithProgressStaySeparate()
    {
        var installer = new CatalogInstaller { SilentArgs = "/catalog-default", SilentSource = "default", SilentWithProgressArgs = "/progress", SilentWithProgressSource = "manifest" };
        InstallerSilentArgumentResolution resolved = CatalogInstallerOptions.ResolveSilent(installer);
        Assert.Equal("/catalog-default", resolved.Arguments);
        Assert.Equal(InstallerSilentArgumentSource.TypeDefault, resolved.Source);
        installer.SilentArgs = null;
        installer.SilentSource = "unknown";
        Assert.Null(CatalogInstallerOptions.ResolveSilent(installer).Arguments);
    }

    [Fact]
    public async Task FtsSearchReadsThePublishedDatabase()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wps-catalog-search", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using (var compressed = new MemoryStream(Fixture("catalog.cc617606.db.br")))
            await using (var brotli = new BrotliStream(compressed, CompressionMode.Decompress))
            await using (var output = File.Create(Path.Combine(directory, "catalog.db"))) await brotli.CopyToAsync(output);
            using var client = new CatalogV2Client(new HttpClient(new HttpClientHandler()), directory);
            var results = await client.SearchAsync("PowerShell", 20);
            Assert.Contains(results, entry => entry.Id == "Microsoft.PowerShell");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SecondRefreshUsesStateEtagAndDoesNotRedownloadDatabase()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wps-catalog-etag", Guid.NewGuid().ToString("N"));
        var handler = new CatalogFixtureHandler(Fixture("state.json"), Fixture("state.sig"), Fixture("catalog.cc617606.db.br"));
        using var http = new HttpClient(handler);
        using var client = new CatalogV2Client(http, directory);
        try
        {
            await client.RefreshAsync();
            await client.RefreshAsync();
            Assert.Equal(2, handler.StateRequests);
            Assert.Equal(1, handler.CatalogRequests);
            Assert.Equal(1, handler.SignatureRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void UnknownSchemaAndFtsInputAreHandledSafely()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":99,\"id\":\"x\",\"version\":\"1\",\"installers\":[]}");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Throws<InvalidDataException>(() => CatalogV2Client.ValidateVersionDetail(bytes, "x", "1", hash));
        Assert.Equal("\"Google\"* AND \"Chrome\"* AND \"OR\"*", CatalogV2Client.SanitizeFtsQuery("Google Chrome OR *"));
    }

    [Fact]
    public void EmbeddedBitsContractMatchesPipelineMaps()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinProvision.Store.Assets.catalog-bits.json")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal("072B36D66E9DDBCB22F31E72FA616DB73861EADE28E67FFDB85F428A85A3F3D5", Convert.ToHexString(SHA256.HashData(bytes.ToArray())));
        using JsonDocument json = JsonDocument.Parse(bytes.ToArray());
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("architecture").GetProperty("x86").GetInt32());
        Assert.Equal(2, root.GetProperty("architecture").GetProperty("x64").GetInt32());
        Assert.Equal(4, root.GetProperty("architecture").GetProperty("arm").GetInt32());
        Assert.Equal(8, root.GetProperty("architecture").GetProperty("arm64").GetInt32());
        Assert.Equal(16, root.GetProperty("architecture").GetProperty("neutral").GetInt32());
        Assert.Equal(512, root.GetProperty("installerType").GetProperty("zip").GetInt32());
        Assert.Equal(2, root.GetProperty("scope").GetProperty("machine").GetInt32());
        Assert.Equal(3, root.GetProperty("elevation").GetProperty("elevatesself").GetInt32());
        Assert.Equal(CatalogBitsContract.ReadMap("architecture"), root.GetProperty("architecture").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()));
    }

    [Fact]
    public async Task Generation9PackageBitsMatchRealAppAndDetailFixtures()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wps-catalog-bits", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string database = Path.Combine(directory, "catalog.db");
        try
        {
            await using (var source = new MemoryStream(Fixture("catalog.cc617606.db.br")))
            await using (var brotli = new BrotliStream(source, CompressionMode.Decompress))
            await using (var output = File.Create(database)) await brotli.CopyToAsync(output);
            using Stream contractStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinProvision.Store.Assets.catalog-bits.json")!;
            using JsonDocument contract = JsonDocument.Parse(contractStream);
            var archBits = contract.RootElement.GetProperty("architecture").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
            var typeBits = contract.RootElement.GetProperty("installerType").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
            var scopeBits = contract.RootElement.GetProperty("scope").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
            var elevBits = contract.RootElement.GetProperty("elevation").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
            var expected = new Dictionary<string, (int Arch, int InstallerType, int Scope, int Elevation)>(StringComparer.Ordinal)
            {
                ["Google.Chrome.EXE"] = (11, 1, 3, 0),
                ["7zip.7zip"] = (15, 65, 2, 3),
                ["Microsoft.PowerShell"] = (15, 68, 2, 3),
                ["Wingware.Wing"] = (2, 16, 2, 3),
                ["Brave.Brave"] = (11, 1, 3, 1),
                ["JetBrains.IntelliJIDEA"] = (10, 32, 3, 1)
            };
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync();
            foreach ((string id, var expectedBits) in expected)
            {
                using JsonDocument app = JsonDocument.Parse(Fixture(id + ".app.json"));
                int detailArch = 0, type = 0, scope = 0, elev = 0;
                foreach (JsonElement version in app.RootElement.GetProperty("versions").EnumerateArray())
                {
                    if (version.GetProperty("detailPath").ValueKind == JsonValueKind.Null) continue;
                    string versionText = version.GetProperty("version").GetString()!;
                    using JsonDocument detail = JsonDocument.Parse(Fixture(id + "-" + versionText + ".detail.json"));
                    JsonElement defaults = detail.RootElement.GetProperty("defaults");
                    foreach (JsonElement installer in detail.RootElement.GetProperty("installers").EnumerateArray())
                    {
                        string? architecture = EffectiveString(installer, defaults, "architecture");
                        string? installerType = EffectiveString(installer, defaults, "type");
                        string? installerScope = EffectiveString(installer, defaults, "scope");
                        string? requirement = EffectiveObjectString(installer, defaults, "elevation", "requirement");
                        if (architecture is not null)
                        {
                            Assert.True(archBits.TryGetValue(architecture, out int archBit), $"Architecture sem bit no contrato: {id}/{versionText}/{architecture}");
                            detailArch |= archBit;
                        }
                        if (installerType is not null)
                        {
                            Assert.True(typeBits.TryGetValue(installerType, out int typeBit), $"InstallerType sem bit no contrato: {id}/{versionText}/{installerType}");
                            type |= typeBit;
                        }
                        if (installerScope is not null)
                        {
                            Assert.True(scopeBits.TryGetValue(installerScope, out int scopeBit), $"Scope sem bit no contrato: {id}/{versionText}/{installerScope}");
                            scope |= scopeBit;
                        }
                        if (requirement is not null)
                        {
                            Assert.True(elevBits.TryGetValue(requirement, out int elevBit), $"ElevationRequirement sem bit no contrato: {id}/{versionText}/{requirement}");
                            elev |= elevBit;
                        }
                    }
                }
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT arch,itype,scope,elev FROM pkg WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                await using SqliteDataReader row = await command.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync());
                int packageArch = row.GetInt32(0);
                Assert.Equal(0, detailArch & ~packageArch);
                Assert.Equal(expectedBits.Arch, packageArch);
                Assert.Equal(expectedBits.InstallerType, row.GetInt32(1));
                Assert.Equal(expectedBits.Scope, row.GetInt32(2));
                Assert.Equal(expectedBits.Elevation, row.GetInt32(3));
                Assert.Equal(type, row.GetInt32(1));
                Assert.Equal(scope, row.GetInt32(2));
                Assert.Equal(elev, row.GetInt32(3));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        static JsonElement Effective(JsonElement installer, JsonElement defaults, string property) =>
            installer.TryGetProperty(property, out JsonElement value) ? value
            : defaults.TryGetProperty(property, out value) ? value : default;

        static string? EffectiveString(JsonElement installer, JsonElement defaults, string property)
        {
            JsonElement value = Effective(installer, defaults, property);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        static string? EffectiveObjectString(JsonElement installer, JsonElement defaults, string property, string nestedProperty)
        {
            JsonElement value = Effective(installer, defaults, property);
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(nestedProperty, out JsonElement nested)
                && nested.ValueKind == JsonValueKind.String ? nested.GetString() : null;
        }
    }

    [Fact]
    public void RealWebpIconPreservesTransparencyAndResizes()
    {
        DecodedWebpImage decoded = WebpImageDecoder.Decode(Fixture("7zip.icon.webp"), 24);
        Assert.True(WebpImageDecoder.IsWebp(Fixture("7zip.icon.webp")));
        Assert.True(decoded.HasAlpha);
        Assert.Equal(24, decoded.Width);
        Assert.Equal(24, decoded.Height);
        Assert.True(decoded.PngBytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47 }));
    }

    private sealed class CatalogFixtureHandler(byte[] state, byte[] signature, byte[] catalog) : HttpMessageHandler
    {
        public int StateRequests { get; private set; }
        public int SignatureRequests { get; private set; }
        public int CatalogRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/meta/state.json", StringComparison.Ordinal))
            {
                StateRequests++;
                if (request.Headers.IfNoneMatch.Count > 0)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(state) };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"state9\"");
                return Task.FromResult(response);
            }
            if (path.EndsWith("/meta/state.sig", StringComparison.Ordinal))
            {
                SignatureRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(signature) });
            }
            if (path.EndsWith("/meta/catalog.cc617606.db.br", StringComparison.Ordinal))
            {
                CatalogRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(catalog) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
