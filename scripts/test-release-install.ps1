param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')][string]$AppVersion,
    [Parameter(Mandatory)][string]$InstallerPath
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:GITHUB_REPOSITORY -ne 'winprovision-services/WinProvision-Store') { throw 'Este teste de instalação exige um runner descartável do repositório oficial.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$taskBeforeMachinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$taskBeforeUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$taskProcessPath = $env:Path
$taskFileVersion = ($AppVersion -split '-')[0] + '.0'
$taskReports = @()
try {
    foreach ($taskScope in @('Machine', 'User')) {
        $taskMachine = $taskScope -eq 'Machine'
        $taskHive = if ($taskMachine) { 'HKLM:' } else { 'HKCU:' }
        $taskKey = "$taskHive\Software\Microsoft\Windows\CurrentVersion\Uninstall\{71B03A9D-2C64-4E81-BF6D-3A90257F1C49}_is1"
        if (Test-Path -LiteralPath $taskKey) { throw 'O teste exige uma máquina sem instalação anterior.' }
        # The default install must work without /ALLUSERS or /MERGETASKS, including silent deployment.
        $taskArguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-'
        if (-not $taskMachine) { $taskArguments += ' /CURRENTUSER' }
        if ($taskMachine) {
            $taskName = 'WinProvision-InstallerValidation-' + [Guid]::NewGuid().ToString('N')
            try {
                $taskAction = New-ScheduledTaskAction -Execute $taskInstaller -Argument $taskArguments
                $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
                Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Force | Out-Null
                Start-ScheduledTask -TaskName $taskName
                $taskDeadline = [DateTime]::UtcNow.AddMinutes(3)
                do {
                    Start-Sleep -Milliseconds 500
                    $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName
                    $taskState = (Get-ScheduledTask -TaskName $taskName).State
                    if ([DateTime]::UtcNow -gt $taskDeadline) { throw 'Instalação SYSTEM excedeu o prazo.' }
                } while ($taskInfo.LastRunTime.Year -lt 2000 -or $taskState -eq 'Running')
                if ($taskInfo.LastTaskResult -notin @(0,3010)) { throw "Instalação SYSTEM falhou: $($taskInfo.LastTaskResult)" }
            } finally {
                Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
                Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
            }
        } else {
            $taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList $taskArguments -WindowStyle Hidden -PassThru -Wait
            if ($taskProcess.ExitCode -notin @(0,3010)) { throw "Instalação $taskScope falhou: $($taskProcess.ExitCode)" }
        }
        $taskInstalled = Get-ItemProperty -LiteralPath $taskKey
        if ($taskInstalled.DisplayName -ne 'WinProvision Store' -or $taskInstalled.Publisher -ne 'WinProvision Store' -or $taskInstalled.DisplayVersion -ne $AppVersion) { throw 'Metadados de desinstalação divergentes.' }
        $taskAppDirectory = [IO.Path]::GetFullPath($taskInstalled.InstallLocation).TrimEnd('\')
        $taskBase = if ($taskMachine) { $env:ProgramFiles } else { Join-Path $env:LOCALAPPDATA 'Programs' }
        $taskExpectedDirectory = [IO.Path]::GetFullPath((Join-Path $taskBase 'WinProvision Store')).TrimEnd('\')
        if ($taskAppDirectory -ne $taskExpectedDirectory) { throw "Diretório inesperado: $taskAppDirectory" }
        $taskCliDirectory = Join-Path $taskAppDirectory 'cli'
        foreach ($taskFile in @('WinProvision.Store.exe','cli/winprovision.exe')) {
            $taskInfo = (Get-Item -LiteralPath (Join-Path $taskAppDirectory $taskFile)).VersionInfo
            if ($taskInfo.ProductName.TrimEnd() -ne 'WinProvision Store' -or $taskInfo.ProductVersion.TrimEnd() -ne $taskFileVersion) { throw "Metadados divergentes: $taskFile" }
        }
        for ($taskPass = 0; $taskPass -lt 2; $taskPass++) {
            if ($taskPass -eq 1) {
                $taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList $taskArguments -WindowStyle Hidden -PassThru -Wait
                if ($taskProcess.ExitCode -notin @(0,3010)) { throw 'A reinstalação falhou.' }
            }
            $taskPath = [Environment]::GetEnvironmentVariable('Path', $taskScope)
            if (@($taskPath -split ';' | Where-Object { $_.TrimEnd('\') -eq $taskCliDirectory }).Count -ne 1) { throw "PATH $taskScope ausente ou duplicado." }
            $taskOtherScope = if ($taskMachine) { 'User' } else { 'Machine' }
            $taskOtherBefore = if ($taskMachine) { $taskBeforeUserPath } else { $taskBeforeMachinePath }
            if ([string][Environment]::GetEnvironmentVariable('Path', $taskOtherScope) -ne [string]$taskOtherBefore) { throw 'O instalador alterou o PATH de outro escopo.' }
            # Model a new execução: existing parent processes retain their old environment.
            $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
            if ((Get-Command winprovision.exe -CommandType Application).Source -ne (Join-Path $taskCliDirectory 'winprovision.exe')) { throw 'A CLI não foi resolvida pelo PATH.' }
            & winprovision.exe --help
            if ($LASTEXITCODE -ne 0) { throw 'A CLI instalada não iniciou.' }
        }
        $taskProcess = Start-Process -FilePath (Join-Path $taskAppDirectory 'unins000.exe') -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -PassThru -Wait
        if ($taskProcess.ExitCode -notin @(0,3010)) { throw 'A desinstalação falhou.' }
        if (Test-Path -LiteralPath $taskKey) { throw 'O registro da aplicação permaneceu após a desinstalação.' }
        if ([string][Environment]::GetEnvironmentVariable('Path', 'Machine') -ne [string]$taskBeforeMachinePath -or [string][Environment]::GetEnvironmentVariable('Path', 'User') -ne [string]$taskBeforeUserPath) { throw 'O PATH original não foi restaurado.' }
        if (Test-Path -LiteralPath (Join-Path $taskAppDirectory 'WinProvision.Store.exe')) { throw 'O executável permaneceu após a desinstalação.' }
        $taskReports += @{ scope=$taskScope; installedAsSystem=$taskMachine; silentInstall=$true; metadata=$true; cliStarts=$true; pathAdded=$true; reinstallWithoutDuplicatePath=$true; silentUninstall=$true; pathRestored=$true }
    }
} finally { $env:Path = $taskProcessPath }
$taskReport = Join-Path $taskRoot 'artifacts/winget-install-test.json'
@{ version=$AppVersion; installerSha256=(Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant(); silentInstall=$true; metadata=$true; cliStarts=$true; pathAdded=$true; reinstallWithoutDuplicatePath=$true; silentUninstall=$true; pathRestored=$true; scopes=$taskReports } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskReport -Encoding utf8NoBOM
Write-Output 'Instalação, reinstalação, CLI, PATH e desinstalação conferidos.'
