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

. (Join-Path $PSScriptRoot 'packaged-validation.ps1')
$tests = @(Get-PackagedSelfTests)

# A self-test that outlives the runner's limit is ended without a report of its own. Before it is
# ended, record where it is: a dump with heap (managed and native stacks), requested through the
# process's .NET diagnostics port as dotnet-dump collect does, and the processes and windows on
# the desktop, which can block the next test as well.
function Request-ProcessDump {
    param([int]$ProcessId, [string]$Path)

    # Diagnostics IPC: 20-byte header (magic, total size, command set Dump = 1,
    # GenerateCoreDump = 1, reserved), then the length-prefixed UTF-16 path with its null,
    # dump type 2 (with heap) and the createdump logging flag.
    $pathBytes = [Text.Encoding]::Unicode.GetBytes($Path + [char]0)
    $size = 20 + 4 + $pathBytes.Length + 8
    $message = New-Object byte[] $size
    [Text.Encoding]::ASCII.GetBytes("DOTNET_IPC_V1").CopyTo($message, 0)
    [BitConverter]::GetBytes([uint16]$size).CopyTo($message, 14)
    $message[16] = 1
    $message[17] = 1
    [BitConverter]::GetBytes([int]($Path.Length + 1)).CopyTo($message, 20)
    $pathBytes.CopyTo($message, 24)
    [BitConverter]::GetBytes([uint32]2).CopyTo($message, 24 + $pathBytes.Length)

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', "dotnet-diagnostic-$ProcessId", [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(10000)
        $pipe.Write($message, 0, $message.Length)
        $pipe.Flush()
        # The reply (header and HRESULT) comes once the runtime has written the dump.
        $response = New-Object byte[] 24
        $read = 0
        while ($read -lt $response.Length) {
            $pending = $pipe.ReadAsync($response, $read, $response.Length - $read)
            if (-not $pending.Wait(180000)) { throw 'The dump request did not finish within 180 seconds.' }
            if ($pending.Result -le 0) { break }
            $read += $pending.Result
        }
        $status = if ($read -ge 24) { '0x{0:X8}' -f [BitConverter]::ToUInt32($response, 20) } else { "short response ($read bytes)" }
        $bytes = if (Test-Path -LiteralPath $Path) { (Get-Item -LiteralPath $Path).Length } else { 0 }
        return "Dump: $Path ($bytes bytes, $(if ($response[17] -eq 0) { 'ok' } else { 'error' }) $status)"
    }
    finally {
        $pipe.Dispose()
    }
}

function Write-TimeoutEvidence {
    param([System.Diagnostics.Process]$Process, [string]$TestRoot)

    # Evidence is best effort: it must never keep the hung process alive.
    try {
        $lines = New-Object 'System.Collections.Generic.List[string]'
        $lines.Add("Timed out after $TimeoutSeconds second(s): PID $($Process.Id)")
        try {
            $lines.Add((Request-ProcessDump -ProcessId $Process.Id -Path (Join-Path $TestRoot 'timeout.dmp')))
        }
        catch {
            $lines.Add("Dump: not written: $($_.Exception.Message)")
        }

        $lines.Add('Processes with a window title:')
        foreach ($candidate in @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle })) {
            $lines.Add("  $($candidate.ProcessName) $($candidate.Id): $($candidate.MainWindowTitle)")
        }
        foreach ($name in @('MyCapture', 'WerFault', 'WerFaultSecure', 'createdump')) {
            foreach ($candidate in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
                $lines.Add("  $name $($candidate.Id) started $($candidate.StartTime.ToString('o'))")
            }
        }

        Set-Content -LiteralPath (Join-Path $TestRoot 'timeout-report.txt') -Value $lines -Encoding UTF8
    }
    catch {
        Write-Host "Timeout evidence was not recorded: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

$evidence = @($tests | ForEach-Object {
    [pscustomobject]@{ Name = $_.Name; Path = "$($_.Name)/$($_.Report)"; ExitCode = $null; Result = ''; Sha256 = '' }
})

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
    # This runner is the eight-test release gate, not the optional 50-iteration soak.
    [Environment]::SetEnvironmentVariable(
        $iterationsVariable,
        '1',
        [EnvironmentVariableTarget]::Process)

    foreach ($test in $tests) {
        $record = @($evidence | Where-Object { $_.Name -ceq $test.Name })[0]
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
            # Keep the native handle alive so Windows PowerShell can read the exit code
            # even when the process terminates before WaitForExit starts.
            $processHandle = $process.Handle

            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $timedOut = $true
                Write-TimeoutEvidence -Process $process -TestRoot $testRoot
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit()
            }
            else {
                if ($null -eq $process.ExitCode) {
                    throw 'The self-test process exit code was not observed.'
                }
                $exitCode = [int]$process.ExitCode
                $record.ExitCode = $exitCode
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
        $record.Result = $resultLines -join ' | '
        $record.Sha256 = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
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

[ordered]@{
    SchemaVersion = 1; PublishRoot = $publish; PassCount = $passCount
    Result = $(if ($failures.Count -eq 0 -and $passCount -eq $tests.Count) { 'PASS' } else { 'FAIL' })
    Tests = $evidence; Failures = @($failures.ToArray())
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding UTF8

if ($failures.Count -gt 0 -or $passCount -ne $tests.Count) {
    Write-Host "SELF_TESTS=FAIL PASSES=$passCount/$($tests.Count) OUTPUT=$runRoot" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    throw "$($failures.Count) packaged self-test(s) failed."
}

Write-Host "SELF_TESTS=PASS PASSES=$passCount/$($tests.Count) OUTPUT=$runRoot" -ForegroundColor Green
