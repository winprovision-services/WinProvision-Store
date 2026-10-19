param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$Publish, [switch]$NativeAot)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src/WinProvision.Viper.Cli/WinProvision.Viper.Cli.csproj'
if ($Publish -or $NativeAot) {
    $taskOutput = Join-Path $taskRoot 'artifacts/viper/win-x64'
    dotnet publish $taskProject -c $Configuration -r win-x64 --self-contained true "-p:PublishAot=$($NativeAot.ToString().ToLowerInvariant())" -o $taskOutput
} else {
    dotnet build $taskProject -c $Configuration
}
if ($LASTEXITCODE -ne 0) { throw 'A compilação do WinProvision Viper falhou.' }
if ($Publish -or $NativeAot) {
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskOutput 'LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot 'src/WinProvision.Viper/README.md') -Destination (Join-Path $taskOutput 'README.md') -Force
    $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskOutput 'winprovision-viper.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt') -Value "$taskHash  winprovision-viper.exe" -Encoding ascii
    Write-Output "WinProvision Viper disponível em $taskOutput"
}
