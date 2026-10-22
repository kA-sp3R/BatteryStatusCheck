; Independently maintained per-user installer. The compiler is a build tool only.
#ifndef AppVersion
  #error AppVersion must be provided by build-installer.ps1
#endif
#ifndef AppVersionInfo
  #error AppVersionInfo must be provided by build-installer.ps1
#endif
#ifndef PayloadDirectory
  #error PayloadDirectory must be provided by build-installer.ps1
#endif
#ifndef InstallerOutputDirectory
  #error InstallerOutputDirectory must be provided by build-installer.ps1
#endif

[Setup]
AppId={{D38FB615-37DB-4AC0-92CE-BD1F947B9732}
AppName=Device Battery Status
AppVersion={#AppVersion}
AppVerName=Device Battery Status {#AppVersion}
AppPublisher=Device Battery Status contributors
AppPublisherURL=https://github.com/kA-sp3R/BatteryStatusCheck
AppSupportURL=https://github.com/kA-sp3R/BatteryStatusCheck
AppUpdatesURL=https://github.com/kA-sp3R/BatteryStatusCheck/releases
VersionInfoVersion={#AppVersionInfo}
VersionInfoDescription=Device Battery Status per-user setup
DefaultDirName={localappdata}\Programs\DeviceBatteryStatus
DefaultGroupName=Device Battery Status
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
DirExistsWarning=auto
UninstallDisplayIcon={app}\DeviceBatteryStatus.exe
UninstallLogMode=append
AppMutex=Local\DeviceBatteryStatus
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=DeviceBatteryStatus-{#AppVersion}-win-x64-setup

[Files]
Source: "{#PayloadDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Device Battery Status"; Filename: "{app}\DeviceBatteryStatus.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall Device Battery Status"; Filename: "{uninstallexe}"

; Deliberately no [Run], startup registration, settings deletion or app-data deletion.
[Code]
const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupValue = 'DeviceBatteryStatus';
var
  PreviousOwnedFiles: TArrayOfString;

function IsSafeOwnedRelativeName(Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  if Name = '' then Exit;
  if (Pos(':', Name) > 0) or (Pos('..', Name) > 0) or
     (Name[1] = '\') or (Name[1] = '/') then Exit;
  for Index := 1 to Length(Name) do
    if Ord(Name[Index]) < 32 then Exit;
  StringChangeEx(Name, '/', '\', True);
  if CompareText(Copy(ExtractFileName(Name), 1, 5), 'unins') = 0 then Exit;
  Result := True;
end;

function ContainsOwnedName(const Names: TArrayOfString; Name: String): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 0 to GetArrayLength(Names) - 1 do
    if CompareText(Names[Index], Name) = 0 then begin
      Result := True;
      Exit;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CheckForMutexes('Local\DeviceBatteryStatus') then begin
    Result := 'Close Device Battery Status from its tray menu, then try installing again. Setup does not close the app or replace a running copy.';
    Exit;
  end;
  LoadStringsFromFile(ExpandConstant('{app}\.installed-files.txt'), PreviousOwnedFiles);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  CurrentOwnedFiles: TArrayOfString;
  Index: Integer;
  RelativeName: String;
  ParentDirectory: String;
  InstallRoot: String;
begin
  if CurStep <> ssPostInstall then Exit;
  if not LoadStringsFromFile(ExpandConstant('{app}\.installed-files.txt'), CurrentOwnedFiles) then Exit;
  InstallRoot := AddBackslash(ExpandConstant('{app}'));
  // Remove only obsolete files recorded by an earlier installer, after copying succeeded.
  // Unknown user files and the separate settings/log directory are never removed.
  for Index := 0 to GetArrayLength(PreviousOwnedFiles) - 1 do begin
    RelativeName := PreviousOwnedFiles[Index];
    if IsSafeOwnedRelativeName(RelativeName) and not ContainsOwnedName(CurrentOwnedFiles, RelativeName) then begin
      StringChangeEx(RelativeName, '/', '\', True);
      if DeleteFile(InstallRoot + RelativeName) then begin
        ParentDirectory := ExtractFileDir(InstallRoot + RelativeName);
        while (Length(ParentDirectory) > Length(RemoveBackslashUnlessRoot(InstallRoot))) and
              (CompareText(Copy(AddBackslash(ParentDirectory), 1, Length(InstallRoot)), InstallRoot) = 0) do begin
          if not RemoveDir(ParentDirectory) then Break;
          ParentDirectory := ExtractFileDir(ParentDirectory);
        end;
      end;
    end;
  end;
end;

function OwnsStartupCommand(Command: String): Boolean;
var
  ClosingQuote: Integer;
  Index: Integer;
  Executable: String;
begin
  Result := False;
  if Length(Command) < 4 then Exit;
  if Command[1] <> '"' then Exit;
  for Index := 1 to Length(Command) do
    if Ord(Command[Index]) < 32 then Exit;
  ClosingQuote := 2;
  while (ClosingQuote <= Length(Command)) and (Command[ClosingQuote] <> '"') do
    ClosingQuote := ClosingQuote + 1;
  if ClosingQuote > Length(Command) then Exit;
  if Trim(Copy(Command, ClosingQuote + 1, Length(Command))) <> '--startup' then Exit;
  Executable := Copy(Command, 2, ClosingQuote - 2);
  if not (((Length(Executable) >= 3) and (Executable[2] = ':') and (Executable[3] = '\')) or
          (Copy(Executable, 1, 2) = '\\')) then Exit;
  try
    Result := CompareText(ExpandFileName(Executable), ExpandConstant('{app}\DeviceBatteryStatus.exe')) = 0;
  except
    Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('Local\DeviceBatteryStatus');
  if not Result then
    SuppressibleMsgBox('Close Device Battery Status from its tray menu before uninstalling.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  // Preserve a startup value belonging to a portable or different installed copy.
  if RegQueryStringValue(HKCU, StartupKey, StartupValue, Command) and OwnsStartupCommand(Command) then
    RegDeleteValue(HKCU, StartupKey, StartupValue);
end;
