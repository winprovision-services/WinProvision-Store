using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationPreferencesTests
{
    [Fact]
    public void Preferences_RoundTripDefaultsValidationAndCategoryIndependence()
    {
        string root = Path.Combine(Path.GetTempPath(), "winprovision-operations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "options.json");
            var service = new OperationPreferencesService(path);
            Assert.Equal(1, service.Load().ParallelOperations);
            Assert.Equal(3, service.Load().ParallelDownloads);
            var options = new OperationPreferences { NotifySuccess = false, NotifyBatchSummary = false,
                ParallelOperations = 2, ParallelDownloads = 4, DownloadDirectory = root, InstallerNames = InstallerNameStyle.PackageVersionArchitecture };
            service.Save(options);
            Assert.Equal(options, service.Load());
            Assert.True(options.Allows(NotificationCategory.Errors));
            Assert.True(options.Allows(NotificationCategory.Updates));
            Assert.False(options.Allows(NotificationCategory.Success));
            Assert.False(options.Allows(NotificationCategory.BatchSummary));
            Assert.Throws<ArgumentException>(() => service.Save(options with { ParallelOperations = 0 }));
            Assert.Throws<ArgumentException>(() => service.Save(options with { ParallelDownloads = 9 }));
            Assert.Throws<ArgumentException>(() => service.Save(options with { DownloadDirectory = "relative" }));
            Assert.Equal(options, service.Load());
            File.WriteAllText(path, "{}"); Assert.Equal(new OperationPreferences(), service.Load());
            File.WriteAllText(path, "invalid"); Assert.Equal(new OperationPreferences(), service.Load());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Limiter_RespectsLimitCancellationAndChangesWithoutInterruptingWork()
    {
        int limit = 1;
        var limiter = new OperationConcurrencyLimiter(() => limit);
        using var first = await limiter.EnterAsync(default);
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> canceled = limiter.EnterAsync(cancellation.Token);
        Assert.False(canceled.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Task<IDisposable> waiting = limiter.EnterAsync(default);
        Assert.False(waiting.IsCompleted);
        limit = 2;
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        limit = 1;
        Task<IDisposable> third = limiter.EnterAsync(default);
        first.Dispose(); Assert.False(third.IsCompleted);
        second.Dispose();
        using var next = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void InstallerNames_AreSafeAndPublishNeverOverwritesExistingFiles()
    {
        Assert.Equal("Vendor.App-2.4-x64.exe", InstallerDownloadPolicy.FileName(InstallerNameStyle.PackageVersionArchitecture, "Vendor.App", "2.4", "x64", "setup.exe"));
        Assert.Equal("setup.exe", InstallerDownloadPolicy.FileName(InstallerNameStyle.Original, "Vendor.App", "2.4", "x64", "setup.exe"));
        Assert.Equal("Meu Aplicativo.exe", InstallerDownloadPolicy.FileName(InstallerNameStyle.Original, "Vendor.App", "2.4", "x64", "Meu Aplicativo.exe"));
        Assert.Equal("Vendor.App-native-2.4-x64.exe", InstallerDownloadPolicy.FileName(InstallerNameStyle.PackageVersion, "Vendor.App", null, null, "native-2.4-x64.exe"));
        string safe = InstallerDownloadPolicy.FileName(InstallerNameStyle.PackageVersion, "../outside", "1/2", null, "setup.exe");
        Assert.DoesNotContain('/', safe); Assert.DoesNotContain('\\', safe);
        string root = Path.Combine(Path.GetTempPath(), "winprovision-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "setup.exe"); File.WriteAllText(original, "old");
            string staging = Path.Combine(root, "temp"); File.WriteAllText(staging, "new");
            string published = InstallerDownloadPolicy.Publish(staging, root, "setup.exe");
            Assert.Equal("old", File.ReadAllText(original)); Assert.Equal("new", File.ReadAllText(published));
            Assert.EndsWith("setup (1).exe", published);
            Assert.Equal(Path.GetFullPath(root), InstallerDownloadPolicy.ResolveDirectory(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Queue_NotifiesOncePerBatchAndSeparatesLaterOperations()
    {
        using var queue = new OperationsQueueService();
        var batches = new List<IReadOnlyList<WinProvision.Core.Models.OperationItem>>();
        queue.BatchCompleted += batches.Add;
        var a = queue.Enqueue("A", WinProvision.Core.Models.OperationKind.Install);
        var b = queue.Enqueue("B", WinProvision.Core.Models.OperationKind.Update);
        a.State = WinProvision.Core.Models.OperationState.Completed; Assert.Empty(batches);
        b.State = WinProvision.Core.Models.OperationState.Failed;
        Assert.Equal(2, Assert.Single(batches).Count);
        queue.Enqueue("C", WinProvision.Core.Models.OperationKind.Install).State = WinProvision.Core.Models.OperationState.Completed;
        Assert.Single(batches[1]);
        using (queue.BeginBatch())
        {
            queue.Enqueue("D", WinProvision.Core.Models.OperationKind.Install).State = WinProvision.Core.Models.OperationState.Completed;
            queue.Enqueue("E", WinProvision.Core.Models.OperationKind.Install).State = WinProvision.Core.Models.OperationState.Completed;
            Assert.Equal(2, batches.Count);
        }
        Assert.Equal(2, batches[2].Count);
    }
}
