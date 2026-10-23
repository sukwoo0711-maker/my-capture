[CmdletBinding()]
param(
    [string]$ArtifactRoot = '',
    [string]$ExpectedSourceCommit = '',
    # Retained for existing workflow callers; the installer no longer needs a reader tool.
    [string]$DotNetPath = '',
    [switch]$Execute
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-test-common.ps1')
$repository = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\')
$evidenceBase = Join-Path $repository 'artifacts\validation\update-restart'

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-NoOutputLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Acceptance paths cannot contain links.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Quote-Argument([string]$Value) {
    if ($Value.Contains('"') -or $Value.Contains("`r") -or $Value.Contains("`n") -or $Value.EndsWith('\')) { throw 'Unsafe process argument.' }
    return '"' + $Value + '"'
}
function Write-Json([string]$Path, $Value) {
    Assert-NoOutputLinks $Path
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
}
function Assert-Inventory([string]$Root, $Manifest) {
    Assert-True (@($Manifest.Files).Count -gt 0 -and @($Manifest.Files).Count -le 10003) 'Invalid inventory count.'
    foreach ($required in @('MyCapture.exe', 'MyCapture.dll')) {
        Assert-True (@($Manifest.Files | Where-Object { $_.Path -ceq $required }).Count -eq 1) "Missing or duplicate binary: $required"
    }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Manifest.Files) {
        $relative = [string]$entry.Path
        Assert-True (-not [IO.Path]::IsPathRooted($relative) -and -not $relative.Contains(':') -and $seen.Add($relative)) 'Invalid or duplicate inventory path.'
        $path = [IO.Path]::GetFullPath((Join-Path $Root $relative))
        Assert-True ($path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Inventory escaped its root.'
        Assert-NoOutputLinks $path
        Assert-True ((Get-Item -LiteralPath $path).Length -eq [long]$entry.Bytes) "Inventory size mismatch: $relative"
        Assert-True ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq [string]$entry.Sha256) "Inventory hash mismatch: $relative"
    }
}
function New-IsolatedStartInfo([string]$Executable, [string]$WorkingDirectory) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.EnvironmentVariables['TEMP'] = $temporary
    $info.EnvironmentVariables['TMP'] = $temporary
    $info.EnvironmentVariables['MYCAPTURE_DATA_ROOT'] = Join-Path $run 'isolated-data'
    $info.EnvironmentVariables.Remove('MYCAPTURE_UPDATE_SESSION')
    $info.EnvironmentVariables.Remove('MYCAPTURE_SETUP_TEST_RESTART_ARGUMENTS')
    return $info
}
function Find-Restart([string]$Executable, [long]$EarliestTicks) {
    foreach ($candidate in @(Get-Process -Name MyCapture -ErrorAction SilentlyContinue)) {
        $matched = $false
        try {
            # Keep a handle for the observed identity before checking its path or waiting.
            $null = $candidate.Handle
            if ($candidate.StartTime.ToUniversalTime().Ticks -ge $EarliestTicks -and
                [string]::Equals($candidate.MainModule.FileName, $Executable, [StringComparison]::OrdinalIgnoreCase)) {
                $matched = $true
                return $candidate
            }
        }
        catch { }
        finally { if (-not $matched) { $candidate.Dispose() } }
    }
    return $null
}

Assert-True ($ExpectedSourceCommit -match '^[a-f0-9]{40}$') 'Supply the exact expected candidate source commit.'
$artifacts = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\')
Assert-NoOutputLinks $artifacts
$release = Get-Content -LiteralPath (Join-Path $artifacts 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $artifacts 'installer-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$manifest.Version
Assert-True ($version -match '^\d+\.\d+\.\d+$') 'Restart acceptance requires a stable numeric package.'
Assert-True ($manifest.Product -eq 'MyCapture') 'Unexpected installer product.'
Assert-True (@($manifest.Files).Count -le 10000) 'Invalid package inventory count.'
Assert-True ($release.SourceCommit -eq $ExpectedSourceCommit -and $release.SourceTreeClean -is [bool] -and $release.SourceTreeClean) 'Package source provenance does not match the expected clean commit.'
Assert-True ($release.Product -eq 'MyCapture' -and $release.Version -eq $version) 'Package manifests disagree.'
$publish = Join-Path $artifacts 'publish-win-x64'
$setupName = "MyCapture-$version-win-x64-setup.exe"
$payloadName = "MyCapture-$version-win-x64-portable.zip"
$setup = Join-Path $artifacts $setupName
$payload = Join-Path $artifacts $payloadName
foreach ($name in @($setupName, $payloadName, 'release-manifest.json')) {
    $lines = @(Get-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') | Where-Object { $_ -match ('^[a-fA-F0-9]{64}\s+\*?' + [regex]::Escape($name) + '$') })
    Assert-True ($lines.Count -eq 1) "Missing or duplicate package checksum: $name"
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $artifacts $name) -Algorithm SHA256).Hash -eq $lines[0].Substring(0, 64)) "Package checksum mismatch: $name"
}
Assert-True ((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash -eq $manifest.Payload.Sha256 -and
    (Get-Item -LiteralPath $payload).Length -eq [long]$manifest.Payload.Bytes) 'Installer manifest payload mismatch.'
Assert-Inventory $publish $manifest
Assert-True (@($manifest.BootstrapFiles).Count -eq 3) 'Expected three bootstrap files.'
foreach ($required in @('MyCaptureSetup.exe', 'MyCaptureSetup.exe.config', 'SETUP-HELP.txt')) {
    Assert-True (@($manifest.BootstrapFiles | Where-Object { $_.Path -ceq $required }).Count -eq 1) 'Missing or duplicate bootstrap file.'
}
foreach ($bootstrap in $manifest.BootstrapFiles) {
    Assert-True ([string]$bootstrap.Path -in @('MyCaptureSetup.exe', 'MyCaptureSetup.exe.config', 'SETUP-HELP.txt')) 'Unexpected installer bootstrap name.'
    $bootstrapPath = Get-SetupBootstrapSource ([string]$bootstrap.Path)
    Assert-True ((Get-FileHash -LiteralPath $bootstrapPath -Algorithm SHA256).Hash -eq [string]$bootstrap.Sha256 -and
        (Get-Item -LiteralPath $bootstrapPath).Length -eq [long]$bootstrap.Bytes) 'Seed installer source does not match the candidate bootstrap inventory.'
}
if (-not $Execute) {
    Write-Output "PREFLIGHT PASS: verified $version at $ExpectedSourceCommit. Re-run with -Execute for isolated installation and diagnostic restart."
    exit 0
}

$run = Join-Path $evidenceBase ([guid]::NewGuid().ToString('N'))
Assert-NoOutputLinks $run
[IO.Directory]::CreateDirectory($run) | Out-Null
$install = Join-Path $run 'installed'
$temporary = Join-Path $run 'temporary'
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$seedLog = Join-Path $run 'seed-install.log'
$seedInfo = New-IsolatedStartInfo (Join-Path $artifacts 'installer-stage\MyCaptureSetup.exe') $artifacts
$seedInfo.Arguments = '/quiet --no-shell-integration --install-root ' + (Quote-Argument $install) +
    ' --payload ' + (Quote-Argument $payload) + ' --manifest ' + (Quote-Argument (Join-Path $artifacts 'installer-manifest.json')) +
    ' --log ' + (Quote-Argument $seedLog)
$seed = [Diagnostics.Process]::Start($seedInfo)
Assert-True ($null -ne $seed) 'Seed installer did not start.'
try {
    Assert-True ($seed.WaitForExit(600000)) "Isolated seed installation timed out; see $seedLog"
    Assert-True ($seed.ExitCode -eq 0) "Isolated seed installation failed; see $seedLog"
}
finally { $seed.Dispose() }
$installedManifest = Get-Content -LiteralPath (Join-Path $install 'install-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-Inventory $install $installedManifest

# The real no-argument IExpress path validates the standard update-session parent.
# Run -Execute only in the dedicated validation account; all app data and install files
# remain under the GUID-owned evidence root, and no shell registration is created.
$updates = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'MyCapture\Updates'
$token = [guid]::NewGuid().ToString('N')
$sessionRoot = Join-Path $updates ('update-' + $version + '-' + $token)
Assert-NoOutputLinks $sessionRoot
[IO.Directory]::CreateDirectory($sessionRoot) | Out-Null
$session = Join-Path $sessionRoot 'update-session.json'
$installer = Join-Path $sessionRoot $setupName
Copy-Item -LiteralPath $setup -Destination $installer
Copy-Item -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Destination (Join-Path $sessionRoot 'SHA256SUMS.txt')
$setupHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
Assert-True ($setupHash -eq (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash) 'Copied setup hash mismatch.'
$parentInfo = New-IsolatedStartInfo (Join-Path ([Environment]::SystemDirectory) 'cmd.exe') $run
$parentInfo.Arguments = '/d /q /c "set /p MYCAPTURE_ACCEPTANCE_PARENT="'
$parentInfo.RedirectStandardInput = $true
$standIn = [Diagnostics.Process]::Start($parentInfo)
Assert-True ($null -ne $standIn) 'Parent stand-in did not start.'
$packageProcess = $null
$diagnostic = $null
$locked = $null
try {
    $parentTicks = $standIn.StartTime.ToUniversalTime().Ticks
    Write-Json $session @{
        Token = $token; Version = $version; InstallRoot = $install; SourceRoot = $install; TargetMode = 'OwnedInstall'
        ParentId = $standIn.Id; ParentStartedUtc = $parentTicks
        WaitForParent = $true; RestartAfterInstall = $true; ShowProgress = $true
    }
    Write-Json (Join-Path $run 'acceptance-owner.json') @{
        Token = $token; Session = $session; SourceCommit = $ExpectedSourceCommit; SetupSha256 = $setupHash
        ParentId = $standIn.Id; ParentStartedUtc = $parentTicks; InstallRoot = $install
    }
    $locked = [IO.File]::Open($installer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $setupInfo = New-IsolatedStartInfo $installer $sessionRoot
    # No IExpress arguments: exercise the same interactive AppLaunched command as the app.
    $setupInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    $setupInfo.EnvironmentVariables['MYCAPTURE_UPDATE_SESSION'] = $session
    $setupInfo.EnvironmentVariables['MYCAPTURE_SETUP_TEST_RESTART_ARGUMENTS'] = '--selftest-ux-review'
    $setupInfo.EnvironmentVariables['MYCAPTURE_UX_REVIEW_HOLD_SECONDS'] = '15'
    $packageStartedTicks = [DateTime]::UtcNow.Ticks
    $packageProcess = [Diagnostics.Process]::Start($setupInfo)
    Assert-True ($null -ne $packageProcess) 'IExpress did not start.'
    Write-Output "ACCEPTANCE_OUTPUT=$run"
    $receiptPath = Join-Path $sessionRoot 'install-result.json'
    $waitDeadline = [DateTime]::UtcNow.AddSeconds(60)
    $waitEvidence = $null
    while ($null -eq $waitEvidence) {
        Assert-True (-not $standIn.HasExited) 'Parent stand-in exited before the installer reached its wait.'
        Assert-True (-not (Test-Path -LiteralPath $receiptPath)) 'Installer completed before waiting for its live parent.'
        foreach ($candidateLog in @(Get-ChildItem -LiteralPath $temporary -Filter 'MyCapture-Install-*.log' -File)) {
            $line = @(Get-Content -LiteralPath $candidateLog.FullName -Encoding UTF8 | Where-Object {
                $_ -match ('Waiting for updater parent to exit\. PID=' + $standIn.Id + ';')
            })
            if ($line.Count -gt 0) { $waitEvidence = [string]$line[0]; break }
        }
        Assert-True ([DateTime]::UtcNow -lt $waitDeadline) 'Installer did not confirm waiting for the live parent within sixty seconds.'
        if ($null -eq $waitEvidence) { Start-Sleep -Milliseconds 100 }
    }
    $standIn.StandardInput.WriteLine('release')
    $standIn.StandardInput.Close()
    Assert-True ($standIn.WaitForExit(5000) -and $standIn.ExitCode -eq 0) 'Parent stand-in did not exit normally.'

    # IExpress exit is not install evidence. Wait for the installer's committed receipt.
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    while (-not (Test-Path -LiteralPath $receiptPath)) {
        Assert-True ([DateTime]::UtcNow -lt $deadline) 'Installer did not write a receipt within ten minutes.'
        Start-Sleep -Milliseconds 100
    }
    Assert-NoOutputLinks $receiptPath
    $receiptBytes = [IO.File]::ReadAllBytes($receiptPath)
    Assert-True (-not ($receiptBytes.Length -ge 3 -and $receiptBytes[0] -eq 239 -and $receiptBytes[1] -eq 187 -and $receiptBytes[2] -eq 191)) 'Receipt has an unexpected UTF-8 BOM.'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True (@($receipt.PSObject.Properties.Name).Count -eq 5 -and $receipt.Token -ceq $token -and $receipt.Version -ceq $version -and
        [int]$receipt.ExitCode -eq 0 -and [string]::Equals([string]$receipt.InstallRoot, $install, [StringComparison]::OrdinalIgnoreCase)) 'Installer failed or returned an unexpected receipt.'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$receipt.LogPath) -and (Test-Path -LiteralPath $receipt.LogPath)) 'Receipt log is missing.'
    Copy-Item -LiteralPath $receiptPath -Destination (Join-Path $run 'verified-install-result.json')
    # Observe the process before hashing the full installation: large packages can take
    # longer than the diagnostic hold, but the retained handle still proves its identity.
    $expectedExe = Join-Path $install 'MyCapture.exe'
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while ($null -eq $diagnostic) {
        $diagnostic = Find-Restart $expectedExe $packageStartedTicks
        Assert-True ([DateTime]::UtcNow -lt $restartDeadline) 'Installer did not restart the actual installed diagnostic.'
        if ($null -eq $diagnostic) { Start-Sleep -Milliseconds 100 }
    }
    $restartStartedUtc = $diagnostic.StartTime.ToUniversalTime()
    Write-Json (Join-Path $run 'restart-process.json') @{
        Id = $diagnostic.Id; StartedUtc = $diagnostic.StartTime.ToUniversalTime().ToString('O')
        StartedUtcTicks = $diagnostic.StartTime.ToUniversalTime().Ticks; Executable = $diagnostic.MainModule.FileName
        Arguments = '--selftest-ux-review'; Mode = 'Actual installed binary observed by process handle and executable path'
        ParentWaitLog = $waitEvidence; ParentId = $standIn.Id; ParentStartedUtc = $parentTicks
    }
    $installedManifest = Get-Content -LiteralPath (Join-Path $install 'install-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($installedManifest.Product -ceq 'MyCapture' -and $installedManifest.Version -ceq $version) 'Installed version was not confirmed.'
    Assert-Inventory $install $installedManifest
    foreach ($entry in $manifest.Files) {
        $installedEntry = @($installedManifest.Files | Where-Object { $_.Path -ceq $entry.Path })
        Assert-True ($installedEntry.Count -eq 1 -and $installedEntry[0].Sha256 -eq $entry.Sha256 -and [long]$installedEntry[0].Bytes -eq [long]$entry.Bytes) 'Installed inventory differs from the candidate package.'
    }
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) {
        $binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install $name))
        $actualVersion = '{0}.{1}.{2}' -f $binaryVersion.FileMajorPart, $binaryVersion.FileMinorPart, $binaryVersion.FileBuildPart
        Assert-True ($actualVersion -ceq $version) "Installed binary version mismatch: $name"
    }
    # The UX review renders every fixture in every theme and can take minutes on a hosted
    # runner. Keep its output and timing as evidence even when it times out or fails.
    $exited = $diagnostic.WaitForExit(600000)
    if (-not $exited) {
        $diagnostic.Kill()
        $diagnostic.WaitForExit(5000) | Out-Null
    }
    $restartSeconds = [Math]::Round(([DateTime]::UtcNow - $restartStartedUtc).TotalSeconds, 1)
    $stdoutPath = Join-Path $sessionRoot 'restart.stdout.txt'
    $stderrPath = Join-Path $sessionRoot 'restart.stderr.txt'
    $stdout = ''; $stderr = ''; $outputReady = $false; $matches = @()
    $flushDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ((Test-Path -LiteralPath $stdoutPath) -and (Test-Path -LiteralPath $stderrPath)) {
            Assert-NoOutputLinks $stdoutPath
            Assert-NoOutputLinks $stderrPath
            try {
                # Ordinary reads require the observer's write handles to be closed.
                # Process exit may precede its final redirected-stream drain.
                $stdout = [IO.File]::ReadAllText($stdoutPath)
                $stderr = [IO.File]::ReadAllText($stderrPath)
                $outputReady = $true
            }
            catch [IO.IOException] { }
        }
        if ($outputReady) {
            $matches = @([regex]::Matches($stdout, '(?m)^UX_REVIEW_OUTPUT=(.+)\r?$'))
            if ($matches.Count -eq 1 -or -not $exited) { break }
        }
        if ([DateTime]::UtcNow -ge $flushDeadline) { break }
        Start-Sleep -Milliseconds 100
    } while ($true)
    [IO.File]::WriteAllText((Join-Path $run 'restart.stdout.txt'), $stdout)
    [IO.File]::WriteAllText((Join-Path $run 'restart.stderr.txt'), $stderr)
    Write-Json (Join-Path $run 'restart-exit.json') @{
        Exited = $exited; ExitCode = $(if ($exited) { $diagnostic.ExitCode } else { $null }); Seconds = $restartSeconds
    }
    Assert-True $exited 'Diagnostic restart timed out.'
    Assert-True ($diagnostic.ExitCode -eq 0) 'Diagnostic restart failed.'
    Assert-True $outputReady 'Diagnostic output was not flushed to its session files.'
    Assert-True ($matches.Count -eq 1) 'Expected exactly one diagnostic output root.'
    $diagnostics = [IO.Path]::GetFullPath($matches[0].Groups[1].Value.Trim()).TrimEnd('\')
    Assert-True ($diagnostics.StartsWith($temporary + '\', [StringComparison]::OrdinalIgnoreCase)) 'Diagnostic output escaped the owned temporary directory.'
    Assert-NoOutputLinks $diagnostics
    $report = [IO.File]::ReadAllText((Join-Path $diagnostics 'ux-review-selftest-report.txt'))
    Assert-True ($report -match '(?m)^RESULT: PASS ' -and $report -notmatch '(?m)^RESULT: FAIL') 'Actual WPF diagnostic report did not pass.'
    Write-Json (Join-Path $run 'acceptance-result.json') @{
        Result = 'PASS'; Version = $version; DiagnosticOutput = $diagnostics
        SetupSha256 = $setupHash; SourceCommit = $ExpectedSourceCommit; ParentWaitObserved = $true
        Evidence = 'Real no-argument IExpress, live parent wait, installer receipt, installed hashes/version, observed installed process and WPF diagnostic startup'
        Limitations = 'Diagnostic mode only. Does not test resident tray, live user settings, or hotkey registration. Visual review remains separate.'
    }
    Write-Output 'RESULT: PASS (real packaged update, parent wait and diagnostic restart; resident startup remains outside this harness)'
}
finally {
    if ($null -ne $diagnostic) {
        try {
            # This handle was retained only after its start time and installed path matched.
            if (-not $diagnostic.HasExited) { $diagnostic.Kill(); $diagnostic.WaitForExit(5000) | Out-Null }
        }
        finally { $diagnostic.Dispose() }
    }
    try {
        if (-not $standIn.HasExited) { $standIn.StandardInput.Close(); $standIn.WaitForExit(5000) | Out-Null }
        if (-not $standIn.HasExited) { $standIn.Kill(); $standIn.WaitForExit(5000) | Out-Null }
    }
    finally { $standIn.Dispose() }
    if ($null -ne $packageProcess) { $packageProcess.Dispose() }
    if ($null -ne $locked) { $locked.Dispose() }
}
# Preserve this exact session, evidence and GUID-owned install for inspection.
