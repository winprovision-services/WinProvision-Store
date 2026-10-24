using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public class CliQueryFallbackTests
{
    [Fact]
    public async Task ComFailureUsesOwnCatalogBeforeCli()
    {
        var calls = new List<string>();
        var result = await PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => { calls.Add("com"); throw new InvalidOperationException("Sem COM"); },
            _ => { calls.Add("api"); return Task.FromResult(7); },
            _ => { calls.Add("cli"); return Task.FromResult(9); }
        ], n => n > 0, _ => { }, default);
        Assert.Equal(7, result);
        Assert.Equal(["com", "api"], calls);
    }

    [Fact]
    public async Task MissingOwnResultReachesCliAndPreservesEmptyResult()
    {
        var calls = new List<string>();
        var result = await PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => { calls.Add("com"); throw new NotSupportedException(); },
            _ => { calls.Add("api"); return Task.FromResult(0); },
            _ => { calls.Add("cli"); return Task.FromResult(0); }
        ], n => n > 0, _ => { }, default);
        Assert.Equal(0, result);
        Assert.Equal(["com", "api", "cli"], calls);
    }

    [Fact]
    public async Task IntegrityFailureDoesNotFallBack()
    {
        bool cliCalled = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => throw new InvalidDataException("Hash divergente"),
            _ => { cliCalled = true; return Task.FromResult(1); }
        ], _ => true, _ => { }, default));
        Assert.False(cliCalled);
    }

    [Fact]
    public async Task CancellationStopsBeforeAnotherProvider()
    {
        using var cancellation = new CancellationTokenSource();
        bool cliCalled = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => { cancellation.Cancel(); return Task.FromResult(0); },
            _ => { cliCalled = true; return Task.FromResult(1); }
        ], _ => false, _ => { }, cancellation.Token));
        Assert.False(cliCalled);
    }
}
