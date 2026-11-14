; DiskPulse Inno Setup 6 installer script

#ifndef MyAppVersion
  #define MyAppVersion "0.0.20"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\publish\installer"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "DiskPulse-Setup"
#endif

#define MyAppName "DiskPulse"
#define MyAppPublisher "rkfsociety"
#define MyAppURL "https://github.com/rkfsociety/DiskPulse"
#define MyAppExeName "DiskPulse.exe"

[Setup]
AppId={{B8E4C7D2-9A3F-4F5B-8C12-6E4A1D9F0B37}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
SetupIconFile=..\assets\cleaner-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall shellexec; Check: ShouldRelaunchAfterSilentInstall

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Cleaner\updates"
Type: filesandordirs; Name: "{localappdata}\Cleaner\installer"

[Code]
function ShouldRelaunchAfterSilentInstall: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

procedure CleanupProfileInstall(const AppSubDir, ExeName, ShortcutName: string);
var
  LegacyDir: string;
  LegacyExe: string;
  LegacyShortcut: string;
begin
  LegacyDir := ExpandConstant('{localappdata}\Programs\') + AppSubDir;
  LegacyExe := LegacyDir + '\' + ExeName;
  LegacyShortcut := ExpandConstant('{userprograms}\') + ShortcutName;

  if FileExists(LegacyExe) then
  begin
    DeleteFile(LegacyExe);
    RemoveDir(LegacyDir);
  end;

  if FileExists(LegacyShortcut) then
  begin
    DeleteFile(LegacyShortcut);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    CleanupProfileInstall('DiskPulse', 'DiskPulse.exe', 'DiskPulse.lnk');
    CleanupProfileInstall('Cleaner', 'Cleaner.exe', 'Cleaner.lnk');
  end;
end;
