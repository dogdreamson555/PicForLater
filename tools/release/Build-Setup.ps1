[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version,

    [string]$OutputRoot,

    [string]$RuntimeCacheRoot,

    [string]$InnoCompilerPath,

    [switch]$NoRestore,

    [switch]$DryRun,

    # Backward-compatible name retained for existing local invocations.
    [switch]$SkipCompile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectPath = Join-Path $repositoryRoot 'src\PicForLater.App\PicForLater.App.csproj'
$setupScriptPath = Join-Path $PSScriptRoot 'setup\PicForLater.iss'
$runtimeManifestPath = Join-Path $PSScriptRoot 'setup\windows-app-runtime.json'
$isDryRun = $DryRun -or $SkipCompile

if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionOutput = & dotnet msbuild $projectPath '-getProperty:Version' '-p:Configuration=Release' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the application version: $($versionOutput -join [Environment]::NewLine)"
    }

    $Version = ($versionOutput | Select-Object -Last 1).Trim()
    if ($Version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "The application version is not a numeric Setup version: $Version"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\setup'
}
elseif (-not [IO.Path]::IsPathFullyQualified($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot $OutputRoot
}

if ([string]::IsNullOrWhiteSpace($RuntimeCacheRoot)) {
    $RuntimeCacheRoot = Join-Path $repositoryRoot 'artifacts\setup-prerequisites'
}
elseif (-not [IO.Path]::IsPathFullyQualified($RuntimeCacheRoot)) {
    $RuntimeCacheRoot = Join-Path $repositoryRoot $RuntimeCacheRoot
}

$architecture = if ($Platform -eq 'ARM64') { 'arm64' } else { 'x64' }
$runtimeIdentifier = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$outputRootPath = [IO.Path]::GetFullPath($OutputRoot)
$runtimeCacheRootPath = [IO.Path]::GetFullPath($RuntimeCacheRoot)
$releaseRoot = Join-Path $outputRootPath "$Version\$architecture"
$publishRoot = Join-Path $releaseRoot 'app'
$installerRoot = Join-Path $releaseRoot 'installer'
$setupPath = Join-Path $installerRoot "PicForLater-Setup-$Version-$architecture.exe"

function Assert-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $candidate = [IO.Path]::GetFullPath($Path)
    $rootPrefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root)) + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The output path escapes its managed root: $candidate"
    }
}

Assert-PathUnderRoot -Path $publishRoot -Root $outputRootPath
Assert-PathUnderRoot -Path $installerRoot -Root $outputRootPath

$setupDefinition = Get-Content -Raw -LiteralPath $setupScriptPath
foreach ($requiredSetupDirective in @(
    'PrivilegesRequired=lowest',
    'DefaultDirName={localappdata}\Programs\PicForLater',
    'MinVersion=10.0.19041',
    'Source: "{#AppPublishDir}\*"',
    'Source: "{#RuntimeInstallerPath}"',
    'Parameters: "--uninstall-notifications"')) {
    if (-not $setupDefinition.Contains($requiredSetupDirective, [StringComparison]::Ordinal)) {
        throw "The Inno Setup definition is missing a required release directive: $requiredSetupDirective"
    }
}
if ($setupDefinition -match '(?im)^\s*(DelTree|DeleteFile|DeleteDir)\b' -or
    $setupDefinition.Contains('{localappdata}\PicForLater', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Inno Setup definition must not delete or target the PicForLater user-data root.'
}

$runtimeManifest = Get-Content -Raw -LiteralPath $runtimeManifestPath | ConvertFrom-Json
$appProject = [xml](Get-Content -Raw -LiteralPath $projectPath)
$windowsAppSdkReference = $appProject.SelectSingleNode(
    "/Project/ItemGroup/PackageReference[@Include='Microsoft.WindowsAppSDK']")
if ($null -eq $windowsAppSdkReference -or
    $windowsAppSdkReference.GetAttribute('Version') -ne $runtimeManifest.version) {
    throw 'The pinned Windows App SDK Runtime does not match the App PackageReference.'
}

$runtimeDefinition = $runtimeManifest.architectures.$architecture
if ($null -eq $runtimeDefinition) {
    throw "No Windows App SDK Runtime is pinned for $architecture."
}

New-Item -ItemType Directory -Path $runtimeCacheRootPath -Force | Out-Null
$runtimeInstallerPath = Join-Path $runtimeCacheRootPath (
    "WindowsAppRuntimeInstall-$architecture-$($runtimeManifest.version).exe")

function Test-RuntimeInstaller {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne [long]$runtimeDefinition.length) {
        return $false
    }

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if (-not $hash.Equals([string]$runtimeDefinition.sha256, [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    $signature = Get-AuthenticodeSignature -FilePath $Path
    return $signature.Status.ToString() -eq 'Valid' -and
        $null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.Subject -like 'CN=Microsoft Corporation,*'
}

if (-not (Test-RuntimeInstaller -Path $runtimeInstallerPath)) {
    $temporaryRuntimePath = "$runtimeInstallerPath.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        Invoke-WebRequest -Uri $runtimeDefinition.uri -OutFile $temporaryRuntimePath -UseBasicParsing
        if (-not (Test-RuntimeInstaller -Path $temporaryRuntimePath)) {
            throw 'The downloaded Windows App SDK Runtime failed length, SHA-256, or Microsoft signature validation.'
        }

        Move-Item -LiteralPath $temporaryRuntimePath -Destination $runtimeInstallerPath -Force
    }
    finally {
        Remove-Item -LiteralPath $temporaryRuntimePath -Force -ErrorAction SilentlyContinue
    }
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $installerRoot) {
    Remove-Item -LiteralPath $installerRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot,$installerRoot -Force | Out-Null

$publishArguments = @(
    'publish',
    $projectPath,
    '-c',
    'Release',
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    '-p:SelfContained=true',
    '-p:WindowsAppSDKSelfContained=false',
    '-p:PublishReadyToRun=false',
    '-p:PublishTrimmed=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '-p:CopyOutputSymbolsToPublishDirectory=false',
    "-p:PublishDir=$publishRoot",
    "-p:Version=$Version"
)
if ($NoRestore) {
    $publishArguments += '--no-restore'
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "The core unpackaged publish failed with exit code $LASTEXITCODE."
}

$forbiddenNames = @(
    'onnxruntime.dll',
    'onnxruntime-genai.dll',
    'DirectML.dll',
    'Microsoft.ML.OnnxRuntime.dll',
    'Microsoft.Windows.AI.MachineLearning.dll',
    'PicForLater.LocalInference.exe',
    'PicForLater.LocalInference.dll'
)
$publishedFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse)
$forbiddenFiles = @($publishedFiles | Where-Object { $_.Name -in $forbiddenNames })
if ($forbiddenFiles.Count -ne 0) {
    throw "The core publish contains local-inference assets: $($forbiddenFiles.Name -join ', ')"
}
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'PicForLater.App.exe') -PathType Leaf)) {
    throw 'The core publish did not produce PicForLater.App.exe.'
}
foreach ($requiredUiAsset in @('PicForLater.App.pri', 'App.xbf', 'MainWindow.xbf', 'Assets\AppIcon.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $requiredUiAsset) -PathType Leaf)) {
        throw "The core publish is missing the required WinUI asset: $requiredUiAsset"
    }
}

$requiredDistributionFiles = @(
    'LICENSE.txt',
    'THIRD-PARTY-NOTICES.md',
    'licenses\dotnet-runtime\LICENSE.txt',
    'licenses\dotnet-runtime\ThirdPartyNotices.txt',
    'licenses\windows-app-sdk\LICENSE.txt',
    'licenses\windows-app-sdk\NOTICE.txt',
    'licenses\webview2\LICENSE.txt',
    'licenses\webview2\NOTICE.txt',
    'licenses\communitytoolkit-mvvm\LICENSE.md',
    'licenses\communitytoolkit-mvvm\ThirdPartyNotices.txt',
    'licenses\communitytoolkit-winui-notifications\LICENSE.md',
    'licenses\fluent-ui-system-icons\LICENSE.txt',
    'licenses\managed-dependencies\MICROSOFT-MIT.txt',
    'licenses\sqlite\LICENSE.txt',
    'licenses\sqlitepclraw\APACHE-2.0.txt'
)
foreach ($requiredDistributionFile in $requiredDistributionFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $requiredDistributionFile) -PathType Leaf)) {
        throw "The core publish is missing a required distribution file: $requiredDistributionFile"
    }
}

$forbiddenPackageFiles = @($publishedFiles | Where-Object {
    $_.Name -in @('AppxManifest.xml', 'Package.appxmanifest') -or
    $_.Extension -in @('.appx', '.appxbundle', '.msix', '.msixbundle')
})
if ($forbiddenPackageFiles.Count -ne 0) {
    throw "The unpackaged publish contains an application package artifact: $($forbiddenPackageFiles.Name -join ', ')"
}

if ($isDryRun) {
    [pscustomobject]@{
        DryRun = $true
        Version = $Version
        Architecture = $architecture
        PublishDirectory = $publishRoot
        PublishFileCount = $publishedFiles.Count
        PublishBytes = ($publishedFiles | Measure-Object Length -Sum).Sum
        RuntimeInstaller = $runtimeInstallerPath
        ExpectedSetup = $setupPath
        Setup = $null
    }
    exit
}

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $compilerCandidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $InnoCompilerPath = $compilerCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoCompilerPath) -or
    -not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) {
    throw 'Inno Setup Compiler (ISCC.exe) was not found. Install Inno Setup or pass -InnoCompilerPath.'
}

& $InnoCompilerPath `
    '/Qp' `
    "/DAppVersion=$Version" `
    "/DAppArchitecture=$architecture" `
    "/DAppPublishDir=$publishRoot" `
    "/DRuntimeInstallerPath=$runtimeInstallerPath" `
    "/DSetupOutputDir=$installerRoot" `
    "/DRepositoryRoot=$repositoryRoot" `
    $setupScriptPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "The expected Setup executable was not produced: $setupPath"
}

[pscustomobject]@{
    DryRun = $false
    Version = $Version
    Architecture = $architecture
    PublishDirectory = $publishRoot
    PublishFileCount = $publishedFiles.Count
    PublishBytes = ($publishedFiles | Measure-Object Length -Sum).Sum
    RuntimeInstaller = $runtimeInstallerPath
    Setup = $setupPath
    SetupBytes = (Get-Item -LiteralPath $setupPath).Length
}
