using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageMatchDeduplicatorTests
{
    [Fact]
    public void KeepsFirstMatchForEachSourceAndIdInQueryOrder()
    {
        var first = new WinGetService.WinGetPackageMatch("Example.App", "Primeiro", "1", "Editor", "winget");
        var duplicate = first with { PackageId = "example.app", Name = "Segundo", Source = "WINGET" };
        var store = first with { Name = "Loja", Source = "msstore" };

        IReadOnlyList<WinGetService.WinGetPackageMatch>[] batches = [new[] { first, store }, new[] { duplicate }];
        var result = PackageMatchDeduplicator.Deduplicate(batches, default);

        Assert.Equal([first, store], result);
    }

    [Fact]
    public void StopsDuringLargeResultWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IReadOnlyList<WinGetService.WinGetPackageMatch>[] batches =
            [new[] { new WinGetService.WinGetPackageMatch("Example.App", "App", "1", "Editor", "winget") }];
        Assert.ThrowsAny<OperationCanceledException>(() =>
            PackageMatchDeduplicator.Deduplicate(batches, cancellation.Token));
    }
}
