; MiaoWu v0.2.1 — Inno Setup 6 脚本
; 目标：每用户安装（不需要管理员）、.NET Framework 4.8 检测、开始菜单、
; 卸载时询问是否保留用户目录\.miaowu 数据。
;
; 构建：
;   1. scripts\publish.ps1 -Configuration Release
;   2. 安装 Inno Setup 6，运行 ISCC.exe installer\MiaoWu.iss
; 产物：.artifacts\installer\MiaoWu-0.2.1-setup.exe

#define MyAppName "MiaoWu"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "MiaoWu"
#define MyAppExeName "MiaoWu.exe"
#define MyAppDataDir "{userprofile}\.miaowu"

[Setup]
AppId={{6C1E4A2B-3D77-4B7E-9A3C-2F5B0D8E1A44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\.artifacts\installer
OutputBaseFilename=MiaoWu-{#MyAppVersion}-setup
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
MinVersion=6.1sp1
ArchitecturesAllowed=x86 x64
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
Name: "startup"; Description: "开机时启动 MiaoWu（托盘）"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "..\src\MiaoWu.App\bin\Release\net48\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--minimized"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\*.log"

[Code]
const
  DotNet48Release = 528040;
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet-framework/net48';

function ReadDotNetRelease(RootKey: Integer): Cardinal;
var
  Value: Cardinal;
begin
  Result := 0;
  if RegQueryDWordValue(RootKey, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
    'Release', Value) then
    Result := Value;
end;

function IsDotNet48Installed: Boolean;
begin
  Result := (ReadDotNetRelease(HKLM) >= DotNet48Release)
    or (ReadDotNetRelease(HKCU) >= DotNet48Release);
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not IsDotNet48Installed then
  begin
    if MsgBox('MiaoWu 需要 .NET Framework 4.8（或更新的 4.x 运行时）。' + #13#10 + #13#10 +
      '是否现在打开下载页？安装完成后请重新运行本安装程序。',
      mbError, MB_YESNO) = IDYES then
    begin
      ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;

    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    DataDir := ExpandConstant('{#MyAppDataDir}');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否保留 MiaoWu 的本地数据？' + #13#10 + #13#10 +
        '包括：config.json、workspaces.json、providers.json、secrets.dat、日志与导出。' + #13#10 +
        '选择“否”会删除这些数据，且无法恢复。',
        mbConfirmation, MB_YESNO) = IDNO then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
