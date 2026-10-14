param([switch]$SkipBuild, [string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
if (-not $SkipBuild -and -not $PublishedDirectory) { & (Join-Path $PSScriptRoot 'build.ps1') -Validation }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskBin = Join-Path $taskRoot 'artifacts\validation\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64'
if ($PublishedDirectory) { $taskBin = (Resolve-Path -LiteralPath $PublishedDirectory).Path }
if (-not (Test-Path -LiteralPath (Join-Path $taskBin 'validation-harness.flag'))) { throw 'Este executável não contém testes. Compile com build.ps1 -Validation ou publique com publish.ps1 -Validation.' }
$taskResults = Join-Path $taskBin 'smoke-results'
$taskResultFile = Join-Path $taskResults 'result.json'
if (Test-Path -LiteralPath $taskResultFile) { Remove-Item -LiteralPath $taskResultFile }
$taskProcess = Start-Process -FilePath (Join-Path $taskBin 'WinProvision.Store.exe') -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
$taskDeadline = [DateTime]::UtcNow.AddSeconds(90)
while (-not $taskProcess.WaitForExit(10000) -and [DateTime]::UtcNow -lt $taskDeadline) { }
if (-not $taskProcess.HasExited) {
    Stop-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue
    throw 'O teste não terminou em 90 segundos. A instância de teste foi encerrada; inspecione os resultados.'
}
if ($taskProcess.ExitCode -ne 0) { throw "A aplicação encerrou com falha. ExitCode: $($taskProcess.ExitCode)" }
if (-not (Test-Path -LiteralPath $taskResultFile)) { throw "A aplicação não gerou resultado. ExitCode: $($taskProcess.ExitCode)" }
$taskResult = Get-Content -LiteralPath $taskResultFile -Raw | ConvertFrom-Json
$taskArtifacts = Join-Path $taskRoot 'artifacts\validation\smoke'
if ($PublishedDirectory) { $taskArtifacts = Join-Path $taskRoot 'artifacts\validation\smoke-published' }
New-Item -ItemType Directory -Force -Path $taskArtifacts | Out-Null
Get-ChildItem -LiteralPath $taskResults -File | Where-Object { $_.Extension -eq '.png' -or $_.Name -eq 'result.json' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskArtifacts }
if (-not $taskResult.success) { throw $taskResult.error }
Write-Output "$($taskResult.checks.Count) verificações concluídas. Capturas em $taskArtifacts"
