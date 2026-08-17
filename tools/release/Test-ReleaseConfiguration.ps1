[CmdletBinding()]
param(
    [string]$ActionlintPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workflowRoot = Join-Path $repositoryRoot '.github\workflows'
$ciPath = Join-Path $workflowRoot 'ci.yml'
$releasePath = Join-Path $workflowRoot 'release.yml'
$setupBuildPath = Join-Path $PSScriptRoot 'Build-Setup.ps1'
$setupDefinitionPath = Join-Path $PSScriptRoot 'setup\PicForLater.iss'
$runtimeManifestPath = Join-Path $PSScriptRoot 'setup\windows-app-runtime.json'
$appProjectPath = Join-Path $repositoryRoot 'src\PicForLater.App\PicForLater.App.csproj'

foreach ($requiredPath in @(
    $ciPath,
    $releasePath,
    $setupBuildPath,
    $setupDefinitionPath,
    $runtimeManifestPath,
    $appProjectPath,
    (Join-Path $repositoryRoot 'global.json'))) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release file is missing: $requiredPath"
    }
}

if ([string]::IsNullOrWhiteSpace($ActionlintPath)) {
    $ActionlintPath = Get-Command actionlint.exe -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Source -First 1
}
if ([string]::IsNullOrWhiteSpace($ActionlintPath) -or
    -not (Test-Path -LiteralPath $ActionlintPath -PathType Leaf)) {
    throw 'actionlint.exe is required for local workflow syntax validation. Pass -ActionlintPath.'
}

& $ActionlintPath $ciPath $releasePath
if ($LASTEXITCODE -ne 0) {
    throw "actionlint reported workflow errors (exit code $LASTEXITCODE)."
}

$ci = Get-Content -Raw -LiteralPath $ciPath
$release = Get-Content -Raw -LiteralPath $releasePath
$allWorkflows = "$ci`n$release"

$usesLines = @($allWorkflows -split "`r?`n" | Where-Object { $_ -match '^\s*uses:\s*' })
if ($usesLines.Count -eq 0) {
    throw 'No GitHub Actions dependencies were found.'
}
foreach ($usesLine in $usesLines) {
    if ($usesLine -notmatch '^\s*uses:\s*actions/[a-z0-9-]+@[0-9a-f]{40}(?:\s+#\s+v\d[^\s]*)?\s*$') {
        throw "Action dependency is not an official action pinned to a full SHA: $($usesLine.Trim())"
    }
}

foreach ($workflow in @($ci, $release)) {
    foreach ($requiredToken in @('runs-on: windows-2025', 'permissions:', 'contents: read', 'persist-credentials: false', '--locked-mode')) {
        if (-not $workflow.Contains($requiredToken, [StringComparison]::Ordinal)) {
            throw "A workflow is missing the required token: $requiredToken"
        }
    }
    if ($workflow -match '\$\{\{\s*secrets\.') {
        throw 'Core build/release workflows must not consume secrets.'
    }
}

if (-not $release.Contains('workflow_dispatch:', [StringComparison]::Ordinal) -or
    $release -match '(?m)^\s*(tags|release):\s*$') {
    throw 'The release workflow must be manual-only and must not use tag/release triggers.'
}
foreach ($requiredReleaseToken in @(
    'needs: validate',
    'Build-Setup.ps1',
    "-Platform '`${{ matrix.platform }}'",
    'PicForLater-Setup-$env:APP_VERSION-$arch.exe',
    'artifacts/github/${{ matrix.artifact_arch }}/*',
    'if-no-files-found: error',
    'Expected exactly one Setup.exe')) {
    if (-not $release.Contains($requiredReleaseToken, [StringComparison]::Ordinal)) {
        throw "The release workflow is missing an artifact-path invariant: $requiredReleaseToken"
    }
}

$runtimeManifest = Get-Content -Raw -LiteralPath $runtimeManifestPath | ConvertFrom-Json
$appProject = [xml](Get-Content -Raw -LiteralPath $appProjectPath)
$windowsAppSdkReference = $appProject.SelectSingleNode(
    "/Project/ItemGroup/PackageReference[@Include='Microsoft.WindowsAppSDK']")
if ($null -eq $windowsAppSdkReference -or
    $windowsAppSdkReference.GetAttribute('Version') -ne $runtimeManifest.version) {
    throw 'The Windows App SDK package and pinned offline Runtime versions differ.'
}
foreach ($architecture in @('x64', 'arm64')) {
    $definition = $runtimeManifest.architectures.$architecture
    if ($null -eq $definition -or
        [long]$definition.length -le 0 -or
        [string]$definition.sha256 -notmatch '^[0-9a-f]{64}$' -or
        [string]$definition.uri -notmatch '^https://') {
        throw "The offline Runtime definition is incomplete for $architecture."
    }
}

$globalJson = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'global.json') | ConvertFrom-Json
if ($globalJson.sdk.version -ne '10.0.302' -or $globalJson.sdk.rollForward -ne 'disable') {
    throw 'global.json must pin exactly .NET SDK 10.0.302 with rollForward disabled.'
}

[pscustomobject]@{
    Actionlint = (& $ActionlintPath -version)
    Workflows = 2
    ActionsPinnedToFullSha = $usesLines.Count
    ReleaseTrigger = 'workflow_dispatch only'
    ArtifactArchitectures = 'x64, arm64'
    ArtifactFilesPerArchitecture = 'Setup.exe'
    WindowsAppRuntime = $runtimeManifest.version
    DotNetSdk = $globalJson.sdk.version
}
