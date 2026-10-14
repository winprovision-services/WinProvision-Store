param([Parameter(Mandatory)][string]$ManifestPath)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'winprovision-services/WinProvision-Store') { throw 'Diagnóstico exclusivo do runner descartável.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'artifacts/winget-diagnostics'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
& winget settings --enable LocalManifestFiles
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível habilitar manifestos locais.' }
$taskManifest = (Resolve-Path -LiteralPath $ManifestPath).Path
$taskArguments = @('install','--manifest',('"' + $taskManifest + '"'),'--silent','--accept-package-agreements','--accept-source-agreements','--disable-interactivity','--verbose-logs')
$taskProcess = Start-Process -FilePath (Get-Command winget).Source -ArgumentList $taskArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskOutput 'stdout.log') -RedirectStandardError (Join-Path $taskOutput 'stderr.log')
$taskSamples = @()
foreach ($taskDelay in @(3000,7000,20000)) {
    $taskExited = $taskProcess.WaitForExit($taskDelay)
    $taskProcesses = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(winget|AppInstaller|AppInstallerCLI|WindowsPackageManagerServer|smartscreen|consent|explorer)\.exe$|^WinProvision.*|^is-.*\.tmp$' } | ForEach-Object {
        $taskManaged = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        @{ name=$_.Name; pid=$_.ProcessId; parent=$_.ParentProcessId; session=$_.SessionId; command=$_.CommandLine; path=$_.ExecutablePath; window=$taskManaged.MainWindowTitle; responding=$taskManaged.Responding }
    })
    $taskSamples += @{ utc=[DateTime]::UtcNow.ToString('o'); exited=$taskExited; processes=$taskProcesses }
    if ($taskExited) { break }
}
$taskPrincipal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
@{ administrator=$taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); samples=$taskSamples } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $taskOutput 'processes.json') -Encoding utf8NoBOM
if (-not $taskProcess.HasExited) { Stop-Process -Id $taskProcess.Id -ErrorAction SilentlyContinue }
foreach ($taskDirectory in @((Join-Path $env:LOCALAPPDATA 'Packages/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe/LocalState/DiagOutputDir'),(Join-Path $env:LOCALAPPDATA 'Microsoft/WinGet/DiagOutputDir'))) {
    if (Test-Path -LiteralPath $taskDirectory) { Get-ChildItem -LiteralPath $taskDirectory -Filter '*.log' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskOutput -Force } }
}
Get-Content (Join-Path $taskOutput 'processes.json')
