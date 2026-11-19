namespace WinProvision.Core.Services.Profile;

/// <summary>The single PowerShell self-elevation implementation emitted into standalone user scripts.</summary>
public static class ScriptSelfElevation
{
    public const string PowerShell = """
function Test-WinProvisionElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($identity.User -and $identity.User.Value -eq 'S-1-5-18') { return $true }
        return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally { $identity.Dispose() }
}
function ConvertTo-WinProvisionArgument {
    param([AllowEmptyString()][string]$Value)
    $builder = [Text.StringBuilder]::new(); $slash = [char]92; $quote = [char]34; [void]$builder.Append($quote); $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq $slash) { $slashes++; continue }
        if ($character -eq $quote) { [void]$builder.Append([string]::new($slash, (2 * $slashes) + 1)); [void]$builder.Append($quote) }
        else { [void]$builder.Append([string]::new($slash, $slashes)); [void]$builder.Append($character) }
        $slashes = 0
    }
    [void]$builder.Append([string]::new($slash, 2 * $slashes)); [void]$builder.Append($quote); return $builder.ToString()
}
function Invoke-WinProvisionSelfElevation {
    param([Parameter(Mandatory)][string]$ScriptPath, [string[]]$ArgumentList = @())
    if (Test-WinProvisionElevated) { return 0 }
    $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $native = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if ([IO.File]::Exists($native)) { $powerShell = $native }
    $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$ScriptPath) + $ArgumentList
    $quoted = @($arguments | ForEach-Object { ConvertTo-WinProvisionArgument -Value ([string]$_) })
    $process = $null
    try {
        $process = Start-Process -FilePath $powerShell -ArgumentList ($quoted -join ' ') -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop
        return $process.ExitCode
    } catch {
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception -is [ComponentModel.Win32Exception] -and $exception.NativeErrorCode -eq 1223) { return 1223 }
            $exception = $exception.InnerException
        }
        throw
    } finally { if ($null -ne $process) { $process.Dispose() } }
}
""";
}
