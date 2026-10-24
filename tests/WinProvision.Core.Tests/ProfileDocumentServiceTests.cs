using System.Net;
using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Provisioning;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ProfileDocumentServiceTests
{
    [Fact]
    public void AppExportRoundTripsPinnedVersionsAndOfficeOptions()
    {
        var office = OfficeCliDownloadService.DefaultRequest(true).Office!;
        office.AdditionalLanguageIds = ["en-us"];
        var profile = new ProfileManifest { Name = "Minha coleção", Apps = [
            new() { Id = "7zip.7zip", PinnedVersion = "25.01" },
            new() { Id = "office.family", Source = "office", OfficeOptions = office }] };
        var plan = ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(ProfileDocumentService.Serialize(profile)));
        Assert.Equal("25.01", plan[0].PinnedVersion);
        Assert.Equal("winget", plan[0].Source);
        Assert.True(plan[1].OfficeOptions!.DownloadBeforeInstall);
        Assert.Contains("en-us", plan[1].OfficeOptions!.AdditionalLanguageIds);
        Assert.Equal(office.ProductId, OfficeProfileConfiguration.Build(plan[1].OfficeOptions!).Plan.ProductId);
    }

    [Fact]
    public void OfficeExportWithDefaultWingetSourceUsesOfficeEngine()
    {
        var profile = new ProfileManifest { Apps = [new() { Id = "office.o365homepremretail", Source = "winget",
            OfficeOptions = OfficeCliDownloadService.DefaultRequest(true).Office }] };
        string originalAppExport = AotJson.Serialize(profile, WinProvisionJsonOptions.Profile);
        var plan = ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(originalAppExport));
        Assert.Equal("office", Assert.Single(plan).Source);
        Assert.True(plan[0].OfficeOptions!.DownloadBeforeInstall);
        Assert.Contains("\"office\"", ProfileDocumentService.Serialize(profile));
    }

    [Fact]
    public void CloudBackupUsesSameContractAndDeduplicatesOnlyMatchingOptions()
    {
        var backup = new ProfileBackupSet { Tabs = [
            new() { Apps = [new() { Id = "7zip.7zip", PinnedVersion = "25.01" }] },
            new() { Apps = [new() { Id = "7zip.7zip", PinnedVersion = "25.01" }, new() { Id = "Microsoft.PowerShell" }] }] };
        string json = AotJson.Serialize(backup, WinProvisionJsonOptions.Profile);
        Assert.Equal(2, ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(json)).Count);
        backup.Tabs[1].Apps[0].PinnedVersion = "24.09";
        Assert.Throws<InvalidDataException>(() => ProfileDocumentService.CreateExecutionPlan(backup));
    }

    [Fact]
    public void ProvisioningProfileRoundTripsAndCanBeImportedWithoutApps()
    {
        var profile = new ProfileManifest
        {
            Name = "Estação padrão",
            Apps = [],
            Provisioning = new ProvisioningManifest
            {
                SystemTheme = SystemThemeMode.Escuro,
                ShowFileExtensions = true,
                MachineName = "ESTACAO-01"
            }
        };

        string json = AotJson.Serialize(profile, WinProvisionJsonOptions.Profile);
        ProfileBackupSet parsed = ProfileDocumentService.Parse(json);

        Assert.Empty(Assert.Single(parsed.Tabs).Apps);
        Assert.Equal(SystemThemeMode.Escuro, parsed.Tabs[0].Provisioning!.SystemTheme);
        Assert.True(parsed.Tabs[0].Provisioning!.ShowFileExtensions);
        Assert.Equal("ESTACAO-01", parsed.Tabs[0].Provisioning!.MachineName);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"apps\":[]}")]
    [InlineData("{\"apps\":null}")]
    [InlineData("{\"tabs\":[null]}")]
    [InlineData("{\"apps\":[{\"id\":\"office.family\",\"source\":\"office\"}]}")]
    [InlineData("{\"unrelated\":true}")]
    public void RejectsUnsupportedOrIncompleteProfiles(string json)
        => Assert.Throws<InvalidDataException>(() => ProfileDocumentService.Parse(json));

    [Fact]
    public void AcceptsBomCommentsAndCaseInsensitiveNativeFields()
    {
        var parsed = ProfileDocumentService.Parse("\uFEFF{/* collection */\"Apps\":[{\"Id\":\"7zip.7zip\"},],}");
        Assert.Equal("7zip.7zip", Assert.Single(Assert.Single(parsed.Tabs).Apps).Id);
    }

    [Fact]
    public async Task ReadsGistPageThroughApiAndPreservesNativeBackup()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("https://api.github.com/gists/0123456789abcdef0123456789abcdef", request.RequestUri!.ToString());
            return Json("{\"files\":{\"profile.json\":{\"content\":\"{\\\"tabs\\\":[{\\\"apps\\\":[{\\\"id\\\":\\\"7zip.7zip\\\"}]}]}\"}}}");
        });
        using var client = new HttpClient(handler);
        using var reader = new ProfileDocumentReader(client);
        string json = await reader.ReadAsync("https://gist.github.com/user/0123456789abcdef0123456789abcdef");
        Assert.Equal("7zip.7zip", Assert.Single(ProfileDocumentService.CreateExecutionPlan(ProfileDocumentService.Parse(json))).Id);
    }

    [Theory]
    [InlineData("https://example.com/version/profile.json", true)]
    [InlineData("http://example.com/profile.json", false)]
    public async Task FollowsHttpsRedirectsButRejectsDowngrade(string target, bool allowed)
    {
        int calls = 0;
        using var client = new HttpClient(new Handler(_ => ++calls == 1
            ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target) } }
            : Json("{\"apps\":[]}")));
        using var reader = new ProfileDocumentReader(client);
        if (allowed) Assert.Empty(ProfileDocumentService.Parse(await reader.ReadAsync("https://example.com/profile.json")).Tabs[0].Apps);
        else await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadAsync("https://example.com/profile.json"));
        Assert.Equal(allowed ? 2 : 1, calls);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
