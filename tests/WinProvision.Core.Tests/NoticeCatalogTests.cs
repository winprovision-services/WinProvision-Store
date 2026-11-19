using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class NoticeCatalogTests
{
    [Fact]
    public void CatalogIgnoresUnknownFields()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "futureRoot": true,
          "notices": [{
            "id": "synthetic-notice",
            "severity": "warning",
            "placement": "page-top",
            "anchor": "invented-page",
            "titleKey": "title",
            "messageKey": "message",
            "dismissible": true,
            "dismissPersistence": "session",
            "futureField": "ignored"
          }]
        }
        """;

        NoticeCatalog catalog = NoticeCatalog.Load(json);

        Assert.Equal("synthetic-notice", Assert.Single(catalog.Notices).Id);
    }

    [Fact]
    public void InvalidJsonFallsBackAndUnknownSeverityOrConditionIsIgnored()
    {
        Assert.Same(NoticeCatalog.Default, NoticeCatalog.Load("{broken"));
        const string json = """
        {
          "schemaVersion": 1,
          "notices": [
            { "id": "unknown-severity", "severity": "urgent", "placement": "page-top", "anchor": "p", "titleKey": "t", "messageKey": "m", "dismissible": false, "dismissPersistence": "none" },
            { "id": "invalid-condition", "severity": "warning", "placement": "page-top", "anchor": "p", "titleKey": "t", "messageKey": "m", "dismissible": false, "dismissPersistence": "none", "showWhen": { "risk": "unknown", "differsFromDefault": true, "minimumCount": 1 } }
          ]
        }
        """;

        Assert.Empty(NoticeCatalog.Load(json).Notices);
    }

    [Fact]
    public void DismissalHonorsSessionPermanentAndCriticalRules()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "notice-preferences.json");
        var session = Notice("session", "warning", "session");
        var permanent = Notice("permanent", "warning", "permanent");
        const string criticalJson = """
        {
          "schemaVersion": 1,
          "notices": [{ "id": "critical", "severity": "critical", "placement": "page-top", "anchor": "p", "titleKey": "title", "messageKey": "message", "dismissible": true, "dismissPersistence": "permanent" }]
        }
        """;
        NoticeDefinition critical = Assert.Single(NoticeCatalog.Load(criticalJson).Notices);
        try
        {
            var store = new NoticeDismissalStore(path);
            store.Dismiss(session);
            store.Dismiss(permanent);
            store.Dismiss(critical);

            Assert.True(store.IsDismissed(session));
            Assert.True(store.IsDismissed(permanent));
            Assert.False(store.IsDismissed(critical));
            Assert.False(new NoticeDismissalStore(path).IsDismissed(session));
            Assert.True(new NoticeDismissalStore(path).IsDismissed(permanent));
            Assert.False(critical.Dismissible);
            Assert.Equal("none", critical.DismissPersistence);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SyntheticNoticesWithMatchingCharacteristicsBehaveTheSame()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "notices": [
            { "id": "invented-a", "severity": "warning", "placement": "page-top", "anchor": "p", "titleKey": "a", "messageKey": "a", "dismissible": false, "dismissPersistence": "none", "showWhen": { "risk": "dangerous", "differsFromDefault": true, "minimumCount": 1 } },
            { "id": "invented-b", "severity": "warning", "placement": "page-top", "anchor": "p", "titleKey": "b", "messageKey": "b", "dismissible": false, "dismissPersistence": "none", "showWhen": { "risk": "dangerous", "differsFromDefault": true, "minimumCount": 1 } }
          ]
        }
        """;
        var settings = new[]
        {
            new ElevationSettingDefinition("unrelated-key", "g", "preference", "dangerous", false,
                null, null, true, true, "t", "d", "w")
        };
        var values = new Dictionary<string, bool> { ["unrelated-key"] = true };

        IReadOnlyList<NoticeDefinition> notices = NoticeCatalog.Load(json).GetApplicable(
            "page-top", "p", settings, values);

        Assert.Equal((string[])["invented-a", "invented-b"], notices.Select(notice => notice.Id));
    }

    private static NoticeDefinition Notice(string id, string severity, string persistence) =>
        new(id, severity, "page-top", "p", "title", "message", null,
            true, persistence, null, null);
}
