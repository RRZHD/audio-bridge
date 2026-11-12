; Audio Bridge Server — Windows installer (Inno Setup 6)
;
; Build the app first, then compile this script:
;   dotnet publish ..\pc-server\AudioBridge.Server -c Release -r win-x64 --self-contained true ^
;       -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\pc-server\AudioBridge.Server\publish
;   iscc setup.iss
;
; Output: installer\output\AudioBridgeServer-Setup.exe

#define MyAppName "Audio Bridge Server"
#define MyAppVersion "1.2.1"
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
SetupIconFile=..\pc-server\AudioBridge.Server\app.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Запускать при входе в Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "usbdk"; Description: "Установить драйвер UsbDk (нужен для подключения телефона по USB-кабелю; запросит права администратора, после установки нужна перезагрузка Windows)"; GroupDescription: "USB"; Check: UsbDkMissing

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; UsbDk (Apache-2.0, signed by Red Hat): the one-time USB driver for the phone-over-cable mode.
Source: "third-party\UsbDk_1.0.22_x64.msi"; DestDir: "{app}"; Flags: ignoreversion
Source: "third-party\UsbDk-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{app}\UsbDk_1.0.22_x64.msi"" /passive /norestart"; Flags: shellexec waituntilterminated; Verb: "runas"; Tasks: usbdk
; Hide rules: keep UsbDk from power-cycling the phone's accessory device (which would drop accessory mode).
Filename: "{commonpf64}\UsbDk Runtime Library\UsbDkController.exe"; Parameters: "-P 0 18D1 2D00 -1 -1 1"; Flags: shellexec waituntilterminated runhidden; Verb: "runas"; Tasks: usbdk
Filename: "{commonpf64}\UsbDk Runtime Library\UsbDkController.exe"; Parameters: "-P 0 18D1 2D01 -1 -1 1"; Flags: shellexec waituntilterminated runhidden; Verb: "runas"; Tasks: usbdk
Filename: "{commonpf64}\UsbDk Runtime Library\UsbDkController.exe"; Parameters: "-P 0 18D1 2D04 -1 -1 1"; Flags: shellexec waituntilterminated runhidden; Verb: "runas"; Tasks: usbdk
Filename: "{commonpf64}\UsbDk Runtime Library\UsbDkController.exe"; Parameters: "-P 0 18D1 2D05 -1 -1 1"; Flags: shellexec waituntilterminated runhidden; Verb: "runas"; Tasks: usbdk
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// UsbDk's service entry exists once its MSI has been installed.
function UsbDkMissing: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\UsbDk');
end;
