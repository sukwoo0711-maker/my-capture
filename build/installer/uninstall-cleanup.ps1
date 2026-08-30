[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$TargetRootBase64,
    [Parameter(Mandatory = $true)][string]$OriginalRootBase64,
    [Parameter(Mandatory = $true)][string]$LogPathBase64,
    [Parameter(Mandatory = $true)][string]$StartMenuBase64
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Decode-Utf8([string]$Value) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

$targetRoot = Decode-Utf8 $TargetRootBase64
$originalRoot = Decode-Utf8 $OriginalRootBase64
$logPath = Decode-Utf8 $LogPathBase64
$startMenu = Decode-Utf8 $StartMenuBase64
$self = $MyInvocation.MyCommand.Path
$mutex = $null
$mutexHeld = $false

function Write-CleanupLog([string]$Level, [string]$Message) {
    $line = "{0:O} [{1}] {2}" -f (Get-Date), $Level.ToUpperInvariant(), $Message
    try { [IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false))) } catch { }
}

function Get-CommandExecutable([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command)) { return $null }
    $value = $Command.Trim()
    if ($value.StartsWith('"')) {
        $end = $value.IndexOf('"', 1)
        if ($end -gt 1) { return $value.Substring(1, $end - 1) }
    }
    $space = $value.IndexOf(' ')
    if ($space -gt 0) { return $value.Substring(0, $space) }
    return $value.Trim('"')
}

function Paths-Equal([string]$First, [string]$Second) {
    try { return [string]::Equals([IO.Path]::GetFullPath($First).TrimEnd('\'), [IO.Path]::GetFullPath($Second).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) }
    catch { return $false }
}

function Test-MyCaptureRoot([string]$Root) {
    try {
        $full = [IO.Path]::GetFullPath($Root).TrimEnd('\')
        $drive = [IO.Path]::GetPathRoot($full).TrimEnd('\')
        if ([string]::Equals($full, $drive, [StringComparison]::OrdinalIgnoreCase)) { return $false }
        $marker = Join-Path $full 'install-manifest.json'
        if (-not [IO.File]::Exists($marker)) { return $false }
        $manifest = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
        return [string]$manifest.Product -eq 'MyCapture'
    }
    catch { return $false }
}

function Remove-Target([string]$Root) {
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        if (-not [IO.Directory]::Exists($Root)) { return $true }
        try { Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction Stop }
        catch { Write-CleanupLog 'WARN' "Removal attempt $attempt failed: $($_.Exception.Message)" }
        if (-not [IO.Directory]::Exists($Root)) { return $true }
        Start-Sleep -Milliseconds ([Math]::Min(1000, 150 + ($attempt * 75)))
    }
    return -not [IO.Directory]::Exists($Root)
}

function Remove-OwnedStartupValue {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    try {
        $command = [string](Get-ItemProperty -Path $runKey -Name MyCapture -ErrorAction SilentlyContinue).MyCapture
        $commandExe = Get-CommandExecutable $command
        $ownedExe = Join-Path $originalRoot 'MyCapture.exe'
        if ($commandExe -and (Paths-Equal $commandExe $ownedExe)) {
            Remove-ItemProperty -Path $runKey -Name MyCapture -Force -ErrorAction Stop
            Write-CleanupLog 'INFO' 'Removed the owned MyCapture startup value.'
        }
        elseif ($command) { Write-CleanupLog 'INFO' 'Preserved a MyCapture startup value that does not point to this installation.' }
    }
    catch { Write-CleanupLog 'WARN' "Could not evaluate/remove startup registration: $($_.Exception.Message)" }
}

function Remove-OwnedShortcuts {
    if ([string]::IsNullOrWhiteSpace($startMenu) -or -not [IO.Directory]::Exists($startMenu)) { return }
    try {
        $shell = New-Object -ComObject WScript.Shell
        $expected = @{
            'MyCapture.lnk' = (Join-Path $originalRoot 'MyCapture.exe')
            'Uninstall MyCapture.lnk' = (Join-Path $originalRoot 'uninstall.cmd')
        }
        foreach ($name in $expected.Keys) {
            $path = Join-Path $startMenu $name
            if (-not [IO.File]::Exists($path)) { continue }
            try {
                $shortcut = $shell.CreateShortcut($path)
                if (Paths-Equal ([string]$shortcut.TargetPath) ([string]$expected[$name])) {
                    [IO.File]::Delete($path)
                    Write-CleanupLog 'INFO' "Removed owned shortcut: $name"
                }
                else { Write-CleanupLog 'INFO' "Preserved modified shortcut: $name" }
            }
            catch { Write-CleanupLog 'WARN' "Could not inspect shortcut $name: $($_.Exception.Message)" }
        }
        if (@(Get-ChildItem -LiteralPath $startMenu -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            [IO.Directory]::Delete($startMenu, $false)
        }
    }
    catch { Write-CleanupLog 'WARN' "Could not clean Start Menu shortcuts: $($_.Exception.Message)" }
}

function Remove-OwnedUninstallKey {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MyCapture'
    try {
        if (-not (Test-Path $key)) { return }
        $location = [string](Get-ItemProperty -Path $key -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
        if ($location -and (Paths-Equal $location $originalRoot)) {
            Remove-Item -Path $key -Recurse -Force -ErrorAction Stop
            Write-CleanupLog 'INFO' 'Removed the owned uninstall registration.'
        }
        else { Write-CleanupLog 'INFO' 'Preserved an uninstall registration that does not belong to this installation.' }
    }
    catch { Write-CleanupLog 'WARN' "Could not clean uninstall registration: $($_.Exception.Message)" }
}

$exitCode = 1
try {
    $mutex = New-Object Threading.Mutex($false, 'Local\MyCapture.InstallOrUninstall')
    try { $mutexHeld = $mutex.WaitOne(30000, $false) }
    catch [Threading.AbandonedMutexException] { $mutexHeld = $true }
    if (-not $mutexHeld) { throw 'Timed out waiting for the installer mutex.' }

    Start-Sleep -Milliseconds 700
    if ([IO.Directory]::Exists($targetRoot) -and -not (Test-MyCaptureRoot $targetRoot)) {
        throw "Cleanup target is not a verified MyCapture installation: $targetRoot"
    }
    if (-not (Remove-Target $targetRoot)) {
        throw "Installation directory remained after all removal retries: $targetRoot"
    }

    # Shell/startup records are removed only after the payload directory is gone. If deletion
    # fails, Apps & Features remains available for a retry rather than leaving an orphan silently.
    Remove-OwnedStartupValue
    Remove-OwnedShortcuts
    Remove-OwnedUninstallKey
    Write-CleanupLog 'INFO' 'MyCapture removal completed. Captures and settings under APPDATA were not accessed.'
    $exitCode = 0
}
catch { Write-CleanupLog 'ERROR' "Deferred uninstall cleanup failed: $($_.Exception.Message)" }
finally {
    if ($mutexHeld -and $mutex) { try { $mutex.ReleaseMutex() } catch { } }
    if ($mutex) { $mutex.Dispose() }
    try { Remove-Item -LiteralPath $self -Force -ErrorAction SilentlyContinue } catch { }
}

exit $exitCode
