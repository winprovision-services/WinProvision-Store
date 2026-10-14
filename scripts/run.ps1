param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskExe = Join-Path $taskRoot "src\WinProvision.Store\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\WinProvision.Store.exe"
Start-Process -FilePath $taskExe -WindowStyle Hidden
