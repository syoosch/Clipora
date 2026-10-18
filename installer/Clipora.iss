; Clipora 安装脚本 (Inno Setup 6.3+) — M6.3c
; 分发模型：仅安装包、per-user（PrivilegesRequired=lowest，无需管理员）、未签名（0.1.0 公开预览）。
; 安装内容：单文件 self-contained Clipora.exe（已捆绑 .NET 运行时）。
; 卸载数据策略：
;   标准卸载保留数据目录与 HKCU\Software\Clipora\Storage\DataRoot（重装可续用旧记录）；
;   仅当用户在卸载时确认「同时删除所有剪贴板数据」，才删除数据目录 + 整个 Software\Clipora。
; 由 scripts/build-installer.ps1 调 ISCC 编译；PublishDir 可用 /DPublishDir=... 覆盖。

#define MyAppName "Clipora"
#define MyAppVersion "0.5.1"
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

[Messages]
; English-first bilingual presentation: preserve Inno Setup's familiar English,
; then add concise Simplified Chinese in the same control so no language picker is needed.
en.SetupAppTitle=Setup / 安装
en.SetupWindowTitle=Setup - %1 / 安装 %1
en.UninstallAppTitle=Uninstall / 卸载
en.UninstallAppFullTitle=%1 Uninstall / 卸载 %1
en.SetupAppRunningError=Setup has detected that %1 is currently running.%n%nPlease close all instances of it now, then click OK to continue, or Cancel to exit.%n%n安装程序检测到 %1 正在运行。%n%n请先关闭所有实例，然后点击“OK / 确定”继续，或点击“Cancel / 取消”退出。
en.ExitSetupTitle=Exit Setup / 退出安装
en.ExitSetupMessage=Setup is not complete. If you exit now, the program will not be installed.%n%nYou may run Setup again at another time to complete the installation.%n%nExit Setup?%n%n安装尚未完成。如果现在退出，程序将不会被安装。%n%n你可以稍后再次运行安装程序来完成安装。%n%n现在退出安装吗？
en.ButtonBack=< &Back / 上一步
en.ButtonNext=&Next > / 下一步
en.ButtonInstall=&Install / 安装
en.ButtonOK=OK / 确定
en.ButtonCancel=Cancel / 取消
en.ButtonYes=&Yes / 是
en.ButtonNo=&No / 否
en.ButtonFinish=&Finish / 完成
en.ButtonBrowse=&Browse... / 浏览...
en.ButtonWizardBrowse=B&rowse... / 浏览...
en.WelcomeLabel1=Welcome to the [name] Setup Wizard%n欢迎使用 [name] 安装向导
en.WelcomeLabel2=This will install [name/ver] on your computer.%n%nIt is recommended that you close all other applications before continuing.%n%n这将在你的电脑上安装 [name/ver]。%n%n建议先关闭其他应用程序，然后继续。
en.ClickNext=Click Next to continue, or Cancel to exit Setup.%n点击“Next / 下一步”继续，或点击“Cancel / 取消”退出安装。
en.WizardSelectDir=Select Destination Location / 选择安装位置
en.SelectDirDesc=Where should [name] be installed? / [name] 应安装到哪里？
en.SelectDirLabel3=Setup will install [name] into the following folder.%n安装程序将把 [name] 安装到以下文件夹。
en.SelectDirBrowseLabel=To continue, click Next. If you would like to select a different folder, click Browse.%n点击“Next / 下一步”继续；如需选择其他文件夹，请点击“Browse / 浏览”。
en.WizardSelectTasks=Select Additional Tasks / 选择附加任务
en.SelectTasksDesc=Which additional tasks should be performed? / 请选择需要执行的附加任务。
en.SelectTasksLabel2=Select the additional tasks you would like Setup to perform while installing [name], then click Next.%n请选择安装 [name] 时需要执行的附加任务，然后点击“Next / 下一步”。
en.WizardReady=Ready to Install / 准备安装
en.ReadyLabel1=Setup is now ready to begin installing [name] on your computer.%n安装程序已准备好在你的电脑上安装 [name]。
en.ReadyLabel2a=Click Install to continue with the installation, or click Back if you want to review or change any settings.%n点击“Install / 安装”继续；如需检查或更改设置，请点击“Back / 上一步”。
en.ReadyLabel2b=Click Install to continue with the installation.%n点击“Install / 安装”继续。
en.ReadyMemoDir=Destination location / 安装位置:
en.ReadyMemoGroup=Start Menu folder / 开始菜单文件夹:
en.ReadyMemoTasks=Additional tasks / 附加任务:
en.WizardPreparing=Preparing to Install / 正在准备安装
en.PreparingDesc=Setup is preparing to install [name] on your computer.%n安装程序正在准备在你的电脑上安装 [name]。
en.WizardInstalling=Installing / 正在安装
en.InstallingLabel=Please wait while Setup installs [name] on your computer.%n安装程序正在安装 [name]，请稍候。
en.StatusClosingApplications=Closing applications... / 正在关闭应用程序...
en.StatusCreateDirs=Creating directories... / 正在创建文件夹...
en.StatusExtractFiles=Extracting files... / 正在解压文件...
en.StatusCreateIcons=Creating shortcuts... / 正在创建快捷方式...
en.StatusCreateRegistryEntries=Creating registry entries... / 正在写入注册表...
en.StatusSavingUninstall=Saving uninstall information... / 正在保存卸载信息...
en.StatusRunProgram=Finishing installation... / 正在完成安装...
en.StatusRollback=Rolling back changes... / 正在回滚更改...
en.FinishedHeadingLabel=Completing the [name] Setup Wizard%n[name] 安装向导即将完成
en.FinishedLabelNoIcons=Setup has finished installing [name] on your computer.%n[name] 已成功安装到你的电脑。
en.FinishedLabel=Setup has finished installing [name] on your computer. The application may be launched by selecting the installed shortcuts.%n[name] 已成功安装到你的电脑，可通过已创建的快捷方式启动。
en.ClickFinish=Click Finish to exit Setup.%n点击“Finish / 完成”退出安装程序。
en.ConfirmUninstall=Are you sure you want to completely remove %1 and all of its components?%n%n确定要完全卸载 %1 及其所有组件吗？
en.UninstallStatusLabel=Please wait while %1 is removed from your computer.%n正在从你的电脑中卸载 %1，请稍候。
en.UninstalledAll=%1 was successfully removed from your computer.%n已成功从你的电脑中卸载 %1。
en.UninstalledMost=%1 uninstall complete.%n%nSome elements could not be removed. These can be removed manually.%n%n%1 已完成卸载。%n%n部分内容无法自动删除，可稍后手动清理。
en.WizardUninstalling=Uninstall Status / 卸载状态
en.StatusUninstalling=Uninstalling %1... / 正在卸载 %1...

[CustomMessages]
en.AdditionalIcons=Additional shortcuts: / 附加快捷方式：
en.CreateDesktopIcon=Create a &desktop shortcut / 创建桌面快捷方式
en.LaunchProgram=Launch %1 / 启动 %1
; Used as a shortcut file name: do not use slash separators in this message.
en.UninstallProgram=Uninstall %1 - 卸载 %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
; 仅安装单文件 EXE（self-contained，含 .NET 运行时）；不打包 PDB。
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  WipeData: Boolean;

{ Bilingual navigation labels need slightly wider buttons than the English defaults.
  Keep the original right-aligned grouping and widen the folder browser without changing page geometry. }
procedure InitializeWizard();
var
  ButtonWidth, Gap, RightEdge, BrowseWidth: Integer;
begin
  ButtonWidth := ScaleX(108);
  Gap := ScaleX(8);

  WizardForm.CancelButton.Width := ButtonWidth;
  WizardForm.NextButton.Width := ButtonWidth;
  WizardForm.BackButton.Width := ButtonWidth;
  WizardForm.CancelButton.Left := WizardForm.ClientWidth - ScaleX(10) - ButtonWidth;
  WizardForm.NextButton.Left := WizardForm.CancelButton.Left - Gap - ButtonWidth;
  WizardForm.BackButton.Left := WizardForm.NextButton.Left - Gap - ButtonWidth;

  RightEdge := WizardForm.DirBrowseButton.Left + WizardForm.DirBrowseButton.Width;
  BrowseWidth := ScaleX(118);
  WizardForm.DirBrowseButton.Left := RightEdge - BrowseWidth;
  WizardForm.DirBrowseButton.Width := BrowseWidth;
  WizardForm.DirEdit.Width := WizardForm.DirBrowseButton.Left - WizardForm.DirEdit.Left - Gap;
end;

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
       'Clipora is still running and must be closed before uninstalling.' + #13#10#13#10 +
       'Yes: close Clipora and continue.' + #13#10 +
       'No: cancel without making changes.' + #13#10#13#10 +
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
           'Also delete all clipboard data (history, images, and settings)?' + #13#10#13#10 +
           'No: keep the data so it can be reused after reinstalling (recommended).' + #13#10 +
           'Yes: permanently delete all clipboard data. This cannot be undone.' + #13#10#13#10 +
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
