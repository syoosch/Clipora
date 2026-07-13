; Clipora 安装脚本 (Inno Setup 6.3+) — M6.3c
; 分发模型：仅安装包、per-user（PrivilegesRequired=lowest，无需管理员）、未签名（0.1.0 公开预览）。
; 安装内容：单文件 self-contained Clipora.exe（已捆绑 .NET 运行时）。
; 卸载数据策略：
;   标准卸载保留数据目录与 HKCU\Software\Clipora\Storage\DataRoot（重装可续用旧记录）；
;   仅当用户在卸载时确认「同时删除所有剪贴板数据」，才删除数据目录 + 整个 Software\Clipora。
; 由 scripts/build-installer.ps1 调 ISCC 编译；PublishDir 可用 /DPublishDir=... 覆盖。

#define MyAppName "Clipora"
#define MyAppVersion "0.4.3"
#define MyAppPublisher "Clipora"
#define MyAppExeName "Clipora.exe"
#ifndef PublishDir
  #define PublishDir "..\src\Clipora\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif

[Setup]
AppId={{40C94E16-24F8-4925-9101-04C46F5F6267}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=Clipora-{#MyAppVersion}-setup
SetupIconFile=..\src\Clipora\Assets\clipora.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
; 仅安装单文件 EXE（self-contained，含 .NET 运行时）；不打包 PDB。
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  WipeData: Boolean;

{ 强制结束 Clipora 进程：必须用 taskkill /F——Clipora 默认「关闭到托盘」，
  普通关闭只会隐藏到托盘、进程仍占用 exe，导致卸载残留可执行文件。 }
procedure ForceCloseApp();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#MyAppExeName}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { 给系统释放文件句柄与单实例 Mutex 一点时间，再继续删除文件 }
  Sleep(1000);
end;

{ 卸载开始前：若 Clipora 仍在运行（单实例 Mutex，见 App.xaml.cs MutexName "Clipora.SingleInstance"），
  交互式卸载弹窗确认——「是」彻底关闭并继续，「否」终止卸载；静默卸载直接关闭不阻塞。 }
function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not CheckForMutexes('Clipora.SingleInstance') then
    Exit;

  if UninstallSilent() then
  begin
    ForceCloseApp();
    Exit;
  end;

  if MsgBox(
       'Clipora 仍在运行，必须先彻底关闭才能完成卸载。' + #13#10#13#10 +
       '是：关闭 Clipora 并继续卸载。' + #13#10 +
       '否：取消卸载，不做任何改动。',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES then
    ForceCloseApp()
  else
    Result := False;  { 用户取消 → 终止卸载 }
end;

{ 解析当前数据目录：优先 HKCU 注册表 locator DataRoot，否则默认 %LOCALAPPDATA%\Clipora }
function GetDataDir(): String;
var
  Root: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Clipora\Storage', 'DataRoot', Root) and (Root <> '') then
    Result := Root
  else
    Result := ExpandConstant('{localappdata}\Clipora');
end;

{ 安全删除闸门：仅当目录内确有 clipora.db 才认定为 Clipora 数据目录，避免误删任意路径 }
function LooksLikeCliporaData(Dir: String): Boolean;
begin
  Result := (Dir <> '') and FileExists(AddBackslash(Dir) + 'clipora.db');
end;

procedure DeleteRunEntry();
begin
  { 移除指向已卸载 exe 的开机自启项（值名固定为 Clipora，见 AutoStartService） }
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Clipora');
end;

procedure CurUninstallStepChanged(CurStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurStep = usUninstall then
  begin
    { 静默卸载默认保留数据（最安全）；交互式才询问是否彻底清理 }
    if not UninstallSilent() then
    begin
      if MsgBox(
           '是否同时删除所有剪贴板数据（历史记录、图片、设置）？' + #13#10#13#10 +
           '否：保留数据目录，重新安装后可继续使用旧记录（推荐）。' + #13#10 +
           '是：永久删除全部剪贴板数据，无法恢复。',
           mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        WipeData := True;
    end;
  end
  else if CurStep = usPostUninstall then
  begin
    { 标准卸载：始终移除开机自启项，但保留数据目录与 Storage\DataRoot 以便重装续用 }
    DeleteRunEntry();

    if WipeData then
    begin
      DataDir := GetDataDir();
      if LooksLikeCliporaData(DataDir) then
        DelTree(DataDir, True, True, True);
      { 彻底清理：删除整个 Software\Clipora（含 Storage\DataRoot） }
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Clipora');
    end;
  end;
end;
