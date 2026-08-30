# Builds the solution and writes a UTF-8 log.
#
# Two environment quirks this works around:
#   - The dotnet CLI is installed per-user and may not be on PATH in a fresh shell.
#   - MSBuild emits localised output in the OEM code page, which turns into
#     mojibake when captured. DOTNET_CLI_UI_LANGUAGE=en keeps diagnostics readable
#     and greppable.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\build.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Test
#   powershell -NoProfile -ExecutionPolicy Bypass -File build\build.ps1 -Configuration Release

param(
    [string]$Configuration = 'Debug',
    [switch]$Test,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'build\logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) {
    $env:Path = "$env:Path;$dotnetDir"
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

function Invoke-Step {
    param([string]$Name, [string[]]$Arguments)

    $log = Join-Path $logDir "$Name.log"
    Write-Host "==> dotnet $($Arguments -join ' ')"
    (& dotnet @Arguments 2>&1) | Out-File -Encoding utf8 -FilePath $log
    $code = $LASTEXITCODE

    if ($code -ne 0) {
        Write-Host "FAILED ($code). Diagnostics:" -ForegroundColor Red
        Get-Content $log |
            Where-Object { $_ -match 'error|warning|Build FAILED|Failed!|Passed!' } |
            Select-Object -First 40 |
            ForEach-Object { Write-Host "  $_" }
        Write-Host "Full log: $log"
        return $false
    }

    Write-Host "OK. Log: $log" -ForegroundColor Green
    return $true
}

$solution = Join-Path $repoRoot 'MyCapture.slnx'

$buildArgs = @('build', $solution, '-c', $Configuration)
if ($NoRestore) { $buildArgs += '--no-restore' }

if (-not (Invoke-Step -Name 'build' -Arguments $buildArgs)) { exit 1 }

if ($Test) {
    $testArgs = @('test', $solution, '-c', $Configuration, '--no-build', '--verbosity', 'normal')
    if (-not (Invoke-Step -Name 'test' -Arguments $testArgs)) { exit 1 }

    Get-Content (Join-Path $logDir 'test.log') |
        Where-Object { $_ -match 'Passed!|Failed!|total:|Total tests' } |
        ForEach-Object { Write-Host "  $_" }
}

Write-Host "Build succeeded ($Configuration)." -ForegroundColor Green
