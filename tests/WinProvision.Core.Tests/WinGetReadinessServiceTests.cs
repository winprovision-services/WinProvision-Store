using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

[Collection("WinGet COM native query gate")]
public sealed class WinGetReadinessServiceTests
{
    [Fact]
    public async Task ConcurrentProvisioningChecksComReadinessOnce()
    {
        int provisionCalls = 0;
        int comChecks = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Create(
            async (_, _) =>
            {
                Interlocked.Increment(ref provisionCalls);
                await Task.Delay(10);
                return new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable);
            },
            async (check, _, token) =>
            {
                Interlocked.Increment(ref comChecks);
                entered.TrySetResult();
                await release.Task;
                return await check(token);
            },
            _ => Task.FromResult(true));

        Task<bool> first = service.EnsureWingetProvisionedAsync(null, CancellationToken.None);
        await entered.Task;
        Task<bool> second = service.EnsureWingetProvisionedAsync(null, CancellationToken.None);
        release.SetResult();

        Assert.All(await Task.WhenAll(first, second), result => Assert.True(result));
        Assert.Equal(2, provisionCalls);
        Assert.Equal(1, comChecks);
    }

    [Fact]
    public async Task CancellationDuringComRecoveryIsPropagatedAndCanRetry()
    {
        int recoveryCalls = 0;
        var service = Create(
            (_, _) => Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable)),
            async (check, _, token) =>
            {
                if (Interlocked.Increment(ref recoveryCalls) == 1)
                    throw new OperationCanceledException(token);
                return await check(token);
            },
            _ => Task.FromResult(true));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EnsureWingetProvisionedAsync(null, cts.Token));
        Assert.True(await service.EnsureWingetProvisionedAsync(null, CancellationToken.None));
        Assert.Equal(2, recoveryCalls);
    }

    [Fact]
    public async Task ComActivationHasFiniteWaitAndDisablesComOnTimeout()
    {
        int activationCalls = 0;
        int breakerCalls = 0;
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WinGetReadinessService(
            (_, _) => Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable)),
            null,
            async _ =>
            {
                Interlocked.Increment(ref activationCalls);
                try { return await activation.Task; }
                finally { nativeCompleted.TrySetResult(); }
            },
            () => true,
            async (check, _, token) => await check(token),
            (_, _, _) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(30),
            resetCom: _ => { }, forceDisableCom: _ => Interlocked.Increment(ref breakerCalls), reportComSuccess: () => { });

        Assert.True(await service.EnsureWingetProvisionedAsync(null, CancellationToken.None));
        Assert.True(await service.EnsureWingetProvisionedAsync(null, CancellationToken.None));
        Assert.Equal(1, activationCalls);
        Assert.Equal(1, breakerCalls);
        activation.SetResult(true);
        await nativeCompleted.Task;
        _ = await WinGetComQueryService.RunBoundedAsync(
            _ => Task.FromResult(true), TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public async Task CancellationDoesNotStartOverlappingComActivation()
    {
        int activationCalls = 0;
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Create(
            (_, _) => Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable)),
            async (check, _, token) => await check(token),
            _ =>
            {
                Interlocked.Increment(ref activationCalls);
                started.TrySetResult();
                return activation.Task;
            });
        using var cts = new CancellationTokenSource();
        Task<bool> canceledCall = service.EnsureWingetProvisionedAsync(null, cts.Token);
        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);
        Task<bool> retry = service.EnsureWingetProvisionedAsync(null, CancellationToken.None);
        Assert.Equal(1, activationCalls);
        activation.SetResult(true);
        Assert.True(await retry);
        Assert.Equal(1, activationCalls);
    }

    [Fact]
    public async Task FaultAfterCallerCancellationIsObserved()
    {
        var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new WinGetReadinessService(
            (_, _) => Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable)),
            message =>
            {
                if (message.Contains("COM ativação tardia falhou", StringComparison.Ordinal))
                    lateObserved.TrySetResult();
            },
            _ => { started.TrySetResult(); return activation.Task; },
            () => true,
            async (check, _, token) => await check(token),
            (_, _, _) => Task.CompletedTask,
            TimeSpan.FromSeconds(1), resetCom: _ => { }, forceDisableCom: _ => { }, reportComSuccess: () => { });
        using var cts = new CancellationTokenSource();
        Task<bool> pending = service.EnsureWingetProvisionedAsync(null, cts.Token);
        await started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        activation.SetException(new InvalidOperationException("late failure after cancellation"));
        await lateObserved.Task;
        _ = await WinGetComQueryService.RunBoundedAsync(
            _ => Task.FromResult(true), TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public async Task PrepareProvisionsBeforeRunningRequestedProbe()
    {
        bool probed = false;
        var service = new WinGetReadinessService(
            (_, _) => Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable)),
            null,
            _ => Task.FromResult(true),
            () => true,
            (_, _, _) => Task.FromResult(true),
            (token, includeStore, _) =>
            {
                token.ThrowIfCancellationRequested();
                Assert.True(includeStore);
                probed = true;
                return Task.CompletedTask;
            }, resetCom: _ => { }, forceDisableCom: _ => { }, reportComSuccess: () => { });

        await service.PrepareAsync(includeMicrosoftStoreCatalog: true);

        Assert.True(probed);
    }

    [Fact]
    public async Task PrepareSkipsProbeInCliOnlyMode()
    {
        bool probed = false;
        var service = new WinGetReadinessService(
            (_, _) => Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable)),
            null,
            _ => Task.FromResult(true),
            () => false,
            (_, _, _) => Task.FromResult(true),
            (_, _, _) => { probed = true; return Task.CompletedTask; },
            resetCom: _ => { }, forceDisableCom: _ => { }, reportComSuccess: () => { });

        await service.PrepareAsync();

        Assert.False(probed);
    }

    private static WinGetReadinessService Create(
        Func<Action<string>?, CancellationToken, Task<WingetBootstrapResult>> provision,
        Func<Func<CancellationToken, Task<bool>>, Action<string>?, CancellationToken, Task<bool>> ensureCom,
        Func<CancellationToken, Task<bool>> activationCheck,
        Action<string>? diagnostic = null) =>
        new(provision, diagnostic, activationCheck, () => true, ensureCom,
            (_, _, _) => Task.CompletedTask, TimeSpan.FromSeconds(1),
            resetCom: _ => { }, forceDisableCom: _ => { }, reportComSuccess: () => { });
}
