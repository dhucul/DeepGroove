; WaveLab — Inno Setup script
; Build: dotnet publish first (self-contained win-x64), then compile this script.
;   dotnet publish src\WaveLab\WaveLab.csproj -c Release -r win-x64 --self-contained true
;   ISCC.exe installer\WaveLab.iss

#define MyAppName "WaveLab"
#define MyAppVersion "2.0.14"
#define MyAppExeName "WaveLab.exe"
#define PublishDir "..\src\WaveLab\bin\Release\net10.0-windows\win-x64\publish"

[Setup]
AppId={{7C4B1F2E-63A8-4D9B-9E1C-2F8A5D0B7E43}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\WaveLab\Assets\wavelab.ico
Compression=lzma2/max
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=WaveLab-Setup-{#MyAppVersion}
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
PrivilegesRequired=admin
DisableProgramGroupPage=yes
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
