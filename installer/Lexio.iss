#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishSourceDir
  #define PublishSourceDir "..\artifacts\lexio-release-candidate\publish"
#endif

[Setup]
AppId={{1D685A2A-08A7-49FC-95B3-7F0D9A8EBA21}
AppName=Lexio
AppVersion={#AppVersion}
AppPublisher=Lexio
AppComments=学习数据保存在 %LOCALAPPDATA%\Lexio，卸载应用时保留。
UninstallDisplayName=Lexio（学习数据保存在 %LOCALAPPDATA%\Lexio，卸载时保留）
DefaultDirName={localappdata}\Programs\Lexio
DefaultGroupName=Lexio
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.19041
Uninstallable=yes
UninstallDisplayIcon={app}\Lexio.exe
SetupLogging=yes
InfoBeforeFile=UserDataNotice.txt
OutputDir=..\artifacts\lexio-release-candidate\installer
OutputBaseFilename=Lexio-Setup-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他选项:"; Flags: unchecked

[Files]
Source: "{#PublishSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Lexio"; Filename: "{app}\Lexio.exe"
Name: "{autodesktop}\Lexio"; Filename: "{app}\Lexio.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Lexio.exe"; Description: "启动 Lexio"; Flags: nowait postinstall skipifsilent
