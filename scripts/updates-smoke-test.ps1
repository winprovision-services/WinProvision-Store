param([switch]$SkipBuild, [ValidateSet('auto','com','cli')][string]$Mode = 'auto', [int]$MinimumExpectedCount = 0,
    [ValidateSet('Automatic','ComApi','WinProvisionApi','WingetCli')][string]$UpdateMethod, [string]$VerifyInstaller)
$ErrorActionPreference = 'Stop'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Validation }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskBin = Join-Path $taskRoot 'artifacts\validation\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64'
if (-not (Test-Path -LiteralPath (Join-Path $taskBin 'validation-harness.flag'))) { throw 'Compile com build.ps1 -Validation antes de executar.' }
if ($UpdateMethod -ne 'WinProvisionApi' -and -not (Get-AppxPackage -Name Microsoft.DesktopAppInstaller)) { throw 'O App Installer deve estar registrado antes da consulta real WinGet.' }
$taskResults = Join-Path $taskBin 'updates-probe-results'
$taskResultFile = Join-Path $taskResults 'result.json'
if (Test-Path -LiteralPath $taskResultFile) { Remove-Item -LiteralPath $taskResultFile }
$taskArguments = @('--probe-updates','--capture-updates',"/winget-mode=$Mode")
if ($UpdateMethod) { $taskArguments += "--probe-update-method=$UpdateMethod" }
if ($VerifyInstaller) { $taskArguments += "--probe-verify-installer=$VerifyInstaller" }
$taskProcess = Start-Process -FilePath (Join-Path $taskBin 'WinProvision.Store.exe') -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
$taskDeadline = [DateTime]::UtcNow.AddSeconds($(if ($VerifyInstaller) { 280 } else { 160 }))
while (-not $taskProcess.WaitForExit(10000) -and [DateTime]::UtcNow -lt $taskDeadline) { }
if (-not $taskProcess.HasExited) {
    Stop-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue
    throw 'A consulta real de atualizações excedeu o prazo; a instância de teste foi encerrada.'
}
if (-not (Test-Path -LiteralPath $taskResultFile)) { throw "A consulta não gerou resultado. ExitCode: $($taskProcess.ExitCode)" }
$taskReport = Get-Content -LiteralPath $taskResultFile -Raw | ConvertFrom-Json
if ($taskProcess.ExitCode -ne 0 -or -not $taskReport.success) { throw $taskReport.error }
if ($taskReport.loading -or $taskReport.count -lt $MinimumExpectedCount) { throw 'A interface continuou carregando ou não apresentou os pacotes esperados.' }
$taskSuffix = if ($UpdateMethod) { "$Mode-$UpdateMethod" } else { $Mode }
$taskArtifacts = Join-Path $taskRoot "artifacts\validation\updates-real-$taskSuffix"
New-Item -ItemType Directory -Force -Path $taskArtifacts | Out-Null
Copy-Item -LiteralPath $taskResultFile -Destination $taskArtifacts
Copy-Item -LiteralPath (Join-Path $taskResults 'updates-real.png') -Destination $taskArtifacts
Write-Output "Atualizações reais ($Mode): $($taskReport.count) pacotes; primeira consulta $([Math]::Round($taskReport.firstScanSeconds, 2)) s; recarga $([Math]::Round($taskReport.reloadSeconds, 2)) s. Evidências: $taskArtifacts"
