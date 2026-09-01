[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$configurationPath = Join-Path $repoRoot 'global.json'
if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw "SDK configuration is missing: $configurationPath"
}

$configuration = Get-Content -LiteralPath $configurationPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$requiredVersion = [string]$configuration.sdk.version
if ($requiredVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "global.json contains an invalid SDK version: $requiredVersion"
}

if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    throw 'LOCALAPPDATA is unavailable; a per-user SDK location cannot be selected.'
}

$installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk'))
$dotnet = Join-Path $installRoot 'dotnet.exe'
if (Test-Path -LiteralPath $dotnet -PathType Leaf) {
    $installed = (& $dotnet --version 2>&1 | Select-Object -Last 1).ToString().Trim()
    if ($LASTEXITCODE -eq 0 -and [string]::Equals($installed, $requiredVersion, [StringComparison]::Ordinal)) {
        Write-Host "MyCapture SDK is already ready: $installed" -ForegroundColor Green
        Write-Host "  $dotnet"
        exit 0
    }
}

Write-Host "Installing the repository-pinned .NET SDK $requiredVersion for the current user..." -ForegroundColor Cyan
Write-Host 'Source: https://dot.net/v1/dotnet-install.ps1'
Write-Host "Target: $installRoot"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$response = Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1'
$scriptText = if ($response.Content -is [byte[]]) {
    [Text.Encoding]::UTF8.GetString([byte[]]$response.Content)
}
else {
    [string]$response.Content
}
if ([string]::IsNullOrWhiteSpace($scriptText)) {
    throw 'The official dotnet-install script download was empty.'
}

$installer = [scriptblock]::Create($scriptText)
& $installer -Version $requiredVersion -InstallDir $installRoot -NoPath
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "The .NET SDK installer did not produce the expected host: $dotnet"
}

$actualVersion = (& $dotnet --version 2>&1 | Select-Object -Last 1).ToString().Trim()
if ($LASTEXITCODE -ne 0 -or
    -not [string]::Equals($actualVersion, $requiredVersion, [StringComparison]::Ordinal)) {
    throw "Installed SDK version '$actualVersion' does not match global.json '$requiredVersion'."
}

Write-Host "RESULT: READY - .NET SDK $actualVersion" -ForegroundColor Green
Write-Host "  $dotnet"
