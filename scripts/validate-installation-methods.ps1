param([Parameter(Mandatory)][string]$PublishedDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'A instalação real exige um runner descartável hospedado pelo GitHub.'
}
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPublished = (Resolve-Path -LiteralPath $PublishedDirectory).Path
$taskProject = Join-Path $taskRoot 'tests/WinProvision.Installation.Validation/WinProvision.Installation.Validation.csproj'
$taskOutput = Join-Path $taskRoot 'artifacts/installation-validation'
dotnet build $taskProject -c Release -o $taskOutput -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Build do teste real falhou.' }
Copy-Item -LiteralPath (Join-Path $taskPublished 'cli') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskPublished 'Microsoft.Management.Deployment.winmd') -Destination $taskOutput -Force
$taskHelper = Join-Path $taskOutput 'Tool/gsudo/WinProvision Elevator.exe'
$taskPidFile = Join-Path $taskOutput 'worker.pid'
$taskReady = Join-Path $taskOutput 'worker.ready'
$taskDotnet = (Get-Command dotnet).Source
$taskDll = Join-Path $taskOutput 'WinProvision.Installation.Validation.dll'
$taskProbe = @'
$ErrorActionPreference = 'Stop'
$env:GITHUB_ACTIONS = 'true'
$env:RUNNER_ENVIRONMENT = 'github-hosted'
[IO.File]::WriteAllText('__PID__', [string]$PID)
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while (-not (Test-Path -LiteralPath '__READY__')) {
    if ([DateTime]::UtcNow -gt $deadline) { throw 'Autorização do teste não recebida.' }
    Start-Sleep -Milliseconds 100
}
& '__DOTNET__' '__DLL__'
exit $LASTEXITCODE
'@
$taskProbe = $taskProbe.Replace('__PID__',$taskPidFile).Replace('__READY__',$taskReady).Replace('__DOTNET__',$taskDotnet).Replace('__DLL__',$taskDll)
$taskEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($taskProbe))
$taskLauncher = Start-Process -FilePath $taskHelper -WindowStyle Hidden -PassThru `
    -ArgumentList "--integrity Medium --direct `"$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe`" -NoProfile -NonInteractive -EncodedCommand $taskEncoded" `
    -RedirectStandardOutput (Join-Path $taskOutput 'stdout.log') -RedirectStandardError (Join-Path $taskOutput 'stderr.log')
$taskWorkerPid = 0
try {
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $taskPidFile)) {
        if ($taskLauncher.HasExited -or [DateTime]::UtcNow -gt $taskDeadline) { throw 'Worker com integridade média não iniciou.' }
        Start-Sleep -Milliseconds 100
    }
    $taskWorkerPid = [int](Get-Content -LiteralPath $taskPidFile -Raw)
    if ($taskWorkerPid -le 0) { throw 'PID de teste inválido.' }
    & $taskHelper cache on --pid $taskWorkerPid --duration 00:15:00
    if ($LASTEXITCODE -ne 0) { throw 'Autorização isolada do teste falhou.' }
    [IO.File]::WriteAllText($taskReady, 'ready')
    $taskDeadline = [DateTime]::UtcNow.AddMinutes(20)
    while (-not $taskLauncher.WaitForExit(10000)) {
        if ([DateTime]::UtcNow -gt $taskDeadline) { throw 'Teste de instalação excedeu o prazo.' }
    }
    if ($taskLauncher.ExitCode -ne 0) { throw "Instalação real falhou: $($taskLauncher.ExitCode)." }
} finally {
    if ($taskWorkerPid -gt 0) { & $taskHelper cache off --pid $taskWorkerPid }
    Get-Content -LiteralPath (Join-Path $taskOutput 'stdout.log') -ErrorAction SilentlyContinue
    Get-Content -LiteralPath (Join-Path $taskOutput 'stderr.log') -ErrorAction SilentlyContinue
    if (-not $taskLauncher.HasExited) { $taskLauncher.Kill() }
    $taskLauncher.Dispose()
}
