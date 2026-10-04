[CmdletBinding()]
param([string]$InnoCompilerPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $InnoCompilerPath = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) { throw 'Inno Setup compiler is required.' }

$testRoot = Join-Path $repositoryRoot ('artifacts\setup-progress-tests\' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'setup\PicForLater.iss') -Raw
$codeStart = $source.IndexOf('[Code]') + '[Code]'.Length
$codeEnd = $source.IndexOf('function PrepareToInstall(', $codeStart)
$productionCode = $source.Substring($codeStart, $codeEnd - $codeStart)
$probe = @'
param([long]$Window, [int]$ExitCode, [string]$Output)
$ErrorActionPreference = 'Stop'
try {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SetupWindowProbe {
    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr window, uint message,
        UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
"@
Start-Sleep -Seconds 1
$messageResult = [UIntPtr]::Zero
$responded = [SetupWindowProbe]::SendMessageTimeout(
    [IntPtr]$Window, 0, [UIntPtr]::Zero, [IntPtr]::Zero, 2, 2000, [ref]$messageResult) -ne [IntPtr]::Zero
@{
    Enabled = [SetupWindowProbe]::IsWindowEnabled([IntPtr]$Window)
    Responsive = $responded
    NativePowerShell = [Environment]::Is64BitProcess
} | ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding UTF8
Start-Sleep -Seconds 1
exit $ExitCode
} catch {
    [IO.File]::WriteAllText($Output + '.error', ($_ | Out-String))
    exit 1
}
'@
$template = @'
[Setup]
AppName=Prerequisite progress check
AppVersion=1.0
DefaultDirName={tmp}\unused
PrivilegesRequired=lowest
Uninstallable=no
DisableDirPage=yes
DisableReadyPage=yes
OutputDir=.
OutputBaseFilename=progress
[Code]
{productionCode}
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer; Started: Boolean; PreviousPage: Integer; ProgramPath, Parameters: String;
begin
  PreviousPage := WizardForm.CurPageID;
  PrerequisitePage.Show;
  try
    SetPrerequisiteStatus('Installing a simulated component...');
    ProgramPath := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
    Parameters := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{src}\probe.ps1') + '" -Window ' + IntToStr(WizardForm.Handle) +
      ' -ExitCode ' + ExpandConstant('{param:code|0}') + ' -Output "' +
      ExpandConstant('{src}\probe.json') + '"';
    if ExpandConstant('{param:missing|0}') = '1' then
      ProgramPath := ExpandConstant('{src}\missing.exe');
    Started := RunWithProgress('open', ProgramPath, Parameters, Code);
    SetIniString('Result', 'Started', IntToStr(Ord(Started)), ExpandConstant('{src}\result.ini'));
    SetIniString('Result', 'Code', IntToStr(Code), ExpandConstant('{src}\result.ini'));
    SetIniString('Result', 'Elapsed', PrerequisitePage.Msg2Label.Caption, ExpandConstant('{src}\result.ini'));
    SetIniString('Result', 'Stage', PrerequisitePage.Msg1Label.Caption, ExpandConstant('{src}\result.ini'));
    SetIniString('Result', 'NavigationHidden',
      IntToStr(Ord(not WizardForm.NextButton.Visible and not WizardForm.BackButton.Visible and
        not WizardForm.CancelButton.Visible)), ExpandConstant('{src}\result.ini'));
  finally
    PrerequisitePage.Hide;
  end;
  SetIniString('Result', 'Restored', IntToStr(Ord(WizardForm.CurPageID = PreviousPage)),
    ExpandConstant('{src}\result.ini'));
  Result := 'Test complete; no application or runtime was installed.';
end;
'@

$checksPassed = $false
try {
    [IO.File]::WriteAllText((Join-Path $testRoot 'probe.ps1'), $probe, [Text.UTF8Encoding]::new($false))
    $scriptPath = Join-Path $testRoot 'progress.iss'
    [IO.File]::WriteAllText($scriptPath, $template.Replace('{productionCode}', $productionCode),
        [Text.UTF8Encoding]::new($false))
    & $InnoCompilerPath /Q $scriptPath
    if ($LASTEXITCODE -ne 0) { throw 'Progress fixture compilation failed.' }
    foreach ($case in @('0', '3010', '1638', '1603', 'missing')) {
        $resultPath = Join-Path $testRoot 'result.ini'
        $probePath = Join-Path $testRoot 'probe.json'
        foreach ($path in @($resultPath, $probePath)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        $arguments += if ($case -eq 'missing') { '/missing=1' } else { "/code=$case" }
        $process = Start-Process -FilePath (Join-Path $testRoot 'progress.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(30000)) { throw "Progress check timed out: $case" }
        $result = Get-Content -LiteralPath $resultPath -Raw
        foreach ($key in @('NavigationHidden', 'Restored')) {
            if ($result -notmatch "(?m)^$key=1\s*$") { throw ('Unexpected {0} for {1}: {2}' -f $key, $case, $result) }
        }
        if ($case -eq 'missing') {
            if ($result -notmatch '(?m)^Started=0\s*$' -or $result -notmatch '(?m)^Code=[1-9][0-9]*\s*$') {
                throw "A missing executable must fail immediately: $result"
            }
        } else {
            if ($result -notmatch '(?m)^Started=1\s*$' -or $result -notmatch "(?m)^Code=$case\s*$" -or
                $result -notmatch '(?m)^Elapsed=Elapsed: [1-9][0-9]* seconds' -or
                $result -notmatch '(?m)^Stage=Installing a simulated component') {
                if (Test-Path -LiteralPath ($probePath + '.error')) { Get-Content -LiteralPath ($probePath + '.error') }
                throw "Unexpected result: $result"
            }
            $windowProbe = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
            if (-not $windowProbe.Enabled -or -not $windowProbe.Responsive -or -not $windowProbe.NativePowerShell) {
                throw "Window or native PowerShell check failed: $($windowProbe | ConvertTo-Json -Compress)"
            }
        }
        "$($case): passed"
    }
    '5 progress checks passed. No application or runtime was installed.'
    $checksPassed = $true
}
finally {
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\setup-progress-tests')).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($testRoot).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test cleanup path escaped its artifacts directory.'
    }
    if ($checksPassed) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
    else { "Failed test artifacts: $testRoot" }
}
