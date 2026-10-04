#ifndef AppVersion
  #error AppVersion must be defined by Build-Setup.ps1.
#endif
#ifndef AppArchitecture
  #error AppArchitecture must be defined by Build-Setup.ps1.
#endif
#ifndef AppPublishDir
  #error AppPublishDir must be defined by Build-Setup.ps1.
#endif
#ifndef PrerequisitesDir
  #error PrerequisitesDir must be defined by Build-Setup.ps1.
#endif
#ifndef PrerequisitesScriptPath
  #error PrerequisitesScriptPath must be defined by Build-Setup.ps1.
#endif
#ifndef SetupOutputDir
  #error SetupOutputDir must be defined by Build-Setup.ps1.
#endif
#ifndef RepositoryRoot
  #error RepositoryRoot must be defined by Build-Setup.ps1.
#endif
#ifndef Distribution
  #error Distribution must be Online or Offline.
#endif

#define AppIdValue "D8947F12-A34E-4A61-A6E2-B406940EE5EC"
#define AppExeName "PicForLater.App.exe"

#if AppArchitecture == "x64"
  #define AllowedArchitecture "x64os and not arm64"
  #define InstallArchitecture "x64os"
#elif AppArchitecture == "arm64"
  #define AllowedArchitecture "arm64"
  #define InstallArchitecture "arm64"
#else
  #error AppArchitecture must be x64 or arm64.
#endif

[Setup]
AppId={#AppIdValue}
AppName=PicForLater
AppVersion={#AppVersion}
AppVerName=PicForLater {#AppVersion}
AppPublisher=PicForLater contributors
AppPublisherURL=https://github.com/dogdreamson555/PicForLater
AppSupportURL=https://github.com/dogdreamson555/PicForLater/issues
AppUpdatesURL=https://github.com/dogdreamson555/PicForLater/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\PicForLater
DefaultGroupName=PicForLater
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#AllowedArchitecture}
ArchitecturesInstallIn64BitMode={#InstallArchitecture}
MinVersion=10.0.19041
OutputDir={#SetupOutputDir}
#if Distribution == "Offline"
OutputBaseFilename=PicForLater-Setup-Offline-{#AppVersion}-{#AppArchitecture}
#else
OutputBaseFilename=PicForLater-Setup-{#AppVersion}-{#AppArchitecture}
#endif
SetupIconFile={#RepositoryRoot}\src\PicForLater.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile={#RepositoryRoot}\LICENSE.txt
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
ChangesAssociations=no
ChangesEnvironment=no

[Files]
Source: "{#PrerequisitesDir}\prerequisites.json"; Flags: dontcopy nocompression
Source: "{#PrerequisitesScriptPath}"; Flags: dontcopy nocompression
#if Distribution == "Offline"
Source: "{#PrerequisitesDir}\*.exe"; Flags: dontcopy nocompression
Source: "{#PrerequisitesDir}\*.zip"; Flags: dontcopy nocompression
#else
Source: "{#RepositoryRoot}\tools\release\setup\Download-Prerequisite.cs"; Flags: dontcopy nocompression
#endif
Source: "{#AppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs solidbreak

[Icons]
Name: "{autoprograms}\PicForLater"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\PicForLater"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch PicForLater"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-notifications"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated skipifdoesntexist

[Code]
const
  SEE_MASK_NOCLOSEPROCESS = $00000040;
  SEE_MASK_NOASYNC = $00000100;
  SEE_MASK_FLAG_NO_UI = $00000400;
  WAIT_OBJECT_0 = 0;
  WAIT_TIMEOUT = 258;

type
  TShellExecuteInfo = record
    Size, Mask: LongWord;
    Window: HWND;
    Verb, FileName, Parameters, Directory: String;
    Show: Integer;
    Instance: THandle;
    IdList: LongWord;
    ClassName: String;
    ClassKey: THandle;
    HotKey: LongWord;
    Icon, Process: THandle;
  end;

var
  DownloadPage: TDownloadWizardPage;
  PrerequisitePage: TOutputMarqueeProgressWizardPage;
  PrerequisiteStatus: String;
  DownloadCancelled: Boolean;
  PrerequisitesReady: Boolean;

function ShellExecuteEx(var Info: TShellExecuteInfo): Boolean;
  external 'ShellExecuteExW@shell32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function GetExitCodeProcess(Handle: THandle; var ExitCode: LongWord): Boolean;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetTickCount64: Int64;
  external 'GetTickCount64@kernel32.dll stdcall';

function StatePath: String;
begin
  Result := ExpandConstant('{tmp}\prerequisites.ini');
end;

procedure SetPrerequisiteStatus(const Status: String);
begin
  PrerequisiteStatus := Status;
  Log(Status);
  PrerequisitePage.SetText(Status, 'This can take a few minutes. Please keep this window open.');
end;

procedure StopDownload(Sender: TObject);
begin
  if SuppressibleMsgBox(SetupMessage(msgStopDownload), mbConfirmation, MB_YESNO, IDYES) = IDYES then
  begin
    DownloadCancelled := True;
    SetIniString('Cancel', 'Requested', '1', StatePath + '.download.ini.cancel');
    DownloadPage.AbortButton.Enabled := False;
  end;
end;

function RunWithProgress(const Verb, FileName, Parameters: String; var ResultCode: Integer;
  Downloading: Boolean): Boolean;
var
  Info: TShellExecuteInfo;
  StartedAt: Int64;
  WaitResult, ExitCode: LongWord;
  ProgressPath: String;
begin
  Result := False;
  ProgressPath := StatePath + '.download.ini';
  StartedAt := GetTickCount64;
  Info.Size := SizeOf(Info);
  Info.Mask := SEE_MASK_NOCLOSEPROCESS or SEE_MASK_NOASYNC or SEE_MASK_FLAG_NO_UI;
  Info.Window := WizardForm.Handle;
  Info.Verb := Verb;
  Info.FileName := FileName;
  Info.Parameters := Parameters;
  Info.Directory := ExpandConstant('{tmp}');
  Info.Show := SW_HIDE;
  if not ShellExecuteEx(Info) then
  begin
    ResultCode := DLLGetLastError;
    Log(PrerequisiteStatus + ' could not start: ' + SysErrorMessage(ResultCode));
    exit;
  end;
  if Info.Process = 0 then
  begin
    ResultCode := 6;
    Log(PrerequisiteStatus + ' did not return a process handle.');
    exit;
  end;
  try
    repeat
      if Downloading then
      begin
        DownloadPage.SetText(PrerequisiteStatus + ' ' +
          GetIniString('Download', 'Status', 'Connecting...', ProgressPath),
          GetIniString('Download', 'Detail', 'Waiting for the server...', ProgressPath));
        DownloadPage.SetProgress(GetIniInt('Download', 'Progress', 0, 0, 1000, ProgressPath), 1000);
      end
      else
        PrerequisitePage.SetText(PrerequisiteStatus,
          'Elapsed: ' + IntToStr((GetTickCount64 - StartedAt) div 1000) +
          ' seconds. This can take a few minutes; please wait.');
      WaitResult := WaitForSingleObject(Info.Process, 100);
    until WaitResult <> WAIT_TIMEOUT;
    if (WaitResult = WAIT_OBJECT_0) and GetExitCodeProcess(Info.Process, ExitCode) then
    begin
      ResultCode := ExitCode;
      Result := True;
    end
    else
      ResultCode := DLLGetLastError;
  finally
    CloseHandle(Info.Process);
  end;
  Log(PrerequisiteStatus + ' finished after ' + IntToStr(GetTickCount64 - StartedAt) +
    ' ms, code ' + IntToStr(ResultCode) + '.');
end;

function HelperError: String;
begin
  Result := GetIniString('Error', 'Message', 'Prerequisite operation failed.', StatePath + '.error.ini');
end;

function RunHelper(const Action, ExtraParameters: String): Boolean;
var
  PowerShellPath, Parameters: String;
  ResultCode: Integer;
begin
  PowerShellPath := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Install-Prerequisites.ps1') + '" -Action ' + Action +
    ' -ManifestPath "' + ExpandConstant('{tmp}\prerequisites.json') +
    '" -OutputPath "' + StatePath + '" ' + ExtraParameters;
  DeleteFile(StatePath + '.error.ini');
  Result := RunWithProgress('open', PowerShellPath, Parameters, ResultCode, Action = 'Download');
  if Result then Result := ResultCode = 0;
end;

function DownloadPrerequisite(const Id, Name, PayloadPath: String): Boolean;
begin
  DownloadCancelled := False;
  DownloadPage.AbortButton.Enabled := True;
  SetPrerequisiteStatus('Downloading ' + Name + '...');
  DeleteFile(StatePath + '.download.ini');
  DeleteFile(StatePath + '.download.ini.cancel');
  DownloadPage.Show;
  try
    DownloadPage.Msg2Label.Visible := True;
    DownloadPage.SetProgress(0, 1000);
    Result := RunHelper('Download', '-PrerequisiteId "' + Id + '" -PayloadPath "' + PayloadPath + '"');
    if Result then DownloadPage.SetProgress(1000, 1000);
    Result := Result and not DownloadCancelled;
  finally
    DownloadPage.Hide;
  end;
end;

procedure InitializeWizard;
begin
  PrerequisitePage := CreateOutputMarqueeProgressPage('Installing required components',
    'If an administrator prompt appears, choose Yes to continue.');
  DownloadPage := CreateDownloadPage('Downloading required components',
    'Only missing or outdated components will be downloaded.', nil);
  DownloadPage.AbortButton.OnClick := @StopDownload;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Index, Count, ResultCode: Integer;
  Section, Id, Name, FileName, Kind, PayloadPath: String;
  Started: Boolean;
  RestartRequired: array[0..3] of Boolean;
begin
  Result := '';
  if PrerequisitesReady then exit;
  PrerequisitePage.Show;
  try
  SetPrerequisiteStatus('Checking installed components...');
  ExtractTemporaryFile('prerequisites.json');
  ExtractTemporaryFile('Install-Prerequisites.ps1');
#if Distribution != "Offline"
  ExtractTemporaryFile('Download-Prerequisite.cs');
#endif
  if not RunHelper('Detect', '') then
  begin
    Result := HelperError;
    exit;
  end;
  Count := GetIniInt('Prerequisites', 'Count', 0, 0, 100, StatePath);
  if Count <> 4 then
  begin
    Result := 'The prerequisites list could not be read.';
    exit;
  end;
  for Index := 0 to Count - 1 do
  begin
    RestartRequired[Index] := False;
    Section := IntToStr(Index);
    if GetIniInt(Section, 'Needed', 1, 0, 1, StatePath) = 0 then continue;
    Id := GetIniString(Section, 'Id', '', StatePath);
    Name := GetIniString(Section, 'Name', '', StatePath);
    FileName := GetIniString(Section, 'FileName', '', StatePath);
    Kind := GetIniString(Section, 'Kind', '', StatePath);
    PayloadPath := ExpandConstant('{tmp}\') + FileName;
    try
#if Distribution == "Offline"
      SetPrerequisiteStatus('Extracting ' + Name + '...');
      ExtractTemporaryFile(FileName);
#else
      if not DownloadPrerequisite(Id, Name, PayloadPath) then
      begin
        if DownloadCancelled then Result := 'Download stopped. Retry to download the required component again.'
        else Result := HelperError;
        exit;
      end;
#endif
      if Kind = 'msixZip' then
      begin
        SetPrerequisiteStatus('Verifying and installing ' + Name + '...');
        if not RunHelper('InstallWindowsRuntime', '-PrerequisiteId "' + Id + '" -PayloadPath "' + PayloadPath + '"') then
        begin
          Result := HelperError;
          exit;
        end;
      end
      else
      begin
        SetPrerequisiteStatus('Verifying ' + Name + '...');
        if not RunHelper('Verify', '-PrerequisiteId "' + Id + '" -PayloadPath "' + PayloadPath + '"') then
        begin
          Result := HelperError;
          exit;
        end;
        SetPrerequisiteStatus('Installing ' + Name + '...');
        Started := RunWithProgress('runas', PayloadPath, '/install /quiet /norestart', ResultCode, False);
        if not Started then
        begin
          Result := Name + ' could not be installed. Allow the administrator prompt or use a machine with the required component installed.';
          exit;
        end;
        Log(Name + ' installer exited with code ' + IntToStr(ResultCode) + '.');
        if ResultCode = 1641 then
        begin
          NeedsRestart := True;
          Result := Name + ' requires a restart. Restart Windows and run Setup again; the existing application has not been replaced.';
          exit;
        end;
        if ResultCode = 3010 then
        begin
          RestartRequired[Index] := True;
          Log(Name + ' was installed with a pending restart; checking availability before replacing the application.');
        end;
        if (ResultCode <> 0) and (ResultCode <> 1638) and (ResultCode <> 3010) then
        begin
          Result := Name + ' installation failed with exit code ' + IntToStr(ResultCode) + '.';
          exit;
        end;
      end;
    except
      Result := 'Unable to prepare ' + Name + ': ' + GetExceptionMessage;
      exit;
    end;
  end;
  SetPrerequisiteStatus('Checking installed components...');
  if not RunHelper('Detect', '') then
  begin
    Result := HelperError;
    exit;
  end;
  for Index := 0 to Count - 1 do
    if GetIniInt(IntToStr(Index), 'Needed', 1, 0, 1, StatePath) <> 0 then
    begin
      Name := GetIniString(IntToStr(Index), 'Name', 'A required component', StatePath);
      if RestartRequired[Index] then
      begin
        NeedsRestart := True;
        Result := Name + ' is not yet available. Restart Windows and run Setup again; the existing application has not been replaced.';
      end
      else
        Result := Name + ' is still unavailable. Setup has not replaced the existing application.';
      exit;
    end;
  SetPrerequisiteStatus('Preparing the application update...');
  if not RunHelper('CaptureLegacy', '-InstallDirectory "' + ExpandConstant('{app}') +
    '" -LegacyPath "' + ExpandConstant('{tmp}\legacy-runtime.json') + '"') then
  begin
    Result := HelperError;
    exit;
  end;
  PrerequisitesReady := True;
  finally
    PrerequisitePage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    PrerequisitePage.Show;
    try
      SetPrerequisiteStatus('Finishing the application update...');
    if not RunHelper('CleanupLegacy', '-InstallDirectory "' + ExpandConstant('{app}') +
      '" -LegacyPath "' + ExpandConstant('{tmp}\legacy-runtime.json') + '"') then
      Log('Legacy runtime cleanup: ' + HelperError);
    finally
      PrerequisitePage.Hide;
    end;
  end;
end;
