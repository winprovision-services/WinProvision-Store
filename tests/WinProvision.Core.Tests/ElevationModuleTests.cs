using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using WinProvision.Elevation;
using Xunit;

namespace WinProvision.Core.Tests;

[Collection("Gsudo cache process state")]
public sealed class ElevationModuleTests
{
    [Fact]
    public void GsudoCommandLineBuilderPreservesWindowsArgumentMatrix()
    {
        string executable = @"C:\Program Files\WinProvision\setup.exe";
        string[] arguments =
        [
            @"C:\Program Files (x86)\Vendor\setup.exe",
            "argument with spaces",
            "embedded\"quote",
            "trailing-backslash\\",
            "ação 東京 😀"
        ];
        var command = new ElevatedCommand(executable, arguments, ElevationCategory.DownloadedInstaller);

        GsudoChildCommandLine built = GsudoCommandLineBuilder.Build(command);
        string[] roundTripped = ParseWindowsArguments("dummy.exe " + built.Arguments).Skip(1).ToArray();

        Assert.Equal(executable, built.ExecutablePath);
        Assert.Equal(arguments, roundTripped);
    }

    [Fact]
    public void GsudoHelperCommandLineSurvivesThePinnedForkSplitterWithoutAnExtraQuoteLayer()
    {
        string executable = @"C:\Program Files\WinProvision\choco.exe";
        string[] arguments = ["upgrade", "bleachbit", "argument with spaces", "embedded\"quote", "trailing-slash\\", "ação 東京"];
        var command = new ElevatedCommand(executable, arguments, ElevationCategory.Chocolatey);
        string helperLine = GsudoCommandLineBuilder.BuildHelperCommandLine(command);
        string[] helperTokens = SplitLikePinnedGsudo(helperLine).ToArray();

        Assert.Equal((string[])["--direct", "--loglevel", "Error"], helperTokens.Take(3));
        Assert.Equal(executable, UnQuote(helperTokens[3]));
        string childArgumentLine = string.Join(' ', helperTokens.Skip(4));
        Assert.Equal(GsudoCommandLineBuilder.Build(command).Arguments, childArgumentLine);
        Assert.DoesNotContain("\\\"upgrade\\\"", helperLine);
    }

    [Fact]
    public void ElevatedCommandCopiesArgumentsAndKeepsContractFields()
    {
        var source = new List<string> { "first", "second" };
        var command = new ElevatedCommand("setup.exe", source, ElevationCategory.Office, "C:\\work");
        source[0] = "changed";

        Assert.Equal("first", command.Arguments[0]);
        Assert.Equal(ElevationCategory.Office, command.Category);
        Assert.Equal("C:\\work", command.WorkingDirectory);
        Assert.Null(command.ExpectedSha256);
        Assert.Contains(ElevationStatus.HelperError, Enum.GetValues<ElevationStatus>());
    }

    [Fact]
    public async Task DownloadedInstallerHashIsCheckedImmediatelyByTheModuleVerifier()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] contents = [1, 2, 3, 4];
            await File.WriteAllBytesAsync(path, contents);
            string correctHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents));

            Assert.True(await ElevationIntegrityVerifier.VerifyAsync(path, correctHash, CancellationToken.None));
            Assert.False(await ElevationIntegrityVerifier.VerifyAsync(path, new string('0', 64), CancellationToken.None));
            Assert.False(await ElevationIntegrityVerifier.VerifyAsync(path, "invalid", CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DirectRunnerRejectsHashMismatchBeforeStartingTheFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "not an executable");
            var command = new ElevatedCommand(path, Array.Empty<string>(), ElevationCategory.DownloadedInstaller,
                expectedSha256: new string('0', 64));

            ElevationResult result = await new DirectRunner().RunAsync(command);

            Assert.Equal(ElevationStatus.Rejected, result.Status);
            Assert.Null(result.ExitCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DirectRunnerPreservesWindowsArgumentMatrixAndExecutablePathsWithSpaces()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "WinProvision Elevation Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "capture arguments.ps1");
        string output = Path.Combine(root, "captured arguments.txt");
        string commandPath = Path.Combine(root, "whoami executable with spaces.exe");
        string whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        string[] matrix = ["argument with spaces", "embedded\"quote", "trailing-backslash\\", "ação 東京 😀"];
        await File.WriteAllTextAsync(script,
            "param([string]$OutputPath)\n[IO.File]::WriteAllLines($OutputPath, [string[]]$args, [Text.UTF8Encoding]::new($false))\n");
        File.Copy(whoami, commandPath);
        try
        {
            var arguments = new List<string> { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, output };
            arguments.AddRange(matrix);
            ElevationResult result = await new DirectRunner().RunAsync(
                new ElevatedCommand(PowerShellPath, arguments, ElevationCategory.Other));
            Assert.Equal(ElevationStatus.Success, result.Status);
            Assert.Equal(matrix, await File.ReadAllLinesAsync(output));

            ElevationResult pathResult = await new DirectRunner().RunAsync(
                new ElevatedCommand(commandPath, Array.Empty<string>(), ElevationCategory.Other));
            Assert.Equal(ElevationStatus.Success, pathResult.Status);
            Assert.Contains(Environment.UserName, pathResult.StandardOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DirectRunnerHonorsCancellationBeforeDispatch()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var progress = new RecordingProgress();

        ElevationResult result = await new DirectRunner().RunAsync(
            new ElevatedCommand("missing.exe", Array.Empty<string>(), ElevationCategory.Other), progress, cancellation.Token);

        Assert.Equal(ElevationStatus.Cancelled, result.Status);
        Assert.False(progress.Started);
    }

    [Fact]
    public async Task SharedSelfElevationScriptEscapesTheWindowsArgumentMatrix()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "WinProvision Self Elevation Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string script = Path.Combine(root, "quote arguments.ps1");
        string output = Path.Combine(root, "quoted line.txt");
        string[] matrix = ["argument with spaces", "embedded\"quote", "trailing-backslash\\", "ação 東京 😀"];
        string encoded = string.Join(';', matrix.Select(value => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value))));
        string generatedScript = WinProvision.Core.Services.Profile.ScriptSelfElevation.PowerShell + "\n"
            + "$values = @('" + encoded + "'.Split(';') | ForEach-Object { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($_)) })\n"
            + "$line = @($values | ForEach-Object { ConvertTo-WinProvisionArgument -Value ([string]$_) }) -join ' '\n"
            + "[IO.File]::WriteAllText($args[0], $line, [Text.UTF8Encoding]::new($false))\n";
        await File.WriteAllTextAsync(script, generatedScript);
        try
        {
            ElevationResult result = await new DirectRunner().RunAsync(new ElevatedCommand(PowerShellPath,
                (string[])["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, output], ElevationCategory.Other));
            Assert.Equal(ElevationStatus.Success, result.Status);
            string[] roundTripped = ParseWindowsArguments("dummy.exe " + await File.ReadAllTextAsync(output)).Skip(1).ToArray();
            Assert.Equal(matrix, roundTripped);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DirectRunnerWaitsForTheChildWhenCancellationArrivesAtDispatch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress(cancellation.Cancel);
        var command = new ElevatedCommand(PowerShellPath,
            (string[])["-NoLogo", "-NoProfile", "-Command", "Start-Sleep -Milliseconds 800; [Console]::WriteLine('finished')"],
            ElevationCategory.Other);

        ElevationResult result = await new DirectRunner().RunAsync(command, progress, cancellation.Token);

        Assert.True(progress.Started);
        Assert.Equal(ElevationStatus.Success, result.Status);
        Assert.Contains("finished", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutputCaptureIsBoundedWhileLiveProgressReceivesTheFullLine()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int outputLength = 300_000;
        var progress = new RecordingProgress();
        var command = new ElevatedCommand(PowerShellPath,
            (string[])["-NoLogo", "-NoProfile", "-Command", $"[Console]::WriteLine(('x' * {outputLength}))"],
            ElevationCategory.Other);

        ElevationResult result = await new DirectRunner().RunAsync(command, progress);

        Assert.Equal(ElevationStatus.Success, result.Status);
        Assert.Equal(256 * 1024, result.StandardOutput.Length);
        Assert.Contains(progress.Lines, line => line.Length == outputLength && line.All(character => character == 'x'));
    }

    [Fact]
    public async Task ChildOutputRemainsSeparatedAndExitCode999IsNotAHelperRefusal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var progress = new RecordingProgress();
        var command = new ElevatedCommand(PowerShellPath,
            (string[])["-NoLogo", "-NoProfile", "-Command",
                "[Console]::Out.WriteLine('stdout-line'); [Console]::Error.WriteLine('stderr-line'); exit 999"],
            ElevationCategory.Other);

        ElevationResult result = await new DirectRunner().RunAsync(command, progress);

        Assert.Equal(ElevationStatus.Failed, result.Status);
        Assert.Equal(999, result.ExitCode);
        Assert.Contains("stdout-line", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("stderr-line", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(progress.Lines, line => line == "stdout-line");
        Assert.Contains(progress.Lines, line => line == "[stderr] stderr-line");
    }

    [Fact]
    public async Task CacheUsesFiveMinuteIdleWindowAndStopIsSafeWhenAlreadyExpired()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), GsudoCacheController.IdleDuration);
        bool available = false;
        int starts = 0;
        var commands = new List<string[]>();
        Task<ElevationResult> RunControl(IReadOnlyList<string> arguments, CancellationToken _)
        {
            string[] command = arguments.ToArray();
            commands.Add(command);
            if (command is ["status", "CacheAvailable"])
                return Task.FromResult(new ElevationResult(ElevationStatus.Success, 0,
                    available ? "CacheAvailable: true" : "CacheAvailable: false", string.Empty));
            if (command.Length >= 2 && command[0] == "cache" && command[1] == "on")
            {
                starts++;
                available = true;
                return Task.FromResult(new ElevationResult(ElevationStatus.Success, 0, string.Empty, string.Empty));
            }
            if (command is ["cache", "off"])
            {
                available = false;
                return Task.FromResult(new ElevationResult(ElevationStatus.HelperError, null, string.Empty,
                    "O helper já encerrou após expirar o cache."));
            }
            throw new InvalidOperationException("Comando de cache inesperado.");
        }
        var cache = new GsudoCacheController(new GsudoRunnerOptions("fake-helper.exe", "unused"), RunControl);

        Assert.Null(await cache.EnsureStartedAsync(CancellationToken.None));
        Assert.True(await cache.IsAvailableAsync(CancellationToken.None));
        Assert.Contains(commands[0], argument => argument == "00:05:00");
        available = false; // Simula o fim do cache por inatividade.
        Assert.Null(await cache.EnsureStartedAsync(CancellationToken.None));
        Assert.Equal(2, starts);
        await cache.StopAsync();
        Assert.False(await cache.IsAvailableAsync(CancellationToken.None));
        Assert.Contains(commands, command => command.SequenceEqual(new[] { "cache", "off" }));
    }

    [Fact]
    public void HelperIntegrityRejectsModifiedBinary()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] contents = [1, 2, 3, 4];
            File.WriteAllBytes(path, contents);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents));
            GsudoRunner.VerifyHelper(new GsudoRunnerOptions(path, hash));
            File.WriteAllBytes(path, [1, 2, 3, 5]);
            Assert.Throws<InvalidDataException>(() => GsudoRunner.VerifyHelper(new GsudoRunnerOptions(path, hash)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("The operation was canceled by the user.", ElevationStatus.UacCancelled)]
    [InlineData("The user name or password is incorrect.", ElevationStatus.InvalidCredentials)]
    [InlineData("other helper failure", ElevationStatus.HelperError)]
    [InlineData(null, ElevationStatus.HelperError)]
    public void HelperFailureClassificationIsCentralized(string? output, ElevationStatus expected)
        => Assert.Equal(expected, GsudoRunner.ClassifyHelperFailure(output));

    [Fact]
    public async Task RunnerSelectsDirectForAlreadyElevatedContext()
    {
        var direct = new RecordingRunner(ElevationStatus.Success);
        var helper = new RecordingRunner(ElevationStatus.HelperError);
        var runner = new ElevationRunner(new AllowAllElevationPolicy(), direct, helper, isAlreadyElevated: () => true);

        ElevationResult result = await runner.RunAsync(new ElevatedCommand("x.exe", Array.Empty<string>(), ElevationCategory.Other));

        Assert.Equal(ElevationStatus.Success, result.Status);
        Assert.Equal(1, direct.CallCount);
        Assert.Equal(0, helper.CallCount);
    }

    [Fact]
    public async Task RunnerSelectsGsudoWhenCurrentContextIsNotElevated()
    {
        var direct = new RecordingRunner(ElevationStatus.Success);
        var helper = new RecordingRunner(ElevationStatus.HelperError);
        var runner = new ElevationRunner(new AllowAllElevationPolicy(), direct, helper, isAlreadyElevated: () => false);

        ElevationResult result = await runner.RunAsync(new ElevatedCommand("x.exe", Array.Empty<string>(), ElevationCategory.Other));

        Assert.Equal(ElevationStatus.HelperError, result.Status);
        Assert.Equal(0, direct.CallCount);
        Assert.Equal(1, helper.CallCount);
    }

    [Fact]
    public async Task LogOnlyPolicyRecordsUnmatchedPathWithoutBlockingAndOmitsArguments()
    {
        string? log = null;
        var resolver = new TestPathResolver(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tool.exe"] = @"C:\Program FilesExtra\vendor\tool.exe"
        });
        var policy = new ElevationPathPolicy(message => log = message, ElevationPolicyMode.LogOnly,
            resolver, _ => new[] { @"C:\Program Files" });
        var command = new ElevatedCommand("tool.exe", new[] { "--token", "secret-value" }, ElevationCategory.Network);

        ElevationPolicyResult result = await policy.EvaluateAsync(command, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Contains("decision=would-reject", log);
        Assert.Contains("outside-approved-roots", log);
        Assert.Contains(@"C:\Program FilesExtra\vendor\tool.exe", log);
        Assert.DoesNotContain("secret-value", log);
    }

    [Fact]
    public async Task PolicyUsesCategorySpecificRoots()
    {
        string? log = null;
        var resolver = new TestPathResolver(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["netsh.exe"] = @"C:\Windows\System32\netsh.exe"
        });
        var policy = new ElevationPathPolicy(message => log = message, ElevationPolicyMode.LogOnly,
            resolver, category => category == ElevationCategory.Network
                ? new[] { @"C:\Windows\System32" }
                : new[] { @"C:\Program Files" });

        ElevationPolicyResult result = await policy.EvaluateAsync(
            new ElevatedCommand("netsh.exe", new[] { "advfirewall" }, ElevationCategory.Network), CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Contains("decision=allowed", log);
        Assert.Contains("within-approved-root", log);
    }

    [Fact]
    public async Task EnforcingPolicyReturnsRejectedOutsideApprovedRoot()
    {
        var resolver = new TestPathResolver(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tool.exe"] = @"C:\Users\Public\tool.exe"
        });
        var policy = new ElevationPathPolicy(_ => { }, ElevationPolicyMode.Enforce,
            resolver, _ => new[] { @"C:\Program Files" });

        ElevationPolicyResult result = await policy.EvaluateAsync(
            new ElevatedCommand("tool.exe", Array.Empty<string>(), ElevationCategory.Other), CancellationToken.None);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void DirectoryComparisonHonorsSegmentBoundaries()
    {
        Assert.True(ElevationPathPolicy.IsWithinDirectory(@"C:\Program Files\Vendor\tool.exe", @"C:\Program Files"));
        Assert.False(ElevationPathPolicy.IsWithinDirectory(@"C:\Program FilesExtra\tool.exe", @"C:\Program Files"));
    }

    [Fact]
    public void ExecutableResolverFollowsDirectoryJunctionToItsFinalTarget()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision.Elevation.Tests", Guid.NewGuid().ToString("N"));
        string approved = Path.Combine(root, "approved");
        string external = Path.Combine(root, "external");
        string junction = Path.Combine(approved, "redirected");
        string executable = Path.Combine(external, "tool.exe");
        Directory.CreateDirectory(approved);
        Directory.CreateDirectory(external);
        File.WriteAllText(executable, "test");

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(junction);
            start.ArgumentList.Add(external);
            using var process = System.Diagnostics.Process.Start(start);
            Assert.NotNull(process);
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);

            var resolver = new ExecutablePathResolver();
            Assert.True(resolver.TryResolve(Path.Combine(junction, "tool.exe"), out string? resolved, out string reason), reason);
            Assert.Equal(Path.GetFullPath(executable), resolved);
            Assert.False(ElevationPathPolicy.IsWithinDirectory(resolved!, approved));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            if (File.Exists(executable)) File.Delete(executable);
            if (Directory.Exists(approved)) Directory.Delete(approved);
            if (Directory.Exists(external)) Directory.Delete(external);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    private static string[] ParseWindowsArguments(string commandLine)
    {
        IntPtr argv = CommandLineToArgvW(commandLine, out int argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[argc];
            for (int index = 0; index < argc; index++)
                result[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))!;
            return result;
        }
        finally { _ = LocalFree(argv); }
    }

    private static IReadOnlyList<string> SplitLikePinnedGsudo(string commandLine)
    {
        var results = new List<string>();
        int pushed = 0;
        bool insideQuotes = false;
        for (int index = 0; index < commandLine.Length; index++)
        {
            if (commandLine[index] == '"') insideQuotes = !insideQuotes;
            else if (commandLine[index] == ' ' && !insideQuotes)
            {
                if (index > pushed) results.Add(commandLine[pushed..index]);
                pushed = index + 1;
            }
        }
        if (pushed < commandLine.Length) results.Add(commandLine[pushed..]);
        return results;
    }

    private static string UnQuote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
        ? value[1..^1]
        : value;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed class RecordingRunner(ElevationStatus status) : IElevatedRunner
    {
        public int CallCount { get; private set; }

        public Task<ElevationResult> RunAsync(ElevatedCommand command, IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ElevationResult(status, status == ElevationStatus.Success ? 0 : null,
                string.Empty, string.Empty));
        }
    }

    private static string PowerShellPath => Path.Combine(Environment.SystemDirectory,
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private sealed class RecordingProgress(Action? onStarted = null) : IElevationDispatchProgress
    {
        public bool Started { get; private set; }
        public List<string> Lines { get; } = [];
        public void OnStarted() { Started = true; onStarted?.Invoke(); }
        public void Report(string value) => Lines.Add(value);
    }

    private sealed class TestPathResolver(IReadOnlyDictionary<string, string> paths) : IExecutablePathResolver
    {
        public bool TryResolve(string executablePath, [NotNullWhen(true)] out string? resolvedPath, out string reason)
        {
            bool found = paths.TryGetValue(executablePath, out resolvedPath);
            reason = found ? "resolved" : "executable-not-found";
            return found;
        }

        public bool TryResolveDirectory(string directoryPath, [NotNullWhen(true)] out string? resolvedPath)
        {
            resolvedPath = directoryPath;
            return true;
        }
    }
}
