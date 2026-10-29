; HomePlayer 安装脚本（Inno Setup 6）
; 先发布 dist\，再编译本文件。也可直接运行 installer\build.ps1。

#ifndef AppVersion
  #define AppVersion "0.1.1"
#endif

#define AppName "HomePlayer"
#define AppExe "HomePlayer.exe"
#define AppPublisher "llzx373"
#define AppId "{{8E3C2A91-4F6B-4D0E-9C51-A1B7E6D4F802}"
#define DistDir "..\dist"
#define AppIcon "..\src\HomePlayer.App\Assets\App.ico"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/llzx373/HomePlayer
AppSupportURL=https://github.com/llzx373/HomePlayer/issues
AppUpdatesURL=https://github.com/llzx373/HomePlayer/releases
AppCopyright=Copyright (c) 2026 llzx373
LicenseFile=license-dialog.txt
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts
OutputBaseFilename=HomePlayer-Setup-{#AppVersion}
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
UsedUserAreasWarning=no
ShowLanguageDialog=no

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked
Name: "assoc"; Description: "登记视频文件类型（之后可在系统设置中设为默认播放器）"; GroupDescription: "文件关联:"; Flags: checkedonce
Name: "assoc\mkv"; Description: "Matroska (.mkv)"; Flags: checkedonce
Name: "assoc\mp4"; Description: "MPEG-4 (.mp4)"; Flags: checkedonce
Name: "assoc\m4v"; Description: "iTunes 视频 (.m4v)"; Flags: checkedonce
Name: "assoc\mov"; Description: "QuickTime (.mov)"; Flags: checkedonce
Name: "assoc\avi"; Description: "AVI (.avi)"; Flags: checkedonce
Name: "assoc\wmv"; Description: "Windows Media (.wmv)"; Flags: checkedonce
Name: "assoc\flv"; Description: "Flash 视频 (.flv)"; Flags: checkedonce
Name: "assoc\ts"; Description: "MPEG Transport Stream (.ts)"; Flags: checkedonce
Name: "assoc\m2ts"; Description: "Blu-ray (.m2ts)"; Flags: checkedonce
Name: "assoc\mpg"; Description: "MPEG (.mpg)"; Flags: checkedonce
Name: "assoc\mpeg"; Description: "MPEG (.mpeg)"; Flags: checkedonce
Name: "assoc\webm"; Description: "WebM (.webm)"; Flags: checkedonce
Name: "assoc\rmvb"; Description: "RealMedia (.rmvb)"; Flags: checkedonce
Name: "context"; Description: "右键菜单添加「用 HomePlayer 播放」（即使不是默认播放器）"; GroupDescription: "文件关联:"; Flags: checkedonce

[Dirs]
Name: "{localappdata}\HomePlayer\crashes"; Flags: uninsneveruninstall

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"
Name: "{group}\崩溃日志"; Filename: "{localappdata}\HomePlayer\crashes"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\HomePlayer"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\HomePlayer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"
Root: HKLM; Subkey: "Software\HomePlayer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "本地视频播放器"
Root: HKLM; Subkey: "Software\HomePlayer\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#AppExe},0"
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\HomePlayer\Capabilities"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""

#define Ext "mkv"
#define Desc "MKV 视频"
#include "assoc-entry.iss"
#define Ext "mp4"
#define Desc "MP4 视频"
#include "assoc-entry.iss"
#define Ext "m4v"
#define Desc "M4V 视频"
#include "assoc-entry.iss"
#define Ext "mov"
#define Desc "QuickTime 视频"
#include "assoc-entry.iss"
#define Ext "avi"
#define Desc "AVI 视频"
#include "assoc-entry.iss"
#define Ext "wmv"
#define Desc "Windows Media 视频"
#include "assoc-entry.iss"
#define Ext "flv"
#define Desc "Flash 视频"
#include "assoc-entry.iss"
#define Ext "ts"
#define Desc "MPEG 传输流"
#include "assoc-entry.iss"
#define Ext "m2ts"
#define Desc "Blu-ray 视频"
#include "assoc-entry.iss"
#define Ext "mpg"
#define Desc "MPEG 视频"
#include "assoc-entry.iss"
#define Ext "mpeg"
#define Desc "MPEG 视频"
#include "assoc-entry.iss"
#define Ext "webm"
#define Desc "WebM 视频"
#include "assoc-entry.iss"
#define Ext "rmvb"
#define Desc "RealMedia 视频"
#include "assoc-entry.iss"

[Run]
Filename: "{app}\{#AppExe}"; Description: "运行 HomePlayer"; Flags: nowait postinstall skipifsilent
Filename: "ms-settings:defaultapps?registeredAppMachine={#AppName}"; Description: "打开系统设置，将 HomePlayer 设为默认播放器"; Flags: nowait postinstall skipifsilent unchecked shellexec

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if UninstallSilent then
    Exit;
  if MsgBox('是否同时删除媒体库、设置、缓存和崩溃日志？' + #13#10 + '选「否」则保留，下次安装后仍可使用。', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(ExpandConstant('{localappdata}\HomePlayer'), True, True, True);
end;
