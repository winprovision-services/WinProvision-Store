#ifndef AppVersion
  #define AppVersion "1.0.6"
#endif
#ifndef FileVersion
  #define FileVersion "1.0.6.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#define AppName "WinProvision Store"
#define AppExeName "WinProvision.Store.exe"
#define AppURL "https://github.com/winprovision-services/WinProvision-Store"

[Setup]
AppId={{71B03A9D-2C64-4E81-BF6D-3A90257F1C49}
AppName={#AppName}
LicenseFile=..\LICENSE
InfoBeforeFile=..\PRIVACY.md
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WinProvision Store
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador do {#AppName}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
DirExistsWarning=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=WinProvision.Store-Setup
SetupIconFile=..\src\WinProvision.Store\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
ChangesEnvironment=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "clipath"; Description: "Adicionar winprovision ao PATH do usuário (CLI)"; GroupDescription: "Linha de comando:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  CliRegistryKey = 'Software\WinProvision Store\CLI';

function WithoutCliPath(const CurrentPath, CliPath: string): string;
var
  Part: string;
  I, Start, Kept: Integer;
begin
  Result := '';
  Kept := 0;
  Start := 1;
  for I := 1 to Length(CurrentPath) + 1 do
  begin
    if (I > Length(CurrentPath)) or (CurrentPath[I] = ';') then
    begin
      Part := Copy(CurrentPath, Start, I - Start);
      if CompareText(RemoveBackslashUnlessRoot(Trim(Part)), RemoveBackslashUnlessRoot(CliPath)) <> 0 then
      begin
        if Kept > 0 then Result := Result + ';';
        Result := Result + Part;
        Kept := Kept + 1;
      end;
      Start := I + 1;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  CurrentPath, CliPath: string;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('clipath') then
  begin
    CliPath := ExpandConstant('{app}\cli');
    RegQueryStringValue(HKCU, 'Environment', 'Path', CurrentPath);
    if WithoutCliPath(CurrentPath, CliPath) = CurrentPath then
    begin
      if CurrentPath <> '' then CurrentPath := CurrentPath + ';';
      if not RegWriteExpandStringValue(HKCU, 'Environment', 'Path', CurrentPath + CliPath) then
        RaiseException('Não foi possível adicionar a CLI ao PATH do usuário.');
      RegWriteStringValue(HKCU, CliRegistryKey, 'OwnedPath', CliPath);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CurrentPath, CliPath: string;
begin
  if (CurUninstallStep = usUninstall) and RegQueryStringValue(HKCU, CliRegistryKey, 'OwnedPath', CliPath) then
  begin
    if CompareText(CliPath, ExpandConstant('{app}\cli')) = 0 then
    begin
      if RegQueryStringValue(HKCU, 'Environment', 'Path', CurrentPath) then
        RegWriteExpandStringValue(HKCU, 'Environment', 'Path', WithoutCliPath(CurrentPath, CliPath));
      RegDeleteValue(HKCU, CliRegistryKey, 'OwnedPath');
      RegDeleteKeyIfEmpty(HKCU, CliRegistryKey);
    end;
  end;
end;

function IsDirNameValid(const Value: string): Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 1 to Length(Value) do
    if Ord(Value[I]) > $007F then
    begin
      Result := False;
      Exit;
    end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not IsDirNameValid(WizardForm.DirEdit.Text) then
  begin
    Result := False;
    MsgBox('O caminho de instalação não pode conter caracteres acentuados ou especiais.' + #13#10 +
           'Escolha um caminho composto somente por caracteres ASCII.', mbError, MB_OK);
  end;
end;
