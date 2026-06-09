; OpenClaw Companion Inno Setup Script (WinUI version)
#define MyAppName "OpenClaw Companion"
#define MyAppPublisher "Scott Hanselman"
#define MyAppURL "https://github.com/openclaw/openclaw-windows-node"
#define MyAppExeName "OpenClaw.Tray.WinUI.exe"

; MyAppArch should be passed via /DMyAppArch=x64 or /DMyAppArch=arm64
#ifndef MyAppArch
  #define MyAppArch "x64"
#endif

#ifndef MyCompression
  #define MyCompression "lzma"
#endif

#ifndef MySolidCompression
  #define MySolidCompression "yes"
#endif

[Setup]
; Inno requires "{{" to emit a literal opening brace in AppId.
; Do not add a second closing brace here; that creates a malformed uninstall registry key.
AppId={{M0LTB0T-TRAY-4PP1-D3N7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://github.com/openclaw/openclaw-windows-node/issues
AppUpdatesURL=https://github.com/openclaw/openclaw-windows-node/releases
DefaultDirName={localappdata}\OpenClawTray
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=OpenClawCompanion-Setup-{#MyAppArch}
Compression={#MyCompression}
SolidCompression={#MySolidCompression}
WizardStyle=modern
PrivilegesRequired=lowest
SetupIconFile=src\OpenClaw.Tray.WinUI\Assets\openclaw.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; Round 2 (Scott #5): block install/uninstall while the tray is running.
; Mutex name matches App.xaml.cs (`new Mutex(true, "OpenClawTray", …)`).
; Tray and Inno run in the same user session, so the bare name resolves
; against Local\OpenClawTray — no Global\ prefix needed.
AppMutex=OpenClawTray
#if MyAppArch == "arm64"
ArchitecturesInstallIn64BitMode=arm64
ArchitecturesAllowed=arm64
#else
ArchitecturesInstallIn64BitMode=x64
ArchitecturesAllowed=x64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=65001
DialogFontName=Microsoft YaHei UI
WelcomeFontName=Microsoft YaHei UI

[Messages]
SetupAppTitle=安装
SetupWindowTitle=安装 - %1
UninstallAppTitle=卸载
UninstallAppFullTitle=%1 卸载
InformationTitle=信息
ConfirmTitle=确认
ErrorTitle=错误
SetupLdrStartupMessage=即将安装 %1。是否继续?
LdrCannotCreateTemp=无法创建临时文件。安装已中止
LdrCannotExecTemp=无法执行临时目录中的文件。安装已中止
SetupAlreadyRunning=安装程序已在运行。
SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请关闭它的所有实例，然后单击“确定”继续，或单击“取消”退出。
UninstallAppRunningError=卸载程序检测到 %1 正在运行。%n%n请关闭它的所有实例，然后单击“确定”继续，或单击“取消”退出。
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，程序将不会被安装。%n%n您可以稍后再次运行安装程序完成安装。%n%n是否退出安装?
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonOK=确定
ButtonCancel=取消
ButtonYes=是(&Y)
ButtonNo=否(&N)
ButtonFinish=完成(&F)
ButtonBrowse=浏览(&B)...
ButtonWizardBrowse=浏览(&R)...
ClickNext=单击“下一步”继续，或单击“取消”退出安装程序。
BrowseDialogTitle=浏览文件夹
BrowseDialogLabel=请在列表中选择一个文件夹，然后单击“确定”。
NewFolderName=新建文件夹
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=此向导将在您的电脑上安装 [name/ver]。%n%n建议您在继续之前关闭其他应用程序。
WizardSelectDir=选择安装位置
SelectDirDesc=[name] 应安装到哪里?
SelectDirLabel3=安装程序会将 [name] 安装到以下文件夹。
SelectDirBrowseLabel=单击“下一步”继续。如果要选择其他文件夹，请单击“浏览”。
DiskSpaceGBLabel=至少需要 [gb] GB 可用磁盘空间。
DiskSpaceMBLabel=至少需要 [mb] MB 可用磁盘空间。
CannotInstallToNetworkDrive=安装程序不能安装到网络驱动器。
CannotInstallToUNCPath=安装程序不能安装到 UNC 路径。
InvalidPath=必须输入带驱动器号的完整路径，例如:%n%nC:\APP%n%n或 UNC 路径:%n%n\\server\share
InvalidDrive=选择的驱动器或 UNC 共享不存在或无法访问。请选择其他位置。
DiskSpaceWarningTitle=磁盘空间不足
DiskSpaceWarning=安装程序至少需要 %1 KB 可用空间，但所选驱动器只有 %2 KB 可用。%n%n是否仍要继续?
DirExistsTitle=文件夹已存在
DirExists=文件夹:%n%n%1%n%n已存在。是否仍要安装到该文件夹?
DirDoesntExistTitle=文件夹不存在
DirDoesntExist=文件夹:%n%n%1%n%n不存在。是否创建此文件夹?
WizardSelectTasks=选择附加任务
SelectTasksDesc=需要执行哪些附加任务?
SelectTasksLabel2=请选择安装 [name] 时要执行的附加任务，然后单击“下一步”。
WizardReady=准备安装
ReadyLabel1=安装程序已准备好开始在您的电脑上安装 [name]。
ReadyLabel2a=单击“安装”继续；如果要检查或更改设置，请单击“上一步”。
ReadyLabel2b=单击“安装”继续。
ReadyMemoDir=安装位置:
ReadyMemoGroup=开始菜单文件夹:
ReadyMemoTasks=附加任务:
WizardPreparing=正在准备安装
PreparingDesc=安装程序正在准备在您的电脑上安装 [name]。
CannotContinue=安装程序无法继续。请单击“取消”退出。
WizardInstalling=正在安装
InstallingLabel=请稍候，安装程序正在将 [name] 安装到您的电脑上。
FinishedHeadingLabel=正在完成 [name] 安装向导
FinishedLabelNoIcons=安装程序已完成 [name] 的安装。
FinishedLabel=安装程序已完成 [name] 的安装。您可以通过已创建的快捷方式启动应用。
ClickFinish=单击“完成”退出安装程序。
RunEntryExec=运行 %1
SetupAborted=安装未完成。%n%n请修正问题后重新运行安装程序。
StatusClosingApplications=正在关闭应用程序...
StatusCreateDirs=正在创建目录...
StatusExtractFiles=正在解压文件...
StatusCreateIcons=正在创建快捷方式...
StatusCreateRegistryEntries=正在创建注册表项...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成安装...
StatusRollback=正在回滚更改...
ErrorExecutingProgram=无法执行文件:%n%1
UninstallNotFound=文件“%1”不存在，无法卸载。
ConfirmUninstall=确定要完全移除 %1 及其所有组件吗?
UninstallStatusLabel=请稍候，正在从您的电脑移除 %1。
UninstalledAll=%1 已成功从您的电脑移除。
UninstalledMost=%1 卸载完成。%n%n某些项目未能移除，可以手动删除。
UninstalledAndNeedsRestart=要完成 %1 的卸载，必须重启电脑。%n%n是否现在重启?
WizardUninstalling=卸载状态
StatusUninstalling=正在卸载 %1...

[CustomMessages]
AdditionalIcons=附加快捷方式:
CreateDesktopIcon=创建桌面快捷方式(&D)
UninstallProgram=卸载 %1
LaunchProgram=启动 %1
AutoStartProgramGroupDescription=启动:

; publish folder should be passed via /Dpublish=publish-x64 or /Dpublish=publish-arm64
#ifndef publish
  #define publish "publish"
#endif

#if !FileExists(publish + "\OpenClaw.Tray.WinUI.exe")
  #error Tray payload missing. Publish OpenClaw.Tray.WinUI before compiling the installer.
#endif

#if FileExists(publish + "\SetupEngine\OpenClaw.SetupEngine.UI.exe")
  #error SetupEngine.UI.exe should not be shipped. Setup UI is hosted by OpenClaw.Tray.WinUI.exe.
#endif

; vcRedist should point at the architecture-matching Visual C++ Runtime
; redistributable in CI release builds.
#ifndef vcRedist
  #define vcRedist ""
#endif

[Files]
; WinUI Tray app - include all files (WinUI needs DLLs, not single-file)
Source: "{#publish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
; WSL gateway uninstall helper copied to {tmp} by [Code] during uninstall.
Source: "scripts\Uninstall-LocalGateway.ps1"; DestDir: "{app}"; Flags: ignoreversion
#if vcRedist != ""
Source: "{#vcRedist}"; DestDir: "{tmp}"; DestName: "vc_redist.exe"; Flags: deleteafterinstall; AfterInstall: InstallVCRuntime
#endif

[Registry]
Root: HKCU; Subkey: "Software\Classes\openclaw"; ValueType: string; ValueName: ""; ValueData: "URL:OpenClaw Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\openclaw"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\openclaw\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\openclaw\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\OpenClaw 网关设置"; Filename: "{app}\{#MyAppExeName}"; Parameters: "openclaw://setup"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\OpenClaw Companion 设置"; Filename: "{app}\{#MyAppExeName}"; Parameters: "openclaw://commandcenter"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\OpenClaw 聊天"; Filename: "{app}\{#MyAppExeName}"; Parameters: "openclaw://chat"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifsilent; Check: ShouldLaunchTray

[Code]
var
  VCRuntimeInstallSucceeded: Boolean;
  LocalGatewayCleanupChoiceInitialized: Boolean;
  LocalGatewayCleanupRequested: Boolean;
  LocalGatewayCleanupSucceeded: Boolean;

#if vcRedist != ""
procedure InstallVCRuntime;
var
  ResultCode: Integer;
  Started: Boolean;
begin
  VCRuntimeInstallSucceeded := False;
  Log('Running bundled Visual C++ Runtime redistributable.');
  Started :=
    Exec(
      ExpandConstant('{tmp}\vc_redist.exe'),
      '/install /quiet /norestart',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);

  if not Started then
  begin
    Log('Failed to start Visual C++ Runtime redistributable. System error: ' + IntToStr(ResultCode) + '.');
    Exit;
  end;

  VCRuntimeInstallSucceeded := (ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1641);
  if VCRuntimeInstallSucceeded then
    Log('Visual C++ Runtime redistributable exited with success code ' + IntToStr(ResultCode) + '.')
  else
    Log('Visual C++ Runtime redistributable failed with exit code ' + IntToStr(ResultCode) + '.');
end;
#endif

function ShouldLaunchTray: Boolean;
begin
#if vcRedist != ""
  Result := VCRuntimeInstallSucceeded;
  if not Result then
    Log('Skipping post-install tray launch because Visual C++ Runtime installation did not succeed.');
#else
  Result := True;
#endif
end;

procedure EnsureLocalGatewayCleanupChoice;
begin
  if LocalGatewayCleanupChoiceInitialized then
    Exit;

  LocalGatewayCleanupChoiceInitialized := True;

  if UninstallSilent() then
  begin
    LocalGatewayCleanupRequested := True;
    Log('Silent uninstall: local gateway cleanup will run automatically.');
  end
  else
  begin
    LocalGatewayCleanupRequested :=
      MsgBox(
        '是否同时移除 OpenClaw 本地 WSL 网关?' + #13#10#13#10 +
        '选择“是”将注销 OpenClawGateway WSL 发行版，并删除生成的本地网关状态。' + #13#10 +
        '选择“否”将保留此电脑上的本地网关和生成状态。',
        mbConfirmation,
        MB_YESNO) = IDYES;

    if LocalGatewayCleanupRequested then
      Log('User chose to remove the local WSL gateway.')
    else
      Log('User chose to preserve the local WSL gateway and generated state.');
  end;
end;

function RunLocalGatewayCleanupOnce(var ResultCode: Integer): Boolean;
var
  SourceScriptPath: string;
  TempScriptPath: string;
  Params: string;
begin
  SourceScriptPath := ExpandConstant('{app}\Uninstall-LocalGateway.ps1');
  TempScriptPath := ExpandConstant('{tmp}\Uninstall-LocalGateway.ps1');

  if not FileExists(SourceScriptPath) then
  begin
    ResultCode := 2;
    Log('Local gateway cleanup script is missing: ' + SourceScriptPath);
    Result := False;
    Exit;
  end;

  if FileExists(TempScriptPath) then
    DeleteFile(TempScriptPath);

  if not CopyFile(SourceScriptPath, TempScriptPath, False) then
  begin
    ResultCode := 3;
    Log('Failed to copy local gateway cleanup script to: ' + TempScriptPath);
    Result := False;
    Exit;
  end;

  Params :=
    '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(TempScriptPath) +
    ' -AppRoot ' + AddQuotes(ExpandConstant('{app}'));

  Log('Running local gateway cleanup script from {tmp}.');
  Result :=
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      Params,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);

  if Result then
    Log('Local gateway cleanup script exited with code ' + IntToStr(ResultCode) + '.')
  else
    Log('Failed to start local gateway cleanup script. System error: ' + IntToStr(ResultCode) + '.');
end;

procedure RunLocalGatewayCleanup;
var
  ResultCode: Integer;
  Retry: Boolean;
  Started: Boolean;
begin
  if not LocalGatewayCleanupRequested then
    Exit;

  LocalGatewayCleanupSucceeded := False;

  repeat
    Retry := False;
    UninstallProgressForm.StatusLabel.Caption := '正在移除本地 WSL 网关...';
    Started := RunLocalGatewayCleanupOnce(ResultCode);

    if Started and (ResultCode = 0) then
    begin
      LocalGatewayCleanupSucceeded := True;
      Log('Local gateway cleanup completed successfully.');
      Exit;
    end;

    if UninstallSilent() then
    begin
      Log('Local gateway cleanup failed during silent uninstall; continuing without deleting generated state.');
      Exit;
    end;

    Retry :=
      MsgBox(
        'OpenClaw 无法移除本地 WSL 网关。' + #13#10#13#10 +
        '退出代码: ' + IntToStr(ResultCode) + #13#10#13#10 +
        '选择“重试”再次尝试，或选择“取消”继续卸载 OpenClaw，并将本地网关状态保留在磁盘上。',
        mbError,
        MB_RETRYCANCEL) = IDRETRY;
  until not Retry;

  Log('User continued uninstall after local gateway cleanup failed; generated state will be preserved.');
end;

procedure DeleteGeneratedAppState;
begin
  if not LocalGatewayCleanupSucceeded then
    Exit;

  if DelTree(ExpandConstant('{app}'), True, True, True) then
    Log('Deleted generated app state from {app}.')
  else
    Log('Generated app state in {app} could not be fully deleted; continuing uninstall.');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    EnsureLocalGatewayCleanupChoice;
    RunLocalGatewayCleanup;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DeleteGeneratedAppState;
  end;
end;
