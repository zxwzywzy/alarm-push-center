#ifndef MyAppVersion
  #define MyAppVersion "1.4.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\installer-publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName "工业报警推送中心"
#define MyAppPublisher "Alarm Push Center"
#define MyAppExeName "AlarmPushCenter.exe"

[Setup]
AppId={{CB1968AD-B025-4CC0-960A-68D56309E8E9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
LicenseFile=..\DISCLAIMER.txt
DefaultDirName={localappdata}\Programs\AlarmPushCenter
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=AlarmPushCenter-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\AlarmPushCenter\Assets\app-icon.ico
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Dirs]
Name: "{localappdata}\AlarmPushCenter"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\RELEASE_NOTES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\OPENWRT_RELEASE_NOTES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\mobile\README.md"; DestDir: "{app}\mobile"; Flags: ignoreversion
Source: "..\openwrt\README.md"; DestDir: "{app}\openwrt"; Flags: ignoreversion
Source: "..\mobile\scripts\build-ios.sh"; DestDir: "{app}\mobile\scripts"; Flags: ignoreversion
Source: "..\钉钉互动卡片模板说明.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\DISCLAIMER.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--config ""{localappdata}\AlarmPushCenter\config.json"""; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--config ""{localappdata}\AlarmPushCenter\config.json"""; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--config ""{localappdata}\AlarmPushCenter\config.json"""; Description: "启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
