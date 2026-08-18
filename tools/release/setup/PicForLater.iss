#ifndef AppVersion
  #error AppVersion must be defined by Build-Setup.ps1.
#endif
#ifndef AppArchitecture
  #error AppArchitecture must be defined by Build-Setup.ps1.
#endif
#ifndef AppPublishDir
  #error AppPublishDir must be defined by Build-Setup.ps1.
#endif
#ifndef RuntimeInstallerPath
  #error RuntimeInstallerPath must be defined by Build-Setup.ps1.
#endif
#ifndef VisualCppRuntimeInstallerPath
  #error VisualCppRuntimeInstallerPath must be defined by Build-Setup.ps1.
#endif
#ifndef SetupOutputDir
  #error SetupOutputDir must be defined by Build-Setup.ps1.
#endif
#ifndef RepositoryRoot
  #error RepositoryRoot must be defined by Build-Setup.ps1.
#endif

#define AppIdValue "D8947F12-A34E-4A61-A6E2-B406940EE5EC"
#define AppExeName "PicForLater.App.exe"
#define RuntimeInstallerName "WindowsAppRuntimeInstall.exe"
#define VisualCppRuntimeInstallerName "VC_redist.exe"

#if AppArchitecture == "x64"
  #define AllowedArchitecture "x64os"
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
OutputBaseFilename=PicForLater-Setup-{#AppVersion}-{#AppArchitecture}
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
Source: "{#RuntimeInstallerPath}"; DestName: "{#RuntimeInstallerName}"; Flags: dontcopy
Source: "{#VisualCppRuntimeInstallerPath}"; DestName: "{#VisualCppRuntimeInstallerName}"; Flags: dontcopy

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
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('{#VisualCppRuntimeInstallerName}');
  if not ShellExec(
    'runas',
    ExpandConstant('{tmp}\{#VisualCppRuntimeInstallerName}'),
    '/install /quiet /norestart',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := 'Microsoft Visual C++ Runtime could not be started.';
    exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
    NeedsRestart := True
  else if (ResultCode <> 0) and (ResultCode <> 1638) then
  begin
    Result := 'Microsoft Visual C++ Runtime installation failed with exit code ' +
      IntToStr(ResultCode) + '.';
    exit;
  end;

  ExtractTemporaryFile('{#RuntimeInstallerName}');
  if not Exec(
    ExpandConstant('{tmp}\{#RuntimeInstallerName}'),
    '--quiet --msix',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := 'Windows App SDK Runtime could not be started.';
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Result := 'Windows App SDK Runtime installation failed with exit code ' +
      IntToStr(ResultCode) + '.';
  end;
end;
