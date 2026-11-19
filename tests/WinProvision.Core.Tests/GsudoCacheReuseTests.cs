using System.Security.Cryptography;
using WinProvision.Elevation;
using Xunit;

namespace WinProvision.Core.Tests;

[CollectionDefinition("Gsudo cache process state", DisableParallelization = true)]
public sealed class GsudoCacheProcessStateCollection;

[Collection("Gsudo cache process state")]
public sealed class GsudoCacheReuseTests
{
    [Fact]
    public async Task RunnerReusesAuthorizationAndReauthorizesAfterCacheExpiry()
    {
        string helper = Path.GetTempFileName();
        File.WriteAllBytes(helper, [1, 2, 3, 4]);
        string hash = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4]));
        bool available = false;
        int authorizations = 0, dispatched = 0, statusChecks = 0;
        var controlCalls = new List<string>();
        Task<ElevationResult> Control(IReadOnlyList<string> args, CancellationToken _)
        {
            controlCalls.Add(string.Join(' ', args));
            if (args is ["status", "CacheAvailable"])
            {
                statusChecks++;
                return Task.FromResult(new ElevationResult(ElevationStatus.Success, 0,
                    available ? "CacheAvailable: true" : "CacheAvailable: false", ""));
            }
            if (args.Count >= 2 && args[0] == "cache" && args[1] == "on")
            {
                authorizations++;
                available = true;
                return Task.FromResult(new ElevationResult(ElevationStatus.Success, 0, "", ""));
            }
            if (args is ["cache", "off"])
            {
                available = false;
                return Task.FromResult(new ElevationResult(ElevationStatus.Success, 0, "", ""));
            }
            throw new InvalidOperationException(string.Join(' ', args));
        }

        try
        {
            var options = new GsudoRunnerOptions(helper, hash, IsElevationAllowed: () => true);
            var cache = new GsudoCacheController(options, Control);
            await cache.StopAsync(); // Isola o estado estático compartilhado do cache entre testes.
            controlCalls.Clear();
            statusChecks = authorizations = dispatched = 0;
            var runner = new GsudoRunner(options, cache, (_, _, _) =>
            {
                dispatched++;
                return Task.FromResult(new ElevationResult(ElevationStatus.Success, 0, "", ""));
            });
            var command = new ElevatedCommand("dummy.exe", [], ElevationCategory.Other);
            Assert.True((await runner.RunAsync(command)).Success);
            Assert.True((await runner.RunAsync(command)).Success);
            Assert.Equal(1, authorizations);
            Assert.Equal(2, dispatched);
            Assert.Equal(1, statusChecks);
            Assert.Equal("cache on --duration 00:05:00", controlCalls[0]);
            Assert.Equal("status CacheAvailable", controlCalls[1]);

            available = false; // Expiration between commands must trigger a fresh UAC authorization.
            Assert.True((await runner.RunAsync(command)).Success);
            Assert.Equal(2, authorizations);
            Assert.Equal(3, dispatched);
            Assert.Equal(2, statusChecks);
            Assert.Equal("status CacheAvailable", controlCalls[2]);
            Assert.Equal("cache on --duration 00:05:00", controlCalls[3]);
        }
        finally
        {
            await new GsudoCacheController(new GsudoRunnerOptions(helper, hash), Control).StopAsync();
            File.Delete(helper);
        }
    }
}
