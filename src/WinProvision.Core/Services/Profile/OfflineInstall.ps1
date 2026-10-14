#Requires -Version 5.1
[CmdletBinding()]
param([switch]$VerifyOnly, [switch]$SetupComplete, [switch]$ContinueInstallers,
    [string]$StateDirectory, [string]$PolicyPath, [string]$CatalogPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$results = [Collections.Generic.List[object]]::new()
$exitCode = 0
$rebootRequired = $false
$officeReady = $false
$officeScript = $null

function Get-NativePath {
    param([string]$Path)
    if ($Path.StartsWith('\\?\')) { return $Path }
    if ($Path.StartsWith('\\')) { return '\\?\UNC\' + $Path.Substring(2) }
    return '\\?\' + $Path
}

function Get-PayloadPath {
    param([Parameter(Mandatory)][string]$RelativePath)
    $parts = $RelativePath.Replace('\', '/').Split('/')
    foreach ($part in $parts) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -in @('.', '..') -or
            $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $part.Contains(':')) {
            throw 'Caminho de instalador inválido no manifesto.'
        }
    }
    $root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    $path = [IO.Path]::GetFullPath([IO.Path]::Combine($root, ($parts -join '\')))
    if (-not $path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'O caminho do instalador sai da pasta da coleção.'
    }
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'A coleção contém um link de arquivo ou diretório.'
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $path
}

function Get-PayloadSha256 {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Invoke-OfficeBootstrap {
    param([string]$Path, [string]$Arguments)
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $native = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if ([IO.File]::Exists($native)) { $powershell = $native }
    $process = Start-Process -FilePath $powershell -ArgumentList ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $Path + '" ' + $Arguments) -WindowStyle Hidden -PassThru
    try { $null = $process.Handle; $process.WaitForExit(); return $process.ExitCode }
    finally { $process.Dispose() }
}

function Get-VerifiedOfficeScript {
    param($Module)
    $required = @('__OFFICE_SETUP__', '__OFFICE_CONFIGURATION__', '__OFFICE_SCRIPT__')
    $hasPayload = $null -ne $Module.PSObject.Properties['IncludesPayload'] -and $Module.IncludesPayload
    $count = @($Module.Files).Count
    if ($Module.Schema -ne 1 -or $count -lt 3 -or $count -gt 20004 -or @($Module.Files.Path | Select-Object -Unique).Count -ne $count) { throw 'Inventário Office inválido.' }
    if (@($required | Where-Object { $_ -notin $Module.Files.Path }).Count -ne 0) { throw 'Módulo Office incompleto.' }
    if (-not $hasPayload -and $count -ne 3) { throw 'Inventário Office online inválido.' }
    if ($hasPayload -and '__OFFICE_PAYLOAD_INDEX__' -notin $Module.Files.Path) { throw 'Inventário offline Office ausente.' }
    foreach ($file in $Module.Files) {
        $allowed = $file.Path -in $required -or ($hasPayload -and ($file.Path -eq '__OFFICE_PAYLOAD_INDEX__' -or $file.Path.StartsWith('__OFFICE_PAYLOAD_PREFIX__', [StringComparison]::OrdinalIgnoreCase)))
        if (-not $allowed -or $file.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Arquivo Office inválido.' }
        if ((Get-PayloadSha256 (Get-PayloadPath $file.Path)) -ne $file.Sha256) { throw 'SHA256 divergente no módulo Office.' }
    }
    $path = Get-PayloadPath '__OFFICE_SCRIPT__'
    if ((Invoke-OfficeBootstrap -Path $path -Arguments '-VerifyOnly') -ne 0) { throw 'O módulo Office falhou na validação.' }
    return $path
}

try {
    $manifestCandidates = @(@('__INSTALLERS_MANIFEST__', '__OFFICE_MANIFEST__', 'colecao.json') | Where-Object { [IO.File]::Exists((Join-Path $PSScriptRoot $_)) })
    if ($manifestCandidates.Count -ne 1) { throw 'O ZIP deve conter um único manifesto da coleção.' }
    $manifestRelative = $manifestCandidates[0]
    $manifest = Get-Content -LiteralPath (Get-PayloadPath $manifestRelative) -Raw -Encoding UTF8 | ConvertFrom-Json
    $hasOffice = $null -ne $manifest.PSObject.Properties['Office'] -and $null -ne $manifest.Office
    if ($manifest.Schema -ne 1 -or (@($manifest.Packages).Count -eq 0 -and -not $hasOffice)) {
        throw 'Manifesto da coleção inválido ou vazio.'
    }
    if ($hasOffice) { $officeScript = Get-VerifiedOfficeScript -Module $manifest.Office }
    $osArchitecture = $env:PROCESSOR_ARCHITEW6432
    if (-not $osArchitecture) { $osArchitecture = $env:PROCESSOR_ARCHITECTURE }
    foreach ($package in $manifest.Packages) {
        if ($SetupComplete -and $package.Scope -ne 'machine') {
            throw ('SetupComplete exige escopo machine explícito: {0}.' -f $package.Name)
        }
        $compatible = switch ($osArchitecture.ToUpperInvariant()) {
            'ARM64' { @('arm64', 'x64', 'x86', 'neutral') }
            'AMD64' { @('x64', 'x86', 'neutral') }
            default { @('x86', 'neutral') }
        }
        if ($package.Architecture -notin $compatible) {
            throw ('Arquitetura incompatível para {0}: {1}.' -f $package.Name, $package.Architecture)
        }
        if ($package.InstallerType -notin @('msi', 'wix', 'wixmsi', 'burn', 'nullsoft', 'inno', 'exe') -or
            [string]::IsNullOrWhiteSpace($package.SilentArguments) -or $package.SilentArguments -match '[\x00-\x1F<>]') {
            throw ('Parâmetros de instalação inválidos para {0}.' -f $package.Name)
        }
        $installerIsListed = $false
        foreach ($file in $package.Files) {
            if ($file.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'SHA-256 inválido no manifesto.' }
            $path = Get-PayloadPath -RelativePath $file.Path
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw ('Arquivo ausente: {0}.' -f $file.Path)
            }
            $actualHash = Get-PayloadSha256 -Path $path
            if ($actualHash -ne $file.Sha256) { throw ('SHA-256 divergente: {0}.' -f $file.Path) }
            if ($file.Path -ceq $package.InstallerPath) { $installerIsListed = $true }
        }
        if (-not $installerIsListed) { throw 'O instalador não está entre os arquivos verificados.' }
    }
    $officeReady = $hasOffice
    if ($VerifyOnly) {
        Write-Output 'Todos os arquivos foram verificados. Nenhum instalador foi executado.'
        exit 0
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally { $identity.Dispose() }
    if (@($manifest.Packages | Where-Object { $_.RequiresElevation }).Count -gt 0 -and -not $isAdministrator) {
        throw 'Abra o PowerShell como administrador para instalar esta coleção.'
    }
    $policy = $null
    if ($ContinueInstallers) {
        if (-not $SetupComplete -or -not $StateDirectory -or -not $PolicyPath -or -not $CatalogPath) { throw 'Retomada exige contexto SetupComplete, estado e manifesto verificados.' }
        $deployment = Get-Content -LiteralPath $PolicyPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $catalog = Get-Content -LiteralPath $CatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($deployment.Schema -ne 2 -or $catalog.Schema -ne 1) { throw 'Manifesto ou tabela OEM não suportados.' }
        $policy = $deployment.Retry
        [IO.Directory]::CreateDirectory($StateDirectory) | Out-Null
    }
    $collectionHash = Get-PayloadSha256 -Path (Get-PayloadPath -RelativePath $manifestRelative)
    $packageIndex = 0
    foreach ($package in $manifest.Packages) {
        $packageIndex++
        $marker = $null
        if ($ContinueInstallers) {
            $marker = Get-NativePath ([IO.Path]::Combine($StateDirectory, ($collectionHash + '-' + $packageIndex.ToString('D3') + '.json')))
            if ([IO.File]::Exists($marker)) {
                $prior = [IO.File]::ReadAllText($marker, [Text.Encoding]::UTF8) | ConvertFrom-Json
                if ($prior.Success -and $prior.Id -eq $package.Id) { $results.Add($prior); if ($prior.ExitCode -eq 3010) { $rebootRequired = $true }; Write-Output ('Já instalado: ' + $package.Name); continue }
            }
        }
        $code = 1; $success = $false; $errorMessage = ''
        try {
        Write-Output ('[{0}] Instalando {1} ({2})...' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $package.Name, $package.Version)
        $path = Get-PayloadPath -RelativePath $package.InstallerPath
        # Não usa Invoke-Expression, cmd /c ou expansão de código para os parâmetros do manifesto.
        $executable = $path
        $argumentLine = [string]$package.SilentArguments
        if ($package.InstallerType -in @('msi', 'wix', 'wixmsi')) {
            $executable = Join-Path $env:SystemRoot 'System32\msiexec.exe'
            $argumentLine = '/i "' + $path + '" ' + $argumentLine
            if ($SetupComplete) { $argumentLine += ' /norestart' }
        }
        $attempts = 1
        if ($null -ne $policy) { $attempts = $policy.Attempts }
        for ($attempt = 1; $attempt -le $attempts; $attempt++) {
            $process = Start-Process -FilePath $executable -ArgumentList $argumentLine -WorkingDirectory ([IO.Path]::GetDirectoryName($path)) -WindowStyle Hidden -PassThru
            try { $null = $process.Handle; $process.WaitForExit(); $code = $process.ExitCode }
            finally { $process.Dispose() }
            if ($attempt -eq $attempts -or $code -notin @($policy.ExitCodes)) { break }
            $delay = [Math]::Min($policy.MaximumDelaySeconds, $policy.InitialDelaySeconds * [Math]::Pow(2, $attempt - 1))
            Write-Output ('Nova tentativa: {0}, código {1}, espera {2}s.' -f $package.Name, $code, $delay)
            Start-Sleep -Seconds $delay
        }
        if ($SetupComplete -and $code -eq 1641) { throw 'O instalador iniciou uma reinicialização, incompatível com SetupComplete. Revise seus parâmetros.' }
        $success = $code -in @(0, 3010, 1641) -or $code -in @($package.SuccessCodes)
        if (-not $success) {
            if (-not $ContinueInstallers) { $results.Add([pscustomobject]@{ Id = $package.Id; Name = $package.Name; ExitCode = $code; Success = $false }) }
            throw ('A instalação de {0} falhou (código {1}).' -f $package.Name, $code)
        }
        if ($code -in @(3010, 1641)) { $rebootRequired = $true }
        }
        catch {
            if (-not $ContinueInstallers) { throw }
            $success = $false; $errorMessage = $_.Exception.Message; $exitCode = 1
            Write-Warning $errorMessage
        }
        $result = [pscustomobject]@{ Id = $package.Id; Name = $package.Name; ExitCode = $code; Success = $success }
        $results.Add($result)
        if ($ContinueInstallers -and $success) {
            $temporary = $marker + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
            try {
                [IO.File]::WriteAllText($temporary, ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($true))
                if ([IO.File]::Exists($marker)) { [IO.File]::Replace($temporary, $marker, $null) } else { [IO.File]::Move($temporary, $marker) }
            }
            finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
        }
        if ($success -and $code -eq 1641) { break }
    }
    if ($rebootRequired -and $exitCode -eq 0) {
        Write-Output 'Reinicie o Windows para concluir a instalação.'
        $exitCode = 3010
    }
    else { Write-Output 'Instalações concluídas.' }
}
catch {
    Write-Warning $_.Exception.Message
    $results.Add([pscustomobject]@{ Error = $_.Exception.Message })
    $exitCode = 1
}
finally {
    if (-not $VerifyOnly) {
        try {
            ConvertTo-Json -InputObject @($results.ToArray()) -Depth 5 |
                Set-Content -LiteralPath (Join-Path $PSScriptRoot 'resultado-instalacao.json') -Encoding UTF8
        }
        catch { Write-Warning ('Não foi possível salvar o resultado: {0}' -f $_.Exception.Message) }
    }
}
if (-not $VerifyOnly -and $officeReady) {
    try {
        Write-Output ('[{0}] Office: Iniciando o bootstrap da coleção.' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
        $officeArguments = ''
        if ($SetupComplete) { $officeArguments = '-SetupComplete' }
        if ($StateDirectory) { $officeArguments += ' -StateDirectory "' + (Join-Path $StateDirectory 'Office') + '"' }
        $officeCode = Invoke-OfficeBootstrap -Path $officeScript -Arguments $officeArguments
        Write-Output ('[{0}] Office: Bootstrap encerrado com código {1}. Consulte %TEMP%\WinProvision-Log.log.' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $officeCode)
        @{ ExitCode = $officeCode; Success = ($officeCode -eq 0) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'resultado-office.json') -Encoding UTF8
        if ($exitCode -in @(0, 3010) -and $officeCode -ne 0) { $exitCode = $officeCode }
    }
    catch { Write-Warning 'Não foi possível executar ou registrar a instalação do Office.'; if ($exitCode -in @(0, 3010)) { $exitCode = 1 } }
}
exit $exitCode
