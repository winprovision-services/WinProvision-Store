param(
    [string]$Executable = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish\win-x64\WinProvision.Store.exe'),
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\memory.json'),
    [int]$WarmupSeconds = 10,
    [int]$SampleCount = 15
)
$ErrorActionPreference = 'Stop'
$taskProcess = Start-Process -FilePath (Resolve-Path -LiteralPath $Executable).Path -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds $WarmupSeconds
    $taskSamples = for ($taskSample = 0; $taskSample -lt $SampleCount; $taskSample++) {
        $taskProcess.Refresh()
        if ($taskProcess.HasExited) { throw 'O aplicativo encerrou durante a medição.' }
        [pscustomobject]@{ privateBytes = $taskProcess.PrivateMemorySize64; workingSetBytes = $taskProcess.WorkingSet64 }
        Start-Sleep -Seconds 1
    }
    [ordered]@{
        executable = $taskProcess.StartInfo.FileName
        scenario = 'Descobrir com catálogo real; 10 segundos de aquecimento; amostras a cada segundo'
        sampleCount = $taskSamples.Count
        averagePrivateMiB = [Math]::Round(($taskSamples.privateBytes | Measure-Object -Average).Average / 1MB, 2)
        averageWorkingSetMiB = [Math]::Round(($taskSamples.workingSetBytes | Measure-Object -Average).Average / 1MB, 2)
        samples = $taskSamples
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Output -Encoding utf8
    Get-Content -LiteralPath $Output
} finally {
    if (-not $taskProcess.HasExited) {
        $null = $taskProcess.CloseMainWindow()
        if (-not $taskProcess.WaitForExit(10000)) { Stop-Process -Id $taskProcess.Id }
    }
}
