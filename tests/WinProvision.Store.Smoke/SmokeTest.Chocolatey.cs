using System.Net;
using System.Security.Authentication;
using System.Text;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal static partial class SmokeTest
{
    private static async Task VerifyChocolateyOutdatedCommandAsync()
    {
        string executable = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "smoke-choco.exe"));
        string? observedExecutable = null;
        string[]? observedArguments = null;
        TimeSpan observedTimeout = TimeSpan.Zero;
        var service = new ChocolateyService(null, (path, arguments, timeout, _) =>
        {
            observedExecutable = path;
            observedArguments = arguments;
            observedTimeout = timeout;
            return Task.FromResult((2, "demo.package|1.0|2.0|false\npinned.package|1.0|2.0|true", string.Empty));
        }, executable);

        var result = await service.GetOutdatedWithExitCodeAsync();
        Check(observedExecutable is { } fullPath && Path.IsPathFullyQualified(fullPath)
            && fullPath == executable
            && observedArguments?.SequenceEqual(["outdated", "--limit-output"]) == true
            && observedTimeout == TimeSpan.FromSeconds(120)
            && result.ExitCode == 2 && result.Packages.Count == 1 && result.Packages[0].Id == "demo.package",
            "Chocolatey: outdated usa executável absoluto, timeout de 120 s e aceita código 2 sem listar pinned");
    }

    private static async Task VerifyChocolateyProcessTimeoutKillsTreeAsync()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool timedOut = false;
        try
        {
            await ChocolateyService.RunAsync("cmd.exe", ["/c", "ping -n 30 127.0.0.1 >NUL"],
                TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch (TimeoutException) { timedOut = true; }

        Check(timedOut && timer.Elapsed < TimeSpan.FromSeconds(10),
            "Chocolatey: timeout cancela o processo e sua árvore sem manter a tarefa presa");
    }

    private static async Task VerifyChocolateyTransientRequestRetryAsync()
    {
        const string feed = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices" xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry>
                <id>https://community.chocolatey.org/api/v2/Packages(Id='sample-app',Version='1.0.0')</id>
                <author><name>Example Publisher</name></author>
                <content type="application/xml"><m:properties><d:Description>Smoke test package</d:Description><d:IconUrl></d:IconUrl></m:properties></content>
              </entry>
            </feed>
            """;
        int requestCount = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new StubHandler((_, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
                throw new HttpRequestException("Response ended prematurely.", new IOException("ResponseEnded"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(feed, Encoding.UTF8, "application/atom+xml"),
            };
        })));
        var service = new ChocolateyService(client);
        IReadOnlyList<WinProvision.Core.Models.AppEntry> results = await service.SearchAsync("sample", CancellationToken.None);
        Check(requestCount == 2 && results.Count == 1 && results[0].Id == "sample-app",
            "Chocolatey: repete busca GET após resposta de rede encerrada prematuramente");
    }

    private static async Task VerifyChocolateyTlsFailureIsReportedOnceAsync()
    {
        int requestCount = 0;
        using var client = new HttpClient(new RequestRateLimitHandler(new StubHandler((_, _) =>
        {
            Interlocked.Increment(ref requestCount);
            throw new HttpRequestException("The SSL connection could not be established.",
                new AuthenticationException("RemoteCertificateNameMismatch"));
        })));
        var service = new ChocolateyService(client);
        HttpRequestException? failure = null;
        try { await service.SearchAsync("sample", CancellationToken.None); }
        catch (HttpRequestException exception) { failure = exception; }

        Check(requestCount == 1
            && failure?.Message.Contains("community.chocolatey.org", StringComparison.OrdinalIgnoreCase) == true
            && failure.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase),
            "Chocolatey: falha TLS não é repetida e informa host e configuração de rede");
    }

    private sealed partial class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, cancellationToken));
        }
    }
}
