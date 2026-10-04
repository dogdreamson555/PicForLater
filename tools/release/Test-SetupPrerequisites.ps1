[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup\Install-Prerequisites.ps1')
$assertions = 0

function Assert-True {
    param([bool]$Value, [string]$Message)
    if (-not $Value) { throw $Message }
    $script:assertions++
}

function Assert-Throws {
    param([scriptblock]$Operation, [string]$Message)
    $threw = $false
    try { & $Operation } catch { $threw = $true }
    Assert-True $threw $Message
}

$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$testRoot = Join-Path $workspace ('artifacts\setup-prerequisite-tests\' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot -Force

function Get-AppxPackage {
    param([string]$Name, $PackageTypeFilter)
    return @($script:installedPackages | Where-Object Name -Like $Name)
}

function Add-AppxPackage {
    param([string]$Path, [string]$ErrorAction)
    $script:appxCalls++
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) 'Package payload must exist before registration.'
    $script:installedPackages = @([pscustomobject]@{
        Name = 'Microsoft.WindowsAppRuntime.Test'; Architecture = 'x64'; Version = '2.0.0.0'
        PublisherId = '8wekyb3d8bbwe'; Status = 'Ok'
    })
}

try {
    Assert-True (Test-CompatibleRuntimeVersion '10.0.13' '10.0.12') 'A newer compatible patch should be reused.'
    Assert-True (-not (Test-CompatibleRuntimeVersion '10.0.11' '10.0.12')) 'An explicitly declared higher minimum must be enforced.'
    Assert-True (-not (Test-CompatibleRuntimeVersion '11.0.0' '10.0.12')) 'A different major version is not automatically compatible.'
    Assert-True (-not (Test-CompatibleRuntimeVersion '10.0.12-preview.1' '10.0.12')) 'A preview runtime must not satisfy a stable requirement.'

    $script:mockHost = Join-Path $testRoot 'dotnet.ps1'
    function Get-GlobalDotNetPath {
        param([string]$Architecture)
        if ($Architecture -eq 'x64') { return $script:mockHost }
        return Join-Path $testRoot 'missing-dotnet.exe'
    }
    [IO.File]::WriteAllText($script:mockHost, @'
$global:LASTEXITCODE = 0
'Microsoft.NETCore.App 10.0.13 [C:\dotnet\shared\Microsoft.NETCore.App]'
'@)
    $baseRuntime = [pscustomobject]@{ framework = 'Microsoft.NETCore.App'; minimumVersion = '10.0.12' }
    $aspRuntime = [pscustomobject]@{ framework = 'Microsoft.AspNetCore.App'; minimumVersion = '10.0.12' }
    Assert-True (Test-DotNetPrerequisite $baseRuntime 'x64') 'The required base runtime should be detected.'
    Assert-True (-not (Test-DotNetPrerequisite $aspRuntime 'x64')) 'Base .NET alone must not satisfy ASP.NET Core.'
    Assert-True (-not (Test-DotNetPrerequisite $baseRuntime 'arm64')) 'A runtime from another architecture must not satisfy the host.'
    [IO.File]::AppendAllText($script:mockHost, "`r`n'Microsoft.AspNetCore.App 10.0.11 [C:\dotnet\shared\Microsoft.AspNetCore.App]'")
    Assert-True (-not (Test-DotNetPrerequisite $aspRuntime 'x64')) 'An outdated ASP.NET Core patch must trigger an upgrade.'

    $sharedRoot = Join-Path $testRoot 'shared'
    foreach ($version in @('10.0.0', '10.0.9', '10.0.11', '10.0.12', '10.1.0')) {
        $directory = Join-Path $sharedRoot "Microsoft.AspNetCore.App\$version"
        $null = New-Item -ItemType Directory -Path $directory -Force
        [IO.File]::WriteAllText((Join-Path $directory 'Microsoft.AspNetCore.App.runtimeconfig.json'),
            ('{"runtimeOptions":{"rollForward":"LatestPatch","framework":{"name":"Microsoft.NETCore.App","version":"' + $version + '"}}}'))
    }
    function Set-MockRuntimes {
        param([string[]]$Core, [string[]]$Asp)
        $lines = @('$global:LASTEXITCODE = 0')
        foreach ($version in $Core) { $lines += "'Microsoft.NETCore.App $version [$sharedRoot\Microsoft.NETCore.App]'" }
        foreach ($version in $Asp) { $lines += "'Microsoft.AspNetCore.App $version [$sharedRoot\Microsoft.AspNetCore.App]'" }
        [IO.File]::WriteAllLines($script:mockHost, $lines)
    }
    $appCore = [pscustomobject]@{ framework = 'Microsoft.NETCore.App'; minimumVersion = '10.0.0' }
    $appAsp = [pscustomobject]@{ framework = 'Microsoft.AspNetCore.App'; minimumVersion = '10.0.0'; installVersion = '10.0.12' }
    foreach ($version in @('10.0.0', '10.0.9', '10.0.11', '10.0.12', '10.1.0')) {
        Set-MockRuntimes -Core @($version) -Asp @($version)
        Assert-True (Test-DotNetPrerequisite $appCore 'x64' $appAsp) "Existing compatible .NET $version must be reused."
        Assert-True (Test-DotNetPrerequisite $appAsp 'x64') "Existing compatible ASP.NET Core $version must be reused."
    }
    Set-MockRuntimes -Core @('10.0.12') -Asp @('10.0.11')
    Assert-True (Test-DotNetPrerequisite $appCore 'x64' $appAsp) 'A newer .NET patch must support the installed ASP.NET patch.'
    Set-MockRuntimes -Core @('10.0.9') -Asp @('10.0.11')
    Assert-True (-not (Test-DotNetPrerequisite $appCore 'x64' $appAsp)) 'ASP.NET Core must not bind to an older .NET patch than its own dependency.'
    Set-MockRuntimes -Core @('10.0.11') -Asp @('10.0.11', '10.0.12')
    Assert-True (-not (Test-DotNetPrerequisite $appCore 'x64' $appAsp)) 'Detection must account for the highest selected ASP.NET patch, not an older installed copy.'
    Set-MockRuntimes -Core @('10.0.11') -Asp @()
    Assert-True (-not (Test-DotNetPrerequisite $appCore 'x64' $appAsp)) 'Installing the recommended missing ASP.NET runtime must plan its matching .NET dependency.'
    Set-MockRuntimes -Core @('10.0.12') -Asp @()
    Assert-True (Test-DotNetPrerequisite $appCore 'x64' $appAsp) 'A sufficient base runtime must be reused when only ASP.NET is missing.'
    Assert-True (-not (Test-DotNetPrerequisite $appAsp 'x64')) 'Missing ASP.NET must be installed independently.'
    Set-MockRuntimes -Core @('11.0.0') -Asp @('11.0.0')
    Assert-True (-not (Test-DotNetPrerequisite $appCore 'x64' $appAsp)) 'Only .NET 11 must not satisfy an app targeting .NET 10.'
    Set-MockRuntimes -Core @('10.0.11') -Asp @('10.0.11')
    [IO.File]::WriteAllText((Join-Path $sharedRoot 'Microsoft.AspNetCore.App\10.0.11\Microsoft.AspNetCore.App.runtimeconfig.json'), 'corrupt')
    Assert-True (-not (Test-DotNetPrerequisite $appAsp 'x64')) 'A damaged ASP.NET runtime definition must not be reused.'

    $definition = [pscustomobject]@{
        name = 'Microsoft.WindowsAppRuntime.Test'; architecture = 'x64'; version = '2.0.0.0'
    }
    $script:installedPackages = @([pscustomobject]@{
        Name = $definition.name; Architecture = 'arm64'; Version = '3.0.0.0'; PublisherId = '8wekyb3d8bbwe'; Status = 'Ok'
    })
    Assert-True (-not (Test-WindowsRuntimePackage $definition)) 'A different architecture must not satisfy the package requirement.'
    $script:installedPackages[0].Architecture = 'x64'
    $script:installedPackages[0].PublisherId = 'untrusted'
    Assert-True (-not (Test-WindowsRuntimePackage $definition)) 'A package from another publisher must not be accepted.'
    $script:installedPackages[0].PublisherId = '8wekyb3d8bbwe'
    Assert-True (Test-WindowsRuntimePackage $definition) 'A newer Microsoft package should be reused.'

    $ddlmDefinition = [pscustomobject]@{ name = 'Microsoft.WinAppRuntime.DDLM.2.3.1.0-x6'; architecture = 'x64'; version = '2.3.1.0' }
    $script:installedPackages = @([pscustomobject]@{
        Name = 'Microsoft.WinAppRuntime.DDLM.2.4.0.0-x6'; Architecture = 'x64'; Version = '2.4.0.0'; PublisherId = '8wekyb3d8bbwe'; Status = 'Ok'
    })
    Assert-True (Test-WindowsRuntimePackage $ddlmDefinition) 'A newer stable DDLM of the same major and architecture must be reused.'
    $script:installedPackages[0].Name = 'Microsoft.WinAppRuntime.DDLM.3.0.0.0-x6'
    $script:installedPackages[0].Version = '3.0.0.0'
    Assert-True (-not (Test-WindowsRuntimePackage $ddlmDefinition)) 'A different major DDLM must not be reused.'
    $script:installedPackages[0].Name = 'Microsoft.WinAppRuntime.DDLM.2.4.0.0-preview1-x6'
    $script:installedPackages[0].Version = '2.4.0.0'
    Assert-True (-not (Test-WindowsRuntimePackage $ddlmDefinition)) 'A preview-tagged DDLM must not satisfy a stable app.'
    $script:installedPackages[0].Name = 'Microsoft.WinAppRuntime.DDLM.2.4.0.0-x8'
    $script:installedPackages[0].Architecture = 'x86'
    Assert-True (-not (Test-WindowsRuntimePackage $ddlmDefinition)) 'A DDLM of another architecture must not be reused.'
    $script:installedPackages[0].Name = 'Microsoft.WinAppRuntime.DDLM.2.3.0.0-x6'
    $script:installedPackages[0].Architecture = 'x64'
    $script:installedPackages[0].Version = '2.3.0.0'
    Assert-True (-not (Test-WindowsRuntimePackage $ddlmDefinition)) 'An older DDLM must not satisfy the bootstrap minimum.'

    $installRoot = Join-Path $testRoot 'installed'
    $null = New-Item -ItemType Directory -Path $installRoot
    foreach ($name in @('System.Private.CoreLib.dll', 'coreclr.dll', 'System.Text.Json.dll', 'e_sqlite3.dll', 'user.db')) {
        [IO.File]::WriteAllText((Join-Path $installRoot $name), 'old content')
    }
    [IO.File]::WriteAllText((Join-Path $installRoot 'PicForLater.App.runtimeconfig.json'),
        '{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.11"}]}}')
    [IO.File]::WriteAllText((Join-Path $installRoot 'PicForLater.App.deps.json'), @'
{"targets":{"net10/win-x64":{
  "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.11":{
    "runtime":{"System.Private.CoreLib.dll":{},"System.Text.Json.dll":{}},"native":{"coreclr.dll":{}}},
  "SQLite/1.0":{"native":{"e_sqlite3.dll":{}}}}}}
'@)
    $manifest = [pscustomobject]@{ appFiles = @('System.Text.Json.dll') }
    $legacyPath = Join-Path $testRoot 'legacy.json'
    Save-LegacyRuntimeFiles $manifest $installRoot $legacyPath
    [IO.File]::WriteAllText((Join-Path $installRoot 'coreclr.dll'), 'changed after capture')
    Remove-LegacyRuntimeFiles $manifest $installRoot $legacyPath
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot 'System.Private.CoreLib.dll'))) 'Old runtime-owned file should be removed.'
    foreach ($name in @('System.Text.Json.dll', 'coreclr.dll', 'e_sqlite3.dll', 'user.db')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $installRoot $name)) "Cleanup must preserve $name."
    }
    Assert-Throws { Get-InstalledFilePath $installRoot '..\outside.dll' } 'Cleanup must reject traversal paths.'
    Assert-Throws { Remove-LegacyRuntimeFiles $manifest $testRoot $legacyPath } 'Cleanup must reject a changed installation root.'

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $msixPath = Join-Path $testRoot 'test.msix'
    [IO.File]::WriteAllText($msixPath, 'mock package bytes')
    $zipPath = Join-Path $testRoot 'runtime.zip'
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $msixPath, 'test.msix') }
    finally { $archive.Dispose() }
    $item = [pscustomobject]@{
        name = 'Windows Runtime'; kind = 'msixZip'; length = (Get-Item -LiteralPath $zipPath).Length
        sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
        packages = @([pscustomobject]@{
            fileName = 'test.msix'; length = (Get-Item -LiteralPath $msixPath).Length
            sha256 = (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash
            name = $definition.name; version = '2.0.0.0'; architecture = 'x64'
        })
    }
    $script:installedPackages = @()
    $script:appxCalls = 0
    $originalHash = $item.sha256
    $item.sha256 = '0' * 64
    Assert-Throws { Install-WindowsRuntime $item $zipPath } 'Corrupt outer payload must be rejected.'
    Assert-True ($script:appxCalls -eq 0) 'A corrupt download must not register any package.'
    $item.sha256 = $originalHash
    $originalPackageHash = $item.packages[0].sha256
    $item.packages[0].sha256 = '0' * 64
    Assert-Throws { Install-WindowsRuntime $item $zipPath } 'Corrupt inner package must be rejected.'
    Assert-True ($script:appxCalls -eq 0) 'A corrupt MSIX must not be registered.'
    $item.packages[0].sha256 = $originalPackageHash
    Install-WindowsRuntime $item $zipPath
    Assert-True ($script:appxCalls -eq 1) 'A valid missing package should be registered once.'
    Install-WindowsRuntime $item $zipPath
    Assert-True ($script:appxCalls -eq 1) 'An installed compatible package should not be registered again.'
    $item.packages[0].fileName = '../test.msix'
    Assert-Throws { Install-WindowsRuntime $item $zipPath } 'Archive package names must reject traversal.'

    [pscustomobject]@{ Assertions = $assertions; SystemPackagesInstalled = 0; UserDataModified = $false }
}
finally {
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts\setup-prerequisite-tests')).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($testRoot).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Test cleanup path escaped its artifacts directory.'
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
