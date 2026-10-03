[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('x64', 'arm64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$WindowsAppSdkVersion,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$PrerequisitesDirectory,
    [Parameter(Mandatory = $true)][string]$RuntimeCacheDirectory,
    [Parameter(Mandatory = $true)][string]$DotNetManifestPath,
    [Parameter(Mandatory = $true)][string]$VisualCppManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PathUnderRoot {
    param([string]$Path, [string]$Root)

    $candidate = [IO.Path]::GetFullPath($Path)
    $rootPrefix = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root)) + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The prerequisite output path escapes its release directory: $candidate"
    }
}

function Test-MicrosoftSignature {
    param([string]$Path)

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
        return $false
    }

    $subject = [string]$signature.SignerCertificate.Subject
    return $subject -like 'CN=Microsoft Corporation,*' -or
        $subject -like 'CN=.NET, O=Microsoft Corporation,*'
}

function Test-ExpectedExecutable {
    param([string]$Path, [object]$Definition)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $file = Get-Item -LiteralPath $Path
        if ($file.Length -ne [long]$Definition.length) { return $false }
        foreach ($algorithm in @('SHA256', 'SHA512')) {
            $hashProperty = $Definition.PSObject.Properties[$algorithm.ToLowerInvariant()]
            if ($algorithm -eq 'SHA512' -and $null -eq $hashProperty) { continue }
            if ($null -eq $hashProperty -or
                (Get-FileHash -LiteralPath $Path -Algorithm $algorithm).Hash -ine [string]$hashProperty.Value) {
                return $false
            }
        }
        return Test-MicrosoftSignature -Path $Path
    }
    catch {
        return $false
    }
}

function Get-VerifiedExecutable {
    param(
        [string]$CachePath,
        [string]$Uri,
        [object]$Definition
    )

    if (Test-ExpectedExecutable -Path $CachePath -Definition $Definition) {
        return Get-Item -LiteralPath $CachePath
    }

    $temporaryPath = "$CachePath.$([Guid]::NewGuid().ToString('N')).partial"
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $temporaryPath -UseBasicParsing
        if (-not (Test-ExpectedExecutable -Path $temporaryPath -Definition $Definition)) {
            throw "The downloaded Microsoft prerequisite failed length, hashes, or signature validation: $Uri"
        }

        Move-Item -LiteralPath $temporaryPath -Destination $CachePath -Force
        return Get-Item -LiteralPath $CachePath
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-WindowsAppSdkPackageRoot {
    param([string]$PackageVersion)

    $globalPackages = $env:NUGET_PACKAGES
    if ([string]::IsNullOrWhiteSpace($globalPackages)) {
        $globalPackages = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
    }
    $packageRoot = Join-Path $globalPackages "microsoft.windowsappsdk.runtime\$PackageVersion\tools\MSIX"
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        throw "The restored Windows App SDK MSIX package directory was not found: $packageRoot"
    }
    return $packageRoot
}

function Get-PackageMetadata {
    param([string]$Path, [string]$ExpectedArchitecture)

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notlike 'CN=Microsoft Corporation,*') {
        throw "The Windows App SDK package has no valid Microsoft signature: $Path"
    }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $packageArchive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $packageArchive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) { throw "The MSIX package has no AppxManifest.xml: $Path" }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = [xml]$reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally {
        $packageArchive.Dispose()
    }

    $identity = $manifest.SelectSingleNode('/*[local-name()="Package"]/*[local-name()="Identity"]')
    if ($null -eq $identity) {
        throw "The MSIX package manifest is missing identity metadata: $Path"
    }
    if ([string]$identity.ProcessorArchitecture -ine $ExpectedArchitecture) {
        throw "The MSIX package architecture does not match its NuGet folder: $Path"
    }

    $file = Get-Item -LiteralPath $Path
    $packageFileName = "$ExpectedArchitecture-$($file.Name)"

    [pscustomobject]@{
        sourcePath = $Path
        fileName = $packageFileName
        name = [string]$identity.Name
        version = [string]$identity.Version
        architecture = [string]$identity.ProcessorArchitecture
        length = [long]$file.Length
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-WindowsAppSdkPackages {
    param([string]$PackageRoot, [string]$TargetArchitecture)

    $selection = if ($TargetArchitecture -eq 'x64') {
        @(
            @{ Architecture = 'x64'; Name = 'Microsoft.WindowsAppRuntime.2.msix' },
            @{ Architecture = 'x86'; Name = 'Microsoft.WindowsAppRuntime.2.msix' },
            @{ Architecture = 'x64'; Name = 'Microsoft.WindowsAppRuntime.Main.2.msix' },
            @{ Architecture = 'x64'; Name = 'Microsoft.WindowsAppRuntime.Singleton.2.msix' },
            @{ Architecture = 'x64'; Name = 'Microsoft.WindowsAppRuntime.DDLM.2.msix' },
            @{ Architecture = 'x86'; Name = 'Microsoft.WindowsAppRuntime.DDLM.2.msix' }
        )
    }
    else {
        @(
            @{ Architecture = 'x64'; Name = 'Microsoft.WindowsAppRuntime.2.msix' },
            @{ Architecture = 'x86'; Name = 'Microsoft.WindowsAppRuntime.2.msix' },
            @{ Architecture = 'arm64'; Name = 'Microsoft.WindowsAppRuntime.2.msix' },
            @{ Architecture = 'arm64'; Name = 'Microsoft.WindowsAppRuntime.Main.2.msix' },
            @{ Architecture = 'arm64'; Name = 'Microsoft.WindowsAppRuntime.Singleton.2.msix' },
            @{ Architecture = 'x64'; Name = 'Microsoft.WindowsAppRuntime.DDLM.2.msix' },
            @{ Architecture = 'x86'; Name = 'Microsoft.WindowsAppRuntime.DDLM.2.msix' },
            @{ Architecture = 'arm64'; Name = 'Microsoft.WindowsAppRuntime.DDLM.2.msix' }
        )
    }

    $packages = @()
    foreach ($entry in $selection) {
        $sourcePath = Join-Path (Join-Path $PackageRoot "win10-$($entry.Architecture)") $entry.Name
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "A required Windows App SDK MSIX package was not found: $sourcePath"
        }
        $packages += Get-PackageMetadata -Path $sourcePath -ExpectedArchitecture $entry.Architecture
    }

    if ($packages.Count -ne $(if ($TargetArchitecture -eq 'x64') { 6 } else { 8 })) {
        throw "The Windows App SDK package set is incomplete for $TargetArchitecture."
    }
    return $packages
}

function New-WindowsAppSdkArchive {
    param([object[]]$Packages, [string]$Path)

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $temporaryPath = "$Path.$([Guid]::NewGuid().ToString('N')).partial"
    $stream = $null
    $archive = $null
    try {
        $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
        foreach ($package in $Packages) {
            $entry = $archive.CreateEntry([string]$package.fileName, [IO.Compression.CompressionLevel]::NoCompression)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $destination = $entry.Open()
            $source = [IO.File]::OpenRead([string]$package.sourcePath)
            try { $source.CopyTo($destination) }
            finally {
                $source.Dispose()
                $destination.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $archive) { $archive.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
    try { Move-Item -LiteralPath $temporaryPath -Destination $Path -Force }
    finally { Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue }
    [pscustomobject]@{
        file = Get-Item -LiteralPath $Path
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

$absolutePublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$absolutePrerequisitesDirectory = [IO.Path]::GetFullPath($PrerequisitesDirectory)
$absoluteRuntimeCacheDirectory = [IO.Path]::GetFullPath($RuntimeCacheDirectory)
$releaseDirectory = Split-Path -Parent $absolutePrerequisitesDirectory
Assert-PathUnderRoot -Path $absolutePublishDirectory -Root $releaseDirectory
Assert-PathUnderRoot -Path $absolutePrerequisitesDirectory -Root $releaseDirectory

$dotnetManifest = Get-Content -LiteralPath $DotNetManifestPath -Raw | ConvertFrom-Json
$visualCppManifest = Get-Content -LiteralPath $VisualCppManifestPath -Raw | ConvertFrom-Json
if ($dotnetManifest.version -notmatch '^\d+\.\d+\.\d+$' -or
    $visualCppManifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'The pinned prerequisite versions are invalid.'
}
$dotnetArchitecture = $dotnetManifest.architectures.$Architecture
$visualCppArchitecture = $visualCppManifest.architectures.$Architecture
if ($null -eq $dotnetArchitecture -or $null -eq $visualCppArchitecture) {
    throw "A prerequisite is not pinned for $Architecture."
}

$publishFiles = @(Get-ChildItem -LiteralPath $absolutePublishDirectory -File -Recurse)
if ($publishFiles.Count -eq 0) { throw 'The app publish directory contains no files.' }
$publishPrefix = [IO.Path]::TrimEndingDirectorySeparator($absolutePublishDirectory) + [IO.Path]::DirectorySeparatorChar
$appFiles = @($publishFiles | ForEach-Object {
    $fullPath = [IO.Path]::GetFullPath($_.FullName)
    if (-not $fullPath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "A published file escapes the app directory: $fullPath"
    }
    $fullPath.Substring($publishPrefix.Length).Replace('\', '/')
})
[Array]::Sort($appFiles, [StringComparer]::OrdinalIgnoreCase)

New-Item -ItemType Directory -Path $absolutePrerequisitesDirectory,$absoluteRuntimeCacheDirectory -Force | Out-Null

$dotnetRuntime = $dotnetArchitecture.frameworks.'Microsoft.NETCore.App'
$aspNetCoreRuntime = $dotnetArchitecture.frameworks.'Microsoft.AspNetCore.App'
if ($null -eq $dotnetRuntime -or $null -eq $aspNetCoreRuntime -or
    $dotnetRuntime.sha512 -notmatch '^[0-9a-fA-F]{128}$' -or
    $aspNetCoreRuntime.sha512 -notmatch '^[0-9a-fA-F]{128}$') {
    throw "The .NET prerequisite manifest is missing a runtime or its SHA512 pin for $Architecture."
}

$dotnetRuntimePath = Join-Path $absoluteRuntimeCacheDirectory $dotnetRuntime.fileName
$dotnetRuntimeFile = Get-VerifiedExecutable -CachePath $dotnetRuntimePath -Uri $dotnetRuntime.uri -Definition $dotnetRuntime
$aspNetCoreRuntimePath = Join-Path $absoluteRuntimeCacheDirectory $aspNetCoreRuntime.fileName
$aspNetCoreRuntimeFile = Get-VerifiedExecutable -CachePath $aspNetCoreRuntimePath -Uri $aspNetCoreRuntime.uri -Definition $aspNetCoreRuntime

$visualCppPath = Join-Path $absoluteRuntimeCacheDirectory $visualCppArchitecture.fileName
$visualCppFile = Get-VerifiedExecutable -CachePath $visualCppPath -Uri $visualCppArchitecture.sourceUri -Definition $visualCppArchitecture
$prerequisitePayloads = @($dotnetRuntimeFile, $aspNetCoreRuntimeFile, $visualCppFile)
foreach ($payload in $prerequisitePayloads) {
    $outputPath = Join-Path $absolutePrerequisitesDirectory $payload.Name
    if ([IO.Path]::GetFullPath($payload.FullName) -ine [IO.Path]::GetFullPath($outputPath)) {
        Copy-Item -LiteralPath $payload.FullName -Destination $outputPath -Force
    }
    if ((Get-Item -LiteralPath $outputPath).Length -ne $payload.Length -or
        (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash -ine
            (Get-FileHash -LiteralPath $payload.FullName -Algorithm SHA256).Hash) {
        throw "The prerequisite payload copy failed verification: $outputPath"
    }
}

$windowsAppSdkPackageRoot = Get-WindowsAppSdkPackageRoot -PackageVersion $WindowsAppSdkVersion
$windowsAppSdkPackages = @(Get-WindowsAppSdkPackages -PackageRoot $windowsAppSdkPackageRoot -TargetArchitecture $Architecture)
$windowsAppSdkArchiveName = "WindowsAppRuntime-$WindowsAppSdkVersion-$Architecture.zip"
$windowsAppSdkArchivePath = Join-Path $absolutePrerequisitesDirectory $windowsAppSdkArchiveName
$windowsAppSdkArchive = New-WindowsAppSdkArchive -Packages $windowsAppSdkPackages -Path $windowsAppSdkArchivePath
$releaseUriRoot = "https://github.com/dogdreamson555/PicForLater/releases/download/v$Version"

$prerequisites = @(
    [ordered]@{
        id = 'dotnet-runtime'
        name = '.NET Runtime'
        kind = 'exe'
        fileName = [string]$dotnetRuntime.fileName
        uri = [string]$dotnetRuntime.uri
        length = [long]$dotnetRuntimeFile.Length
        sha256 = [string]$dotnetRuntime.sha256
        minimumVersion = [string]$dotnetManifest.version
        framework = 'Microsoft.NETCore.App'
    },
    [ordered]@{
        id = 'aspnetcore-runtime'
        name = 'ASP.NET Core Runtime'
        kind = 'exe'
        fileName = [string]$aspNetCoreRuntime.fileName
        uri = [string]$aspNetCoreRuntime.uri
        length = [long]$aspNetCoreRuntimeFile.Length
        sha256 = [string]$aspNetCoreRuntime.sha256
        minimumVersion = [string]$dotnetManifest.version
        framework = 'Microsoft.AspNetCore.App'
    },
    [ordered]@{
        id = 'visual-cpp-runtime'
        name = 'Microsoft Visual C++ Runtime'
        kind = 'exe'
        fileName = [string]$visualCppArchitecture.fileName
        uri = "$releaseUriRoot/$($visualCppArchitecture.fileName)"
        length = [long]$visualCppFile.Length
        sha256 = [string]$visualCppArchitecture.sha256
        minimumVersion = [string]$visualCppManifest.version
    },
    [ordered]@{
        id = 'windows-app-runtime'
        name = 'Windows App Runtime'
        kind = 'msixZip'
        fileName = $windowsAppSdkArchiveName
        uri = "$releaseUriRoot/$windowsAppSdkArchiveName"
        length = [long]$windowsAppSdkArchive.file.Length
        sha256 = [string]$windowsAppSdkArchive.sha256
        minimumVersion = [string]$WindowsAppSdkVersion
        packages = @($windowsAppSdkPackages | ForEach-Object {
            [ordered]@{
                fileName = [string]$_.fileName
                name = [string]$_.name
                version = [string]$_.version
                architecture = [string]$_.architecture
                sha256 = [string]$_.sha256
                length = [long]$_.length
            }
        })
    }
)

$manifest = [ordered]@{
    schemaVersion = 1
    architecture = $Architecture
    appFiles = $appFiles
    prerequisites = $prerequisites
}
$manifestPath = Join-Path $absolutePrerequisitesDirectory 'prerequisites.json'
$temporaryManifestPath = "$manifestPath.$([Guid]::NewGuid().ToString('N')).partial"
try {
    $json = $manifest | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($temporaryManifestPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryManifestPath -Destination $manifestPath -Force
}
finally {
    Remove-Item -LiteralPath $temporaryManifestPath -Force -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    Directory = $absolutePrerequisitesDirectory
    Manifest = $manifestPath
    PrerequisiteFiles = @(
        (Join-Path $absolutePrerequisitesDirectory $visualCppFile.Name),
        $windowsAppSdkArchive.file.FullName
    )
    WindowsAppRuntimeFile = $windowsAppSdkArchive.file.FullName
    VisualCppRuntimeFile = $visualCppFile.FullName
}
