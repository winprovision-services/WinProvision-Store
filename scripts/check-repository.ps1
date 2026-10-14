$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskRequired = @('README.md','LICENSE','CONTRIBUTING.md','CODE_OF_CONDUCT.md','SECURITY.md',
    'PRIVACY.md','THIRD-PARTY-NOTICES.md','CHANGELOG.md','global.json',
    '.github/CODEOWNERS','.github/workflows/ci.yml','.github/workflows/release-candidate.yml',
    'docs/REPOSITORY-AUDIT.md','src/WinProvision.Core/packages.lock.json','src/WinProvision.Core/packages.aot.lock.json',
    'src/WinProvision.Store/packages.lock.json','src/WinProvision.Store/packages.aot.lock.json',
    'src/WinProvision.Cli/packages.lock.json','src/WinProvision.Cli/packages.aot.lock.json','tests/WinProvision.Core.Tests/packages.lock.json')
$taskErrors = [Collections.Generic.List[string]]::new()
foreach ($taskName in $taskRequired) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskRoot $taskName) -PathType Leaf)) { $taskErrors.Add("Arquivo obrigatório ausente: $taskName") }
}
$taskFiles = @(& git -C $taskRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível inventariar o repositório com Git.' }
$taskSecrets = '(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16}|sk-(?:proj-)?[A-Za-z0-9_-]{32,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)'
$taskMarkdownLinks = '\[[^\]]*\]\((?<target><[^>]+>|[^)\s]+)\)'
foreach ($taskFile in $taskFiles) {
    $taskPath = Join-Path $taskRoot $taskFile
    if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { continue }
    if ($taskFile -match '(^|/)(bin|obj|\.vs)/' -or $taskFile -match '^artifacts/') {
        $taskErrors.Add("Artefato/cache incluído no inventário versionável: $taskFile")
        continue
    }
    $taskExtension = [IO.Path]::GetExtension($taskPath)
    if ($taskExtension -notin @('.md','.cs','.xaml','.ps1','.yml','.json','.txt','.props','.csproj','.iss')) { continue }
    $taskText = [IO.File]::ReadAllText($taskPath)
    if ([regex]::IsMatch($taskText,$taskSecrets)) { $taskErrors.Add("Possível segredo em $taskFile (valor ocultado)") }
    if ($taskExtension -eq '.yml' -and $taskFile.StartsWith('.github/workflows/')) {
        foreach ($taskAction in [regex]::Matches($taskText,'(?m)^\s*-?\s*uses:\s*(?<action>[^\s#]+)')) {
            $taskValue = $taskAction.Groups['action'].Value
            if (-not $taskValue.StartsWith('./') -and $taskValue -notmatch '@[0-9a-f]{40}$') {
                $taskErrors.Add("Ação externa sem commit fixado: $taskValue em $taskFile")
            }
        }
        if ($taskText -match '(?m)^\s*pull_request_target\s*:') { $taskErrors.Add("Revisar gatilho privilegiado pull_request_target em $taskFile") }
    }
    if ($taskExtension -ne '.md') { continue }
    foreach ($taskLink in [regex]::Matches($taskText,$taskMarkdownLinks)) {
        $taskTarget = $taskLink.Groups['target'].Value.Trim('<','>')
        if ($taskTarget -match '^(https?://|mailto:|codex:|#)') { continue }
        $taskTarget = [Uri]::UnescapeDataString(($taskTarget -split '#',2)[0])
        if ($taskTarget -and -not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $taskPath) $taskTarget))) {
            $taskErrors.Add("Link local quebrado em ${taskFile}: $taskTarget")
        }
    }
}
if ($taskErrors.Count -gt 0) { throw ($taskErrors -join [Environment]::NewLine) }
Write-Output "Estrutura, links locais, ações fixadas e padrões comuns de segredos conferidos ($($taskFiles.Count) arquivos)."
