[CmdletBinding()]
param(
    [string]$CredentialFile = ".env\myapikey_for_test.txt",
    [string]$ModelId = "qwen3-vl-flash-2026-01-22"
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$credentialPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $CredentialFile))
if (-not (Test-Path -LiteralPath $credentialPath -PathType Leaf)) {
    throw "Credential file was not found: $credentialPath"
}

$env:PICFORLATER_RUN_REAL_QWEN_VISION_CONTRACT = '1'
$env:PICFORLATER_REMOTE_CONTRACT_CREDENTIAL_FILE = $credentialPath
$env:PICFORLATER_QWEN_VISION_CONTRACT_MODEL = $ModelId
try {
    dotnet test `
        (Join-Path $repositoryRoot 'tests\PicForLater.IntegrationTests\PicForLater.IntegrationTests.csproj') `
        --configuration Debug `
        --filter 'ContractProvider=QwenVision' `
        --logger 'console;verbosity=detailed'
    if ($LASTEXITCODE -ne 0) {
        throw "The explicit real Qwen vision contract test failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:PICFORLATER_RUN_REAL_QWEN_VISION_CONTRACT -ErrorAction SilentlyContinue
    Remove-Item Env:PICFORLATER_REMOTE_CONTRACT_CREDENTIAL_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:PICFORLATER_QWEN_VISION_CONTRACT_MODEL -ErrorAction SilentlyContinue
}
