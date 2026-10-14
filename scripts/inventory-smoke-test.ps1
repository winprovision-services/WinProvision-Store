param([string]$PublishedDirectory = 'artifacts/validation/publish/win-x64')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDirectory = (Resolve-Path -LiteralPath $PublishedDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $taskDirectory 'validation-harness.flag'))) { throw 'Use uma publicação criada com publish.ps1 -Validation.' }
if (-not (Get-AppxPackage -Name Microsoft.DesktopAppInstaller)) { throw 'O App Installer deve estar registrado antes do diagnóstico; este script não o instala.' }
$taskResult = Join-Path $taskDirectory 'inventory-probe-results/result.json'
if (Test-Path -LiteralPath $taskResult) { Remove-Item -LiteralPath $taskResult }
$taskProcess = Start-Process -FilePath (Join-Path $taskDirectory 'WinProvision.Store.exe') -ArgumentList '--probe-inventory' -WindowStyle Hidden -PassThru
$taskDeadline = [DateTime]::UtcNow.AddSeconds(90)
while (-not $taskProcess.WaitForExit(10000) -and [DateTime]::UtcNow -lt $taskDeadline) { }
if (-not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id; throw 'O diagnóstico de inventário excedeu 90 segundos.' }
if (-not (Test-Path -LiteralPath $taskResult)) { throw 'O diagnóstico não gerou resultado.' }
$taskReport = Get-Content -LiteralPath $taskResult -Raw | ConvertFrom-Json
if ($taskProcess.ExitCode -ne 0 -or -not $taskReport.success) { throw $taskReport.error }
if (-not $taskReport.nativeIconExtraction -or $taskReport.localIcons -lt 1 -or $taskReport.uninstallMetadata -lt 1) { throw 'O diagnóstico não confirmou ícones locais e dados de desinstalação.' }
Copy-Item -LiteralPath $taskResult -Destination (Join-Path $taskRoot 'artifacts/inventory-validation.json')
Write-Output "Inventário real: $($taskReport.count) aplicativos, $($taskReport.localIcons) ícones locais e $($taskReport.uninstallMetadata) registros de remoção; nenhum aplicativo alterado."
