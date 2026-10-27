# Forward and reverse installation compatibility with the installer from commit 4769935.
# The legacy fixture adapts policy arguments, shell isolation and file removal for this host.
# Manifest parsing, ownership, extraction, transaction and uninstall integrity stay intact;
# file removal uses verified Recycle Bin moves confined to this test's TEMP fixture.
[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-test-common.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-compatibility-' + [guid]::NewGuid().ToString('N'))
$legacy = Join-Path $root 'legacy'
$package = Join-Path $root 'new'
$powerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$previousData = $env:MYCAPTURE_DATA_ROOT
$previousSession = $env:MYCAPTURE_UPDATE_SESSION
$env:MYCAPTURE_DATA_ROOT = Join-Path $root 'isolated-data'
$env:MYCAPTURE_UPDATE_SESSION = $null
$fixtureSucceeded = $false
$keys = New-Object 'System.Collections.Generic.List[string]'
$testKeyParent = 'Software\MyCapture.SetupTests'
$parentKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyParent)
$testKeyParentExisted = $null -ne $parentKey
if ($null -ne $parentKey) { $parentKey.Dispose() }
[IO.Directory]::CreateDirectory($legacy) | Out-Null
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
function Invoke-Legacy([string]$Script, [string[]]$Arguments) {
    $preference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $powerShell -NoProfile -File $Script @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $preference }
    if ($code -ne 0 -and (($output | Out-String) -match 'PSSecurityException|UnauthorizedAccess|running scripts is disabled|execution policy')) {
        Write-Output 'SKIP: machine policy prevents executing the local legacy fixture.'
        Get-ExecutionPolicy -List | Out-Host
        throw 'LEGACY_POLICY_SKIP'
    }
    if ($code -ne 0) { throw "Legacy fixture failed ($code): $output" }
}
function Read-SharedText([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return '' }
    # Share write access so polling never blocks the deferred worker's appended log lines.
    $stream = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8); return $reader.ReadToEnd() }
    finally { $stream.Dispose() }
}
function Wait-Removed([string]$Install, [string]$Log, [bool]$NewWorker = $false) {
    # Shell and startup cleanup follows the root deletion; the final log line marks completion.
    $final = if ($NewWorker) { 'Cleanup worker folder retained' } else { 'MyCapture removal completed' }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (([IO.Directory]::Exists($Install) -or -not (Read-SharedText $Log).Contains($final)) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Assert-True (-not [IO.Directory]::Exists($Install)) "Removal incomplete: $Install; log: $Log"
    Assert-True ((Read-SharedText $Log).Contains('MyCapture removal completed')) "Removal did not complete; log: $Log"
    if ($NewWorker) {
        # The exited worker keeps only its own image until the next setup or uninstall run.
        $started = [regex]::Match((Read-SharedText $Log), 'Deferred cleanup worker started \(PID (\d+)\)')
        Assert-True $started.Success "Missing worker PID; log: $Log"
        $worker = Get-Process -Id ([int]$started.Groups[1].Value) -ErrorAction SilentlyContinue
        if ($null -ne $worker -and $worker.ProcessName -eq 'MyCaptureUninstall') { Assert-True ($worker.WaitForExit(30000)) 'Cleanup worker did not exit.' }
        if ($null -ne $worker) { $worker.Dispose() }
    }
}
$legacyRemovalPrelude = @'
# Development-only adapter: every removal remains inside this unique fixture and is recoverable.
$script:LegacyFixtureRoot = '__LEGACY_FIXTURE_ROOT__'
[Reflection.Assembly]::LoadFrom('__LEGACY_SETUP_ASSEMBLY__') | Out-Null
function Remove-LegacyFixtureItem {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$LiteralPath,
        [switch]$Recurse,
        [switch]$Force
    )
    $fixture = [IO.Path]::GetFullPath($script:LegacyFixtureRoot).TrimEnd('\')
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($fixture), $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($fixture) -cnotmatch '\AMyCapture-compatibility-[a-f0-9]{32}\z') {
        throw "Invalid legacy fixture root: $fixture"
    }
    if (-not [IO.Path]::IsPathRooted($LiteralPath)) { throw "Legacy removal requires an absolute path: $LiteralPath" }
    $full = [IO.Path]::GetFullPath($LiteralPath).TrimEnd('\')
    if (-not $full.StartsWith($fixture + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Legacy removal must remain inside its fixture: $full"
    }
    if (-not (Test-Path -LiteralPath $full -ErrorAction Stop)) { return }
    # Reject links in the target, every ancestor, and descendants before touching any attributes.
    $current = $full
    while ($current) {
        $entry = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Legacy removal refuses a link: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    $target = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    $entries = New-Object 'System.Collections.Generic.List[System.IO.FileSystemInfo]'
    $entries.Add($target)
    if ($target.PSIsContainer) {
        $pending = New-Object 'System.Collections.Generic.Stack[string]'
        $pending.Push($full)
        while ($pending.Count -gt 0) {
            $directory = $pending.Pop()
            foreach ($child in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
                if (-not $child.FullName.StartsWith($full + '\', [StringComparison]::OrdinalIgnoreCase) -or
                    ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Legacy removal refuses an unsafe descendant: $($child.FullName)"
                }
                $entries.Add($child)
                if ($child.PSIsContainer) { $pending.Push($child.FullName) }
            }
        }
        if (-not $Recurse -and $entries.Count -ne 1) { throw "Legacy nonrecursive removal requires an empty directory: $full" }
    }
    if ($Force) {
        foreach ($entry in $entries) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) {
                [IO.File]::SetAttributes($entry.FullName, ($entry.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
            }
        }
    }
    $recycler = New-Object MyCapture.Core.Storage.RecoverableFileRemoval
    $identity = $recycler.GetIdentity($full)
    if ($target.PSIsContainer) {
        [MyCapture.Core.Storage.RecoverableFileRemoval]::DeleteDirectory($full, [bool]$Recurse, $identity)
    }
    else { [MyCapture.Core.Storage.RecoverableFileRemoval]::DeleteFile($full, $identity) }
    if (Test-Path -LiteralPath $full -ErrorAction Stop) { throw "Legacy recycle did not remove its original path: $full" }
}
'@
$legacyRemovalPrelude = $legacyRemovalPrelude.Replace('__LEGACY_FIXTURE_ROOT__', $root.Replace("'", "''")).Replace('__LEGACY_SETUP_ASSEMBLY__', $setupTestExe.Replace("'", "''"))
try {
    New-SyntheticSetupPackage $package
    $manifest = Get-Content -LiteralPath (Join-Path $package 'installer-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $version = [string]$manifest.Version
    $payload = Join-Path $package "MyCapture-$version-win-x64-portable.zip"
    $newExe = Join-Path $package 'installer-stage\MyCaptureSetup.exe'
    $legacyNames = @('install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'uninstall-cleanup.ps1', 'SETUP-HELP.txt')
    foreach ($name in $legacyNames) {
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = (Get-Command git -CommandType Application | Select-Object -First 1).Source
        $info.Arguments = '-C ' + [MyCapture.Setup.Program]::Quote($setupTestRepo) + ' show 4769935:build/installer/' + $name
        $info.UseShellExecute = $false; $info.RedirectStandardOutput = $true; $info.CreateNoWindow = $true
        $process = [Diagnostics.Process]::Start($info)
        try { $text = $process.StandardOutput.ReadToEnd(); $process.WaitForExit(); Assert-True ($process.ExitCode -eq 0) "Cannot read legacy $name" }
        finally { $process.Dispose() }
        if ($name.EndsWith('.ps1')) {
            # Preserve the old ownership checks, retry loops, rollback and manifest validation.
            # Only the final filesystem removal primitive changes to verified recycling.
            $replacements = switch ($name) {
                'install.ps1' { @(
                    @('[IO.File]::Delete($probe)', 'Remove-LegacyFixtureItem -LiteralPath $probe'),
                    @('Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop', 'Remove-LegacyFixtureItem -LiteralPath $Path -Recurse -Force -ErrorAction Stop')
                ) }
                'uninstall.ps1' { @(
                    @('Remove-Item -LiteralPath $cleanupCopy -Force -ErrorAction SilentlyContinue', 'Remove-LegacyFixtureItem -LiteralPath $cleanupCopy -Force -ErrorAction SilentlyContinue'),
                    @('$cleanupCopy = Join-Path ([IO.Path]::GetTempPath())', '$cleanupCopy = Join-Path $script:LegacyFixtureRoot')
                ) }
                'uninstall-cleanup.ps1' { @(
                    @('Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction Stop', 'Remove-LegacyFixtureItem -LiteralPath $Root -Recurse -Force -ErrorAction Stop'),
                    @('Remove-Item -LiteralPath $self -Force -ErrorAction SilentlyContinue', 'Remove-LegacyFixtureItem -LiteralPath $self -Force -ErrorAction SilentlyContinue'),
                    @('[IO.File]::Delete($path)', 'Remove-LegacyFixtureItem -LiteralPath $path'),
                    @('[IO.Directory]::Delete($startMenu, $false)', 'Remove-LegacyFixtureItem -LiteralPath $startMenu')
                ) }
                default { throw "Unexpected legacy PowerShell fixture: $name" }
            }
            foreach ($replacement in $replacements) {
                Assert-True ($text.Contains($replacement[0])) "Missing legacy removal contract in ${name}: $($replacement[0])"
                $text = $text.Replace($replacement[0], $replacement[1])
            }
            Assert-True (-not [regex]::IsMatch($text, '(?i)\bRemove-Item\s+-LiteralPath\b|\[(?:System\.)?IO\.(?:File|Directory)\]\s*::\s*Delete\s*\(')) "Unadapted permanent file removal in legacy $name"
            $strictMarker = 'Set-StrictMode -Version 2.0'
            Assert-True ([regex]::Matches($text, [regex]::Escape($strictMarker)).Count -eq 1) "Missing or duplicated strict-mode boundary in legacy $name"
            $text = $text.Replace($strictMarker, $legacyRemovalPrelude + [Environment]::NewLine + $strictMarker)
            # The old uninstaller has no shell-target options. Seed it with NoShellIntegration,
            # and suppress only its real-shell cleanup calls in this isolated test fixture.
            $text = [regex]::Replace($text, '(?m)^    Remove-Owned(?:StartupValue|Shortcuts|UninstallKey)\s*$', '    # Shell cleanup is isolated out for the legacy NoShellIntegration fixture.')
            $text = $text.Replace("        '-ExecutionPolicy', 'Bypass',", '')
        }
        # The legacy .cmd files are inventory fixtures only and are never invoked.
        [IO.File]::WriteAllText((Join-Path $legacy $name), $text, (New-Object Text.UTF8Encoding($name -eq 'SETUP-HELP.txt')))
    }
    $manifest.BootstrapFiles = @($legacyNames | ForEach-Object { [MyCapture.Setup.FileRecord]::Create((Join-Path $legacy $_), $_) })
    $legacyManifest = Join-Path $legacy 'installer-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $legacyManifest -Encoding UTF8
    Copy-Item -LiteralPath $payload -Destination (Join-Path $legacy 'payload.zip')
    foreach ($mode in @('direct', 'receipt')) {
        $install = Join-Path $root $mode
        Invoke-Legacy (Join-Path $legacy 'install.ps1') @('-Quiet', '-NoShellIntegration', '-InstallRoot', $install, '-LogPath', (Join-Path $root "$mode-old.log"))
        Assert-True ([IO.File]::Exists((Join-Path $install 'uninstall.ps1'))) 'Legacy installation did not contain its uninstaller.'
        $sentinel = Join-Path $install 'user-notes.txt'; [IO.File]::WriteAllText($sentinel, 'keep user content')
        $key = 'Software\MyCapture.SetupTests\' + [guid]::NewGuid().ToString('N'); $keys.Add($key)
        $menu = Join-Path $root ("isolated-start-menu-$mode")
        $args = @('/quiet', '--install-root', $install, '--log', (Join-Path $root "$mode-new.log"), '--test-uninstall-key', $key, '--test-start-menu', $menu)
        if ($mode -eq 'receipt') {
            # Model the old shell entries only in the strictly isolated targets. The
            # historical script itself runs with NoShellIntegration throughout.
            $registration = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($key)
            try {
                $registration.SetValue('InstallLocation', $install)
                $registration.SetValue('UninstallString', ('"' + (Join-Path $install 'uninstall.cmd') + '"'))
            }
            finally { $registration.Dispose() }
            [IO.Directory]::CreateDirectory($menu) | Out-Null
            [MyCapture.Setup.ShellLink]::Write((Join-Path $menu 'MyCapture.lnk'), (Join-Path $install 'MyCapture.exe'), '', $install, (Join-Path $install 'MyCapture.exe'), 'MyCapture')
            [MyCapture.Setup.ShellLink]::Write((Join-Path $menu 'Uninstall MyCapture.lnk'), (Join-Path $install 'uninstall.cmd'), '', $install, (Join-Path $install 'MyCapture.exe'), 'Uninstall MyCapture')
            $sessionDirectory = Join-Path (Join-Path $root '.MyCapture.updates') ('update-' + $version + '-' + [guid]::NewGuid().ToString('N'))
            [IO.Directory]::CreateDirectory($sessionDirectory) | Out-Null
            $token = [guid]::NewGuid().ToString('N')
            $env:MYCAPTURE_UPDATE_SESSION = Join-Path $sessionDirectory 'update-session.json'
            @{ Token = $token; Version = $version; InstallRoot = $install; SourceRoot = $install; TargetMode = 'OwnedInstall'; ParentId = [int]::MaxValue } |
                ConvertTo-Json | Set-Content -LiteralPath $env:MYCAPTURE_UPDATE_SESSION -Encoding UTF8
            $args += '--no-shell-integration'
        }
        & $newExe @args | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) "Forward $mode upgrade failed."
        $env:MYCAPTURE_UPDATE_SESSION = $null
        foreach ($name in @('uninstall.ps1', 'uninstall.cmd', 'uninstall-cleanup.ps1')) { Assert-True (-not [IO.File]::Exists((Join-Path $install $name))) "Legacy residue: $name" }
        # install.ps1 contract: updater sessions keep extra files; a direct reinstall replaces the root.
        if ($mode -eq 'receipt') { Assert-True ([IO.File]::Exists($sentinel) -and [IO.File]::ReadAllText($sentinel) -eq 'keep user content') 'Forward update lost user content.' }
        else { Assert-True (-not [IO.File]::Exists($sentinel)) 'Direct reinstall kept a file from the replaced legacy root.' }
        if ($mode -eq 'receipt') {
            $receiptPath = Join-Path $sessionDirectory 'install-result.json'
            $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
            Assert-True ($receipt.Token -ceq $token -and $receipt.Version -ceq $version -and $receipt.ExitCode -eq 0 -and $receipt.InstallRoot -eq $install) 'Forward receipt mismatch.'
            Assert-True ([IO.File]::ReadAllBytes($receiptPath)[0] -ne 239) 'Receipt unexpectedly has a BOM.'
        }
        $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
        try { Assert-True ($registration.GetValue('UninstallString') -eq ('"' + (Join-Path $install 'MyCaptureUninstall.exe') + '" /uninstall')) 'New uninstall registration was not installed or migrated.' }
        finally { $registration.Dispose() }
        Assert-True ([MyCapture.Setup.PathSafety]::Equal([MyCapture.Setup.ShellLink]::Target((Join-Path $menu 'Uninstall MyCapture.lnk')), (Join-Path $install 'MyCaptureUninstall.exe'))) 'New shortcut target mismatch.'
        $log = Join-Path $root "$mode-uninstall.log"
        & (Join-Path $install 'MyCaptureUninstall.exe') /uninstall /quiet --log $log | Out-Null
        Assert-True ($LASTEXITCODE -eq 0) 'New uninstall launch failed.'
        Wait-Removed $install $log $true
        Assert-True (-not [IO.Directory]::Exists($menu)) 'New uninstall retained the isolated shortcuts.'
        $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
        try { Assert-True ($null -eq $registration) 'New uninstall retained its isolated registration.' }
        finally { if ($null -ne $registration) { $registration.Dispose() } }
    }
    $install = Join-Path $root 'reverse'
    & $newExe /quiet --no-shell-integration --install-root $install --log (Join-Path $root 'reverse-new.log') | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Reverse seed failed.'
    Invoke-Legacy (Join-Path $legacy 'install.ps1') @('-Quiet', '-NoShellIntegration', '-InstallRoot', $install, '-LogPath', (Join-Path $root 'reverse-old.log'))
    Assert-True ([IO.File]::Exists((Join-Path $install 'uninstall.cmd'))) 'Reverse install did not restore legacy bootstrap.'
    foreach ($name in @('MyCaptureUninstall.exe', 'MyCaptureUninstall.exe.config', 'SETUP-HELP.txt')) { Assert-True (-not [IO.File]::Exists((Join-Path $install $name))) "Reverse install retained new bootstrap: $name" }
    $log = Join-Path $root 'reverse-uninstall.log'
    Invoke-Legacy (Join-Path $install 'uninstall.ps1') @('-Quiet', '-LogPath', $log)
    Wait-Removed $install $log
    $fixtureSucceeded = $true
    Write-Output 'INSTALLER_COMPATIBILITY_TESTS=PASS (legacy -> new direct/receipt -> uninstall; new -> legacy -> uninstall)'
    Write-Output 'LEGACY_ISOLATION: NoShellIntegration; policy arguments removed; fixture-scoped verified recycling; real shell cleanup calls suppressed. Legacy real registry/shortcut rewriting is not exercised.'
}
catch {
    Write-Output ('TEST_FAILURE=' + $_.Exception.Message)
    if ($_.Exception.Message -ne 'LEGACY_POLICY_SKIP') { throw }
}
finally {
    $env:MYCAPTURE_DATA_ROOT = $previousData
    $env:MYCAPTURE_UPDATE_SESSION = $previousSession
    foreach ($key in $keys) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($key, $false) }
    if (-not $testKeyParentExisted) {
        $parentKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testKeyParent)
        if ($null -ne $parentKey) {
            $empty = $parentKey.SubKeyCount -eq 0 -and $parentKey.ValueCount -eq 0
            $parentKey.Dispose()
            if ($empty) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($testKeyParent, $false) }
        }
    }
    if ($fixtureSucceeded) { Remove-TestItem $root }
    else { Write-Output "FIXTURE_PRESERVED=$root" }
}
