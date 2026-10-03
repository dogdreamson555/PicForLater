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
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
ChangesAssociations=no
ChangesEnvironment=no

[Files]
Source: "{#AppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PrerequisitesDir}\prerequisites.json"; Flags: dontcopy
Source: "{#PrerequisitesScriptPath}"; Flags: dontcopy
#if Distribution == "Offline"
Source: "{#PrerequisitesDir}\*.exe"; Flags: dontcopy
Source: "{#PrerequisitesDir}\*.zip"; Flags: dontcopy
#endif

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
var
  DownloadPage: TDownloadWizardPage;
  PrerequisitesReady: Boolean;

function StatePath: String;
begin
  Result := ExpandConstant('{tmp}\prerequisites.ini');
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
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Install-Prerequisites.ps1') + '" -Action ' + Action +
    ' -ManifestPath "' + ExpandConstant('{tmp}\prerequisites.json') +
    '" -OutputPath "' + StatePath + '" ' + ExtraParameters;
  DeleteFile(StatePath + '.error.ini');
  Result := Exec(PowerShellPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then Result := ResultCode = 0;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Installing required components',
    'Only missing or outdated components will be downloaded.', nil);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Index, Count, ResultCode: Integer;
  Section, Id, Name, FileName, Hash, Kind, PayloadPath: String;
  Started: Boolean;
begin
  Result := '';
  if PrerequisitesReady then exit;
  ExtractTemporaryFile('prerequisites.json');
  ExtractTemporaryFile('Install-Prerequisites.ps1');
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
    Section := IntToStr(Index);
    if GetIniInt(Section, 'Needed', 1, 0, 1, StatePath) = 0 then continue;
    Id := GetIniString(Section, 'Id', '', StatePath);
    Name := GetIniString(Section, 'Name', '', StatePath);
    FileName := GetIniString(Section, 'FileName', '', StatePath);
    Hash := GetIniString(Section, 'Sha256', '', StatePath);
    Kind := GetIniString(Section, 'Kind', '', StatePath);
    PayloadPath := ExpandConstant('{tmp}\') + FileName;
    try
#if Distribution == "Offline"
      ExtractTemporaryFile(FileName);
#else
      DownloadPage.Clear;
      DownloadPage.Add(GetIniString(Section, 'Uri', '', StatePath), FileName, Hash);
      DownloadPage.Show;
      try
        DownloadPage.Download;
      finally
        DownloadPage.Hide;
      end;
#endif
      if not RunHelper('Verify', '-PrerequisiteId "' + Id + '" -PayloadPath "' + PayloadPath + '"') then
      begin
        Result := HelperError;
        exit;
      end;
      WizardForm.StatusLabel.Caption := 'Installing ' + Name + '...';
      if Kind = 'msixZip' then
      begin
        if not RunHelper('InstallWindowsRuntime', '-PrerequisiteId "' + Id + '" -PayloadPath "' + PayloadPath + '"') then
        begin
          Result := HelperError;
          exit;
        end;
      end
      else
      begin
        Started := ShellExec('runas', PayloadPath, '/install /quiet /norestart', '',
          SW_HIDE, ewWaitUntilTerminated, ResultCode);
        if not Started then
        begin
          Result := Name + ' could not be installed. Allow the administrator prompt or use a machine with the required component installed.';
          exit;
        end;
        if (ResultCode = 3010) or (ResultCode = 1641) then
        begin
          NeedsRestart := True;
          Result := Name + ' requires a restart. Restart Windows and run Setup again; the existing application has not been replaced.';
          exit;
        end;
        if (ResultCode <> 0) and (ResultCode <> 1638) then
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
  if not RunHelper('Detect', '') then
  begin
    Result := HelperError;
    exit;
  end;
  for Index := 0 to Count - 1 do
    if GetIniInt(IntToStr(Index), 'Needed', 1, 0, 1, StatePath) <> 0 then
    begin
      Result := GetIniString(IntToStr(Index), 'Name', 'A required component', StatePath) +
        ' is still unavailable. Setup has not replaced the existing application.';
      exit;
    end;
  if not RunHelper('CaptureLegacy', '-InstallDirectory "' + ExpandConstant('{app}') +
    '" -LegacyPath "' + ExpandConstant('{tmp}\legacy-runtime.json') + '"') then
  begin
    Result := HelperError;
    exit;
  end;
  PrerequisitesReady := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if not RunHelper('CleanupLegacy', '-InstallDirectory "' + ExpandConstant('{app}') +
      '" -LegacyPath "' + ExpandConstant('{tmp}\legacy-runtime.json') + '"') then
      Log('Legacy runtime cleanup: ' + HelperError);
end;
