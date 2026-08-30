[CmdletBinding()]
param(
    [switch]$Quiet,
    [string]$LogPath = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$ExitSuccess = 0
$ExitInvalidInstall = 30
$ExitConcurrent = 31
$ExitRestrictedPowerShell = 32
$ExitProcessStop = 33
$ExitCleanupLaunch = 34
$ExitUnexpected = 35

$script:QuietMode = [bool]$Quiet
$script:LogFile = $null
$script:Mutex = $null
$script:MutexHeld = $false

function Get-DefaultLogPath {
    $base = $null
    try { $base = [IO.Path]::GetTempPath() } catch { }
    if ([string]::IsNullOrWhiteSpace($base)) { $base = $PSScriptRoot }
    return Join-Path $base ("MyCapture-Uninstall-{0:yyyyMMdd-HHmmss}-{1}.log" -f (Get-Date), $PID)
}

function Initialize-Log([string]$RequestedPath) {
    $candidate = $RequestedPath
    if ([string]::IsNullOrWhiteSpace($candidate)) { $candidate = Get-DefaultLogPath }
    try {
        $candidate = [IO.Path]::GetFullPath($candidate)
        [IO.Directory]::CreateDirectory((Split-Path -Parent $candidate)) | Out-Null
        [IO.File]::WriteAllText($candidate, '', (New-Object Text.UTF8Encoding($false)))
        $script:LogFile = $candidate
    }
    catch { $script:LogFile = $null }
}

function Write-UninstallLog([string]$Level, [string]$Message) {
    $line = "{0:O} [{1}] {2}" -f (Get-Date), $Level.ToUpperInvariant(), $Message
    if (-not $script:QuietMode) { Write-Host $line }
    if ($script:LogFile) {
        try { [IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))) } catch { }
    }
}

function Throw-UninstallError([int]$Code, [string]$Message) {
    $exception = New-Object InvalidOperationException($Message)
    $exception.Data['UninstallExitCode'] = $Code
    throw $exception
}

function Get-UninstallExitCode($ErrorRecord) {
    try {
        $value = $ErrorRecord.Exception.Data['UninstallExitCode']
        if ($null -ne $value) { return [int]$value }
    }
    catch { }
    return $ExitUnexpected
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Encode-Utf8([string]$Value) {
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

function Get-BootstrapEntry($Manifest, [string]$Name) {
    $matches = @($Manifest.BootstrapFiles | Where-Object { [string]$_.Path -eq $Name })
    if ($matches.Count -ne 1) { Throw-UninstallError $ExitInvalidInstall "Install manifest entry is missing or duplicated: $Name" }
    return $matches[0]
}

function Assert-VerifiedInstallation([string]$Root) {
    try { $full = [IO.Path]::GetFullPath($Root).TrimEnd('\') }
    catch { Throw-UninstallError $ExitInvalidInstall "Invalid installation path: $Root" }
    $driveRoot = [IO.Path]::GetPathRoot($full).TrimEnd('\')
    if ([string]::Equals($full, $driveRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Throw-UninstallError $ExitInvalidInstall 'Refusing to uninstall from a drive root.'
    }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Throw-UninstallError $ExitInvalidInstall 'Refusing to uninstall through a junction or symbolic link.'
    }
    $marker = Join-Path $full 'install-manifest.json'
    if (-not [IO.File]::Exists($marker)) { Throw-UninstallError $ExitInvalidInstall 'Installed manifest is missing; recursive deletion was refused.' }
    try { $manifest = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop }
    catch { Throw-UninstallError $ExitInvalidInstall "Installed manifest is invalid: $($_.Exception.Message)" }
    if ([string]$manifest.Product -ne 'MyCapture' -or [int]$manifest.SchemaVersion -ne 1) {
        Throw-UninstallError $ExitInvalidInstall 'Installed manifest does not identify a supported MyCapture installation.'
    }

    foreach ($name in @('uninstall.ps1', 'uninstall.cmd', 'uninstall-cleanup.ps1')) {
        $entry = Get-BootstrapEntry $manifest $name
        $path = Join-Path $full $name
        if (-not [IO.File]::Exists($path) -or (Get-Item -LiteralPath $path).Length -ne [long]$entry.Bytes -or -not [string]::Equals((Get-Sha256 $path), [string]$entry.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
            Throw-UninstallError $ExitInvalidInstall "Uninstaller integrity check failed: $name"
        }
    }
    return $manifest
}

function Stop-InstalledApplication([string]$ExecutablePath) {
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
            Write-UninstallLog 'INFO' "Stopping MyCapture process $($process.Id)."
            try { $null = $process.CloseMainWindow() } catch { }
            if (-not $process.WaitForExit(4000)) {
                $process.Kill()
                if (-not $process.WaitForExit(6000)) { Throw-UninstallError $ExitProcessStop "Process $($process.Id) did not stop." }
            }
        }
        catch {
            if ($_.Exception.Data['UninstallExitCode']) { throw }
            Throw-UninstallError $ExitProcessStop "Unable to stop process $($process.Id): $($_.Exception.Message)"
        }
        finally { $process.Dispose() }
    }
}

function Show-Result([bool]$Success, [string]$Message) {
    if ($script:QuietMode) { return }
    try {
        $shell = New-Object -ComObject WScript.Shell
        $icon = 16
        if ($Success) { $icon = 64 }
        $null = $shell.Popup($Message, 0, 'MyCapture Uninstall', $icon)
    }
    catch { }
}

Initialize-Log $LogPath
$exitCode = $ExitSuccess
try {
    Write-UninstallLog 'INFO' "MyCapture uninstaller starting. PID=$PID; script=$($MyInvocation.MyCommand.Path)"
    if ($PSVersionTable.PSVersion -lt [Version]'5.1' -or [string]$ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
        Throw-UninstallError $ExitRestrictedPowerShell 'Windows PowerShell 5.1 FullLanguage mode is required to uninstall safely.'
    }

    $script:Mutex = New-Object Threading.Mutex($false, 'Local\MyCapture.InstallOrUninstall')
    try { $script:MutexHeld = $script:Mutex.WaitOne(0, $false) }
    catch [Threading.AbandonedMutexException] { $script:MutexHeld = $true }
    if (-not $script:MutexHeld) { Throw-UninstallError $ExitConcurrent 'Another MyCapture install or uninstall operation is already running.' }

    $installRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path)).TrimEnd('\')
    $manifest = Assert-VerifiedInstallation $installRoot
    Stop-InstalledApplication (Join-Path $installRoot 'MyCapture.exe')

    $cleanupSource = Join-Path $installRoot 'uninstall-cleanup.ps1'
    $cleanupCopy = Join-Path ([IO.Path]::GetTempPath()) ('.MyCapture-uninstall-cleanup-' + [guid]::NewGuid().ToString('N') + '.ps1')
    Copy-Item -LiteralPath $cleanupSource -Destination $cleanupCopy -Force
    $cleanupEntry = Get-BootstrapEntry $manifest 'uninstall-cleanup.ps1'
    if (-not [string]::Equals((Get-Sha256 $cleanupCopy), [string]$cleanupEntry.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        Throw-UninstallError $ExitInvalidInstall 'Temporary cleanup worker failed its integrity check.'
    }

    $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $startMenu = ''
    if (-not [string]::IsNullOrWhiteSpace($programs)) { $startMenu = Join-Path $programs 'MyCapture' }
    $powerShellExe = Join-Path $PSHOME 'powershell.exe'
    if (-not [IO.File]::Exists($powerShellExe)) { Throw-UninstallError $ExitCleanupLaunch 'Unable to locate Windows PowerShell for deferred cleanup.' }

    $arguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + $cleanupCopy + '"'),
        '-TargetRootBase64', (Encode-Utf8 $installRoot),
        '-OriginalRootBase64', (Encode-Utf8 $installRoot),
        '-LogPathBase64', (Encode-Utf8 ([string]$script:LogFile)),
        '-StartMenuBase64', (Encode-Utf8 $startMenu)
    )
    try {
        $worker = Start-Process -FilePath $powerShellExe -ArgumentList $arguments -WindowStyle Hidden -PassThru -ErrorAction Stop
        Write-UninstallLog 'INFO' "Deferred cleanup worker started (PID $($worker.Id))."
    }
    catch {
        try { Remove-Item -LiteralPath $cleanupCopy -Force -ErrorAction SilentlyContinue } catch { }
        Throw-UninstallError $ExitCleanupLaunch "Unable to launch deferred cleanup: $($_.Exception.Message)"
    }

    $message = "MyCapture removal is scheduled. Captures and settings are preserved. Log: $($script:LogFile)"
    Write-UninstallLog 'INFO' $message
    if ($script:MutexHeld -and $script:Mutex) {
        try {
            $script:Mutex.ReleaseMutex()
            $script:MutexHeld = $false
            Write-UninstallLog 'INFO' 'Installer mutex handed off to the deferred cleanup worker.'
        }
        catch { Throw-UninstallError $ExitCleanupLaunch "Unable to hand off the installer mutex: $($_.Exception.Message)" }
    }
    Show-Result $true $message
}
catch {
    $exitCode = Get-UninstallExitCode $_
    $message = "Uninstall failed (exit $exitCode): $($_.Exception.Message)"
    Write-UninstallLog 'ERROR' $message
    Show-Result $false ($message + [Environment]::NewLine + "Log: $($script:LogFile)")
}
finally {
    if ($script:MutexHeld -and $script:Mutex) { try { $script:Mutex.ReleaseMutex() } catch { } }
    if ($script:Mutex) { $script:Mutex.Dispose() }
}

exit $exitCode
