[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [string]$OutputRoot = '',

    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 900,

    # Optional force-on for diagnostics. Trimmed or unknown projection bytes always verify.
    [switch]$VerifyWindowsProjection
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

function Get-WindowsProjectionVerification {
    param([string]$Root)

    # Crossgen keeps the module identity; ILLink creates a new deterministic MVID. Read
    # metadata only, without loading either assembly into an AppDomain. A missing
    # cache, failed metadata read or unfamiliar SDK is conservative: verify it.
    $detection = [ordered]@{ RequiresVerification = $true; State = 'unknown'; Reason = ''; PublishedMvid = ''; OriginalMvid = '' }
    try {
        $projection = Join-Path $Root 'Microsoft.Windows.SDK.NET.dll'
        $dependencies = Get-Content -LiteralPath (Join-Path $Root 'MyCapture.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $sdk = @($dependencies.libraries.PSObject.Properties.Name | Where-Object { $_ -cmatch '^runtimepack\.Microsoft\.Windows\.SDK\.NET\.Ref/[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$' })
        if ($sdk.Count -ne 1) { throw 'The dependency manifest does not identify exactly one Windows SDK pack.' }
        $sdkVersion = $sdk[0].Substring($sdk[0].IndexOf('/') + 1)
        $cacheLines = @(& dotnet nuget locals global-packages --list --force-english-output)
        if ($LASTEXITCODE -ne 0) { throw 'The NuGet package cache could not be located.' }
        $cacheLine = @($cacheLines | Where-Object { $_ -cmatch '^global-packages: .+$' })
        if ($cacheLine.Count -ne 1) { throw 'The NuGet package cache response was ambiguous.' }
        $cache = $cacheLine[0].Substring('global-packages: '.Length).Trim()
        $sdkRoot = Join-Path $cache ('microsoft.windows.sdk.net.ref\' + $sdkVersion + '\lib')
        $originals = @(Get-ChildItem -LiteralPath $sdkRoot -Filter 'Microsoft.Windows.SDK.NET.dll' -File -Recurse)
        if ($originals.Count -ne 1) { throw 'Exactly one original SDK projection is required for classification.' }
        $metadataTool = Join-Path $PSScriptRoot 'tools\projection-identity.cs'
        $metadataBuildRoot = Join-Path ([IO.Path]::GetTempPath()) ('mycapture-projection-identity-' + [guid]::NewGuid().ToString('N'))
        $identityLines = @(& dotnet run --file $metadataTool --artifacts-path $metadataBuildRoot --verbosity quiet -- $projection $originals[0].FullName)
        if ($LASTEXITCODE -ne 0) { throw "Projection module metadata could not be compared (exit $LASTEXITCODE)." }
        $identity = ($identityLines -join "`n") | ConvertFrom-Json
        if ($identity.SchemaVersion -ne 1 -or $identity.RequiresVerification -isnot [bool] -or
            $identity.State -cnotin @('original-sdk', 'trimmed-or-modified')) {
            throw 'Projection module metadata response is incomplete.'
        }
        $detection.PublishedMvid = [string]$identity.PublishedMvid
        $detection.OriginalMvid = [string]$identity.OriginalMvid
        $detection.RequiresVerification = $identity.RequiresVerification
        $detection.State = [string]$identity.State
        $detection.Reason = [string]$identity.Reason
    }
    catch {
        $detection.Reason = $_.Exception.Message
    }
    return [pscustomobject]$detection
}

$projectionDetection = Get-WindowsProjectionVerification -Root $publish
$VerifyWindowsProjection = [bool]$VerifyWindowsProjection -or $projectionDetection.RequiresVerification
Write-Host "Windows projection: $($projectionDetection.State); verification=$([bool]$VerifyWindowsProjection). $($projectionDetection.Reason)"

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
$previousProjectionDataRoot = $null
if ($VerifyWindowsProjection) {
    $previousProjectionDataRoot = [Environment]::GetEnvironmentVariable('MYCAPTURE_DATA_ROOT', 'Process')
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
        if ($VerifyWindowsProjection) {
            $isolatedDataRoot = Join-Path $testRoot 'data'
            [IO.Directory]::CreateDirectory($isolatedDataRoot) | Out-Null
            [Environment]::SetEnvironmentVariable('MYCAPTURE_DATA_ROOT', $isolatedDataRoot, 'Process')
        }

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
    if ($VerifyWindowsProjection) {
        [Environment]::SetEnvironmentVariable('MYCAPTURE_DATA_ROOT', $previousProjectionDataRoot, 'Process')
    }
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

$projectionEvidence = $null
if ($VerifyWindowsProjection) {
    $projectionRoot = Join-Path $runRoot 'windows-projection'
    $probeBuildRoot = Join-Path $runRoot 'projection-probe-build'
    [IO.Directory]::CreateDirectory($projectionRoot) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $probeBuildRoot 'packages')) | Out-Null
    $projectionEvidence = [ordered]@{ Result = 'FAIL'; ExitCode = $null; WgcSessions = 0 }
    try {
        # No package references or network source: this companion loads the exact WinRT DLLs
        # directly from PublishRoot and leaves every published file untouched.
        $probeProject = Join-Path $PSScriptRoot 'tools\WindowsProjectionProbe\WindowsProjectionProbe.csproj'
        $appStream = [IO.File]::OpenRead($exe)
        $appReader = New-Object IO.BinaryReader($appStream)
        try {
            if ($appReader.ReadUInt16() -ne 0x5A4D) { throw 'Published app has no DOS executable header.' }
            $appStream.Position = 0x3C
            $headerOffset = $appReader.ReadInt32()
            if ($headerOffset -lt 0 -or $headerOffset -gt $appStream.Length - 6) { throw 'Published app PE header is outside the file.' }
            $appStream.Position = $headerOffset
            if ($appReader.ReadUInt32() -ne 0x4550) { throw 'Published app has no PE header.' }
            $machine = $appReader.ReadUInt16()
            # The companion is built for the application's own architecture so that it can load the published DLLs.
            $probeRuntime = switch ($machine) { 0x8664 { 'win-x64' } 0x014C { 'win-x86' } 0xAA64 { 'win-arm64' } default { throw 'Projection validation requires an x64, x86 or arm64 app host.' } }
        }
        finally { $appReader.Dispose(); $appStream.Dispose() }
        $appDependencies = Get-Content -LiteralPath (Join-Path $publish 'MyCapture.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $runtimeTarget = [string]$appDependencies.runtimeTarget.name
        if (-not $runtimeTarget.EndsWith('/' + $probeRuntime, [StringComparison]::Ordinal)) {
            throw 'Published app host architecture does not match its dependency manifest.'
        }
        & dotnet restore $probeProject "-p:ProbeBuildRoot=$probeBuildRoot" -r $probeRuntime -p:SelfContained=true --source (Join-Path $probeBuildRoot 'packages') *> (Join-Path $projectionRoot 'restore.log')
        if ($LASTEXITCODE -ne 0) { throw "Projection companion restore failed: $LASTEXITCODE" }
        $probePublish = Join-Path $probeBuildRoot 'publish'
        & dotnet publish $probeProject -c Release --no-restore -r $probeRuntime --self-contained true -m:1 -nr:false -p:UseSharedCompilation=false "-p:ProbeBuildRoot=$probeBuildRoot" -o $probePublish *> (Join-Path $projectionRoot 'build.log')
        if ($LASTEXITCODE -ne 0) { throw "Projection companion publish failed: $LASTEXITCODE" }
        $probeExe = Join-Path $probePublish 'WindowsProjectionProbe.exe'
        $probe = $null
        try {
            $probe = Start-Process -FilePath $probeExe -ArgumentList ('"{0}" "{1}"' -f $publish, $projectionRoot) `
                -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $projectionRoot 'stdout.log') `
                -RedirectStandardError (Join-Path $projectionRoot 'stderr.log')
            $probeHandle = $probe.Handle
            if (-not $probe.WaitForExit($TimeoutSeconds * 1000)) {
                Write-TimeoutEvidence -Process $probe -TestRoot $projectionRoot
                Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue
                $probe.WaitForExit()
                throw 'Projection companion timed out.'
            }
            if ($null -eq $probe.ExitCode) { throw 'Projection companion exit code was not observed.' }
            $projectionEvidence.ExitCode = [int]$probe.ExitCode
            if ($probe.ExitCode -ne 0) { throw "Projection companion failed: $($probe.ExitCode)" }
        }
        finally { if ($null -ne $probe) { $probe.Dispose() } }

        $probeResultPath = Join-Path $projectionRoot 'projection-probe-result.json'
        $probeResult = Get-Content -LiteralPath $probeResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $probeReportPath = Join-Path $projectionRoot 'projection-probe-report.txt'
        $probeLines = @(Get-Content -LiteralPath $probeReportPath -Encoding UTF8 |
            Where-Object { $_.StartsWith('RESULT:', [StringComparison]::Ordinal) })
        if ($probeResult.SchemaVersion -ne 1 -or $probeResult.ExitCode -ne 0 -or $probeResult.Result -cne 'PASS' -or
            $probeResult.PublishRoot -ine $publish -or $probeLines.Count -ne 1 -or $probeLines[0] -cne 'RESULT: PASS') {
            throw 'Projection companion evidence is incomplete or failed.'
        }
        $expectedAssemblies = @('Microsoft.Windows.SDK.NET', 'WinRT.Runtime')
        if (@($probeResult.Assemblies).Count -ne $expectedAssemblies.Count) { throw 'Projection assembly evidence is incomplete.' }
        foreach ($name in $expectedAssemblies) {
            $loaded = @($probeResult.Assemblies | Where-Object { $_.Name -ceq $name })
            $source = Join-Path $publish ($name + '.dll')
            if ($loaded.Count -ne 1 -or $loaded[0].Path -ine $source -or
                (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ine $loaded[0].Sha256) {
                throw "Projection companion tested different bytes: $name"
            }
        }

        # The ordinary recording self-test legitimately accepts GDI fallback. The trimming
        # release gate must prove actual WinRT frames in ALL four sessions, not that fallback.
        $recordingPath = Join-Path $runRoot 'recording\recording-selftest-report.txt'
        $recordingLines = @(Get-Content -LiteralPath $recordingPath -Encoding UTF8)
        foreach ($number in 1..4) {
            $backend = @($recordingLines | Where-Object { $_ -ceq "Session ${number}: capture backend requested=WindowsGraphicsCapture active=WindowsGraphicsCapture" })
            $frames = @($recordingLines | Where-Object { $_ -cmatch "^Session ${number}: WGC create=[0-9.]+ms, first frame wait=[0-9.]+ms, copied=[1-9][0-9]*, reused=[0-9]+$" })
            if ($backend.Count -ne 1 -or $frames.Count -ne 1) {
                throw "Projection validation requires live WGC frames without GDI fallback in recording session $number."
            }
        }
        $projectionEvidence.Result = 'PASS'
        $projectionEvidence.WgcSessions = 4
        $projectionEvidence.Report = 'windows-projection/projection-probe-report.txt'
        $projectionEvidence.ReportSha256 = (Get-FileHash -LiteralPath $probeReportPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $projectionEvidence.ResultFile = 'windows-projection/projection-probe-result.json'
        $projectionEvidence.ResultSha256 = (Get-FileHash -LiteralPath $probeResultPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $projectionEvidence.NotExercised = @($probeResult.NotExercised | Where-Object { $_ -ne 'graphics device/frame events' -and $_ -ne 'OCR recognition' })
        Write-Host 'PASS Windows projection: companion bytes verified; live WGC frames in four sessions.' -ForegroundColor Green
    }
    catch {
        $projectionEvidence.Error = $_.Exception.Message
        $failures.Add("Windows projection: $($_.Exception.Message)")
        Write-Host "FAIL Windows projection: $($_.Exception.Message)" -ForegroundColor Red
    }
}

$result = [ordered]@{
    SchemaVersion = 1; PublishRoot = $publish; PassCount = $passCount
    Result = $(if ($failures.Count -eq 0 -and $passCount -eq $tests.Count) { 'PASS' } else { 'FAIL' })
    Tests = $evidence; Failures = @($failures.ToArray())
    WindowsProjectionDetection = $projectionDetection
}
if ($VerifyWindowsProjection) { $result.WindowsProjection = $projectionEvidence }
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'result.json') -Encoding UTF8

if ($failures.Count -gt 0 -or $passCount -ne $tests.Count) {
    Write-Host "SELF_TESTS=FAIL PASSES=$passCount/$($tests.Count) OUTPUT=$runRoot" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    throw "$($failures.Count) packaged self-test(s) failed."
}

Write-Host "SELF_TESTS=PASS PASSES=$passCount/$($tests.Count) OUTPUT=$runRoot" -ForegroundColor Green
