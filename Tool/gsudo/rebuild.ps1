param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$taskManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($taskManifest.commit -notmatch '^[a-f0-9]{40}$') { throw 'Commit do helper inválido.' }
$taskWork = Join-Path $taskRoot ('artifacts/gsudo-rebuild-' + [Guid]::NewGuid().ToString('N'))
$taskSource = Join-Path $taskWork 'source'
$taskOutput = Join-Path $taskWork 'win-x64'
New-Item -ItemType Directory -Path $taskWork | Out-Null
git clone --filter=blob:none --no-checkout https://github.com/winprovision-services/gsudo.git $taskSource
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível obter o fork.' }
git -C $taskSource checkout --detach $taskManifest.commit
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível selecionar o commit fixado.' }
Push-Location $taskSource
try {
    dotnet publish src/gsudo/gsudo.csproj -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o $taskOutput
    if ($LASTEXITCODE -ne 0) { throw 'Build do helper falhou.' }
} finally { Pop-Location }
Copy-Item -LiteralPath (Join-Path $taskSource 'LICENSE.txt') -Destination (Join-Path $taskOutput 'LICENSE.txt')
Move-Item -LiteralPath (Join-Path $taskOutput 'WinProvision.Elevator.exe') -Destination (Join-Path $taskOutput 'WinProvision Elevator.exe')
Get-FileHash -LiteralPath (Join-Path $taskOutput 'WinProvision Elevator.exe') -Algorithm SHA256
Write-Output "Helper recompilado para revisão em $taskOutput"
