; Restoria.iss — 通用 Windows 安装包（Inno Setup 6）
;
; 由 tools\make_package.ps1 调用 ISCC.exe 编译，版本号通过 /DMyAppVersion=<ver> 注入。
; 旧的自研单文件安装器源码保留在本目录，但发行不再使用它。
;
; 关键约定：
;   · 默认装到 Program Files\Restoria，标准 UAC 提权、标准卸载入口；
;   · 载荷来自 发布包\Restoria-install（make_package.ps1 的舞台目录）；
;   · 排除旧安装器的 setup-boot.exe / Uninstall.exe / 安装标记；
;   · 向导提供简体中文与英文两种语言。

#define MyAppName "Restoria"
#define MyPublisher "Restoria"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

[Setup]
AppId={{8F3C2A41-6D5B-4E7A-9C21-3B8E5A7D1F40}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\Restoria
DefaultGroupName=Restoria
UninstallDisplayIcon={app}\Restoria.exe
SetupIconFile=..\..\assets\app.ico
LicenseFile=EULA.txt
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
MinVersion=6.1sp1
OutputDir=..\..\发布包
OutputBaseFilename=Restoria-setup
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName} Setup

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\..\发布包\Restoria-install\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "setup-boot.exe,Uninstall.exe,archivesuite.install"

[Icons]
Name: "{group}\Restoria"; Filename: "{app}\Restoria.exe"
Name: "{group}\{cm:UninstallProgram,Restoria}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Restoria"; Filename: "{app}\Restoria.exe"; Tasks: desktopicon
