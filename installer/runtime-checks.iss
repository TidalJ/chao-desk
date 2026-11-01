{ Runtime checks are shared with the isolated installer regression harness. }
function IsReleaseVersion(const Value: String; ComponentCount: Integer): Boolean;
var
  I, Components, Digits: Integer;
begin
  Result := False;
  Components := 1;
  Digits := 0;
  for I := 1 to Length(Value) do
  begin
    if Value[I] = '.' then
    begin
      if Digits = 0 then Exit;
      Components := Components + 1;
      Digits := 0;
    end
    else if (Value[I] >= '0') and (Value[I] <= '9') then
      Digits := Digits + 1
    else
      Exit;
  end;
  Result := (Components = ComponentCount) and (Digits > 0);
end;

function IsDotNet8Release(const Value: String): Boolean;
var
  Version: Int64;
begin
  { Default framework roll-forward never crosses a major version. }
  Result := IsReleaseVersion(Value, 3) and (Copy(Value, 1, 2) = '8.') and
    StrToVersion(Value, Version);
end;

function IsWebView2Version(const Value: String): Boolean;
var
  Version: Int64;
begin
  Result := IsReleaseVersion(Value, 4) and StrToVersion(Value, Version) and (Version > 0);
end;

function VersionLine(const Value: String): String;
var
  FirstDot, SecondDot: Integer;
begin
  FirstDot := Pos('.', Value);
  SecondDot := FirstDot + Pos('.', Copy(Value, FirstDot + 1, Length(Value)));
  Result := Copy(Value, 1, SecondDot);
end;

function HasRuntimeFiles(const Directory, RequiredFile, OtherFile: String;
  Minimum: Int64; SameVersionLine: Boolean): Boolean;
var
  Rec: TFindRec;
  Version: Int64;
begin
  Result := False;
  if FindFirst(AddBackslash(Directory) + '*', Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
        IsReleaseVersion(Rec.Name, 3) and StrToVersion(Rec.Name, Version) then
        if (ComparePackedVersion(Version, Minimum) >= 0) and
          ((not SameVersionLine) or (VersionLine(Rec.Name) = VersionLine(VersionToStr(Minimum)))) and
          FileExists(AddBackslash(Directory) + Rec.Name + '\' + RequiredFile) and
          ((OtherFile = '') or FileExists(AddBackslash(Directory) + Rec.Name + '\' + OtherFile)) then
          Result := True;
    until Result or not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function DotNetDesktopAtRoot(const Root: String): Boolean;
var
  Rec: TFindRec;
  DesktopPath, CorePath: String;
  Minimum, Version: Int64;
begin
  Result := False;
  if Root = '' then Exit;
  StrToVersion('8.0.0', Minimum);
  if not HasRuntimeFiles(AddBackslash(Root) + 'host\fxr', 'hostfxr.dll', '', Minimum, False) then Exit;
  DesktopPath := AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\';
  CorePath := AddBackslash(Root) + 'shared\Microsoft.NETCore.App\';
  if FindFirst(DesktopPath + '*', Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and IsDotNet8Release(Rec.Name) then
      begin
        StrToVersion(Rec.Name, Version);
        { Desktop's runtimeconfig uses LatestPatch for its underlying core runtime. }
        if FileExists(DesktopPath + Rec.Name + '\PresentationFramework.dll') and
          FileExists(DesktopPath + Rec.Name + '\Microsoft.WindowsDesktop.App.runtimeconfig.json') and
          HasRuntimeFiles(CorePath, 'coreclr.dll', 'hostpolicy.dll', Version, True) then
          Result := True;
      end;
    until Result or not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function RuntimeInstallResult(const Name: String; Started: Boolean; ExitCode: Integer;
  Installed: Boolean; var NeedsRestart: Boolean): String;
begin
  Result := '';
  if Started and (ExitCode = 3010) then
  begin
    NeedsRestart := True;
    Result := FmtMessage(CustomMessage('PrereqRestart'), [Name]);
  end
  else if (not Started) or ((ExitCode <> 0) and (ExitCode <> 1638)) then
    Result := FmtMessage(CustomMessage('PrereqFailed'), [Name, IntToStr(ExitCode)])
  else if not Installed then
    Result := FmtMessage(CustomMessage('PrereqNotDetected'), [Name]);
end;
