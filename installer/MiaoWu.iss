; MiaoWu v0.8.2 — Inno Setup 6 脚本
; 目标：管理员安装（装到 Program Files，SetupLdr 先提权）、.NET Framework 4.8 检测、开始菜单、
; 卸载时询问是否保留用户目录\.miaowu 数据；setup.exe / unins000.exe / 向导使用品牌图标。
; 运行期不需要管理员：装完以原用户身份启动，配置与会话仍写在当前用户的 .miaowu。
;
; 构建：
;   1. scripts\publish.ps1 -Configuration Release（产出管理员安装包与 SHA-256）
;   2. 安装 Inno Setup 6，运行 ISCC.exe installer\MiaoWu.iss
; 产物：.artifacts\installer\MiaoWu-0.8.2-setup.exe
;
; 全量包（自带 .NET Framework 4.8 离线安装程序，105 MB 左右）：
;   scripts\publish.ps1 -Configuration Release -BundleDotNet
;   需要先把 ndp48-x86-x64-allos-enu.exe 放到 redist\ 或 .artifacts\redist\，
;   或用 scripts\fetch-dotnet-redist.ps1 下载。脚本会传 /DBUNDLE_DOTNET 与
;   /DDotNetRedist=<绝对路径>，产物改名为 MiaoWu-0.8.2-setup-offline.exe。
;   缺 4.8 时安装器在“准备安装”阶段静默装 4.8；已有 4.8 的机器只多背体积，不会重复安装。
;
; 为什么强制 admin：SetupLdr 会先把安装器解到 %TEMP%\is-*.tmp 再进向导。低完整性进程
; （受限终端 / 沙箱 / 部分安全软件）或不可写的 Temp 会在这一步报
; "Setup was unable to create the directory" + Error 5；提权后安装器以高完整性运行，
; 不再受该限制（2026-11-19 实机验证：普通双击失败、管理员启动正常）。

#define MyAppName "MiaoWu"
#define MyAppVersion "0.8.2"
#define MyAppPublisher "MiaoWu"
#define MyAppExeName "MiaoWu.exe"
#define MyAppDataDir "{userprofile}\.miaowu"
; 应用图标：setup.exe、卸载程序 unins000.exe 与向导标题栏共用这一份多分辨率 ico。
#define MyAppIcon "..\src\MiaoWu.App\Assets\Branding\MiaoWu.ico"
; 全量包内嵌的 .NET Framework 4.8 离线安装程序；publish.ps1 用 /DDotNetRedist=<绝对路径> 覆盖。
#ifndef DotNetRedist
#define DotNetRedist "..\redist\ndp48-x86-x64-allos-enu.exe"
#endif

[Setup]
AppId={{6C1E4A2B-3D77-4B7E-9A3C-2F5B0D8E1A44}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile={#MyAppIcon}
; 不签名：scripts\publish.ps1 不再传 /DSIGN，产物（含 unins000.exe）保持未签名。
; 需要签名时手工跑 scripts\sign.ps1；想连卸载程序一起签，再自行启用 SignTool 与 SignedUninstaller。
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; 只能提权安装：SetupLdr 会先请求 UAC 再解包，避开低完整性令牌写不了 %TEMP% 的问题
; （2026-11-19 实机验证：普通双击报 "Setup was unable to create the directory ...\is-*.tmp" Error 5，
; 管理员启动正常）。运行期仍不需要管理员，见 [Run] 的 runasoriginaluser。
PrivilegesRequired=admin
OutputDir=..\.artifacts\installer
#ifdef BUNDLE_DOTNET
; 全量包换名字，和日常小包并存，不会互相覆盖。
OutputBaseFilename=MiaoWu-{#MyAppVersion}-setup-offline
#else
OutputBaseFilename=MiaoWu-{#MyAppVersion}-setup
#endif
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
MinVersion=6.1sp1
; 应用是 AnyCPU .NET Framework，x86 / x64 都能跑；x86compatible 即“支持所有 x86 兼容架构”。
ArchitecturesAllowed=x86compatible
; x64 上装进 64 位 Program Files，注册表也写 64 位视图。
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
Name: "startup"; Description: "开机时启动 MiaoWu（托盘）"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "..\src\MiaoWu.App\bin\Release\net48\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef BUNDLE_DOTNET
; 自带 .NET 4.8 离线安装程序：只有目标机缺 4.8 时才会解到 {tmp} 运行（dontcopy + ExtractTemporaryFile），
; 安装器退出时随 {tmp} 一起清掉。nocompression：这份 exe 本身已压缩，再套 lzma2 收益很小却要几分钟编译。
Source: "{#DotNetRedist}"; DestName: "ndp48-x86-x64-allos-enu.exe"; DestDir: "{tmp}"; Flags: dontcopy nocompression
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
; 机器级安装，快捷方式放公共桌面 / 公共启动；开机启动是可选项，安装时默认不勾。
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{commonstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--minimized"; Tasks: startup

[Run]
; runasoriginaluser：装完以“安装前那个用户”的身份启动，不要继承提权令牌，
; 否则首启会以管理员身份创建 .miaowu 并注册到提权的托盘会话里。
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: files; Name: "{app}\*.log"

[Code]
const
  DotNet48Release = 528040;
  DotNetDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet-framework/net48';
#ifdef BUNDLE_DOTNET
  DotNetRedistName = 'ndp48-x86-x64-allos-enu.exe';
#endif

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
#ifndef BUNDLE_DOTNET
var
  ErrorCode: Integer;
#endif
begin
  Result := True;
  if IsDotNet48Installed then
    Exit;
#ifdef BUNDLE_DOTNET
  // 自带离线安装程序：这里只确认一次，真正安装放到 PrepareToInstall（点“安装”之后，有进度条）。
  if MsgBox('本机没有 .NET Framework 4.8。' + #13#10 + #13#10 +
    '本安装包自带 4.8 的离线安装程序，会在正式安装前静默装好：大约 3–10 分钟，装完可能要求重启。' + #13#10 + #13#10 +
    '是否继续？', mbConfirmation, MB_YESNO) <> IDYES then
    Result := False;
#else
  if MsgBox('MiaoWu 需要 .NET Framework 4.8（或更新的 4.x 运行时）。' + #13#10 + #13#10 +
    '是否现在打开下载页？安装完成后请重新运行本安装程序。',
    mbError, MB_YESNO) = IDYES then
  begin
    ShellExec('open', DotNetDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;

  Result := False;
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
#ifdef BUNDLE_DOTNET
var
  RedistPath, LogPath: String;
  ResultCode: Integer;
#endif
begin
  Result := '';
#ifdef BUNDLE_DOTNET
  if IsDotNet48Installed then
    Exit;

  ExtractTemporaryFile(DotNetRedistName);
  RedistPath := ExpandConstant('{tmp}\' + DotNetRedistName);
  LogPath := ExpandConstant('{tmp}\ndp48-install.log');
  WizardForm.StatusLabel.Caption := '正在安装 .NET Framework 4.8，可能需要几分钟…';
  if not Exec(RedistPath, '/quiet /norestart /log "' + LogPath + '"', '',
    SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := '无法启动自带的 .NET Framework 4.8 安装程序。';
    Exit;
  end;

  if (ResultCode = 1641) or (ResultCode = 3010) then
    NeedsRestart := True
  else if ResultCode <> 0 then
  begin
    Result := '安装 .NET Framework 4.8 失败（退出码 ' + IntToStr(ResultCode) + '）。' + #13#10 +
      '安装日志：' + LogPath + #13#10 +
      'Windows 7 需要先装 SHA-2 签名支持更新（KB4474419、KB4490628）后再试。';
    Exit;
  end;

  // 重启挂起时注册表可能还没写全，读不到就提示重启而不是继续装一个跑不起来的副本。
  if not IsDotNet48Installed then
    Result := '安装程序已结束，但注册表里仍读不到 .NET Framework 4.8。请重启后重新运行本安装包。';
#endif
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
