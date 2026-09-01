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

function Invoke-Step {
    param([string]$Name, [string[]]$Arguments)

    $log = Join-Path $logDir "$Name.log"
    Write-Host "==> dotnet $($Arguments -join ' ')"
    (& dotnet @Arguments 2>&1) | Out-File -Encoding utf8 -FilePath $log
    $code = $LASTEXITCODE

    if ($code -ne 0) {
        Write-Host "FAILED ($code). Diagnostics:" -ForegroundColor Red
        $logLines = @(Get-Content $log)
        $hitIndices = @(
            for ($index = 0; $index -lt $logLines.Count; $index++) {
                if ($logLines[$index] -match '(?i)(\[FAIL\]|\bFailed\b|\bFailure\b|Error Message:|MSB\d{4}|Test Run Failed)') {
                    $index
                }
            }
        )

        $diagnosticIndices = @(
            foreach ($hit in $hitIndices) {
                $start = [Math]::Max(0, $hit - 3)
                $end = [Math]::Min($logLines.Count - 1, $hit + 12)
                $start..$end
            }
        ) | Sort-Object -Unique

        if ($diagnosticIndices.Count -eq 0) {
            $diagnosticIndices = @(
                [Math]::Max(0, $logLines.Count - 120)..($logLines.Count - 1)
            )
        }

        $diagnosticIndices |
            Select-Object -First 240 |
            ForEach-Object { Write-Host "  $($logLines[$_])" }
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
