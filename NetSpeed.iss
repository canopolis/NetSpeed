#define AppVer "2.0.0"

[Setup]
AppId={{6E7C3B1A-4F2D-4C8E-9A1B-2D5F7E8C9A01}
AppName=NetSpeed
AppVersion={#AppVer}
AppPublisher=Canopolis
DefaultDirName={autopf}\NetSpeed
DefaultGroupName=NetSpeed
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=NetSpeed_Setup
SetupIconFile=netspeed.ico
UninstallDisplayIcon={app}\NetSpeed.exe
UninstallDisplayName=NetSpeed
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
MinVersion=10.0

[Tasks]
Name: "startup"; Description: "Start NetSpeed with Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "build\NetSpeed.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "build\NetSpeed.exe.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\NetSpeed"; Filename: "{app}\NetSpeed.exe"
Name: "{autodesktop}\NetSpeed"; Filename: "{app}\NetSpeed.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NetSpeed"; ValueData: """{app}\NetSpeed.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\NetSpeed"; Flags: uninsdeletekey

[Run]
Filename: "{app}\NetSpeed.exe"; Description: "Start NetSpeed now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im NetSpeed.exe"; Flags: runhidden; RunOnceId: "KillNetSpeed"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'NetSpeed');
end;
