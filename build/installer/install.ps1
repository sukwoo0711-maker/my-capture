[CmdletBinding()]
param(
    [string]$InstallRoot = '',
    [string]$PayloadPath = '',
    [string]$ManifestPath = '',
    [string]$LogPath = '',
    [switch]$Quiet,
    [switch]$VerifyOnly,
    [switch]$NoShellIntegration,
    [long]$MinimumFreeBytes = 0,
    [ValidateSet('', 'AfterBackup', 'AfterCommit')]
    [string]$TestFault = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Stable installer exit taxonomy. IExpress itself does not guarantee that a child exit code is
# surfaced by Explorer, so every failure is also written to a durable log. Direct install.cmd and
# install.ps1 callers receive these codes.
$ExitSuccess = 0
$ExitUnsupportedOs = 10
$ExitUnsupportedArchitecture = 11
$ExitUnsafePath = 12
$ExitInsufficientDisk = 13
$ExitIntegrity = 14
$ExitProcessStop = 15
$ExitStaging = 16
$ExitCommit = 17
$ExitConcurrent = 18
$ExitRestrictedPowerShell = 19
$ExitExistingInstall = 20
$ExitUnexpected = 21

$script:QuietMode = [bool]$Quiet
$script:Warnings = New-Object System.Collections.Generic.List[string]
$script:LogFile = $null
$script:Mutex = $null
$script:MutexHeld = $false
$script:StageRoot = $null
$script:BackupRoot = $null
$script:InstallRootFull = $null
$script:OldInstallMoved = $false
$script:NewInstallCommitted = $false

function Get-DefaultLogPath {
    $base = $null
    try { $base = [IO.Path]::GetTempPath() } catch { }
    if ([string]::IsNullOrWhiteSpace($base)) { $base = $PSScriptRoot }
    return Join-Path $base ("MyCapture-Install-{0:yyyyMMdd-HHmmss}-{1}.log" -f (Get-Date), $PID)
}

function Initialize-Log {
    param([string]$RequestedPath)
    $candidate = $RequestedPath
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = Get-DefaultLogPath }
    try {
        $candidate = [IO.Path]::GetFullPath($candidate)
        $directory = Split-Path -Parent $candidate
        if (-not [string]::IsNullOrWhiteSpace($directory)) {
            [IO.Directory]::CreateDirectory($directory) | Out-Null
        }
        [IO.File]::WriteAllText($candidate, '', (New-Object Text.UTF8Encoding($false)))
        $script:LogFile = $candidate
    }
    catch {
        $fallback = Join-Path $PSScriptRoot ("MyCapture-Install-{0}.log" -f $PID)
        try {
            [IO.File]::WriteAllText($fallback, '', (New-Object Text.UTF8Encoding($false)))
            $script:LogFile = $fallback
        }
        catch { $script:LogFile = $null }
    }
}

function Write-InstallLog {
    param([string]$Level, [string]$Message)
    $line = "{0:O} [{1}] {2}" -f (Get-Date), $Level.ToUpperInvariant(), $Message
    if (-not $script:QuietMode) { Write-Host $line }
    if ($script:LogFile) {
        try { [IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))) } catch { }
    }
}

function Add-InstallWarning {
    param([string]$Message)
    $script:Warnings.Add($Message)
    Write-InstallLog 'WARN' $Message
}

function Throw-InstallerError {
    param([int]$Code, [string]$Message)
    $exception = New-Object InvalidOperationException($Message)
    $exception.Data['InstallerExitCode'] = $Code
    throw $exception
}

function Get-InstallerExitCode {
    param($ErrorRecord)
    try {
        $value = $ErrorRecord.Exception.Data['InstallerExitCode']
        if ($null -ne $value) { return [int]$value }
    }
    catch { }
    return $ExitUnexpected
}

function Get-Sha256 {
    param([string]$Path)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Test-HashEquals {
    param([string]$Path, [string]$Expected)
    if (-not [IO.File]::Exists($Path)) { return $false }
    return [string]::Equals((Get-Sha256 $Path), $Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Resolve-ExistingFile {
    param([string]$Path, [string]$Description)
    if ([string]::IsNullOrWhiteSpace($Path)) { Throw-InstallerError $ExitIntegrity "$Description path is empty." }
    try { $full = [IO.Path]::GetFullPath($Path) }
    catch { Throw-InstallerError $ExitIntegrity "$Description path is invalid: $Path" }
    if (-not [IO.File]::Exists($full)) { Throw-InstallerError $ExitIntegrity "$Description is missing: $full" }
    return $full
}

function Get-WindowsBuildNumber {
    $build = [Environment]::OSVersion.Version.Build
    try {
        $current = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $registryBuild = 0
        if ([int]::TryParse([string]$current.CurrentBuildNumber, [ref]$registryBuild) -and $registryBuild -gt $build) {
            $build = $registryBuild
        }
    }
    catch { }
    return $build
}

function Assert-SupportedHost {
    param($Manifest)
    if ($PSVersionTable.PSVersion -lt [Version]'5.1') {
        Throw-InstallerError $ExitRestrictedPowerShell 'Windows PowerShell 5.1 or later is required. Use the portable ZIP if PowerShell is unavailable.'
    }
    if ([string]$ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
        Throw-InstallerError $ExitRestrictedPowerShell ("PowerShell language mode is {0}; FullLanguage is required. Use the portable ZIP or ask the administrator to allow this installer." -f $ExecutionContext.SessionState.LanguageMode)
    }
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        Throw-InstallerError $ExitUnsupportedOs 'MyCapture can only be installed on Windows.'
    }

    $build = Get-WindowsBuildNumber
    $minimumBuild = [int]$Manifest.MinimumWindowsBuild
    if ([Environment]::OSVersion.Version.Major -lt 10 -or $build -lt $minimumBuild) {
        Throw-InstallerError $ExitUnsupportedOs "Windows 11 version 21H2 (build $minimumBuild) or later is required; detected build $build."
    }

    try {
        $installationType = [string](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name InstallationType -ErrorAction Stop).InstallationType
        if ($installationType -match 'Core|Nano') {
            Throw-InstallerError $ExitUnsupportedOs "Windows Desktop Experience is required; detected '$installationType'."
        }
    }
    catch {
        if ($_.Exception.Data['InstallerExitCode']) { throw }
    }

    if (-not [Environment]::Is64BitOperatingSystem) {
        Throw-InstallerError $ExitUnsupportedArchitecture 'This package requires a 64-bit Windows installation.'
    }
    $architectureText = (($env:PROCESSOR_ARCHITEW6432, $env:PROCESSOR_ARCHITECTURE) -join ';').ToUpperInvariant()
    if ($architectureText -notmatch 'AMD64|ARM64') {
        Throw-InstallerError $ExitUnsupportedArchitecture "Unsupported Windows architecture: $architectureText"
    }
    if ($architectureText -match 'ARM64' -and $build -lt 22000) {
        Throw-InstallerError $ExitUnsupportedArchitecture 'The x64 package requires Windows 11 when running on ARM64 emulation.'
    }

    Write-InstallLog 'INFO' "Host preflight passed: Windows build $build; architecture $architectureText; PowerShell $($PSVersionTable.PSVersion)."
}

function Normalize-PackagePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { Throw-InstallerError $ExitIntegrity 'Payload manifest contains an empty path.' }
    if ($Path.Contains('\')) { Throw-InstallerError $ExitIntegrity "Payload path must use forward slashes: $Path" }
    if ($Path.StartsWith('/') -or $Path -match '^[A-Za-z]:' -or $Path.Contains(':')) {
        Throw-InstallerError $ExitIntegrity "Payload contains an absolute or alternate-stream path: $Path"
    }
    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            Throw-InstallerError $ExitIntegrity "Payload contains an unsafe path segment: $Path"
        }
    }
    return $Path
}

function Read-And-ValidateManifest {
    param([string]$Path)
    try { $manifest = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch { Throw-InstallerError $ExitIntegrity "Installer manifest is invalid JSON: $($_.Exception.Message)" }

    if ([int]$manifest.SchemaVersion -ne 1 -or [string]$manifest.Product -ne 'MyCapture') {
        Throw-InstallerError $ExitIntegrity 'Installer manifest schema or product is invalid.'
    }
    $semVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?$'
    if ([string]$manifest.Version -notmatch $semVerPattern) {
        Throw-InstallerError $ExitIntegrity "Installer manifest version is invalid: $($manifest.Version)"
    }
    if ([string]$manifest.Runtime -ne 'win-x64' -or [string]$manifest.Architecture -ne 'x64' -or -not [bool]$manifest.SelfContained -or -not [bool]$manifest.Offline) {
        Throw-InstallerError $ExitIntegrity 'Installer manifest does not describe an offline self-contained win-x64 payload.'
    }

    $files = @($manifest.Files)
    if ($files.Count -ne [int]$manifest.FileCount -or $files.Count -lt 1 -or $files.Count -gt 10000) {
        Throw-InstallerError $ExitIntegrity 'Payload file count is invalid.'
    }
    $seen = @{}
    [long]$total = 0
    foreach ($file in $files) {
        $relative = Normalize-PackagePath ([string]$file.Path)
        if ($seen.ContainsKey($relative)) { Throw-InstallerError $ExitIntegrity "Duplicate payload path: $relative" }
        $seen[$relative] = $true
        $length = [long]$file.Bytes
        if ($length -lt 0 -or $length -gt 1073741824) { Throw-InstallerError $ExitIntegrity "Payload file has an invalid size: $relative" }
        if ([string]$file.Sha256 -notmatch '^[A-Fa-f0-9]{64}$') { Throw-InstallerError $ExitIntegrity "Payload file has an invalid SHA-256: $relative" }
        $total += $length
        if ($total -gt 2147483648) { Throw-InstallerError $ExitIntegrity 'Payload expanded size exceeds the 2 GiB safety limit.' }
    }
    if ($total -ne [long]$manifest.TotalExpandedBytes) { Throw-InstallerError $ExitIntegrity 'Payload expanded byte total does not match the manifest.' }

    return $manifest
}

function Get-BootstrapEntry {
    param($Manifest, [string]$Name)
    $matches = @($Manifest.BootstrapFiles | Where-Object { [string]$_.Path -eq $Name })
    if ($matches.Count -ne 1) { Throw-InstallerError $ExitIntegrity "Bootstrap manifest entry is missing or duplicated: $Name" }
    return $matches[0]
}

function Assert-BootstrapIntegrity {
    param($Manifest)
    foreach ($name in @('install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'uninstall-cleanup.ps1')) {
        $entry = Get-BootstrapEntry $Manifest $name
        $path = Resolve-ExistingFile (Join-Path $PSScriptRoot $name) "Bootstrap file '$name'"
        if ((Get-Item -LiteralPath $path).Length -ne [long]$entry.Bytes -or -not (Test-HashEquals $path ([string]$entry.Sha256))) {
            Throw-InstallerError $ExitIntegrity "Bootstrap integrity check failed: $name"
        }
    }
}

function Get-SafeInstallRoot {
    param([string]$RequestedRoot, $Manifest)
    $root = $RequestedRoot
    if ([string]::IsNullOrWhiteSpace($root)) {
        $local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
        if ([string]::IsNullOrWhiteSpace($local)) { Throw-InstallerError $ExitUnsafePath 'LOCALAPPDATA is unavailable.' }
        $root = Join-Path $local 'Programs\MyCapture'
    }
    if ($root.StartsWith('\\?\') -or $root.StartsWith('\\.\')) {
        Throw-InstallerError $ExitUnsafePath 'Extended/device install paths are not accepted.'
    }
    try { $full = [IO.Path]::GetFullPath($root).TrimEnd('\') }
    catch { Throw-InstallerError $ExitUnsafePath "Install path is invalid: $root" }
    $pathRoot = [IO.Path]::GetPathRoot($full)
    if ([string]::IsNullOrWhiteSpace($pathRoot) -or [string]::Equals($full, $pathRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        Throw-InstallerError $ExitUnsafePath "A drive root cannot be used as InstallRoot: $full"
    }
    if ((New-Object Uri($full)).IsUnc) {
        Throw-InstallerError $ExitUnsafePath 'UNC/network install roots are not supported. Use a local drive or the portable ZIP.'
    }

    $maxRelative = 0
    foreach ($file in @($Manifest.Files)) { $maxRelative = [Math]::Max($maxRelative, ([string]$file.Path).Length) }
    if (($full.Length + 1 + $maxRelative) -gt 240) {
        Throw-InstallerError $ExitUnsafePath 'InstallRoot is too deep for reliable setup/uninstall on systems without long-path policy. Choose a shorter local path.'
    }

    if ([IO.Directory]::Exists($full)) {
        $item = Get-Item -LiteralPath $full -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Throw-InstallerError $ExitUnsafePath 'InstallRoot cannot be a junction or symbolic link.'
        }
        $children = @(Get-ChildItem -LiteralPath $full -Force -ErrorAction Stop)
        if ($children.Count -gt 0) {
            $owned = $false
            $marker = Join-Path $full 'install-manifest.json'
            if ([IO.File]::Exists($marker)) {
                try { $owned = ([string](Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json).Product -eq 'MyCapture') } catch { }
            }
            if (-not $owned) {
                $legacyDll = Join-Path $full 'MyCapture.dll'
                if ([IO.File]::Exists($legacyDll)) {
                    try { $owned = ([Diagnostics.FileVersionInfo]::GetVersionInfo($legacyDll).ProductName -eq 'MyCapture') } catch { }
                }
            }
            if (-not $owned) {
                Throw-InstallerError $ExitExistingInstall "InstallRoot contains files not proven to belong to MyCapture: $full"
            }
        }
    }
    return $full
}

function Assert-ParentWritable {
    param([string]$Parent)
    try {
        [IO.Directory]::CreateDirectory($Parent) | Out-Null
        $probe = Join-Path $Parent ('.mycapture-write-probe-' + [guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($probe, 'probe')
        [IO.File]::Delete($probe)
    }
    catch { Throw-InstallerError $ExitUnsafePath "Install location is not writable: $Parent ($($_.Exception.Message))" }
}

function Assert-FreeSpace {
    param([string]$Parent, [long]$ExpandedBytes, [long]$RequestedMinimum)
    try {
        $driveRoot = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Parent))
        $drive = New-Object IO.DriveInfo($driveRoot)
        $required = $ExpandedBytes + 67108864L
        if ($RequestedMinimum -gt $required) { $required = $RequestedMinimum }
        if ($drive.AvailableFreeSpace -lt $required) {
            Throw-InstallerError $ExitInsufficientDisk ("Insufficient free space on {0}: need {1:N0} bytes, available {2:N0} bytes." -f $driveRoot, $required, $drive.AvailableFreeSpace)
        }
        Write-InstallLog 'INFO' ("Disk preflight passed: required {0:N0}, available {1:N0} bytes on {2}." -f $required, $drive.AvailableFreeSpace, $driveRoot)
    }
    catch {
        if ($_.Exception.Data['InstallerExitCode']) { throw }
        Throw-InstallerError $ExitInsufficientDisk "Unable to query free space for ${Parent}: $($_.Exception.Message)"
    }
}

function Assert-ZipEntryName {
    param([string]$Name)
    return Normalize-PackagePath $Name
}

function Expand-VerifiedPayload {
    param([string]$ZipPath, $Manifest, [string]$Destination)
    try { Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop }
    catch { Throw-InstallerError $ExitRestrictedPowerShell 'System.IO.Compression is unavailable; Windows PowerShell 5.1 desktop components may be damaged.' }

    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $zip = $null
    try {
        $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
        $entryMap = @{}
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            $name = Assert-ZipEntryName ([string]$entry.FullName)
            if ($entryMap.ContainsKey($name)) { Throw-InstallerError $ExitIntegrity "Duplicate ZIP entry: $name" }
            $entryMap[$name] = $entry
        }
        if ($entryMap.Count -ne [int]$Manifest.FileCount) {
            Throw-InstallerError $ExitIntegrity "ZIP file count $($entryMap.Count) does not match manifest count $($Manifest.FileCount)."
        }

        $destinationPrefix = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
        foreach ($file in @($Manifest.Files)) {
            $relative = [string]$file.Path
            if (-not $entryMap.ContainsKey($relative)) { Throw-InstallerError $ExitIntegrity "ZIP entry is missing: $relative" }
            $entry = $entryMap[$relative]
            if ([long]$entry.Length -ne [long]$file.Bytes) { Throw-InstallerError $ExitIntegrity "ZIP entry size mismatch: $relative" }
            $target = [IO.Path]::GetFullPath((Join-Path $Destination ($relative.Replace('/', '\'))))
            if (-not $target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                Throw-InstallerError $ExitIntegrity "ZIP entry escapes staging root: $relative"
            }
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            $input = $entry.Open()
            $output = New-Object IO.FileStream($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $input.CopyTo($output) }
            finally { $output.Dispose(); $input.Dispose() }
            if (-not (Test-HashEquals $target ([string]$file.Sha256))) { Throw-InstallerError $ExitIntegrity "Extracted file SHA-256 mismatch: $relative" }
        }
    }
    catch {
        if ($_.Exception.Data['InstallerExitCode']) { throw }
        Throw-InstallerError $ExitStaging "Payload extraction failed: $($_.Exception.Message)"
    }
    finally { if ($zip) { $zip.Dispose() } }
}

function Get-PeMachine {
    param([string]$Path)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 128 -or $reader.ReadUInt16() -ne 0x5A4D) { return 0 }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or ($peOffset + 6) -gt $stream.Length) { return 0 }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return 0 }
        return $reader.ReadUInt16()
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-StagedApplication {
    param([string]$Root, $Manifest)
    $exe = Join-Path $Root 'MyCapture.exe'
    $dll = Join-Path $Root 'MyCapture.dll'
    if (-not [IO.File]::Exists($exe) -or -not [IO.File]::Exists($dll)) { Throw-InstallerError $ExitIntegrity 'Payload is missing MyCapture.exe or MyCapture.dll.' }
    if ((Get-PeMachine $exe) -ne 0x8664) { Throw-InstallerError $ExitIntegrity 'MyCapture.exe is not an x64 PE image.' }
    $productVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -split '\+')[0]
    if ($productVersion -ne [string]$Manifest.Version) { Throw-InstallerError $ExitIntegrity "Payload product version $productVersion does not match manifest version $($Manifest.Version)." }
    foreach ($asset in @('tray-idle.ico', 'tray-capturing.ico', 'tray-busy.ico')) {
        if (-not [IO.File]::Exists((Join-Path $Root "Assets\$asset"))) { Throw-InstallerError $ExitIntegrity "Required tray asset is missing: $asset" }
    }
}

function Stop-InstalledApplication {
    param([string]$ExecutablePath)
    $target = [IO.Path]::GetFullPath($ExecutablePath)
    $matches = New-Object System.Collections.Generic.List[Diagnostics.Process]
    foreach ($process in [Diagnostics.Process]::GetProcessesByName('MyCapture')) {
        try {
            $candidate = [IO.Path]::GetFullPath($process.MainModule.FileName)
            if ([string]::Equals($candidate, $target, [StringComparison]::OrdinalIgnoreCase)) { $matches.Add($process) }
            else { $process.Dispose() }
        }
        catch { $process.Dispose() }
    }
    foreach ($process in $matches) {
        try {
            Write-InstallLog 'INFO' "Stopping running MyCapture process $($process.Id)."
            try { $null = $process.CloseMainWindow() } catch { }
            if (-not $process.WaitForExit(4000)) {
                $process.Kill()
                if (-not $process.WaitForExit(6000)) { Throw-InstallerError $ExitProcessStop "MyCapture process $($process.Id) did not stop." }
            }
        }
        catch {
            if ($_.Exception.Data['InstallerExitCode']) { throw }
            Throw-InstallerError $ExitProcessStop "Unable to stop MyCapture process $($process.Id): $($_.Exception.Message)"
        }
        finally { $process.Dispose() }
    }
}

function Move-DirectoryWithRetry {
    param([string]$Source, [string]$Destination)
    $last = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try { Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop; return }
        catch { $last = $_; Start-Sleep -Milliseconds (150 * $attempt) }
    }
    throw $last
}

function Remove-DirectoryWithRetry {
    param([string]$Path)
    if (-not [IO.Directory]::Exists($Path)) { return }
    $last = $null
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop; return }
        catch { $last = $_; Start-Sleep -Milliseconds (180 * $attempt) }
    }
    throw $last
}

function Verify-InstalledInventory {
    param([string]$Root, $Manifest)
    foreach ($file in @($Manifest.Files)) {
        $path = Join-Path $Root (([string]$file.Path).Replace('/', '\'))
        if (-not [IO.File]::Exists($path) -or (Get-Item -LiteralPath $path).Length -ne [long]$file.Bytes -or -not (Test-HashEquals $path ([string]$file.Sha256))) {
            Throw-InstallerError $ExitCommit "Installed payload verification failed: $($file.Path)"
        }
    }
}

function Rollback-Install {
    Write-InstallLog 'WARN' 'Rolling back the installation transaction.'
    if ($script:NewInstallCommitted -and [IO.Directory]::Exists($script:InstallRootFull)) {
        Remove-DirectoryWithRetry $script:InstallRootFull
        $script:NewInstallCommitted = $false
    }
    if ($script:OldInstallMoved -and [IO.Directory]::Exists($script:BackupRoot)) {
        Move-DirectoryWithRetry $script:BackupRoot $script:InstallRootFull
        $script:OldInstallMoved = $false
    }
}

function Install-ShellIntegration {
    param([string]$Root, $Manifest)
    $exe = Join-Path $Root 'MyCapture.exe'
    $uninstallCmd = Join-Path $Root 'uninstall.cmd'
    try {
        $startMenuBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
        if ([string]::IsNullOrWhiteSpace($startMenuBase)) { throw 'Start Menu Programs path is unavailable.' }
        $startMenu = Join-Path $startMenuBase 'MyCapture'
        [IO.Directory]::CreateDirectory($startMenu) | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu 'MyCapture.lnk'))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $Root
        $shortcut.IconLocation = "$exe,0"
        $shortcut.Description = 'MyCapture screen capture and annotation tool'
        $shortcut.Save()
        $remove = $shell.CreateShortcut((Join-Path $startMenu 'Uninstall MyCapture.lnk'))
        $remove.TargetPath = $uninstallCmd
        $remove.WorkingDirectory = $Root
        $remove.Description = 'Uninstall MyCapture'
        $remove.Save()
        Write-InstallLog 'INFO' "Start Menu shortcuts installed at $startMenu."
    }
    catch { Add-InstallWarning "Core files were installed, but Start Menu shortcuts could not be created: $($_.Exception.Message)" }

    try {
        $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MyCapture'
        New-Item -Path $uninstallKey -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'MyCapture' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value ([string]$Manifest.Version) -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'MyCapture' -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name DisplayIcon -Value $exe -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $Root -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name UninstallString -Value ('"' + $uninstallCmd + '"') -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name QuietUninstallString -Value ('"' + $uninstallCmd + '" /quiet') -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name InstallDate -Value (Get-Date -Format 'yyyyMMdd') -PropertyType String -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name EstimatedSize -Value ([int][Math]::Ceiling(([long]$Manifest.TotalExpandedBytes) / 1KB)) -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
        New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
        Write-InstallLog 'INFO' 'Per-user uninstall registration updated.'
    }
    catch { Add-InstallWarning "Core files were installed, but the uninstall registry entry could not be written: $($_.Exception.Message)" }
}

function Show-ResultMessage {
    param([bool]$Success, [string]$Message)
    if ($script:QuietMode) { return }
    try {
        $shell = New-Object -ComObject WScript.Shell
        $icon = 16
        if ($Success) { $icon = 64 }
        $null = $shell.Popup($Message, 0, 'MyCapture Setup', $icon)
    }
    catch { }
}

Initialize-Log $LogPath
$exitCode = $ExitSuccess
try {
    Write-InstallLog 'INFO' "MyCapture installer starting. PID=$PID; script=$($MyInvocation.MyCommand.Path)"

    $script:Mutex = New-Object Threading.Mutex($false, 'Local\MyCapture.InstallOrUninstall')
    try { $script:MutexHeld = $script:Mutex.WaitOne(0, $false) }
    catch [Threading.AbandonedMutexException] { $script:MutexHeld = $true }
    if (-not $script:MutexHeld) { Throw-InstallerError $ExitConcurrent 'Another MyCapture install or uninstall operation is already running.' }

    if ([string]::IsNullOrWhiteSpace($PayloadPath)) { $PayloadPath = Join-Path $PSScriptRoot 'payload.zip' }
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) { $ManifestPath = Join-Path $PSScriptRoot 'installer-manifest.json' }
    $PayloadPath = Resolve-ExistingFile $PayloadPath 'Payload ZIP'
    $ManifestPath = Resolve-ExistingFile $ManifestPath 'Installer manifest'
    $manifest = Read-And-ValidateManifest $ManifestPath

    Assert-SupportedHost $manifest
    Assert-BootstrapIntegrity $manifest
    if ((Get-Item -LiteralPath $PayloadPath).Length -ne [long]$manifest.Payload.Bytes -or -not (Test-HashEquals $PayloadPath ([string]$manifest.Payload.Sha256))) {
        Throw-InstallerError $ExitIntegrity 'Payload ZIP SHA-256 or size does not match the installer manifest.'
    }
    Write-InstallLog 'INFO' "Payload cryptographic verification passed: $($manifest.Payload.Sha256)."

    $script:InstallRootFull = Get-SafeInstallRoot $InstallRoot $manifest
    $parent = Split-Path -Parent $script:InstallRootFull
    Assert-ParentWritable $parent
    Assert-FreeSpace $parent ([long]$manifest.TotalExpandedBytes) $MinimumFreeBytes

    $transactionId = [guid]::NewGuid().ToString('N')
    $script:StageRoot = Join-Path $parent ('.MyCapture.stage.' + $transactionId)
    $script:BackupRoot = Join-Path $parent ('.MyCapture.backup.' + $transactionId)
    if ([IO.Directory]::Exists($script:StageRoot) -or [IO.Directory]::Exists($script:BackupRoot)) { Throw-InstallerError $ExitUnsafePath 'Transaction staging path already exists.' }

    Expand-VerifiedPayload $PayloadPath $manifest $script:StageRoot
    Assert-StagedApplication $script:StageRoot $manifest
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $script:StageRoot 'uninstall.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.cmd') -Destination (Join-Path $script:StageRoot 'uninstall.cmd') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall-cleanup.ps1') -Destination (Join-Path $script:StageRoot 'uninstall-cleanup.ps1') -Force
    Copy-Item -LiteralPath $ManifestPath -Destination (Join-Path $script:StageRoot 'install-manifest.json') -Force
    Write-InstallLog 'INFO' "Payload extracted and verified in transaction staging: $($manifest.FileCount) files."

    if ($VerifyOnly) {
        Write-InstallLog 'INFO' 'VerifyOnly completed; no installation state was changed.'
    }
    else {
        Stop-InstalledApplication (Join-Path $script:InstallRootFull 'MyCapture.exe')
        try {
            if ([IO.Directory]::Exists($script:InstallRootFull)) {
                Move-DirectoryWithRetry $script:InstallRootFull $script:BackupRoot
                $script:OldInstallMoved = $true
                Write-InstallLog 'INFO' "Existing installation moved to rollback backup: $($script:BackupRoot)"
            }
            if ($TestFault -eq 'AfterBackup') { Throw-InstallerError $ExitCommit 'Injected test fault after backup.' }

            Move-DirectoryWithRetry $script:StageRoot $script:InstallRootFull
            $script:StageRoot = $null
            $script:NewInstallCommitted = $true
            if ($TestFault -eq 'AfterCommit') { Throw-InstallerError $ExitCommit 'Injected test fault after commit.' }

            Verify-InstalledInventory $script:InstallRootFull $manifest
            Assert-StagedApplication $script:InstallRootFull $manifest
            Write-InstallLog 'INFO' "Transactional payload commit verified at $($script:InstallRootFull)."
        }
        catch {
            $original = $_
            try { Rollback-Install }
            catch { Throw-InstallerError $ExitCommit "Installation failed and rollback also failed. Backup retained at $($script:BackupRoot). Rollback error: $($_.Exception.Message)" }
            throw $original
        }

        if (-not $NoShellIntegration) { Install-ShellIntegration $script:InstallRootFull $manifest }
        else { Write-InstallLog 'INFO' 'Shell integration was intentionally skipped.' }

        if ($script:OldInstallMoved -and [IO.Directory]::Exists($script:BackupRoot)) {
            try {
                Remove-DirectoryWithRetry $script:BackupRoot
                $script:OldInstallMoved = $false
            }
            catch { Add-InstallWarning "Installation succeeded, but the old rollback directory could not be removed: $($script:BackupRoot)" }
        }
    }

    $summary = "MyCapture $($manifest.Version) " + $(if ($VerifyOnly) { 'package verification succeeded.' } else { "installed successfully to $($script:InstallRootFull)." })
    if ($script:Warnings.Count -gt 0) { $summary += " Warnings: $($script:Warnings.Count)." }
    $summary += " Log: $($script:LogFile)"
    Write-InstallLog 'INFO' $summary
    Show-ResultMessage $true $summary
}
catch {
    $exitCode = Get-InstallerExitCode $_
    $message = "Installation failed (exit $exitCode): $($_.Exception.Message)"
    Write-InstallLog 'ERROR' $message
    if ($script:BackupRoot -and [IO.Directory]::Exists($script:BackupRoot)) {
        Write-InstallLog 'ERROR' "A rollback backup remains at $($script:BackupRoot). Do not delete it until the previous installation is confirmed."
    }
    Show-ResultMessage $false ($message + [Environment]::NewLine + "Log: $($script:LogFile)")
}
finally {
    if ($script:StageRoot -and [IO.Directory]::Exists($script:StageRoot)) {
        try { Remove-DirectoryWithRetry $script:StageRoot } catch { Write-InstallLog 'WARN' "Could not remove staging directory: $($script:StageRoot)" }
    }
    if ($script:MutexHeld -and $script:Mutex) { try { $script:Mutex.ReleaseMutex() } catch { } }
    if ($script:Mutex) { $script:Mutex.Dispose() }
}

exit $exitCode
