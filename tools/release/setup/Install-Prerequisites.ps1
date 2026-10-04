[CmdletBinding()]
param(
    [ValidateSet('Detect', 'Download', 'Verify', 'InstallWindowsRuntime', 'CaptureLegacy', 'CleanupLegacy')]
    [string]$Action = 'Detect',
    [string]$ManifestPath,
    [string]$OutputPath,
    [string]$PayloadPath,
    [string]$PrerequisiteId,
    [string]$InstallDirectory,
    [string]$LegacyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-SetupManifest {
    param([string]$Path)
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.architecture -notin @('x64', 'arm64')) {
        throw 'Unsupported prerequisites manifest.'
    }
    $expectedIds = @('dotnet-runtime', 'aspnetcore-runtime', 'visual-cpp-runtime', 'windows-app-runtime')
    if (@($manifest.prerequisites).Count -ne $expectedIds.Count -or
        @(Compare-Object $expectedIds @($manifest.prerequisites.id)).Count -ne 0) {
        throw 'Incomplete prerequisites manifest.'
    }
    foreach ($item in $manifest.prerequisites) {
        if ($item.fileName -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*$' -or
            $item.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$item.length -le 0 -or
            $item.uri -notmatch '^https://[^\s]+$' -or $item.name -match '[\r\n]' -or
            $item.kind -notin @('exe', 'msixZip')) {
            throw 'Invalid prerequisite definition.'
        }
        $null = [version]$item.minimumVersion
    }
    return $manifest
}

function Test-CompatibleRuntimeVersion {
    param([string]$Installed, [string]$Minimum, [ValidateSet('Minor', 'LatestPatch')][string]$RollForward = 'Minor')
    $version = $null
    if (-not [version]::TryParse($Installed, [ref]$version)) { return $false }
    $required = [version]$Minimum
    return $version.Major -eq $required.Major -and
        $version -ge $required -and ($RollForward -eq 'Minor' -or $version.Minor -eq $required.Minor)
}

function Get-GlobalDotNetPath {
    param([string]$Architecture)
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry32)
    try {
        $key = $registry.OpenSubKey("SOFTWARE\dotnet\Setup\InstalledVersions\$Architecture")
        try {
            if ($null -ne $key) {
                $location = [string]$key.GetValue('InstallLocation')
                if (-not [string]::IsNullOrWhiteSpace($location)) { return Join-Path $location 'dotnet.exe' }
            }
        }
        finally { if ($null -ne $key) { $key.Dispose() } }
    }
    finally { $registry.Dispose() }
    return Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
}

function Get-AspNetCoreDependency {
    param($Runtime)
    if ($null -eq $Runtime) { return $null }
    $configPath = Join-Path (Join-Path $Runtime.Directory $Runtime.Version.ToString()) 'Microsoft.AspNetCore.App.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { return $null }
    try {
        $options = (Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json).runtimeOptions
        $dependency = $options.framework
        if ($dependency.name -ne 'Microsoft.NETCore.App') { return $null }
        $rollForward = if ($null -ne $options.PSObject.Properties['rollForward']) { [string]$options.rollForward } else { 'Minor' }
        if ($rollForward -notin @('Minor', 'LatestPatch')) { return $null }
        return [pscustomobject]@{ Version = [version]$dependency.version; RollForward = $rollForward }
    } catch { return $null }
}

function Get-DotNetRuntimes {
    param([string]$Architecture)
    $hostPath = Get-GlobalDotNetPath $Architecture
    if (-not (Test-Path -LiteralPath $hostPath -PathType Leaf)) { return @() }
    $runtimes = @(& $hostPath --list-runtimes 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'The registered .NET host could not enumerate runtimes.' }
    foreach ($line in $runtimes) {
        $version = $null
        if ([string]$line -match '^([^ ]+) ([^ ]+) \[(.+)\]$' -and [version]::TryParse($Matches[2], [ref]$version)) {
            [pscustomobject]@{ Name = $Matches[1]; Version = $version; Directory = $Matches[3] }
        }
    }
}

function Test-DotNetPrerequisite {
    param($Item, [string]$Architecture, $AspNetRuntime = $null, $RuntimeDefinitions = $null)
    $definitions = if ($null -eq $RuntimeDefinitions) { @(Get-DotNetRuntimes $Architecture) } else { $RuntimeDefinitions }
    if ($Item.framework -eq 'Microsoft.AspNetCore.App' -or $null -ne $AspNetRuntime) {
        $aspMinimum = if ($Item.framework -eq 'Microsoft.AspNetCore.App') { $Item.minimumVersion } else { $AspNetRuntime.minimumVersion }
        $selectedAsp = $definitions | Where-Object {
            $_.Name -eq 'Microsoft.AspNetCore.App' -and (Test-CompatibleRuntimeVersion $_.Version.ToString() $aspMinimum)
        } | Sort-Object @{ Expression = { $_.Version.Minor } }, @{ Expression = { $_.Version }; Descending = $true } | Select-Object -First 1
        $dependency = Get-AspNetCoreDependency $selectedAsp
        if ($Item.framework -eq 'Microsoft.AspNetCore.App') { return $null -ne $dependency }
        if ($null -eq $dependency) {
            $installVersion = if ($null -ne $AspNetRuntime.PSObject.Properties['installVersion']) { $AspNetRuntime.installVersion } else { $AspNetRuntime.minimumVersion }
            $dependency = [pscustomobject]@{ Version = [version]$installVersion; RollForward = 'LatestPatch' }
        }
    } else { $dependency = $null }
    foreach ($runtime in $definitions) {
        if ($runtime.Name -eq $Item.framework -and (Test-CompatibleRuntimeVersion $runtime.Version.ToString() $Item.minimumVersion) -and
            ($null -eq $dependency -or (Test-CompatibleRuntimeVersion $runtime.Version.ToString() $dependency.Version.ToString() $dependency.RollForward))) {
            return $true
        }
    }
    return $false
}

function Test-VisualCppPrerequisite {
    param($Item, [string]$Architecture)
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $registry.OpenSubKey("SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\$Architecture")
        try {
            if ($null -eq $key -or $key.GetValue('Installed', 0) -ne 1) { return $false }
            $installed = [version](([string]$key.GetValue('Version', '')).TrimStart('v'))
            return $installed -ge [version]$Item.minimumVersion
        }
        finally { if ($null -ne $key) { $key.Dispose() } }
    }
    finally { $registry.Dispose() }
}

function Test-WindowsRuntimePackage {
    param($Definition)
    $ddlm = [regex]::Match($Definition.name, '^Microsoft\.WinAppRuntime\.DDLM\.(\d+)\.\d+\.\d+\.\d+-(x6|x8|a6)$')
    $name = if ($ddlm.Success) { "Microsoft.WinAppRuntime.DDLM.$($ddlm.Groups[1].Value).*" } else { $Definition.name }
    foreach ($package in @(Get-AppxPackage -Name $name -PackageTypeFilter Framework, Main)) {
        if ($ddlm.Success -and ([version]$package.Version).Major -ne ([version]$Definition.version).Major) { continue }
        if ($ddlm.Success -and $package.Name -notmatch
            ('^Microsoft\.WinAppRuntime\.DDLM\.' + $ddlm.Groups[1].Value + '\.\d+\.\d+\.\d+-' + $ddlm.Groups[2].Value + '$')) { continue }
        if ([string]$package.Architecture -ieq $Definition.architecture -and
            [version]$package.Version -ge [version]$Definition.version -and
            $package.PublisherId -eq '8wekyb3d8bbwe' -and $package.Status -eq 'Ok') { return $true }
    }
    return $false
}

function Test-SetupPrerequisite {
    param($Item, [string]$Architecture, $AspNetRuntime = $null, $RuntimeDefinitions = $null)
    switch ($Item.id) {
        'dotnet-runtime' { return Test-DotNetPrerequisite $Item $Architecture $AspNetRuntime $RuntimeDefinitions }
        'aspnetcore-runtime' { return Test-DotNetPrerequisite $Item $Architecture -RuntimeDefinitions $RuntimeDefinitions }
        'visual-cpp-runtime' { return Test-VisualCppPrerequisite $Item $Architecture }
        'windows-app-runtime' {
            foreach ($package in $Item.packages) {
                if (-not (Test-WindowsRuntimePackage $package)) { return $false }
            }
            return $true
        }
    }
    throw 'Unknown prerequisite.'
}

function Write-DetectionResult {
    param($Manifest, [string]$Path)
    $lines = @('[Prerequisites]', "Count=$(@($Manifest.prerequisites).Count)")
    $index = 0
    $aspNetRuntime = @($Manifest.prerequisites | Where-Object id -EQ 'aspnetcore-runtime')[0]
    $runtimeDefinitions = @(Get-DotNetRuntimes $Manifest.architecture)
    foreach ($item in $Manifest.prerequisites) {
        $needed = if (Test-SetupPrerequisite $item $Manifest.architecture $aspNetRuntime $runtimeDefinitions) { 0 } else { 1 }
        $lines += @("[$index]", "Id=$($item.id)", "Name=$($item.name)", "Needed=$needed",
            "FileName=$($item.fileName)", "Kind=$($item.kind)")
        $index++
    }
    [IO.File]::WriteAllLines($Path, [string[]]$lines, [Text.Encoding]::Unicode)
}

function Assert-Payload {
    param($Item, [string]$Path)
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne [long]$Item.length -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Item.sha256) {
        throw "Integrity verification failed for $($Item.name)."
    }
    if ($Item.kind -eq 'exe') {
        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)CN=(?:Microsoft Corporation|\.NET)(?:,|$)' -or
            $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
            throw "Microsoft signature verification failed for $($Item.name)."
        }
    }
}

function Get-PrerequisiteDownload {
    param($Item, [string]$Path, [string]$ProgressPath)
    if (-not ('PrerequisiteDownloader' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'Download-Prerequisite.cs') -ReferencedAssemblies System.Net.Http
    }
    $download = New-Object PrerequisiteDownloader
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $task = $download.DownloadAsync($Item.uri, $Path, [long]$Item.length)
    try {
        do {
            if (Test-Path -LiteralPath "$ProgressPath.cancel") { $download.Cancel() }
            $bytes = $download.BytesDownloaded
            $speed = [long]($bytes / [Math]::Max(1, $timer.Elapsed.TotalSeconds))
            $position = [int]($bytes * 1000 / [long]$Item.length)
            $detail = '{0:F1} MiB / {1:F1} MiB ({2:F1} MiB/s)' -f ($bytes / 1MB), ($Item.length / 1MB), ($speed / 1MB)
            $progress = "[Download]`r`nBytes=$bytes`r`nProgress=$position`r`nStatus=$($download.Status)`r`nDetail=$detail`r`n"
            [IO.File]::WriteAllText("$ProgressPath.new", $progress, [Text.Encoding]::Unicode)
            if (Test-Path -LiteralPath $ProgressPath) { [IO.File]::Replace("$ProgressPath.new", $ProgressPath, [NullString]::Value) }
            else { [IO.File]::Move("$ProgressPath.new", $ProgressPath) }
            if ($task.IsCompleted) { $null = $task.GetAwaiter().GetResult(); break }
            Start-Sleep -Milliseconds 200
        } while ($true)
    }
    finally {
        $download.Cancel()
        try { $null = $task.GetAwaiter().GetResult() } catch { }
        $download.Dispose()
        foreach ($temporary in @("$ProgressPath.new", "$ProgressPath.cancel")) {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
        }
    }
}

function Install-WindowsRuntime {
    param($Item, [string]$Path)
    Assert-Payload $Item $Path
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    $stagingRoot = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($Path)).TrimEnd('\') + '\'
    $staging = [IO.Path]::GetFullPath((Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))))
    if (-not $staging.StartsWith($stagingRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Staging path escapes its temporary directory.'
    }
    $null = New-Item -ItemType Directory -Path $staging
    try {
        foreach ($package in $Item.packages) {
            if ($package.fileName -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*\.msix$') {
                throw 'Invalid Windows Runtime package filename.'
            }
            if (Test-WindowsRuntimePackage $package) { continue }
            $entries = @($archive.Entries | Where-Object FullName -CEQ $package.fileName)
            if ($entries.Count -ne 1 -or $entries[0].Length -ne [long]$package.length) {
                throw 'Windows Runtime archive does not match its package manifest.'
            }
            $destination = Join-Path $staging $package.fileName
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $destination, $false)
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ine $package.sha256) {
                throw 'Windows Runtime MSIX integrity verification failed.'
            }
            Add-AppxPackage -Path $destination -ErrorAction Stop
            if (-not (Test-WindowsRuntimePackage $package)) {
                throw "Windows Runtime package was not registered: $($package.name)."
            }
        }
    }
    finally {
        $archive.Dispose()
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

function Get-InstalledFilePath {
    param([string]$Root, [string]$RelativePath)
    if ($RelativePath -notmatch '^[A-Za-z0-9_.-]+\.(dll|exe|json|dat)$') {
        throw 'Invalid legacy runtime filename.'
    }
    $absoluteRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $path = [IO.Path]::GetFullPath((Join-Path $absoluteRoot $RelativePath))
    if (-not $path.StartsWith($absoluteRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Legacy runtime path escapes the installation directory.'
    }
    $rootItem = Get-Item -LiteralPath $Root
    if ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse installation root is unsupported.' }
    if (Test-Path -LiteralPath $path) {
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Reparse legacy runtime file is unsupported.'
        }
    }
    return $path
}

function Save-LegacyRuntimeFiles {
    param($Manifest, [string]$Root, [string]$Path)
    $files = @()
    $configPath = Join-Path $Root 'PicForLater.App.runtimeconfig.json'
    $depsPath = Join-Path $Root 'PicForLater.App.deps.json'
    if ((Test-Path -LiteralPath $configPath) -and (Test-Path -LiteralPath $depsPath)) {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        if ($null -ne $config.runtimeOptions.PSObject.Properties['includedFrameworks']) {
            $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
            $newFiles = @($Manifest.appFiles)
            foreach ($target in $deps.targets.PSObject.Properties) {
                foreach ($library in $target.Value.PSObject.Properties) {
                    if ($library.Name -notmatch '^runtimepack\.Microsoft\.(NETCore|AspNetCore)\.App\.Runtime\.win-(x64|arm64)/[0-9.]+$') { continue }
                    foreach ($group in @('runtime', 'native')) {
                        $assets = $library.Value.PSObject.Properties[$group]
                        if ($null -eq $assets) { continue }
                        foreach ($asset in $assets.Value.PSObject.Properties) {
                            if ($asset.Name -in $newFiles) { continue }
                            $installedPath = Get-InstalledFilePath $Root $asset.Name
                            if (Test-Path -LiteralPath $installedPath -PathType Leaf) {
                                $files += @{ path = $asset.Name; sha256 = (Get-FileHash -LiteralPath $installedPath -Algorithm SHA256).Hash }
                            }
                        }
                    }
                }
            }
        }
    }
    $snapshot = @{ root = [IO.Path]::GetFullPath($Root); files = @($files) }
    [IO.File]::WriteAllText($Path, ($snapshot | ConvertTo-Json -Depth 5), [Text.Encoding]::UTF8)
}

function Remove-LegacyRuntimeFiles {
    param($Manifest, [string]$Root, [string]$Path)
    $snapshot = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath($Root) -ine $snapshot.root) { throw 'Legacy installation root changed.' }
    foreach ($file in $snapshot.files) {
        if ($file.path -in @($Manifest.appFiles)) { continue }
        $installedPath = Get-InstalledFilePath $Root $file.path
        if ((Test-Path -LiteralPath $installedPath -PathType Leaf) -and
            (Get-FileHash -LiteralPath $installedPath -Algorithm SHA256).Hash -ieq $file.sha256) {
            Remove-Item -LiteralPath $installedPath -Force
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $manifest = Read-SetupManifest $ManifestPath
        switch ($Action) {
            'Detect' { Write-DetectionResult $manifest $OutputPath }
            'CaptureLegacy' { Save-LegacyRuntimeFiles $manifest $InstallDirectory $LegacyPath }
            'CleanupLegacy' { Remove-LegacyRuntimeFiles $manifest $InstallDirectory $LegacyPath }
            default {
                $item = @($manifest.prerequisites | Where-Object id -EQ $PrerequisiteId)
                if ($item.Count -ne 1) { throw 'Unknown prerequisite requested.' }
                switch ($Action) {
                    'Download' { Get-PrerequisiteDownload $item[0] $PayloadPath "$OutputPath.download.ini" }
                    'Verify' { Assert-Payload $item[0] $PayloadPath }
                    'InstallWindowsRuntime' { Install-WindowsRuntime $item[0] $PayloadPath }
                }
            }
        }
    }
    catch {
        if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
            $message = $_.Exception.Message -replace '[\r\n]+', ' '
            [IO.File]::WriteAllText("$OutputPath.error.ini", "[Error]`r`nMessage=$message`r`n", [Text.Encoding]::Unicode)
        }
        Write-Error $_ -ErrorAction Continue
        exit 1
    }
}
