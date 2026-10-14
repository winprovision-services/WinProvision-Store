param([string]$PublishedDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\validation\publish\win-x64'))
$ErrorActionPreference = 'Stop'
if (-not (Get-AppxPackage Microsoft.DesktopAppInstaller)) { throw 'Registre o App Installer antes do diagnóstico COM; este teste não o instala.' }
$taskBin = (Resolve-Path -LiteralPath $PublishedDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $taskBin 'validation-harness.flag'))) { throw 'Use uma publicação criada com publish.ps1 -Validation.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskBin 'Microsoft.Management.Deployment.winmd'))) { throw 'Metadados COM do WinGet ausentes.' }
$taskResultFile = Join-Path $taskBin 'com-probe-results\result.json'
if (Test-Path -LiteralPath $taskResultFile) { Remove-Item -LiteralPath $taskResultFile }
$taskProcess = Start-Process -FilePath (Join-Path $taskBin 'WinProvision.Store.exe') -ArgumentList '--probe-com' -WindowStyle Hidden -PassThru
if (-not $taskProcess.WaitForExit(55000)) { Stop-Process -Id $taskProcess.Id; throw 'O diagnóstico COM excedeu 55 segundos.' }
if (-not (Test-Path -LiteralPath $taskResultFile)) { throw "O diagnóstico não gerou resultado. ExitCode: $($taskProcess.ExitCode)" }
$taskResult = Get-Content -LiteralPath $taskResultFile -Raw | ConvertFrom-Json
if ($taskProcess.ExitCode -ne 0 -or -not $taskResult.success) { throw $taskResult.error }
if ($taskResult.mode -ne 'ComOnly' -or $taskResult.cliFallbackAllowed) { throw 'O diagnóstico não comprovou a rota COM exclusiva.' }
$taskArtifacts = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\com-smoke'
New-Item -ItemType Directory -Path $taskArtifacts -Force | Out-Null
Copy-Item -LiteralPath $taskResultFile -Destination (Join-Path $taskArtifacts 'result.json')
Write-Output "COM $($taskResult.version): $($taskResult.activationClasses) classes ativadas; $($taskResult.updateCount) atualizações; sem fallback CLI."
