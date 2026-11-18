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

if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $bootstrapDotnetDir = Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk'
    $dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    # Prepend the repository bootstrap location last so it wins over a runtime-only or
    # incompatible conventional per-user installation.
    foreach ($candidateDir in @($dotnetDir, $bootstrapDotnetDir)) {
        if (Test-Path (Join-Path $candidateDir 'dotnet.exe')) {
            $env:Path = "$candidateDir;$env:Path"
        }
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# Fail before restore/build with an actionable message when a machine has only a
# runtime (or an incompatible SDK). doctor.ps1 keeps the per-user SDK preference
# above but can also select a compatible dotnet host already present on PATH.
try {
    & (Join-Path $PSScriptRoot 'doctor.ps1') -Quiet
}
catch {
    Write-Host "SDK prerequisite check failed:`n$($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

. (Join-Path $PSScriptRoot 'step-diagnostics.ps1')

function Invoke-Step {
    param([string]$Name, [string[]]$Arguments)

    $log = Join-Path $logDir "$Name.log"
    Write-Host "==> dotnet $($Arguments -join ' ')"
    (& dotnet @Arguments 2>&1) | Out-File -Encoding utf8 -FilePath $log
    $code = $LASTEXITCODE

    if ($code -ne 0) {
        Write-Host "FAILED ($code). Diagnostics:" -ForegroundColor Red
        # Failure lines first, then context around them (build\step-diagnostics.ps1).
        Get-StepFailureDiagnostics -Lines @(Get-Content $log) |
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
