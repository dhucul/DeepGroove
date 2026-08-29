; Deep Groove — Inno Setup script
; Build: dotnet publish first (self-contained win-x64), then compile this script. Both the
; payload and the intermediate build land under artifacts\, so the Visual Studio output
; folder (bin\Release\net10.0-windows) holds nothing but the framework-dependent build.
;   dotnet publish src\WaveLab\WaveLab.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish --artifacts-path artifacts\build
;   ISCC.exe installer\WaveLab.iss

; Display name only: the wizard, the Start Menu group, the desktop shortcut and the
; Add/Remove Programs entry. MyAppExeName stays WaveLab.exe because that is what
; dotnet publish produces from AssemblyName, and AppId below is what Inno matches an
; upgrade on — so an existing install is still recognised and updated in place.
#define MyAppName "Deep Groove"
#define MyAppVersion "2.0.44"
#define MyAppExeName "WaveLab.exe"
#define PublishDir "..\artifacts\publish"

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
OutputBaseFilename=DeepGroove-Setup-{#MyAppVersion}
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
