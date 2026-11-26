; CADAI (AutoCAD 2019-2026 AI Assistant) Inno Setup 安装脚本
; 打包产物：..\dist\CADAI-Setup-v1.0.0.exe
; 安装位置：C:\Program Files\Autodesk\ApplicationPlugins\CADAI.bundle（受 AutoCAD SECURELOAD 信任）

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "CADAI - AutoCAD AI Assistant"
#define MyAppId "{{E4B8C9D2-3F1A-4E6B-8C2D-9A7F1E5B3042}"
#define MyAppPublisher "zly258"
#define MyAppURL "https://github.com/zly258/CADAI"
#define BundleSourceDir "..\dist\CADAI.bundle"
#define CanonicalBundleDir "{commonpf64}\Autodesk\ApplicationPlugins\CADAI.bundle"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
LicenseFile=..\LICENSE
DefaultDirName={#CanonicalBundleDir}
DisableDirPage=yes
DirExistsWarning=no
AppendDefaultDirName=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\dist
OutputBaseFilename=CADAI-Setup-v{#MyAppVersion}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
AlwaysRestart=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
; 安装前清理旧版目录及 ProgramData / AppData 遗留多副本，防止 AutoCAD 重复加载冲突
Type: filesandordirs; Name: "{app}\Contents"
Type: files; Name: "{app}\PackageContents.xml"
Type: filesandordirs; Name: "{commonappdata}\Autodesk\ApplicationPlugins\CADAI.bundle"
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\CADAI.bundle"

[Dirs]
Name: "{app}"; Permissions: users-readexec

[Files]
Source: "{#BundleSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Registry]
Root: HKLM; Subkey: "Software\Autodesk\AutoCAD\ApplicationPlugins\CADAI"; ValueType: string; ValueName: "Name"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Autodesk\AutoCAD\ApplicationPlugins\CADAI"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Autodesk\AutoCAD\ApplicationPlugins\CADAI"; ValueType: string; ValueName: "BundlePath"; ValueData: "{#CanonicalBundleDir}"; Flags: uninsdeletekey

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{commonappdata}\Autodesk\ApplicationPlugins\CADAI.bundle"
Type: filesandordirs; Name: "{userappdata}\Autodesk\ApplicationPlugins\CADAI.bundle"

[Code]
// 清理 AutoCAD 各版本注册表中的旧版 Loaded / Applications 缓存，确保下次启动加载最新 Bundle
procedure CleanStaleAutoCadRegistryCache;
var
  Releases, Products, LoadedKeys: TArrayOfString;
  I, J, K: Integer;
  AcadRoot, RelPath, ProdPath, LoadedPath, KeyName: String;
begin
  AcadRoot := 'Software\Autodesk\AutoCAD';
  if not RegGetSubkeyNames(HKCU, AcadRoot, Releases) then Exit;

  for I := 0 to GetArrayLength(Releases) - 1 do
  begin
    RelPath := AcadRoot + '\' + Releases[I];
    if RegGetSubkeyNames(HKCU, RelPath, Products) then
    begin
      for J := 0 to GetArrayLength(Products) - 1 do
      begin
        ProdPath := RelPath + '\' + Products[J];
        RegDeleteKeyIncludingSubkeys(HKCU, ProdPath + '\Applications\CADAI');

        LoadedPath := ProdPath + '\Loaded';
        if RegGetSubkeyNames(HKCU, LoadedPath, LoadedKeys) then
        begin
          for K := 0 to GetArrayLength(LoadedKeys) - 1 do
          begin
            KeyName := Uppercase(LoadedKeys[K]);
            if Pos('CADAI.BUNDLE', KeyName) > 0 then
              RegDeleteKeyIncludingSubkeys(HKCU, LoadedPath + '\' + LoadedKeys[K]);
          end;
        end;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanStaleAutoCadRegistryCache;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    CleanStaleAutoCadRegistryCache;
end;
