; WMonitor per-user installer (Inno Setup 6).
; ASCII only. Version is read from the built portable exe (single source of truth = csproj).
; Payload = self-contained single-file exe (no .NET runtime prerequisite, cold start 1-2s documented).
; Per-user install: no admin, matches the app's zero-dependency zero-elevation stance
; (config stays in %APPDATA%\WMonitor, autostart is HKCU Run -- the app manages both itself).

#define AppName "WMonitor"
#define AppExe "WMonitor.exe"
#define RawVer GetVersionNumbersString("..\\build\\portable\\" + AppExe)
#define AppVersion Copy(RawVer, 1, RPos(".", RawVer) - 1)

[Setup]
AppId={{9F6B8D4E-3C2A-4A1B-9E7D-8C5F1A2B3D4E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
DefaultDirName={localappdata}\WMonitor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\build\installer
OutputBaseFilename=WMonitorSetup-v{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no

[Messages]
SelectDirDesc=Where should WMonitor be installed?
SelectDirLabel3=Setup will install WMonitor into the following folder. Per-user install, no admin rights needed.

[Files]
Source: "..\build\portable\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch WMonitor"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillWMonitor"

[Code]
// note: config/history in %APPDATA%\WMonitor intentionally survives uninstall (user data).
