using Xunit;

namespace WinProvision.Viper.Tests;

public sealed class ViperTransportTests
{
    [Theory]
    [InlineData("http://mirror.example/file.exe?direct", "https://mirror.example/file.exe?direct")]
    [InlineData("https://mirror.example/file.exe", "https://mirror.example/file.exe")]
    public void HttpsOriginNeverFollowsMirrorRedirectOverPlainHttp(string redirect, string expected) =>
        Assert.Equal(new Uri(expected), ViperTransport.RequireSecureRedirect(new Uri("https://origin.example/file.exe"), new Uri(redirect)));

    [Fact]
    public void NonstandardInsecureMirrorPortIsRejected() =>
        Assert.Throws<ViperProtocolException>(() => ViperTransport.RequireSecureRedirect(
            new Uri("https://origin.example/file.exe"), new Uri("http://mirror.example:8080/file.exe")));

    [Fact]
    public async Task UnrequestedTransportCancellationIsRetriedAndReportedAsTimeout()
    {
        using var transport = new ViperTransport(new ViperOptions { MaxAttempts = 2 });
        int attempts = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => transport.RetryAsync<int>(() =>
        {
            attempts++;
            return Task.FromException<int>(new TaskCanceledException("transport aborted"));
        }, CancellationToken.None));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedWithoutRetry()
    {
        using var transport = new ViperTransport(new ViperOptions { MaxAttempts = 2 });
        using var cancellation = new CancellationTokenSource();
        int attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.RetryAsync<int>(() =>
        {
            attempts++;
            cancellation.Cancel();
            return Task.FromCanceled<int>(cancellation.Token);
        }, cancellation.Token));
        Assert.Equal(1, attempts);
    }
}
