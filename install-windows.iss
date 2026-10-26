[Setup]
AppId=PrintAgent
AppName=PrintAgent
AppVersion=1.0
DefaultDirName={localappdata}\PrintAgent
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=artifacts
OutputBaseFilename=PrintAgent-Setup-Windows
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "bin\Release\net10.0\win-x64\publish\PrintAgent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\net10.0\win-x64\publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PrintAgent"; ValueData: """{app}\PrintAgent.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{app}\PrintAgent.exe"; Description: "Iniciar PrintAgent ahora"; Flags: nowait postinstall skipifsilent
