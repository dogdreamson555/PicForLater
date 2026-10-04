[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version,

    [string]$OutputRoot,

    [string]$RuntimeCacheRoot,

    [string]$InnoCompilerPath,

    [ValidateSet('Online', 'Offline', 'Both')]
    [string]$Distribution = 'Online',

    [switch]$NoRestore,

    [switch]$DryRun,

    [switch]$SkipCompile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectPath = Join-Path $repositoryRoot 'src\PicForLater.App\PicForLater.App.csproj'
$setupScriptPath = Join-Path $PSScriptRoot 'setup\PicForLater.iss'
$prerequisitesScriptPath = Join-Path $PSScriptRoot 'setup\Install-Prerequisites.ps1'
$dependencyBuilderPath = Join-Path $PSScriptRoot 'Build-SetupDependencies.ps1'
$dotnetManifestPath = Join-Path $PSScriptRoot 'setup\dotnet-runtime.json'
$windowsAppSdkManifestPath = Join-Path $PSScriptRoot 'setup\windows-app-runtime.json'
$visualCppManifestPath = Join-Path $PSScriptRoot 'setup\visual-cpp-runtime.json'
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
$prerequisitesRoot = Join-Path $releaseRoot 'prerequisites'
$onlineSetupPath = Join-Path $installerRoot "PicForLater-Setup-$Version-$architecture.exe"
$offlineSetupPath = Join-Path $installerRoot "PicForLater-Setup-Offline-$Version-$architecture.exe"
$setupPath = if ($Distribution -eq 'Offline') { $offlineSetupPath } else { $onlineSetupPath }

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
Assert-PathUnderRoot -Path $prerequisitesRoot -Root $outputRootPath
$cachePrefix = [IO.Path]::TrimEndingDirectorySeparator($runtimeCacheRootPath) + [IO.Path]::DirectorySeparatorChar
$releasePrefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($releaseRoot)) + [IO.Path]::DirectorySeparatorChar
if ([IO.Path]::TrimEndingDirectorySeparator($runtimeCacheRootPath) -ieq
        [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($releaseRoot)) -or
    $runtimeCacheRootPath.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFullPath($releaseRoot).StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RuntimeCacheRoot must not overlap the versioned release output directory.'
}

$setupDefinition = Get-Content -Raw -LiteralPath $setupScriptPath
foreach ($requiredSetupDirective in @(
    'PrivilegesRequired=lowest',
    'DefaultDirName={localappdata}\Programs\PicForLater',
    'MinVersion=10.0.19041',
    'Source: "{#AppPublishDir}\*"',
    'Source: "{#PrerequisitesDir}\prerequisites.json"; Flags: dontcopy',
    'Parameters: "--uninstall-notifications"',
    '#if Distribution == "Offline"')) {
    if (-not $setupDefinition.Contains($requiredSetupDirective, [StringComparison]::Ordinal)) {
        throw "The Inno Setup definition is missing a required release directive: $requiredSetupDirective"
    }
}
if ($setupDefinition -match '(?im)^\s*(DelTree|DeleteDir)\b' -or
    $setupDefinition.Contains('{localappdata}\PicForLater', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Inno Setup definition must not delete or target the PicForLater user-data root.'
}
foreach ($requiredBuildFile in @($prerequisitesScriptPath, $dependencyBuilderPath, $dotnetManifestPath,
    $windowsAppSdkManifestPath, $visualCppManifestPath)) {
    if (-not (Test-Path -LiteralPath $requiredBuildFile -PathType Leaf)) {
        throw "A required setup build file was not found: $requiredBuildFile"
    }
}

$appProject = [xml](Get-Content -Raw -LiteralPath $projectPath)
$windowsAppSdkReference = $appProject.SelectSingleNode(
    "/Project/ItemGroup/PackageReference[@Include='Microsoft.WindowsAppSDK']")
$windowsAppSdkManifest = Get-Content -Raw -LiteralPath $windowsAppSdkManifestPath | ConvertFrom-Json
if ($null -eq $windowsAppSdkReference -or
    $windowsAppSdkReference.GetAttribute('Version') -ne [string]$windowsAppSdkManifest.version) {
    throw 'The pinned Windows App SDK Runtime does not match the app PackageReference.'
}

$dotnetManifest = Get-Content -Raw -LiteralPath $dotnetManifestPath | ConvertFrom-Json
$visualCppManifest = Get-Content -Raw -LiteralPath $visualCppManifestPath | ConvertFrom-Json
if ($dotnetManifest.version -notmatch '^\d+\.\d+\.\d+$' -or
    $visualCppManifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'A prerequisite manifest has an invalid pinned version.'
}
foreach ($framework in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
    foreach ($manifestArchitecture in @('x64', 'arm64')) {
        $definition = $dotnetManifest.architectures.$manifestArchitecture.frameworks.$framework
        if ($null -eq $definition -or
            $definition.fileName -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*\.exe$' -or
            $definition.uri -notmatch '^https://builds\.dotnet\.microsoft\.com/' -or
            [long]$definition.length -le 0 -or
            $definition.sha256 -notmatch '^[0-9a-f]{64}$' -or
            $definition.sha512 -notmatch '^[0-9a-f]{128}$') {
            throw "Invalid $framework prerequisite metadata for $manifestArchitecture."
        }
    }
}
foreach ($manifestArchitecture in @('x64', 'arm64')) {
    $definition = $visualCppManifest.architectures.$manifestArchitecture
    if ($null -eq $definition -or
        $definition.fileName -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*\.exe$' -or
        $definition.sourceUri -notmatch '^https://download\.visualstudio\.microsoft\.com/' -or
        [long]$definition.length -le 0 -or
        $definition.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Invalid Microsoft Visual C++ prerequisite metadata for $manifestArchitecture."
    }
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
if (Test-Path -LiteralPath $installerRoot) {
    Remove-Item -LiteralPath $installerRoot -Recurse -Force
}
if (Test-Path -LiteralPath $prerequisitesRoot) {
    Remove-Item -LiteralPath $prerequisitesRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot,$installerRoot -Force | Out-Null

$publishArguments = @(
    'publish',
    $projectPath,
    '-c',
    'Release',
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    '-p:SelfContained=false',
    '-p:WindowsAppSDKSelfContained=false',
    '-p:AppHostDotNetSearch=Global',
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

& dotnet @publishArguments | Out-Host
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

$runtimeConfigPath = Join-Path $publishRoot 'PicForLater.App.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
    throw 'The framework-dependent publish is missing its runtime configuration.'
}
$runtimeConfig = Get-Content -Raw -LiteralPath $runtimeConfigPath | ConvertFrom-Json
$runtimeFrameworkDefinitions = @($runtimeConfig.runtimeOptions.frameworks)
$runtimeFrameworkNames = @($runtimeFrameworkDefinitions | ForEach-Object { [string]$_.name })
$requiredFrameworks = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')
if ($runtimeFrameworkDefinitions.Count -ne $requiredFrameworks.Count -or
    @(Compare-Object $requiredFrameworks $runtimeFrameworkNames).Count -ne 0) {
    throw 'The framework-dependent publish must declare exactly .NET and ASP.NET Core runtimes.'
}
if ($null -ne $runtimeConfig.runtimeOptions.PSObject.Properties['includedFrameworks']) {
    throw 'The app publish unexpectedly bundles .NET runtime frameworks.'
}
$localRuntimeFiles = @($publishedFiles | Where-Object {
    $_.Name -in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')
})
if ($localRuntimeFiles.Count -ne 0) {
    throw "The framework-dependent app publish contains a local .NET runtime: $($localRuntimeFiles.Name -join ', ')"
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
    'licenses\h-notifyicon\LICENSE.txt',
    'licenses\localsenddotnet-core\LICENSE',
    'licenses\localsenddotnet-core\NOTICE',
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

$dependencyResult = & $dependencyBuilderPath `
    -Version $Version `
    -Architecture $architecture `
    -WindowsAppSdkVersion ([string]$windowsAppSdkManifest.version) `
    -PublishDirectory $publishRoot `
    -PrerequisitesDirectory $prerequisitesRoot `
    -RuntimeCacheDirectory $runtimeCacheRootPath `
    -DotNetManifestPath $dotnetManifestPath `
    -VisualCppManifestPath $visualCppManifestPath
$prerequisiteFiles = @($dependencyResult.PrerequisiteFiles)
$visualCppRuntimeInstallerPath = [string]$dependencyResult.VisualCppRuntimeFile
$runtimeInstallerPath = [string]$dependencyResult.WindowsAppRuntimeFile
$publishedFiles = @(Get-ChildItem -LiteralPath $publishRoot -File -Recurse)

if (-not $isDryRun) {
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

    $distributionsToBuild = if ($Distribution -eq 'Both') { @('Online', 'Offline') } else { @($Distribution) }
    foreach ($currentDistribution in $distributionsToBuild) {
        $compilerArguments = @(
            '/Qp',
            "/DAppVersion=$Version",
            "/DAppArchitecture=$architecture",
            "/DAppPublishDir=$publishRoot",
            "/DPrerequisitesDir=$prerequisitesRoot",
            "/DPrerequisitesScriptPath=$prerequisitesScriptPath",
            "/DDistribution=$currentDistribution",
            "/DSetupOutputDir=$installerRoot",
            "/DRepositoryRoot=$repositoryRoot"
        )
        & $InnoCompilerPath @compilerArguments $setupScriptPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compilation failed for $currentDistribution with exit code $LASTEXITCODE."
        }
    }

    foreach ($expectedPath in @(
        if ($Distribution -in @('Online', 'Both')) { $onlineSetupPath }
        if ($Distribution -in @('Offline', 'Both')) { $offlineSetupPath }
    )) {
        if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
            throw "The expected Setup executable was not produced: $expectedPath"
        }
    }
}

$result = [ordered]@{
    DryRun = $isDryRun
    Version = $Version
    Architecture = $architecture
    Distribution = $Distribution
    PublishDirectory = $publishRoot
    PublishFileCount = $publishedFiles.Count
    PublishBytes = ($publishedFiles | Measure-Object Length -Sum).Sum
    PrerequisitesDirectory = $prerequisitesRoot
    PrerequisiteFiles = $prerequisiteFiles
    RuntimeInstaller = $runtimeInstallerPath
    VisualCppRuntimeInstaller = $visualCppRuntimeInstallerPath
    Setup = if (-not $isDryRun) { $setupPath } else { $null }
    SetupBytes = if (-not $isDryRun) { (Get-Item -LiteralPath $setupPath).Length } else { $null }
    OfflineSetup = if (-not $isDryRun -and $Distribution -in @('Offline', 'Both')) { $offlineSetupPath } else { $null }
    OfflineSetupBytes = if (-not $isDryRun -and $Distribution -in @('Offline', 'Both')) { (Get-Item -LiteralPath $offlineSetupPath).Length } else { $null }
}
if ($isDryRun) {
    $result['ExpectedSetup'] = $setupPath
    $result['ExpectedOfflineSetup'] = if ($Distribution -in @('Offline', 'Both')) { $offlineSetupPath } else { $null }
}
[pscustomobject]$result
