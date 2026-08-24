; Audio Bridge Server — Windows installer (Inno Setup 6)
;
; Build the app first, then compile this script:
;   dotnet publish ..\pc-server\AudioBridge.Server -c Release -r win-x64 --self-contained true ^
;       -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\pc-server\AudioBridge.Server\publish
;   iscc setup.iss
;
; Output: installer\output\AudioBridgeServer-Setup.exe

#define MyAppName "Audio Bridge Server"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "RRZHD"
#define MyAppURL "https://github.com/RRZHD/audio-bridge"
#define MyAppExeName "AudioBridgeServer.exe"
#define PublishDir "..\pc-server\AudioBridge.Server\publish"

[Setup]
AppId={{B7B6C6F0-6E7B-4B1E-9C7B-6C2C1A8B9D3F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Per-user install, no admin/UAC prompt needed for a personal utility.
DefaultDirName={localappdata}\AudioBridge
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=AudioBridgeServer-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Запускать при входе в Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
