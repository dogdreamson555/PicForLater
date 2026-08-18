[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Version,

    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = 'x64',

    [ValidateRange(1, 2147483647)]
    [int]$ProtocolMinimumVersion = 1,

    [ValidateRange(1, 2147483647)]
    [int]$ProtocolMaximumVersion = 1,

    [string]$OutputRoot,

    [string]$SigningPrivateKeyPath,

    [switch]$SkipArchive,

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-SafeComponentName {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value.EndsWith('.')) {
        return $false
    }

    $deviceName = $Value.Split('.', 2)[0]
    if ($deviceName -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
        return $false
    }

    return $true
}

if (-not (Test-SafeComponentName -Value $Version)) {
    throw "Version is not a safe Windows component directory name: $Version"
}

if ($ProtocolMaximumVersion -lt $ProtocolMinimumVersion) {
    throw 'ProtocolMaximumVersion must be greater than or equal to ProtocolMinimumVersion.'
}

if (-not $SkipArchive -and [string]::IsNullOrWhiteSpace($SigningPrivateKeyPath)) {
    throw 'SigningPrivateKeyPath is required when producing a release archive.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\local-inference-components'
}
elseif (-not [IO.Path]::IsPathFullyQualified($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot $OutputRoot
}

$architecture = if ($Platform -eq 'ARM64') { 'arm64' } else { 'x64' }
$runtimeIdentifier = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$architectureRoot = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $architecture
$componentRoot = Join-Path $architectureRoot $Version
if (Test-Path -LiteralPath $componentRoot) {
    throw "The component output already exists: $componentRoot"
}

$projectPath = Join-Path $repositoryRoot 'src\PicForLater.LocalInference\PicForLater.LocalInference.csproj'
$publishArguments = @(
    'publish',
    $projectPath,
    '-c',
    'Release',
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    '-p:SelfContained=true',
    '-p:PublishTrimmed=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '-p:CopyOutputSymbolsToPublishDirectory=false',
    '-o',
    $componentRoot
)
if ($NoRestore) {
    $publishArguments += '--no-restore'
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Local inference publish failed with exit code $LASTEXITCODE."
}

$workerPath = Join-Path $componentRoot 'PicForLater.LocalInference.exe'
if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
    throw 'The published component does not contain PicForLater.LocalInference.exe.'
}

$files = @(
    Get-ChildItem -LiteralPath $componentRoot -File -Recurse |
        Where-Object { $_.Name -ne 'component.json' } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath($componentRoot, $_.FullName).Replace('\', '/')
            [ordered]@{
                path = $relativePath
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
)
if ($files.Count -eq 0 -or $files.Count -gt 512) {
    throw "The component must contain between 1 and 512 payload files; found $($files.Count)."
}

$componentManifest = [ordered]@{
    schemaVersion = 1
    componentId = 'PicForLater.LocalInference'
    version = $Version
    architecture = $architecture
    protocolMinimumVersion = $ProtocolMinimumVersion
    protocolMaximumVersion = $ProtocolMaximumVersion
    files = $files
}
$componentManifestPath = Join-Path $componentRoot 'component.json'
$componentManifest |
    ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $componentManifestPath -Encoding utf8NoBOM

$activeManifestPath = Join-Path $architectureRoot 'active.json'
[ordered]@{
    schemaVersion = 1
    version = $Version
} |
    ConvertTo-Json |
    Set-Content -LiteralPath $activeManifestPath -Encoding utf8NoBOM

$archivePath = $null
$releaseManifestPath = $null
$releaseSignaturePath = $null
if (-not $SkipArchive) {
    $archiveName = "PicForLater.LocalInference-$architecture-$Version.zip"
    $archivePath = Join-Path $architectureRoot $archiveName
    if (Test-Path -LiteralPath $archivePath) {
        throw "The component archive already exists: $archivePath"
    }

    Compress-Archive -Path (Join-Path $componentRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
    $archiveInfo = Get-Item -LiteralPath $archivePath
    $releaseManifestPath = Join-Path $architectureRoot "local-inference-$architecture.release.json"
    [ordered]@{
        schemaVersion = 1
        componentId = 'PicForLater.LocalInference'
        version = $Version
        architecture = $architecture
        protocolMinimumVersion = $ProtocolMinimumVersion
        protocolMaximumVersion = $ProtocolMaximumVersion
        archiveFileName = $archiveName
        archiveLength = $archiveInfo.Length
        componentLength = (Get-ChildItem -LiteralPath $componentRoot -File -Recurse |
            Measure-Object Length -Sum).Sum
        archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        componentManifestSha256 = (Get-FileHash -LiteralPath $componentManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath $releaseManifestPath -Encoding utf8NoBOM

    if (-not [IO.Path]::IsPathFullyQualified($SigningPrivateKeyPath)) {
        throw 'SigningPrivateKeyPath must be an absolute path outside the repository.'
    }

    $resolvedSigningKeyPath = (Resolve-Path -LiteralPath $SigningPrivateKeyPath).Path
    $repositoryPrefix = $repositoryRoot + [IO.Path]::DirectorySeparatorChar
    if ($resolvedSigningKeyPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The local inference signing private key must not be stored inside the repository.'
    }

    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem([IO.File]::ReadAllText($resolvedSigningKeyPath))
        if ($rsa.KeySize -lt 3072) {
            throw 'The local inference signing key must be RSA 3072 bits or stronger.'
        }

        $signatureBytes = $rsa.SignData(
            [IO.File]::ReadAllBytes($releaseManifestPath),
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
        $releaseSignaturePath = "$releaseManifestPath.sig"
        [IO.File]::WriteAllText(
            $releaseSignaturePath,
            [Convert]::ToBase64String($signatureBytes),
            [Text.UTF8Encoding]::new($false))
    }
    finally {
        $rsa.Dispose()
    }
}

$publishedFiles = Get-ChildItem -LiteralPath $componentRoot -File -Recurse
[pscustomobject]@{
    ComponentDirectory = $componentRoot
    ComponentFileCount = $publishedFiles.Count
    ComponentBytes = ($publishedFiles | Measure-Object Length -Sum).Sum
    ComponentManifest = $componentManifestPath
    ActiveManifest = $activeManifestPath
    Archive = $archivePath
    ReleaseManifest = $releaseManifestPath
    ReleaseSignature = $releaseSignaturePath
}
