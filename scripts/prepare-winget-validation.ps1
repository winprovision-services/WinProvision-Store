$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Este provisionamento é exclusivo do runner descartável.' }
$taskCommand = Get-Command winget -ErrorAction SilentlyContinue
if ($taskCommand) {
    $taskVersion = (& $taskCommand.Source --version).Trim().TrimStart('v')
    if ($LASTEXITCODE -eq 0 -and [version]$taskVersion -ge [version]'1.12.0') { return }
}
$taskTools = Join-Path $env:RUNNER_TEMP 'winget-validation-1.29.380'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
$taskBundle = Join-Path $taskTools 'appinstaller.msixbundle'
$taskDependencies = Join-Path $taskTools 'dependencies.zip'
Invoke-WebRequest 'https://github.com/microsoft/winget-cli/releases/download/v1.29.380/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle' -OutFile $taskBundle
Invoke-WebRequest 'https://github.com/microsoft/winget-cli/releases/download/v1.29.380/DesktopAppInstaller_Dependencies.zip' -OutFile $taskDependencies
if ((Get-FileHash $taskBundle -Algorithm SHA256).Hash.ToLowerInvariant() -ne '65dea9c01ce08ee7b763366b27c0e651f97db857c11ca9b9c301826c10092f2e' -or
    (Get-FileHash $taskDependencies -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'ba875afe9d190f61218985ac0292a99d1db710bf93e13c68944ca9d89f0d82d1') { throw 'Hash dos componentes WinGet inválido.' }
$taskExtracted = Join-Path $taskTools 'dependencies'
Expand-Archive -LiteralPath $taskDependencies -DestinationPath $taskExtracted
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($taskDependency in Get-ChildItem -LiteralPath $taskExtracted -Recurse -File | Where-Object { $_.Extension -in @('.appx','.msix') -and $_.FullName -match '[\\/]x64[\\/]|_x64_' }) {
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskDependency.FullName)
    try {
        $taskReader = [IO.StreamReader]::new($taskZip.GetEntry('AppxManifest.xml').Open())
        try { [xml]$taskManifest = $taskReader.ReadToEnd() } finally { $taskReader.Dispose() }
    } finally { $taskZip.Dispose() }
    $taskIdentity = $taskManifest.Package.Identity
    $taskInstalled = Get-AppxPackage -Name $taskIdentity.Name | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $taskInstalled -or [version]$taskInstalled.Version -lt [version]$taskIdentity.Version) {
        Add-AppxPackage -Path $taskDependency.FullName
    }
}
Add-AppxPackage -Path $taskBundle
$taskPackage = Get-AppxPackage -Name Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1
if (-not $taskPackage) { throw 'App Installer não foi registrado.' }
$env:PATH = $taskPackage.InstallLocation + ';' + $env:PATH
$taskPackage.InstallLocation | Out-File -FilePath $env:GITHUB_PATH -Encoding utf8 -Append
& (Join-Path $taskPackage.InstallLocation 'winget.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'O WinGet atualizado não iniciou.' }
