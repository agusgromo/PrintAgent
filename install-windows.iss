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
Name: "{group}\PrintAgent - Support"; Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Sta -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\support-windows.ps1"" -Open"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PrintAgent"; ValueData: """{app}\PrintAgent.exe"""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PrintAgentSupport"; ValueData: """{sys}\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -Sta -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\support-windows.ps1"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\PrintAgent.exe"; Description: "Start PrintAgent now"; Flags: nowait postinstall skipifsilent
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Sta -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\support-windows.ps1"" -Restart"; Flags: nowait skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -Sta -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\support-windows.ps1"" -Stop"; Flags: runhidden; RunOnceId: "StopPrintAgentSupport"
