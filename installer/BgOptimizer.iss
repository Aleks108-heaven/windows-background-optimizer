; Inno Setup script for Background Optimizer.
;
; Build locally (after `dotnet publish ... --output ./publish`):
;   ISCC.exe /DAppVersion=1.0.0 installer\BgOptimizer.iss
; CI passes the version from the csproj and the publish folder automatically.
;
; The installer is NOT code-signed yet, so Windows SmartScreen and Smart App Control will warn
; about (or block) it until it is. Sign BgOptimizer.exe, BgOptimizer.dll and the produced Setup.exe.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "Background Optimizer"
#define AppExe  "BgOptimizer.exe"

[Setup]
; A fixed GUID so upgrades replace the previous install instead of installing side by side.
AppId={{6B1D5C0E-3C57-4F0B-9E53-8D2A7F4C91A7}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Aleks108-heaven
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\icon.ico
OutputDir=Output
OutputBaseFilename=BgOptimizer-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The app's manifest is requireAdministrator (it edits services, firewall rules and IFEO keys),
; so it can only be installed for all users, from an elevated installer.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The app lives in the tray: close it before replacing files. Closing it normally lets it release
; any process Auto-Optimize froze; if it is force-killed instead, the next start releases them.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english";   MessagesFile: "compiler:Default.isl"
Name: "ukrainian"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "spanish";   MessagesFile: "compiler:Languages\Spanish.isl"
Name: "german";    MessagesFile: "compiler:Languages\German.isl"
Name: "italian";   MessagesFile: "compiler:Languages\Italian.isl"
Name: "polish";    MessagesFile: "compiler:Languages\Polish.isl"
Name: "russian";   MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only the app's own data: settings, the action log and the frozen-process list. Settings it wrote elsewhere
; in Windows (priority rules, firewall rules, tweaks) are deliberately left alone: undo those in the app first.
; The files are named one by one and the folder is removed only if it is then empty. There is no recursive
; delete: this runs as administrator on a folder the user's own programs can write to, so it must never walk
; into whatever a junction or symbolic link planted there might point at.
Type: files; Name: "{localappdata}\BgOptimizer\settings.json"
Type: files; Name: "{localappdata}\BgOptimizer\settings.json.tmp"
Type: files; Name: "{localappdata}\BgOptimizer\actions.log"
Type: files; Name: "{localappdata}\BgOptimizer\actions.log.1"
Type: files; Name: "{localappdata}\BgOptimizer\suspended.json"
Type: dirifempty; Name: "{localappdata}\BgOptimizer"
