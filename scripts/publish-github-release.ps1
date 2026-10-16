param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')][string]$AppVersion)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_REPOSITORY -ne 'winprovision-services/WinProvision-Store' -or $env:GITHUB_REF -ne 'refs/heads/main') { throw 'Publique somente pela main do repositório oficial.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDirectory = Join-Path $taskRoot 'artifacts/release'
$taskMetadata = Get-Content -LiteralPath (Join-Path $taskDirectory 'release.json') -Raw | ConvertFrom-Json
if ($taskMetadata.version -ne $AppVersion -or $taskMetadata.sourceCommit -ne $env:GITHUB_SHA) { throw 'Metadados de origem divergentes.' }
$taskNames = @('WinProvision.Store-Setup.exe','WinProvision.Store-Setup.exe.sha256','WinProvision.Store-win-x64.zip','release.json','SHA256SUMS')
foreach ($taskName in $taskNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskDirectory $taskName) -PathType Leaf)) { throw "Arquivo ausente: $taskName" }
}
foreach ($taskName in $taskNames[0..2]) {
    $taskExpected = @($taskMetadata.files | Where-Object name -eq $taskName)
    if ($taskExpected.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $taskDirectory $taskName) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpected[0].sha256) { throw "Hash divergente: $taskName" }
}
$taskTag = "v$AppVersion"
$taskNotes = Join-Path $env:RUNNER_TEMP 'release-notes.md'
$taskChangelog = Get-Content -LiteralPath (Join-Path $taskRoot 'CHANGELOG.md') -Raw
$taskVersionPattern = [regex]::Escape($AppVersion)
$taskSection = [regex]::Match($taskChangelog, "(?ms)^##\s+$taskVersionPattern(?:\s[^\r\n]*)?\r?\n(?<notes>.*?)(?=^##\s|\z)")
$taskChanges = if ($taskSection.Success) { $taskSection.Groups['notes'].Value.Trim() } else { '' }
@"
WinProvision Store $AppVersion para Windows x64.

$taskChanges

- Instalador: ``WinProvision.Store-Setup.exe``.
- Versão portátil: ``WinProvision.Store-win-x64.zip``.
- Integridade: ``SHA256SUMS`` e ``release.json``.

Commit de origem: $env:GITHUB_SHA
"@ | Set-Content -LiteralPath $taskNotes -Encoding utf8NoBOM
& gh release create $taskTag --repo $env:GITHUB_REPOSITORY --target $env:GITHUB_SHA --draft --title "WinProvision Store $AppVersion" --notes-file $taskNotes
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível criar o rascunho. Uma release existente nunca é sobrescrita.' }
$taskAssets = @($taskNames | ForEach-Object { Join-Path $taskDirectory $_ })
& gh release upload $taskTag --repo $env:GITHUB_REPOSITORY @taskAssets
if ($LASTEXITCODE -ne 0) { throw 'Upload falhou; a release permanece em rascunho.' }
$taskDownloaded = Join-Path $env:RUNNER_TEMP ('release-verify-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskDownloaded | Out-Null
& gh release download $taskTag --repo $env:GITHUB_REPOSITORY --dir $taskDownloaded
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível conferir os anexos; a release permanece em rascunho.' }
foreach ($taskName in $taskNames) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskDirectory $taskName) -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $taskDownloaded $taskName) -Algorithm SHA256).Hash) { throw "Upload divergente: $taskName" }
}
$taskPrerelease = $AppVersion.Contains('-').ToString().ToLowerInvariant()
$taskLatest = (-not $AppVersion.Contains('-')).ToString().ToLowerInvariant()
& gh release edit $taskTag --repo $env:GITHUB_REPOSITORY --draft=false "--prerelease=$taskPrerelease" "--latest=$taskLatest"
if ($LASTEXITCODE -ne 0) { throw 'A publicação falhou.' }
"Release publicada: https://github.com/$env:GITHUB_REPOSITORY/releases/tag/$taskTag" | Tee-Object -FilePath $env:GITHUB_STEP_SUMMARY -Append
