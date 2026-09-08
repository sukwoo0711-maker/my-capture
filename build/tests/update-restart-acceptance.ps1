[CmdletBinding()]
param(
    [string]$ArtifactRoot = '',
    [string]$ExpectedSourceCommit = '',
    [string]$DotNetPath = '',
    [switch]$Execute,
    [string]$WorkerRoot = '',
    [string]$WorkerSession = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\')
$evidenceBase = Join-Path $repository 'artifacts\validation\update-restart'
$powerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'

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
    Assert-True (@($Manifest.Files).Count -gt 0 -and @($Manifest.Files).Count -le 10000) 'Invalid inventory count.'
    foreach ($required in @('MyCapture.exe', 'MyCapture.dll')) {
        Assert-True (@($Manifest.Files | Where-Object { $_.Path -ceq $required }).Count -eq 1) "Missing or duplicate binary: $required"
    }
    foreach ($entry in $Manifest.Files) {
        $relative = [string]$entry.Path
        Assert-True (-not [IO.Path]::IsPathRooted($relative) -and -not $relative.Contains(':')) 'Invalid inventory path.'
        $path = [IO.Path]::GetFullPath((Join-Path $Root $relative))
        Assert-True ($path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Inventory escaped its root.'
        Assert-NoOutputLinks $path
        Assert-True ((Get-Item -LiteralPath $path).Length -eq [long]$entry.Bytes) "Inventory size mismatch: $relative"
        Assert-True ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq [string]$entry.Sha256) "Inventory hash mismatch: $relative"
    }
}

# This private subprocess mode instruments only the existing test seam. The helper's
# production functions and main body are read from the verified candidate's PE resource.
if ($WorkerRoot) {
    $run = [IO.Path]::GetFullPath($WorkerRoot).TrimEnd('\')
    Assert-True ([string]::Equals((Split-Path -Parent $run), $evidenceBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $run) -match '^[a-f0-9]{32}$') 'Invalid acceptance worker root.'
    Assert-NoOutputLinks $run
    $owner = Get-Content -LiteralPath (Join-Path $run 'acceptance-owner.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ([string]::Equals([string]$owner.Session, $WorkerSession, [StringComparison]::OrdinalIgnoreCase)) 'Worker session mismatch.'
    $configuration = Get-Content -LiteralPath $WorkerSession -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($configuration.Token -eq $owner.Token -and $configuration.InstallRoot -eq (Join-Path $run 'installed')) 'Worker ownership mismatch.'
    $helper = Join-Path (Split-Path -Parent $WorkerSession) 'update-helper.ps1'
    Assert-True ((Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash -eq $owner.HelperSha256) 'Embedded helper changed.'
    $helperText = [IO.File]::ReadAllText($helper)
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($helperText, [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) 'Embedded helper parse failed.'
    $main = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.AssignmentStatementAst] -and $_.Left.Extent.Text -eq '$sessionRoot'
    })
    Assert-True ($main.Count -eq 1) 'Expected one helper main-body boundary.'
    . $helper -Session $WorkerSession -FunctionsOnly
    $script:AcceptanceRun = $run
    $script:AcceptanceConfig = $configuration
    $script:RestartObserver = $null
    function Start-UpdateProcess($Info) {
        $expected = Join-Path $script:AcceptanceConfig.InstallRoot 'MyCapture.exe'
        Assert-True ($Info.FileName -eq $expected -and -not $Info.UseShellExecute -and [string]::IsNullOrEmpty($Info.Arguments)) 'Unexpected helper restart boundary.'
        Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $WorkerSession) 'install-result.json') -Destination (Join-Path $script:AcceptanceRun 'verified-install-result.json')
        $Info.Arguments = '--selftest-ux-review'
        $Info.CreateNoWindow = $true
        $Info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $Info.RedirectStandardOutput = $true
        $Info.RedirectStandardError = $true
        $Info.EnvironmentVariables['TEMP'] = Join-Path $script:AcceptanceRun 'temporary'
        $Info.EnvironmentVariables['TMP'] = Join-Path $script:AcceptanceRun 'temporary'
        $Info.EnvironmentVariables['MYCAPTURE_UX_REVIEW_HOLD_SECONDS'] = '15'
        $actual = [Diagnostics.Process]::Start($Info)
        Assert-True ($null -ne $actual) 'Actual installed process did not start.'
        # The helper disposes its handle after the three-second gate. Keep the original
        # redirected-stream owner alive until the diagnostic has exited and output is read.
        $script:RestartObserver = $actual
        $script:RestartOutput = $actual.StandardOutput.ReadToEndAsync()
        $script:RestartError = $actual.StandardError.ReadToEndAsync()
        Write-Json (Join-Path $script:AcceptanceRun 'restart-process.json') @{
            Id = $actual.Id; StartedUtc = $actual.StartTime.ToUniversalTime().ToString('O'); Executable = $expected
            Arguments = '--selftest-ux-review'; Mode = 'Actual installed binary, isolated existing diagnostic startup'
        }
        return [Diagnostics.Process]::GetProcessById($actual.Id)
    }
    try {
        # All installer launch, receipt/hash/version validation, liveness and session cleanup
        # statements run unchanged. No fake Process object and no installer override exists.
        . ([scriptblock]::Create($helperText.Substring($main[0].Extent.StartOffset)))
        Assert-True ($null -ne $script:RestartObserver) 'Helper never reached actual restart.'
        Assert-True ($script:RestartObserver.WaitForExit(180000)) 'Diagnostic restart timed out.'
        Assert-True ($script:RestartObserver.ExitCode -eq 0) 'Diagnostic restart failed.'
        $stdout = $script:RestartOutput.GetAwaiter().GetResult()
        $stderr = $script:RestartError.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $run 'restart.stdout.txt'), $stdout)
        [IO.File]::WriteAllText((Join-Path $run 'restart.stderr.txt'), $stderr)
        $matches = @([regex]::Matches($stdout, '(?m)^UX_REVIEW_OUTPUT=(.+)\r?$'))
        Assert-True ($matches.Count -eq 1) 'Expected exactly one diagnostic output root.'
        $diagnostics = [IO.Path]::GetFullPath($matches[0].Groups[1].Value.Trim()).TrimEnd('\')
        Assert-True ($diagnostics.StartsWith((Join-Path $run 'temporary') + '\', [StringComparison]::OrdinalIgnoreCase)) 'Diagnostic output escaped the owned temporary directory.'
        Assert-NoOutputLinks $diagnostics
        $report = [IO.File]::ReadAllText((Join-Path $diagnostics 'ux-review-selftest-report.txt'))
        Assert-True ($report -match '(?m)^RESULT: PASS ' -and $report -notmatch '(?m)^RESULT: FAIL') 'Actual WPF diagnostic report did not pass.'
        Write-Json (Join-Path $run 'acceptance-result.json') @{
            Result = 'PASS'; Version = $configuration.Version; DiagnosticOutput = $diagnostics
            SetupSha256 = $configuration.Sha256; HelperSha256 = $owner.HelperSha256; SourceCommit = $owner.SourceCommit
            Evidence = 'Real IExpress, installer receipt, installed hashes/version, real process and existing WPF diagnostic startup'
            Limitations = 'Diagnostic mode only. Does not test resident tray, live user settings, or hotkey registration. Visual review remains separate.'
        }
        exit 0
    }
    finally {
        if ($null -ne $script:RestartObserver) {
            try {
                if (-not $script:RestartObserver.HasExited) {
                    $script:RestartObserver.Kill()
                    $script:RestartObserver.WaitForExit(5000) | Out-Null
                }
            }
            finally { $script:RestartObserver.Dispose() }
        }
    }
}

Assert-True ($ExpectedSourceCommit -match '^[a-f0-9]{40}$') 'Supply the exact expected candidate source commit.'
$artifacts = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\')
Assert-NoOutputLinks $artifacts
$release = Get-Content -LiteralPath (Join-Path $artifacts 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $artifacts 'installer-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$manifest.Version
Assert-True ($version -match '^\d+\.\d+\.\d+$') 'Restart acceptance requires a stable numeric package.'
Assert-True ($manifest.Product -eq 'MyCapture') 'Unexpected installer product.'
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
foreach ($bootstrap in $manifest.BootstrapFiles) {
    Assert-True ([string]$bootstrap.Path -in @('install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'uninstall-cleanup.ps1')) 'Unexpected installer bootstrap name.'
    $bootstrapPath = Join-Path (Join-Path $repository 'build\installer') ([string]$bootstrap.Path)
    Assert-True ((Get-FileHash -LiteralPath $bootstrapPath -Algorithm SHA256).Hash -eq [string]$bootstrap.Sha256) 'Seed installer source does not match the candidate bootstrap inventory.'
}
if (-not $Execute) {
    Write-Output "PREFLIGHT PASS: verified $version at $ExpectedSourceCommit. Re-run with -Execute for isolated installation and diagnostic restart."
    exit 0
}
if (-not $DotNetPath) { $DotNetPath = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source }
$toolProject = Join-Path $repository 'build\tools\UpdateHelperResourceReader\UpdateHelperResourceReader.csproj'
& $DotNetPath build $toolProject -c Release --nologo
Assert-True ($LASTEXITCODE -eq 0) 'Embedded helper reader build failed.'
$tool = Join-Path (Split-Path -Parent $toolProject) 'bin\Release\net10.0-windows10.0.22000.0\UpdateHelperResourceReader.dll'
$readerInfo = New-Object Diagnostics.ProcessStartInfo
$readerInfo.FileName = $DotNetPath
$readerInfo.Arguments = Quote-Argument $tool
$readerInfo.WorkingDirectory = $publish
$readerInfo.UseShellExecute = $false
$readerInfo.CreateNoWindow = $true
$readerInfo.RedirectStandardOutput = $true
$readerInfo.RedirectStandardError = $true
$reader = [Diagnostics.Process]::Start($readerInfo)
try {
    $encoded = $reader.StandardOutput.ReadToEnd()
    $readerError = $reader.StandardError.ReadToEnd()
    $reader.WaitForExit()
    Assert-True ($reader.ExitCode -eq 0) "Embedded helper extraction failed: $readerError"
    $helperBytes = [Convert]::FromBase64String($encoded)
}
finally { $reader.Dispose() }

$run = Join-Path $evidenceBase ([guid]::NewGuid().ToString('N'))
Assert-NoOutputLinks $run
[IO.Directory]::CreateDirectory($run) | Out-Null
$install = Join-Path $run 'installed'
[IO.Directory]::CreateDirectory((Join-Path $run 'temporary')) | Out-Null
$seedLog = Join-Path $run 'seed-install.log'
& $powerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $repository 'build\installer\install.ps1') -Quiet -NoShellIntegration -InstallRoot $install -PayloadPath $payload -ManifestPath (Join-Path $artifacts 'installer-manifest.json') -LogPath $seedLog
Assert-True ($LASTEXITCODE -eq 0) "Isolated seed installation failed; see $seedLog"
$installedManifest = Get-Content -LiteralPath (Join-Path $install 'install-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-Inventory $install $installedManifest
$updates = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'MyCapture\Updates'
$sessionRoot = Join-Path $updates ('update-' + $version + '-' + [guid]::NewGuid().ToString('N'))
Assert-NoOutputLinks $sessionRoot
[IO.Directory]::CreateDirectory($sessionRoot) | Out-Null
$session = Join-Path $sessionRoot 'update-session.json'
$installer = Join-Path $sessionRoot $setupName
Copy-Item -LiteralPath $setup -Destination $installer
Copy-Item -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Destination (Join-Path $sessionRoot 'SHA256SUMS.txt')
$helperPath = Join-Path $sessionRoot 'update-helper.ps1'
[IO.File]::WriteAllBytes($helperPath, $helperBytes)
$token = [guid]::NewGuid().ToString('N')
$setupHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
Write-Json $session @{
    ParentId = [int]::MaxValue; ParentStartedUtc = 0; Installer = $installer; Sha256 = $setupHash
    Bytes = (Get-Item -LiteralPath $installer).Length; Version = $version; Token = $token
    InstallRoot = $install; SourceRoot = $install; TargetMode = 'OwnedInstall'
    FailureMessage = 'Isolated updater acceptance failed. Inspect the retained test evidence.'
}
Write-Json (Join-Path $run 'acceptance-owner.json') @{
    Token = $token; Session = $session; SourceCommit = $ExpectedSourceCommit
    HelperSha256 = (Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash
}
$workerInfo = New-Object Diagnostics.ProcessStartInfo
$workerInfo.FileName = $powerShell
$workerInfo.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' + (Quote-Argument $PSCommandPath) + ' -WorkerRoot ' + (Quote-Argument $run) + ' -WorkerSession ' + (Quote-Argument $session)
$workerInfo.UseShellExecute = $false
$workerInfo.CreateNoWindow = $true
$workerInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$workerInfo.EnvironmentVariables['TEMP'] = Join-Path $run 'temporary'
$workerInfo.EnvironmentVariables['TMP'] = Join-Path $run 'temporary'
$worker = [Diagnostics.Process]::Start($workerInfo)
try {
    Write-Output "ACCEPTANCE_OUTPUT=$run"
    Assert-True ($worker.WaitForExit(900000)) "Acceptance timed out. Worker PID $($worker.Id); owned evidence: $run"
    Assert-True ($worker.ExitCode -eq 0) "Acceptance failed; inspect $run and $sessionRoot"
    $result = Get-Content -LiteralPath (Join-Path $run 'acceptance-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-True ($result.Result -eq 'PASS') 'Acceptance result was not PASS.'
    Write-Output 'RESULT: PASS (real packaged update and diagnostic restart; resident startup remains outside this harness)'
}
finally {
    try {
        if (-not $worker.HasExited) {
            # Stop only this harness worker and its recorded diagnostic, never all MyCapture processes.
            try {
            $identityPath = Join-Path $run 'restart-process.json'
            if (Test-Path -LiteralPath $identityPath) {
                $identity = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json
                $diagnostic = Get-Process -Id ([int]$identity.Id) -ErrorAction SilentlyContinue
                if ($null -ne $diagnostic) {
                    try {
                        if ($diagnostic.StartTime.ToUniversalTime().ToString('O') -eq $identity.StartedUtc -and
                            [string]::Equals($diagnostic.MainModule.FileName, (Join-Path $install 'MyCapture.exe'), [StringComparison]::OrdinalIgnoreCase)) {
                            $diagnostic.Kill()
                            $diagnostic.WaitForExit(5000) | Out-Null
                        }
                    }
                    finally { $diagnostic.Dispose() }
                }
            }
            }
            finally {
                if (-not $worker.HasExited) {
                    $worker.Kill()
                    $worker.WaitForExit(5000) | Out-Null
                }
            }
        }
    }
    finally { $worker.Dispose() }
}
# Evidence and the GUID-owned installation are deliberately retained for inspection.
# The real helper removes only its exact successful session files; no parent is swept.
