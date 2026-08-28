#define AppName "Last War Weekly Extractor"
#define AppVersion "0.1.0"
#define AppPublisher "Last War Tools"
#define AppExeName "lastwar-weekly-extractor.exe"
#define SidecarName "lastwar-backend-x86_64-pc-windows-msvc.exe"

[Setup]
AppId={{A1C0F9B8-7A6D-4A77-8BC7-7E9B8C1F4B19}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Last War Weekly Extractor
DefaultGroupName={#AppName}
OutputDir=..\dist\installer
OutputBaseFilename=LastWarWeeklyExtractor-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src-tauri\icons\icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=admin

[Files]
Source: "..\src-tauri\target\release\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src-tauri\binaries\{#SidecarName}"; DestDir: "{app}\binaries"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
