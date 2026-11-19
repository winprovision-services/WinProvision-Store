using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class NotificationPolicyTests
{
    private const string PolicyJson = """
        {
          "schemaVersion": 1,
          "categories": {
            "OperationResult": {
              "defaultSeverity": "Success",
              "channels": ["InApp", "WindowsToast"],
              "autoDismissSeconds": 6,
              "dedupeWindowSeconds": 30,
              "cooldownSeconds": 0,
              "coalesce": true,
              "showWhenAppFocused": true,
              "showToastWhenUnfocused": true,
              "requiresAction": false,
              "persistInHistory": true,
              "unusedFutureField": "ignored"
            },
            "BlockingError": {
              "defaultSeverity": "Error",
              "channels": ["InApp", "WindowsToast"],
              "autoDismissSeconds": null,
              "dedupeWindowSeconds": 30,
              "cooldownSeconds": 0,
              "coalesce": true,
              "showWhenAppFocused": true,
              "showToastWhenUnfocused": true,
              "requiresAction": false,
              "persistInHistory": true
            }
          }
        }
        """;

    [Fact]
    public void LoadsCategoryPolicyAndIgnoresExtraFields()
    {
        NotificationPolicy policy = NotificationPolicyCatalog.Load(PolicyJson)
            .Resolve(NotificationCategories.OperationResult);

        Assert.Equal(NotificationSeverity.Success, policy.DefaultSeverity);
        Assert.Equal(6, policy.AutoDismissSeconds);
        Assert.Equal(30, policy.DedupeWindowSeconds);
        Assert.True(policy.Coalesce);
        Assert.Equal(new[] { NotificationChannel.InApp, NotificationChannel.WindowsToast }, policy.Channels);
    }

    [Fact]
    public void UnknownCategoryUsesSafeDefault()
    {
        NotificationPolicy policy = NotificationPolicyCatalog.Load(PolicyJson).Resolve("FutureCategory");

        Assert.Equal(NotificationSeverity.Information, policy.DefaultSeverity);
        Assert.Equal(new[] { NotificationChannel.InApp }, policy.Channels);
        Assert.False(policy.ShowToastWhenUnfocused);
        Assert.True(policy.PersistInHistory);
    }

    [Fact]
    public void InvalidJsonFallsBackAndLogs()
    {
        var logs = new List<string>();
        NotificationPolicyCatalog catalog = NotificationPolicyCatalog.Load("{", logs.Add);

        Assert.Equal(NotificationSeverity.Error, catalog.Resolve(NotificationCategories.BlockingError).DefaultSeverity);
        Assert.Single(logs);
        Assert.Contains("padrões seguros", logs[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameCategoryAndSeverityBehaveTheSameForDifferentOrigins()
    {
        NotificationPolicyCatalog catalog = NotificationPolicyCatalog.Load(PolicyJson);
        NotificationPolicy policy = catalog.Resolve(NotificationCategories.OperationResult);
        Notification first = Create("Synthetic.SourceOne");
        Notification second = Create("Random.SourceTwo");

        Assert.Equal(
            NotificationPolicyCatalog.SelectChannel(policy, appFocused: false, windowsNotificationsEnabled: true),
            NotificationPolicyCatalog.SelectChannel(policy, appFocused: false, windowsNotificationsEnabled: true));
        Assert.Equal(first.Category, second.Category);
        Assert.Equal(first.Severity, second.Severity);

        static Notification Create(string origin) => new(
            Guid.NewGuid().ToString("N"),
            NotificationCategories.OperationResult,
            NotificationSeverity.Success,
            "Concluído",
            "Operação concluída.",
            DeduplicationKey: "same-operation",
            Origin: origin);
    }

    [Fact]
    public void FocusAndQuietPreferenceSelectOnlyOneChannel()
    {
        NotificationPolicy policy = NotificationPolicyCatalog.Load(PolicyJson)
            .Resolve(NotificationCategories.OperationResult);

        Assert.Equal(NotificationChannel.InApp,
            NotificationPolicyCatalog.SelectChannel(policy, appFocused: true, windowsNotificationsEnabled: true));
        Assert.Equal(NotificationChannel.WindowsToast,
            NotificationPolicyCatalog.SelectChannel(policy, appFocused: false, windowsNotificationsEnabled: true));
        Assert.Null(NotificationPolicyCatalog.SelectChannel(policy, appFocused: false, windowsNotificationsEnabled: false));
        Assert.Null(NotificationPolicyCatalog.SelectChannel(policy, appFocused: false,
            windowsNotificationsEnabled: true, systemQuietMode: true));
    }

    [Fact]
    public void QueueCoalescesDuplicatesAndPrioritizesErrors()
    {
        NotificationPolicyCatalog catalog = NotificationPolicyCatalog.Load(PolicyJson);
        NotificationPolicy resultPolicy = catalog.Resolve(NotificationCategories.OperationResult);
        NotificationPolicy errorPolicy = catalog.Resolve(NotificationCategories.BlockingError);
        var queue = new NotificationQueue();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(queue.Enqueue(Create(NotificationCategories.OperationResult, NotificationSeverity.Success, "same"), resultPolicy, now));
        Assert.True(queue.Enqueue(Create(NotificationCategories.OperationResult, NotificationSeverity.Success, "same"), resultPolicy, now.AddSeconds(1)));
        Assert.Equal(2, queue.Active?.Count);
        Assert.True(queue.Enqueue(Create(NotificationCategories.BlockingError, NotificationSeverity.Error, "error"), errorPolicy, now.AddSeconds(2)));
        Assert.Equal(NotificationSeverity.Error, queue.Active?.Notification.Severity);
        Assert.Single(queue.Pending);
        Assert.Equal(NotificationSeverity.Success, queue.DismissActive()?.Notification.Severity);
    }

    [Fact]
    public void QueuedNoticesExposeCoalescedPendingCount()
    {
        NotificationPolicy policy = NotificationPolicyCatalog.CreateDefault()
            .Resolve(NotificationCategories.PassiveInformation);
        var queue = new NotificationQueue();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(queue.Enqueue(Create(NotificationCategories.PassiveInformation, NotificationSeverity.Information, "first"), policy, now));
        Assert.True(queue.Enqueue(Create(NotificationCategories.PassiveInformation, NotificationSeverity.Information, "second"), policy, now.AddSeconds(1)));
        Assert.True(queue.Enqueue(Create(NotificationCategories.PassiveInformation, NotificationSeverity.Information, "second"), policy, now.AddSeconds(2)));

        Assert.Single(queue.Pending);
        Assert.Equal(2, queue.PendingNotificationCount);
    }

    [Fact]
    public void ErrorFilteringOnlyAllowsErrors()
    {
        Assert.False(NotificationPolicyCatalog.AllowsSeverity(NotificationLevel.ErrorsOnly, NotificationSeverity.Success));
        Assert.True(NotificationPolicyCatalog.AllowsSeverity(NotificationLevel.ErrorsOnly, NotificationSeverity.Error));
        Assert.True(NotificationPolicyCatalog.AllowsSeverity(NotificationLevel.Important, NotificationSeverity.Warning));
    }

    [Fact]
    public void OneOperationSummaryIsOneNotificationRegardlessOfItemCount()
    {
        var queue = new NotificationQueue();
        NotificationPolicy policy = NotificationPolicyCatalog.CreateDefault().Resolve(NotificationCategories.OperationResult);
        Notification summary = Create(NotificationCategories.OperationResult, NotificationSeverity.Success, "batch-123");

        Assert.True(queue.Enqueue(summary, policy, DateTimeOffset.UtcNow));
        Assert.Single(new[] { queue.Active });
        Assert.Equal("3 de 3 incluídos", queue.Active?.Notification.Message);
    }

    private static Notification Create(string category, NotificationSeverity severity, string key) => new(
        Guid.NewGuid().ToString("N"),
        category,
        severity,
        severity == NotificationSeverity.Error ? "Erro" : "Resultado",
        key == "batch-123" ? "3 de 3 incluídos" : "Resultado",
        DeduplicationKey: key);
}
