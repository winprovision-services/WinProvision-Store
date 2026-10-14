param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')][string]$AppVersion)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCommit = (& git -C $taskRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or $taskCommit -notmatch '^[a-f0-9]{40,64}$') { throw 'Crie um commit revisado antes de produzir um candidato rastreável.' }
$taskChanges = @(& git -C $taskRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $taskChanges.Count -gt 0) { throw 'O candidato exige uma árvore de trabalho limpa e revisada.' }

& (Join-Path $PSScriptRoot 'publish.ps1') -AppVersion $AppVersion -SourceRevisionId $taskCommit
& (Join-Path $PSScriptRoot 'package.ps1') -AppVersion $AppVersion -SkipPublish

$taskPublished = Join-Path $taskRoot 'artifacts\publish\win-x64'
$taskInstaller = Join-Path $taskRoot 'artifacts\installer\WinProvision.Store-Setup.exe'
$taskExe = Join-Path $taskPublished 'WinProvision.Store.exe'
if (Test-Path (Join-Path $taskPublished 'validation-harness.flag')) { throw 'Um candidato não pode conter o harness de testes.' }
$taskExpectedVersion = ($AppVersion -split '-')[0] + '.0'
foreach ($taskBinary in @($taskExe,(Join-Path $taskPublished 'cli\winprovision.exe'),$taskInstaller)) {
    $taskInfo = (Get-Item -LiteralPath $taskBinary).VersionInfo
    if ($taskInfo.ProductName -ne 'WinProvision Store' -or $taskInfo.ProductVersion -ne $taskExpectedVersion) {
        throw "Metadados de produto/versão divergentes: $taskBinary"
    }
}
$taskDirectory = Join-Path $taskRoot ("artifacts\release-candidate\$AppVersion-" + $taskCommit.Substring(0,12) + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
Copy-Item -LiteralPath $taskInstaller -Destination $taskDirectory
Copy-Item -LiteralPath "$taskInstaller.sha256" -Destination $taskDirectory
$taskArchive = Join-Path $taskDirectory 'WinProvision.Store-win-x64.zip'
Compress-Archive -Path (Join-Path $taskPublished '*') -DestinationPath $taskArchive
$taskFiles = @(Get-ChildItem -LiteralPath $taskDirectory -File | ForEach-Object {
    @{ name = $_.Name; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$taskManifest = @{
    product = 'WinProvision Store'; version = $AppVersion; sourceCommit = $taskCommit
    sdk = (& dotnet --version); runnerImage = $env:ImageVersion; githubRunId = $env:GITHUB_RUN_ID
    githubRepository = $env:GITHUB_REPOSITORY; signingStatus = 'unsigned-candidate'; files = $taskFiles
    note = 'Candidato revisável. Não é uma release assinada. Recalcule hashes após qualquer assinatura.'
}
$taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskDirectory 'candidate.json') -Encoding utf8
Write-Output "Candidato sem assinatura: $taskDirectory"
