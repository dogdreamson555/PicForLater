[CmdletBinding()]
param(
    [string]$CredentialFile = ".env\myapikey_for_test.txt",
    [string]$ModelId = "deepseek-v4-flash",
    [ValidateRange(1, 5)]
    [int]$Samples = 3,
    [string]$MetricsPath = "tests\artifacts\remote-contract\deepseek-remote-ocr-text.json"
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$credentialPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $CredentialFile))
$metricsFullPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $MetricsPath))
if (-not (Test-Path -LiteralPath $credentialPath -PathType Leaf)) {
    throw "Credential file was not found: $credentialPath"
}

$env:PICFORLATER_RUN_REAL_REMOTE_CONTRACT = '1'
$env:PICFORLATER_REMOTE_CONTRACT_CREDENTIAL_FILE = $credentialPath
$env:PICFORLATER_REMOTE_CONTRACT_METRICS_PATH = $metricsFullPath
$env:PICFORLATER_REMOTE_CONTRACT_SAMPLES = $Samples.ToString(
    [Globalization.CultureInfo]::InvariantCulture)
$env:PICFORLATER_REMOTE_CONTRACT_MODEL = $ModelId
try {
    dotnet test `
        (Join-Path $repositoryRoot 'tests\PicForLater.IntegrationTests\PicForLater.IntegrationTests.csproj') `
        --configuration Debug `
        --filter 'Category=ExplicitRealApiContract' `
        --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) {
        throw "The explicit real remote contract test failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:PICFORLATER_RUN_REAL_REMOTE_CONTRACT -ErrorAction SilentlyContinue
    Remove-Item Env:PICFORLATER_REMOTE_CONTRACT_CREDENTIAL_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:PICFORLATER_REMOTE_CONTRACT_METRICS_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:PICFORLATER_REMOTE_CONTRACT_SAMPLES -ErrorAction SilentlyContinue
    Remove-Item Env:PICFORLATER_REMOTE_CONTRACT_MODEL -ErrorAction SilentlyContinue
}

Write-Host "Safe measurement written to: $metricsFullPath"
