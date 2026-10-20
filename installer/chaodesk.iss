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
english.WebView2Name=Microsoft Edge WebView2 Runtime (about 2 MB; needed by the wallpaper)
english.InstallingDotNet=Installing the Microsoft .NET 8 Desktop Runtime…
english.InstallingWebView2=Installing the Microsoft Edge WebView2 Runtime…
english.PrereqFailed=%1 could not be installed (%2). Chao Desk is installed anyway; install it from Microsoft's website, then start Chao Desk.
english.DeleteUserData=Also delete your Chao Desk settings, clipboard history, logs and imported wallpapers?
chinesesimplified.TaskStartup=开机自动启动 Chao Desk
chinesesimplified.GroupStartup=启动：
chinesesimplified.TaskDesktopIcon=创建桌面快捷方式
chinesesimplified.GroupShortcuts=快捷方式：
chinesesimplified.IconComment=启动器、剪贴板历史、文件搜索和桌面壁纸
chinesesimplified.LaunchNow=立即运行 %1
chinesesimplified.ReadyPrereqs=同时从微软下载并安装：
chinesesimplified.DotNetName=Microsoft .NET 8 桌面运行时（约 56 MB，Windows 会请求权限）
chinesesimplified.WebView2Name=Microsoft Edge WebView2 运行时（约 2 MB，壁纸需要）
chinesesimplified.InstallingDotNet=正在安装 Microsoft .NET 8 桌面运行时…
chinesesimplified.InstallingWebView2=正在安装 Microsoft Edge WebView2 运行时…
chinesesimplified.PrereqFailed=%1 未能安装（%2）。Chao Desk 已照常安装；请从微软官网安装它，然后再启动 Chao Desk。
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
function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, '{#WebView2Key}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, '{#WebView2Key}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
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

{ The app is framework-dependent: it needs the .NET 8 (or newer) Desktop Runtime, x64.
  Look at the runtime folders: SDK installs do not write the sharedfx registry keys. }
function IsVersion8OrNewer(const Name: String): Boolean;
begin
  Result := StrToIntDef(Copy(Name, 1, Pos('.', Name) - 1), 0) >= 8;
end;

function DotNetDesktopInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Rec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\*'), Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and IsVersion8OrNewer(Rec.Name) then Result := True;
    until Result or not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
  if not Result and RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if IsVersion8OrNewer(Names[I]) then Result := True;
end;

{ ---- Missing runtimes: downloaded from Microsoft and installed before Chao Desk ---- }

var
  DownloadPage: TDownloadWizardPage;
  NeedDotNet, NeedWebView2: Boolean;

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

{ Runs a downloaded installer and reports a failure (the install of Chao Desk itself goes on). }
procedure RunPrereq(const FileName, Params, Name: String; Elevate: Boolean);
var
  ResultCode: Integer;
  Ok: Boolean;
begin
  if Elevate then
    { The .NET runtime is installed for all users: ShellExec lets Windows ask for administrator permission. }
    Ok := ShellExec('', ExpandConstant('{tmp}') + FileName, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
  else
    Ok := Exec(ExpandConstant('{tmp}') + FileName, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
  { 0 = done; 3010 = done, restart later; 1638 = a newer version is already there. }
  if not Ok or not ((ResultCode = 0) or (ResultCode = 3010) or (ResultCode = 1638)) then
  begin
    Log(Format('%s failed: ok=%d code=%d', [FileName, Ord(Ok), ResultCode]));
    if not WizardSilent() then
      MsgBox(FmtMessage(CustomMessage('PrereqFailed'), [Name, IntToStr(ResultCode)]), mbError, MB_OK);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not (NeedDotNet or NeedWebView2) then Exit;
  DownloadPage.Clear;
  if NeedDotNet then DownloadPage.Add('{#DotNetInstallerUrl}', 'windowsdesktop-runtime-8-win-x64.exe', '');
  if NeedWebView2 then DownloadPage.Add('{#WebView2InstallerUrl}', 'MicrosoftEdgeWebview2Setup.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if NeedWebView2 then
      begin
        DownloadPage.SetText(CustomMessage('InstallingWebView2'), '');
        RunPrereq('MicrosoftEdgeWebview2Setup.exe', '/silent /install', 'Microsoft Edge WebView2 Runtime', False);
      end;
      if NeedDotNet then
      begin
        DownloadPage.SetText(CustomMessage('InstallingDotNet'), '');
        RunPrereq('windowsdesktop-runtime-8-win-x64.exe', '/install /quiet /norestart', 'Microsoft .NET 8 Desktop Runtime', True);
      end;
    except
      { The download failed or was cancelled: Chao Desk still installs; say what is missing. }
      Log('Runtime download failed: ' + GetExceptionMessage);
      if not WizardSilent() then
        MsgBox(FmtMessage(CustomMessage('PrereqFailed'), ['Microsoft .NET 8 / WebView2', GetExceptionMessage]), mbError, MB_OK);
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
  StopRunningApp(ExpandConstant('{app}\{#AppExe}'));
  Result := '';
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
