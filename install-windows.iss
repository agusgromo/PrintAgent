[Setup]
AppId=PrintAgent
AppName=PrintAgent
AppVersion=1.0
DefaultDirName={localappdata}\PrintAgent
DefaultGroupName=PrintAgent
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=artifacts
OutputBaseFilename=PrintAgent-Setup-Windows
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Files]
Source: "bin\Release\net10.0\win-x64\publish\PrintAgent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\net10.0\win-x64\publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "support-windows.ps1"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
Type: files; Name: "{group}\PrintAgent - Soporte.lnk"

[Icons]
Name: "{group}\PrintAgent - Support"; Filename: "{app}\PrintAgent.exe"; Parameters: "--tray -Open"; WorkingDir: "{app}"
Name: "{userstartup}\PrintAgent"; Filename: "{app}\PrintAgent.exe"; Parameters: "--tray"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PrintAgentSupport"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "PrintAgent"; Flags: deletevalue

[Run]
Filename: "{app}\PrintAgent.exe"; Parameters: "--tray -Restart -Open"; Flags: nowait skipifsilent
Filename: "{app}\PrintAgent.exe"; Parameters: "--tray -Restart"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{app}\PrintAgent.exe"; Parameters: "--tray -Stop"; Flags: runhidden; RunOnceId: "StopPrintAgentSupport"
