[CmdletBinding()]
param([string]$Session, [switch]$FunctionsOnly)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
function Assert-NoLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Update path contains a reparse point.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Get-Hash([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Get-InstalledBinaryVersion([string]$Path) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    return '{0}.{1}.{2}' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart
}
function Start-UpdateProcess($Info) { return [Diagnostics.Process]::Start($Info) }
function Assert-SessionTarget($Config) {
    $defaultRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\MyCapture'
    if ($Config.TargetMode -eq 'InstallToDefault') {
        if (-not [string]::Equals([string]$Config.InstallRoot, $defaultRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Portable conversion must use the default per-user destination.' }
        return
    }
    if ($Config.TargetMode -ne 'OwnedInstall' -or -not [string]::Equals([string]$Config.SourceRoot, [string]$Config.InstallRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Update source and target do not match.' }
    $marker = Join-Path $Config.SourceRoot 'install-manifest.json'
    Assert-NoLinks $marker
    $old = Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($old.Product -ne 'MyCapture') { throw 'Update source is not an owned installation.' }
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) {
        $file = Join-Path $Config.SourceRoot $name
        Assert-NoLinks $file
        $entries = @($old.Files | Where-Object { $_.Path -ceq $name })
        if ($entries.Count -ne 1 -or (Get-Item -LiteralPath $file).Length -ne [long]$entries[0].Bytes -or
            (Get-Hash $file) -ne [string]$entries[0].Sha256) { throw 'Installed source changed before update.' }
    }
}
function Confirm-AndRestart($config, $receipt) {
    if ($receipt.Token -ne $config.Token -or $receipt.Version -ne $config.Version -or [int]$receipt.ExitCode -ne 0 -or
        -not [string]::Equals([string]$receipt.InstallRoot, [string]$config.InstallRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer failed or returned an unexpected receipt.' }
    $manifestPath = Join-Path $config.InstallRoot 'install-manifest.json'
    Assert-NoLinks $manifestPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.Product -ne 'MyCapture' -or $manifest.Version -ne $config.Version) { throw 'Installed version was not confirmed.' }
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) {
        $entries = @($manifest.Files | Where-Object { $_.Path -ceq $name })
        $binary = Join-Path $config.InstallRoot $name
        Assert-NoLinks $binary
        if ($entries.Count -ne 1 -or (Get-Item -LiteralPath $binary).Length -ne [long]$entries[0].Bytes -or
            (Get-Hash $binary) -ne [string]$entries[0].Sha256) { throw 'Installed application hash was not confirmed.' }
        $actual = Get-InstalledBinaryVersion $binary
        if ($actual -ne $config.Version) { throw 'Installed binary version was not confirmed.' }
    }
    $restart = New-Object Diagnostics.ProcessStartInfo
    $restart.FileName = Join-Path $config.InstallRoot 'MyCapture.exe'
    $restart.WorkingDirectory = $config.InstallRoot
    $restart.UseShellExecute = $false
    $restart.EnvironmentVariables.Remove('MYCAPTURE_UPDATE_SESSION')
    $process = (Start-UpdateProcess $restart)
    if (-not $process -or $process.WaitForExit(3000)) { throw 'Updated application exited during restart.' }
    $process.Dispose()
}
if ($FunctionsOnly) { return }
$sessionRoot = Split-Path -Parent ([IO.Path]::GetFullPath($Session))
$log = Join-Path $sessionRoot 'update.log'
$config = $null
try {
    Assert-NoLinks $Session
    $config = Get-Content -LiteralPath $Session -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config.Token -notmatch '^[a-f0-9]{32}$' -or $config.Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid update session.' }
    $expectedInstaller = Join-Path $sessionRoot ('MyCapture-' + $config.Version + '-win-x64-setup.exe')
    if (-not [string]::Equals($expectedInstaller, [string]$config.Installer, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer escaped session.' }
    $parent = Get-Process -Id ([int]$config.ParentId) -ErrorAction SilentlyContinue
    if ($parent) {
        if ($parent.StartTime.ToUniversalTime().Ticks -ne [long]$config.ParentStartedUtc) { throw 'Parent process identity changed.' }
        if (-not $parent.WaitForExit(60000)) { throw 'Application did not exit; installation was not started.' }
        $parent.Dispose()
    }
    Assert-SessionTarget $config
    Assert-NoLinks $expectedInstaller
    # Recheck after parent exit. Keep the package locked until the installer receipt arrives.
    $locked = [IO.File]::Open($expectedInstaller, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($locked.Length -ne [long]$config.Bytes -or (Get-Hash $expectedInstaller) -ne [string]$config.Sha256) { throw 'Installer changed after verification.' }
        $receiptPath = Join-Path $sessionRoot 'install-result.json'
        if (Test-Path -LiteralPath $receiptPath) { throw 'An installer receipt already exists.' }
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $expectedInstaller
        $start.Arguments = '/Q'
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.EnvironmentVariables['MYCAPTURE_UPDATE_SESSION'] = $Session
        $installer = [Diagnostics.Process]::Start($start)
        if (-not $installer) { throw 'Installer did not start.' }
        # IExpress may exit before install.cmd. Its exit code never proves install success.
        $deadline = [DateTime]::UtcNow.AddMinutes(10)
        while (-not (Test-Path -LiteralPath $receiptPath)) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Installer did not confirm completion within ten minutes.' }
            Start-Sleep -Milliseconds 250
        }
        Assert-NoLinks $receiptPath
        $receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($receipt.Token -ne $config.Token -or $receipt.Version -ne $config.Version -or [int]$receipt.ExitCode -ne 0 -or
            -not [string]::Equals([string]$receipt.InstallRoot, [string]$config.InstallRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer failed or returned an unexpected receipt.' }
        $installer.Dispose()
    }
    finally { $locked.Dispose() }
    Confirm-AndRestart $config $receipt
    # Only these exact files belong to this session. Never recursively delete a caller path.
    foreach ($name in @([IO.Path]::GetFileName($expectedInstaller), 'SHA256SUMS.txt', 'install-result.json', 'update-session.json', 'update-helper.ps1')) {
        $owned = Join-Path $sessionRoot $name
        try { if (Test-Path -LiteralPath $owned) { Assert-NoLinks $owned; Remove-Item -LiteralPath $owned -Force } } catch { }
    }
    try { [IO.Directory]::Delete($sessionRoot, $false) } catch { }
}
catch {
    $message = $_.Exception.Message
    try { [IO.File]::WriteAllText($log, $message, (New-Object Text.UTF8Encoding($false))) } catch { }
    try {
        Add-Type -AssemblyName PresentationFramework
        $description = if ($config) { [string]$config.FailureMessage } else { 'MyCapture update could not be confirmed.' }
        [System.Windows.MessageBox]::Show($description + [Environment]::NewLine + $message + [Environment]::NewLine + $log, 'MyCapture', 'OK', 'Error') | Out-Null
    } catch { }
    exit 1
}
