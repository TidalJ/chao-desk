; Chao Desk installer (Inno Setup 6). Built by `./build.ps1 -Package`.
; Per-user install (no admin rights) to %LOCALAPPDATA%\Programs\ChaoDesk; user data lives in %LOCALAPPDATA%\ChaoDesk.

#ifndef AppVersion
  #define AppVersion "1.0.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Chao Desk"
#define AppExe "ChaoDesk.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall"
#define AppIdKey "{7CBE2AA3-F1DE-4CCC-9A2D-EAAB8C49DEDB}"
#define IndexerTask "\Chao Desk\File index"
#define WebView2Key "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
; Microsoft's own installers, always the latest: the .NET 8 Desktop Runtime (x64) and the WebView2 bootstrapper.
#define DotNetInstallerUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
#define WebView2InstallerUrl "https://go.microsoft.com/fwlink/p/?LinkId=2124703"

[Setup]
AppId={{7CBE2AA3-F1DE-4CCC-9A2D-EAAB8C49DEDB}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Chao Desk
DefaultDirName={localappdata}\Programs\ChaoDesk
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=ChaoDeskSetup-{#AppVersion}
SetupIconFile=..\host\src\ChaoDesk.Host\Assets\chaodesk.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; Tell Explorer to refresh its icon cache, so an upgraded app icon shows on the taskbar and in Task Manager.
ChangesAssociations=yes
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; We stop the app ourselves (ChaoDesk.exe --exit) so the desktop is restored cleanly.
CloseApplications=no
RestartApplications=no

[Languages]
; Setup picks the language from the Windows display language (Inno's default detection).
; ChineseSimplified.isl: Inno Setup's translation (THIRD-PARTY-NOTICES.md), saved as UTF-8 with a BOM.
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
english.TaskStartup=Start Chao Desk when I sign in
english.GroupStartup=Startup:
english.TaskDesktopIcon=Create a desktop shortcut
english.GroupShortcuts=Shortcuts:
english.IconComment=Launcher, clipboard history, file search and desktop wallpaper
english.LaunchNow=Launch %1 now
english.ReadyPrereqs=Also downloaded from Microsoft and installed:
english.DotNetName=Microsoft .NET 8 Desktop Runtime (about 56 MB; Windows asks for permission)
english.WebView2Name=Microsoft Edge WebView2 Runtime (online installer; needed by wallpaper and previews)
english.InstallingDotNet=Installing the Microsoft .NET 8 Desktop Runtime…
english.InstallingWebView2=Installing the Microsoft Edge WebView2 Runtime…
english.PrereqFailed=%1 could not be installed (code %2). Chao Desk has not been installed or updated. Allow the Windows permission prompt, or install the runtime from Microsoft and try again.
english.PrereqNotDetected=%1 is still missing after its installer finished. Chao Desk has not been installed or updated. Repair the runtime from Microsoft, then try again.
english.PrereqDownloadFailed=The required Microsoft runtime could not be downloaded: %1. Chao Desk has not been installed or updated. Check your internet connection and try again, or install the runtime from Microsoft first.
english.PrereqCancelled=The runtime download was cancelled. Chao Desk has not been installed or updated.
english.PrereqRestart=%1 requires a Windows restart. Restart Windows, then run this installer again. Chao Desk has not been installed or updated.
english.DeleteUserData=Also delete your Chao Desk settings, clipboard history, logs and imported wallpapers?
chinesesimplified.TaskStartup=开机自动启动 Chao Desk
chinesesimplified.GroupStartup=启动：
chinesesimplified.TaskDesktopIcon=创建桌面快捷方式
chinesesimplified.GroupShortcuts=快捷方式：
chinesesimplified.IconComment=启动器、剪贴板历史、文件搜索和桌面壁纸
chinesesimplified.LaunchNow=立即运行 %1
chinesesimplified.ReadyPrereqs=同时从微软下载并安装：
chinesesimplified.DotNetName=Microsoft .NET 8 桌面运行时（约 56 MB，Windows 会请求权限）
chinesesimplified.WebView2Name=Microsoft Edge WebView2 运行时（在线安装，壁纸和预览需要）
chinesesimplified.InstallingDotNet=正在安装 Microsoft .NET 8 桌面运行时…
chinesesimplified.InstallingWebView2=正在安装 Microsoft Edge WebView2 运行时…
chinesesimplified.PrereqFailed=%1 未能安装（错误码 %2）。Chao Desk 尚未安装或更新。请允许 Windows 权限请求，或先从微软官网安装运行时，然后重试。
chinesesimplified.PrereqNotDetected=%1 的安装程序已结束，但仍未检测到所需运行时。Chao Desk 尚未安装或更新。请先从微软官网修复运行时，然后重试。
chinesesimplified.PrereqDownloadFailed=无法下载所需的微软运行时：%1。Chao Desk 尚未安装或更新。请检查网络连接后重试，或先从微软官网安装运行时。
chinesesimplified.PrereqCancelled=已取消运行时下载。Chao Desk 尚未安装或更新。
chinesesimplified.PrereqRestart=%1 需要重启 Windows。请重启后再次运行安装程序。Chao Desk 尚未安装或更新。
chinesesimplified.DeleteUserData=同时删除 Chao Desk 的设置、剪贴板历史、日志和导入的壁纸吗？

[Tasks]
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:GroupStartup}"; Check: not IsUpgrade
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:GroupShortcuts}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:IconComment}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The app reads this on first run and turns its own "Start with Windows" setting on.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "ChaoDesk"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchNow,{#AppName}}"; Flags: nowait postinstall skipifsilent
; The app's own updater runs this installer with /SILENT /RELAUNCH=1 and has exited: start the new version.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: RelaunchRequested

[UninstallDelete]
; Files the app may create next to itself (none today, but never leave the folder behind).
Type: filesandordirs; Name: "{app}"

[Code]
#include "runtime-checks.iss"

function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, '{#WebView2Key}', 'pv', Version) and IsWebView2Version(Version)) or
    (RegQueryStringValue(HKCU, '{#WebView2Key}', 'pv', Version) and IsWebView2Version(Version));
end;

{ Chao Desk is already installed: its own "Start with Windows" setting decides, the installer does not ask again. }
function IsUpgrade(): Boolean;
begin
  Result := RegKeyExists(HKCU, '{#UninstallKey}\{#AppIdKey}_is1');
end;

function RelaunchRequested(): Boolean;
begin
  Result := WizardSilent() and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

function DotNetDesktopInstalled(): Boolean;
var
  Root: String;
begin
  { .NET registers every architecture in the 32-bit registry view. Inspect files too,
    so stale registry entries and SDK-only folders cannot satisfy the prerequisite. }
  if RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) and
    (Root <> '') then
    Result := DotNetDesktopAtRoot(Root)
  else if ProcessorArchitecture = paArm64 then
    Result := DotNetDesktopAtRoot(ExpandConstant('{commonpf64}\dotnet\x64'))
  else
    Result := DotNetDesktopAtRoot(ExpandConstant('{commonpf64}\dotnet'));
end;

{ ---- Missing runtimes: downloaded from Microsoft and installed before Chao Desk ---- }

var
  DownloadPage: TDownloadWizardPage;
  NeedDotNet, NeedWebView2: Boolean;
  RuntimeRestartMessage: String;

function InitializeSetup(): Boolean;
begin
  NeedDotNet := not DotNetDesktopInstalled();
  NeedWebView2 := not WebView2Installed();
  Result := True;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

{ The Ready page lists what will be added, so the permission prompt for .NET is no surprise. }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoGroupInfo <> '' then Result := Result + NewLine + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
  if NeedDotNet or NeedWebView2 then
  begin
    Result := Result + NewLine + NewLine + CustomMessage('ReadyPrereqs');
    if NeedDotNet then Result := Result + NewLine + Space + CustomMessage('DotNetName');
    if NeedWebView2 then Result := Result + NewLine + Space + CustomMessage('WebView2Name');
  end;
end;

{ Return an error to PrepareToInstall before any app files or running processes are touched. }
function RunPrereq(const FileName, Params, Name: String; IsDotNet: Boolean;
  var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Ok, Installed: Boolean;
  InstallerPath: String;
begin
  InstallerPath := AddBackslash(ExpandConstant('{tmp}')) + FileName;
  if IsDotNet then
    { Only the system-wide .NET runtime needs elevation; Chao Desk remains per-user. }
    Ok := ShellExec('runas', InstallerPath, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
  else
    Ok := Exec(InstallerPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if IsDotNet then Installed := DotNetDesktopInstalled() else Installed := WebView2Installed();
  Log(Format('%s finished: started=%d code=%d detected=%d', [FileName, Ord(Ok), ResultCode, Ord(Installed)]));
  Result := RuntimeInstallResult(Name, Ok, ResultCode, Installed, NeedsRestart);
  if NeedsRestart then RuntimeRestartMessage := Result;
end;

function InstallMissingRuntimes(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if RuntimeRestartMessage <> '' then
  begin
    NeedsRestart := True;
    Result := RuntimeRestartMessage;
    Exit;
  end;
  { Recheck on every attempt, including silent updates and retries after a failed download. }
  NeedDotNet := not DotNetDesktopInstalled();
  NeedWebView2 := not WebView2Installed();
  if not (NeedDotNet or NeedWebView2) then Exit;
  DownloadPage.Clear;
  if NeedDotNet then DownloadPage.Add('{#DotNetInstallerUrl}', 'windowsdesktop-runtime-8-win-x64.exe', '');
  if NeedWebView2 then DownloadPage.Add('{#WebView2InstallerUrl}', 'MicrosoftEdgeWebview2Setup.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if NeedDotNet then
      begin
        DownloadPage.SetText(CustomMessage('InstallingDotNet'), '');
        Result := RunPrereq('windowsdesktop-runtime-8-win-x64.exe', '/install /quiet /norestart',
          'Microsoft .NET 8 Desktop Runtime (x64)', True, NeedsRestart);
        if Result <> '' then Exit;
      end;
      if NeedWebView2 then
      begin
        DownloadPage.SetText(CustomMessage('InstallingWebView2'), '');
        Result := RunPrereq('MicrosoftEdgeWebview2Setup.exe', '/silent /install',
          'Microsoft Edge WebView2 Runtime', False, NeedsRestart);
      end;
    except
      Log('Runtime download failed: ' + GetExceptionMessage);
      if DownloadPage.AbortedByUser then
        Result := CustomMessage('PrereqCancelled')
      else
        Result := FmtMessage(CustomMessage('PrereqDownloadFailed'), [GetExceptionMessage]);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

{ Ask a running Chao Desk to exit cleanly (restores the normal wallpaper), then make sure it is gone. }
procedure StopRunningApp(const ExePath: String);
var
  ResultCode: Integer;
begin
  if FileExists(ExePath) then
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := InstallMissingRuntimes(NeedsRestart);
  if Result <> '' then Exit;
  StopRunningApp(ExpandConstant('{app}\{#AppExe}'));
end;

{ The file search task was created with administrator rights; removing it needs them too (one prompt). }
procedure RemoveIndexerTask();
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "{#IndexerTask}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then Exit;
  ShellExec('runas', ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "{#IndexerTask}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp(ExpandConstant('{app}\{#AppExe}'));
    { The app can add the Run entry itself (Settings > Start with Windows): always remove it. }
    RegDeleteValue(HKCU, '{#RunKey}', 'ChaoDesk');
    RemoveIndexerTask();
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\ChaoDesk');
    if DirExists(DataDir) then
    begin
      if UninstallSilent() then
        Exit; { silent uninstall keeps user data }
      if MsgBox(CustomMessage('DeleteUserData') + #13#10#13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
