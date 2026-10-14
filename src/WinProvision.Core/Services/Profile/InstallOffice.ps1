#Requires -Version 5.1
[CmdletBinding()]
param([switch]$VerifyOnly, [switch]$SetupComplete, [string]$StateDirectory,
    [Parameter(DontShow)][switch]$ElevationRequested)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Write-OfficeLog {
    param([string]$Message)
    $line = '[{0}] Office: {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Output $line | Out-Host
    $path = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'WinProvision-Log.log')
    try {
        if ([IO.File]::Exists($path) -and [IO.FileInfo]::new($path).Length -ge 10MB) {
            Move-Item -LiteralPath $path -Destination ($path + '.previous') -Force
        }
        [IO.File]::AppendAllText($path, $line + [Environment]::NewLine)
    }
    catch { Write-Warning 'Não foi possível gravar o log do Office.' }
}

function Test-OfficeAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { return [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
}

function Get-OfficeSha256 {
    param([string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Invoke-OfficeSetup {
    param([string]$SetupPath, [string]$Mode, [string]$ConfigurationPath)
    $process = Start-Process -FilePath $SetupPath -ArgumentList ($Mode + ' "' + $ConfigurationPath + '"') -WorkingDirectory ([IO.Path]::GetDirectoryName($SetupPath)) -WindowStyle Hidden -PassThru
    try { $null = $process.Handle; $process.WaitForExit(); return $process.ExitCode }
    finally { $process.Dispose() }
}

function Invoke-OfficeElevation {
    param([string]$ScriptPath, [string]$StateDirectory)
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $native = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if ([IO.File]::Exists($native)) { $powershell = $native }
    $command = "& '" + $ScriptPath.Replace("'", "''") + "' -ElevationRequested"
    if ($StateDirectory) { $command += " -StateDirectory '" + $StateDirectory.Replace("'", "''") + "'" }
    $command += '; exit $LASTEXITCODE'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $process = $null
    try {
        $process = Start-Process -FilePath $powershell -ArgumentList ('-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encoded) -Verb RunAs -WindowStyle Hidden -PassThru -ErrorAction Stop
        $null = $process.Handle; $process.WaitForExit()
        return $process.ExitCode
    }
    catch {
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception -is [ComponentModel.Win32Exception] -and $exception.NativeErrorCode -eq 1223) {
                Write-OfficeLog 'A solicitação de elevação foi cancelada. O Office não foi instalado.'
                return 1223
            }
            $exception = $exception.InnerException
        }
        throw
    }
    finally { if ($null -ne $process) { $process.Dispose() } }
}

function Test-OfficeLocalPayload {
    $expectedIndex = '__PAYLOAD_INDEX_SHA256__'
    if (-not $expectedIndex) { return }
    $index = Join-Path $PSScriptRoot 'payload-sha256.txt'
    if (([IO.File]::GetAttributes($index) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Links não são permitidos no inventário Office.' }
    if ((Get-OfficeSha256 -Path $index) -ne $expectedIndex) { throw 'SHA256 do inventário Office divergente.' }
    if ([IO.FileInfo]::new($index).Length -gt 8388608) { throw 'Inventário Office excessivo.' }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in [IO.File]::ReadLines($index)) {
        if ($line -notmatch '^([A-Fa-f0-9]{64})\t(.+)$') { throw 'Registro de integridade Office inválido.' }
        $hash = $Matches[1]; $relative = $Matches[2]
        if (-not $relative.StartsWith('Office/Data/', [StringComparison]::OrdinalIgnoreCase) -or $relative.Contains(':') -or $relative.Contains('\') -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) { throw 'Caminho de conteúdo Office inválido.' }
        $current = $PSScriptRoot
        foreach ($segment in $relative.Split('/')) {
            $current = Join-Path $current $segment
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Links não são permitidos no conteúdo Office.' }
        }
        if (-not $paths.Add([IO.Path]::GetFullPath($current)) -or $paths.Count -gt 20000) { throw 'Inventário Office duplicado ou excessivo.' }
        if ((Get-OfficeSha256 -Path $current) -ne $hash) { throw 'SHA256 divergente no conteúdo Office.' }
    }
    $folders = [Collections.Generic.Stack[string]]::new()
    $folders.Push((Join-Path $PSScriptRoot 'Office\Data'))
    $count = 0
    $directoryCount = 1
    while ($folders.Count) {
        $folder = $folders.Pop()
        foreach ($file in [IO.Directory]::EnumerateFiles($folder)) {
            if (-not $paths.Contains([IO.Path]::GetFullPath($file))) { throw 'Arquivo Office não consta no inventário.' }
            $count++
        }
        foreach ($child in [IO.Directory]::EnumerateDirectories($folder)) {
            $directoryCount++
            if ($directoryCount -gt 20000) { throw 'Conteúdo Office excede o limite de pastas.' }
            if (([IO.File]::GetAttributes($child) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Links não são permitidos no conteúdo Office.' }
            $folders.Push($child)
        }
    }
    if ($count -eq 0 -or $count -ne $paths.Count) { throw 'Conteúdo Office incompleto.' }
}

function Invoke-OfficeInstallation {
    param([switch]$VerifyOnly, [switch]$SetupComplete, [string]$StateDirectory, [switch]$ElevationRequested)
    $runtimeXml = $null; $phase = 'validação de hashes'
    try {
        $setup = Join-Path $PSScriptRoot '__SETUP_FILE__'
        $configuration = Join-Path $PSScriptRoot '__CONFIGURATION_FILE__'
        if ((Get-OfficeSha256 -Path $setup) -ne '__SETUP_SHA256__' -or
            (Get-OfficeSha256 -Path $configuration) -ne '__XML_SHA256__') {
            throw 'SHA256 do módulo Office divergente.'
        }
        $phase = 'leitura do XML'
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($configuration, $settings)
        $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null
        try { $xml.Load($reader) }
        finally { $reader.Dispose() }
        if ($xml.SelectNodes('/Configuration/Add').Count -ne 1 -or $xml.SelectNodes('/Configuration/Add/Product').Count -eq 0) { throw 'Configuração Office inválida.' }
        Test-OfficeLocalPayload
        if ($VerifyOnly) { Write-OfficeLog 'XML e hashes verificados. Nenhuma instalação ou conexão foi iniciada.'; return 0 }
        $phase = 'privilégios'
        if (-not (Test-OfficeAdministrator)) {
            if ($ElevationRequested -or $SetupComplete) {
                Write-OfficeLog 'Não foi possível executar o Office com privilégios de administrador.'
                return 5
            }
            Write-OfficeLog 'Solicitando permissão de administrador pelo UAC.'
            return (Invoke-OfficeElevation -ScriptPath $PSCommandPath -StateDirectory $StateDirectory)
        }
        if ($SetupComplete) {
            $display = $xml.SelectSingleNode('/Configuration/Display')
            if ($null -eq $display) { $display = $xml.CreateElement('Display'); $null = $xml.DocumentElement.AppendChild($display) }
            $display.SetAttribute('Level', 'None'); $display.SetAttribute('AcceptEULA', 'TRUE')
        }
        if ('__PAYLOAD_INDEX_SHA256__') {
            $add = $xml.SelectSingleNode('/Configuration/Add')
            $add.SetAttribute('SourcePath', $PSScriptRoot)
            $add.SetAttribute('AllowCdnFallback', 'FALSE')
        }
        if ($SetupComplete -or '__PAYLOAD_INDEX_SHA256__') {
            $runtimeXml = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'WinProvision-Office-' + [Guid]::NewGuid().ToString('N') + '.xml')
            $xml.Save($runtimeXml)
            $configuration = $runtimeXml
        }
        $phase = 'configure'
        Write-OfficeLog 'Iniciando setup.exe /configure. Aguarde o ODT concluir.'
        $code = Invoke-OfficeSetup -SetupPath $setup -Mode '/configure' -ConfigurationPath $configuration
        Write-OfficeLog ('ODT encerrado com código {0}. Logs Microsoft em %TEMP%.' -f $code)
        if ($StateDirectory) {
            try {
                [IO.Directory]::CreateDirectory($StateDirectory) | Out-Null
                @{ ExitCode = $code; Success = ($code -eq 0); At = [DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json |
                    Set-Content -LiteralPath (Join-Path $StateDirectory 'configure.json') -Encoding UTF8
            }
            catch { Write-OfficeLog 'Não foi possível registrar o resultado do Office.' }
        }
        return $code
    }
    catch {
        Write-OfficeLog ('Falha na etapa {0}, HRESULT 0x{1:X8}.' -f $phase, $_.Exception.HResult)
        return 1
    }
    finally { if ($runtimeXml -and [IO.File]::Exists($runtimeXml)) { [IO.File]::Delete($runtimeXml) } }
}

exit (Invoke-OfficeInstallation -VerifyOnly:$VerifyOnly -SetupComplete:$SetupComplete -StateDirectory $StateDirectory -ElevationRequested:$ElevationRequested)
