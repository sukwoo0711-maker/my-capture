[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [string]$OutputRoot = '',

    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 900
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$publish = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $publish -PathType Container)) {
    throw "Published application directory was not found: $publish"
}

$exe = Join-Path $publish 'MyCapture.exe'
foreach ($requiredFile in @('MyCapture.exe', 'MyCapture.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
    $requiredPath = Join-Path $publish $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or
        (Get-Item -LiteralPath $requiredPath).Length -le 0) {
        throw "PublishRoot is not a complete self-contained MyCapture package; missing: $requiredFile"
    }
}

$runningInstances = @(Get-Process -Name 'MyCapture' -ErrorAction SilentlyContinue)
if ($runningInstances.Count -gt 0) {
    $runningIds = @($runningInstances | ForEach-Object { $_.Id }) -join ', '
    throw "Close every running MyCapture instance before the packaged self-tests (PID: $runningIds)."
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\validation\packaged-self-tests'
}
$outputBase = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
$runName = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [guid]::NewGuid().ToString('N')
$runRoot = Join-Path $outputBase $runName
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

$tests = @(
    [pscustomobject]@{ Name = 'capture'; Switch = '--selftest-capture'; Report = 'selftest-report.txt' },
    [pscustomobject]@{ Name = 'shell'; Switch = '--selftest-shell'; Report = 'shell-selftest-report.txt' },
    [pscustomobject]@{ Name = 'advanced'; Switch = '--selftest-advanced'; Report = 'advanced-selftest-report.txt' },
    [pscustomobject]@{ Name = 'settings'; Switch = '--selftest-settings'; Report = 'settings-selftest-report.txt' },
    [pscustomobject]@{ Name = 'ocr'; Switch = '--selftest-ocr'; Report = 'ocr-selftest-report.txt' },
    [pscustomobject]@{ Name = 'recording'; Switch = '--selftest-recording'; Report = 'recording-selftest-report.txt' },
    [pscustomobject]@{ Name = 'video-editor'; Switch = '--selftest-video-editor'; Report = 'video-editor-selftest-report.txt' }
)

$failures = New-Object 'System.Collections.Generic.List[string]'
$passCount = 0
$iterationsVariable = 'MYCAPTURE_VIDEO_EDITOR_SELFTEST_ITERATIONS'
$hadIterationsOverride = Test-Path -LiteralPath "Env:\$iterationsVariable"
$previousIterationsOverride = if ($hadIterationsOverride) {
    [Environment]::GetEnvironmentVariable($iterationsVariable, [EnvironmentVariableTarget]::Process)
}
else {
    $null
}

try {
    # This runner is the seven-test release gate, not the optional 50-iteration soak.
    [Environment]::SetEnvironmentVariable(
        $iterationsVariable,
        '1',
        [EnvironmentVariableTarget]::Process)

    foreach ($test in $tests) {
        $testRoot = Join-Path $runRoot ([string]$test.Name)
        [IO.Directory]::CreateDirectory($testRoot) | Out-Null

        if ($testRoot.Contains('"')) {
            throw "A self-test output path cannot contain a quote: $testRoot"
        }

        $arguments = '{0} "{1}"' -f $test.Switch, $testRoot
        $process = $null
        $timedOut = $false
        $exitCode = $null

        try {
            $startParameters = @{
                FilePath = $exe
                ArgumentList = $arguments
                WorkingDirectory = $publish
                WindowStyle = 'Hidden'
                PassThru = $true
            }
            $process = Start-Process @startParameters

            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $timedOut = $true
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit()
            }
            else {
                $exitCode = [int]$process.ExitCode
            }
        }
        catch {
            $failures.Add("$($test.Name): process launch/wait failed: $($_.Exception.Message)")
            Write-Host "FAIL $($test.Name): process launch/wait failed" -ForegroundColor Red
            continue
        }
        finally {
            if ($null -ne $process) {
                $process.Dispose()
            }
        }

        if ($timedOut) {
            $failures.Add("$($test.Name): timed out after $TimeoutSeconds second(s)")
            Write-Host "FAIL $($test.Name): timeout" -ForegroundColor Red
            continue
        }

        $reportPath = Join-Path $testRoot ([string]$test.Report)
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            $failures.Add("$($test.Name): report missing: $reportPath")
            Write-Host "FAIL $($test.Name): report missing (exit $exitCode)" -ForegroundColor Red
            continue
        }

        $resultLines = @(Get-Content -LiteralPath $reportPath -Encoding UTF8 |
            Where-Object { $_.StartsWith('RESULT:', [StringComparison]::Ordinal) })
        $hasExactPass =
            $resultLines.Count -eq 1 -and
            [string]::Equals($resultLines[0], 'RESULT: PASS', [StringComparison]::Ordinal)

        if ($exitCode -ne 0 -or -not $hasExactPass) {
            $reported = if ($resultLines.Count -eq 0) { '(no RESULT line)' } else { $resultLines -join ' | ' }
            $failures.Add("$($test.Name): exit=$exitCode; report=$reported; path=$reportPath")
            Write-Host "FAIL $($test.Name): exit=$exitCode report=$reported" -ForegroundColor Red
            continue
        }

        $passCount++
        Write-Host "PASS $($test.Name): exit=0, RESULT: PASS" -ForegroundColor Green
    }
}
finally {
    if ($hadIterationsOverride) {
        [Environment]::SetEnvironmentVariable(
            $iterationsVariable,
            $previousIterationsOverride,
            [EnvironmentVariableTarget]::Process)
    }
    else {
        [Environment]::SetEnvironmentVariable(
            $iterationsVariable,
            $null,
            [EnvironmentVariableTarget]::Process)
    }
}

if ($failures.Count -gt 0 -or $passCount -ne $tests.Count) {
    Write-Host "SELF_TESTS=FAIL PASSES=$passCount/$($tests.Count) OUTPUT=$runRoot" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    throw "$($failures.Count) packaged self-test(s) failed."
}

Write-Host "SELF_TESTS=PASS PASSES=$passCount/$($tests.Count) OUTPUT=$runRoot" -ForegroundColor Green
