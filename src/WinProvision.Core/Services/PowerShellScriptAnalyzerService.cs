using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;

namespace WinProvision.Core.Services;

public sealed record PowerShellScriptDiagnostic(
    [property: JsonPropertyName("ruleName")] string RuleName,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("column")] int? Column);

/// <summary>Valida scripts exportados usando uma versão fixa do PSScriptAnalyzer da Gallery.</summary>
[SupportedOSPlatform("windows")]
public sealed class PowerShellScriptAnalyzerService(PowerShell7Bootstrapper bootstrapper)
{
    public const string AnalyzerVersion = "1.25.0";
    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _analysisGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string ModuleRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "PowerShell", "Modules");

    /// <summary>
    /// Instala a versão fixada no diretório privado do app quando necessário, valida os
    /// arquivos assinados pela Gallery e retorna os diagnósticos sem executar o script.
    /// </summary>
    public async Task<IReadOnlyList<PowerShellScriptDiagnostic>> AnalyzeAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AnalyzeCoreAsync(script, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    private async Task<IReadOnlyList<PowerShellScriptDiagnostic>> AnalyzeCoreAsync(string script, CancellationToken cancellationToken)
    {
        PowerShell7Runtime runtime = await bootstrapper
            .EnsureAvailableAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(ModuleRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildAnalyzerCommand())));

        string existingModulePath = startInfo.Environment.TryGetValue("PSModulePath", out string? inherited)
            ? inherited ?? string.Empty
            : Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;
        startInfo.Environment["PSModulePath"] = string.IsNullOrWhiteSpace(existingModulePath)
            ? ModuleRoot
            : ModuleRoot + Path.PathSeparator + existingModulePath;

        using var process = new Process { StartInfo = startInfo };
        Task<string[]>? readers = null;
        bool started = false;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Não foi possível iniciar o PowerShell 7 para validar o script.");

            started = true;
            readers = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            await process.StandardInput.WriteAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AnalysisTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            string[] captured = await readers.ConfigureAwait(false);
            string stdout = captured[0];
            string stderr = captured[1];
            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                throw new InvalidOperationException(
                    $"PSScriptAnalyzer {AnalyzerVersion} não conseguiu validar o script. {detail}".Trim());
            }

            string json = stdout.Trim();
            if (string.IsNullOrEmpty(json))
                return [];

            return WinProvision.Core.Services.AotJson.Deserialize<List<PowerShellScriptDiagnostic>>(json, JsonOptions) ?? [];
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"A validação com PSScriptAnalyzer excedeu {AnalysisTimeout.TotalMinutes:0} minutos.", exception);
        }
        finally
        {
            if (started)
            {
                TryKill(process);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            if (readers is not null) await readers.ConfigureAwait(false);
        }
    }

    private static string BuildAnalyzerCommand()
    {
        string modulePath = PowerShellLiteral(Path.Combine(ModuleRoot, "PSScriptAnalyzer", AnalyzerVersion, "PSScriptAnalyzer.psd1"));
        string moduleRoot = PowerShellLiteral(ModuleRoot);
        return $$"""
            $ErrorActionPreference = 'Stop'
            $moduleManifest = {{modulePath}}
            if (-not (Test-Path -LiteralPath $moduleManifest -PathType Leaf)) {
                if (-not (Get-Command Save-PSResource -ErrorAction SilentlyContinue)) {
                    throw 'PSResourceGet não está disponível para obter o PSScriptAnalyzer.'
                }

                Save-PSResource -Name PSScriptAnalyzer -Version '{{AnalyzerVersion}}' -Repository PSGallery -Path {{moduleRoot}} -AuthenticodeCheck -TrustRepository -Quiet -ErrorAction Stop | Out-Null
            }

            Import-Module -Name $moduleManifest -RequiredVersion '{{AnalyzerVersion}}' -Force -ErrorAction Stop
            $scriptDefinition = [Console]::In.ReadToEnd()
            $diagnostics = @(Invoke-ScriptAnalyzer -ScriptDefinition $scriptDefinition -ErrorAction Stop |
                ForEach-Object {
                    [pscustomobject]@{
                        RuleName = [string]$_.RuleName
                        Severity = [string]$_.Severity
                        Message = [string]$_.Message
                        Line = if ($null -ne $_.Line) { [int]$_.Line } else { $null }
                        Column = if ($null -ne $_.Column) { [int]$_.Column } else { $null }
                    }
                })
            ConvertTo-Json -InputObject $diagnostics -Depth 4 -Compress
            """;
    }

    private static string PowerShellLiteral(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CoreLog.Warning(exception, "ProcessCleanup");
        }
    }
}
