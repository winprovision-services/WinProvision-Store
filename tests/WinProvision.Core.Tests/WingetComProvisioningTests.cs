using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class WingetComProvisioningTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 1, 0, true)]
    [InlineData(2, 1, 1, true)]
    [InlineData(3, 1, 1, false)]
    public async Task RepairUsesRegistrationBeforeDownloadAndVerifiesComAgain(int failures, int registrations, int repairs, bool expected)
    {
        var bootstrapper = new FakeBootstrapper();
        int probes = 0;
        bool ready = await bootstrapper.EnsureComAsync(_ => Task.FromResult(probes++ >= failures));
        Assert.Equal(expected, ready);
        Assert.Equal(registrations, bootstrapper.Registrations);
        Assert.Equal(repairs, bootstrapper.Repairs);
    }

    [Fact]
    public async Task CancellationDoesNotStartRegistrationOrRepair()
    {
        var bootstrapper = new FakeBootstrapper();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bootstrapper.EnsureComAsync(_ => Task.FromResult(false), ct: new(true)));
        Assert.Equal(0, bootstrapper.Registrations);
        Assert.Equal(0, bootstrapper.Repairs);
    }

    private sealed class FakeBootstrapper : WingetBootstrapper
    {
        public int Registrations { get; private set; }
        public int Repairs { get; private set; }
        protected override Task<(bool Success, string Output)> RegisterAppInstallerByFamilyNameAsync(CancellationToken ct)
        { Registrations++; return Task.FromResult((true, "registered")); }
        public override Task<WingetBootstrapResult> EnsureWingetAsync(Action<string>? log = null, CancellationToken ct = default, bool repair = false)
        {
            Assert.True(repair);
            Repairs++;
            return Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.Bootstrapped));
        }
    }
}
