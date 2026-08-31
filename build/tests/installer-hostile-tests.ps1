[CmdletBinding()]
param(
    [string]$Version = '1.1.0',
    [string]$ArtifactRoot = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$semVerPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?$'
$versionMatch = [regex]::Match($Version, $semVerPattern)
if (-not $versionMatch.Success) {
    throw "Version must use a SemVer core with an optional prerelease (for example 1.1.0 or 1.1.0-rc.1): $Version"
}
$baseVersion = '{0}.{1}.{2}' -f $versionMatch.Groups['major'].Value, $versionMatch.Groups['minor'].Value, $versionMatch.Groups['patch'].Value
$binaryVersion = "$baseVersion.0"

$repo = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot))).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repo "artifacts\release\$Version"
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd('\')
$installerScript = Join-Path $repo 'build\installer\install.ps1'
$powerShellExe = Join-Path $PSHOME 'powershell.exe'
$payload = Join-Path $ArtifactRoot "MyCapture-$Version-win-x64-portable.zip"
$manifestPath = Join-Path $ArtifactRoot 'installer-manifest.json'
$releaseManifestPath = Join-Path $ArtifactRoot 'release-manifest.json'
$publish = Join-Path $ArtifactRoot 'publish-win-x64'
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-installer-tests-' + [guid]::NewGuid().ToString('N'))
$script:PassCount = 0
$script:InvocationCount = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -ne $Actual) { throw "$Message Expected=$Expected Actual=$Actual" }
}

function Write-Pass([string]$Name, [string]$Details) {
    $script:PassCount++
    Write-Output ("PASS {0}: {1}" -f $Name, $Details)
}

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-StreamSha256([IO.Stream]$Stream) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Stream))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Invoke-Installer(
    [string]$InstallRoot,
    [string]$PayloadPath,
    [string]$ManifestFile,
    [string[]]$ExtraArguments)
{
    $script:InvocationCount++
    $log = Join-Path $tempRoot ("install-{0:00}.log" -f $script:InvocationCount)
    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $installerScript,
        '-Quiet', '-NoShellIntegration',
        '-InstallRoot', $InstallRoot,
        '-PayloadPath', $PayloadPath,
        '-ManifestPath', $ManifestFile,
        '-LogPath', $log
    )
    $arguments += $ExtraArguments
    $null = & $powerShellExe @arguments 2>&1
    return [int]$LASTEXITCODE
}

function Invoke-Uninstaller([string]$ScriptPath, [string]$LogPath) {
    $arguments = @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', $ScriptPath, '-Quiet', '-LogPath', $LogPath
    )
    $null = & $powerShellExe @arguments 2>&1
    return [int]$LASTEXITCODE
}

function New-ZipWithEntries([string]$Path, [string[]]$Names) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $file = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = New-Object IO.Compression.ZipArchive($file, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($name in $Names) {
            $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            try {
                [byte[]]$bytes = [Text.Encoding]::UTF8.GetBytes("hostile-$name")
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $archive.Dispose(); $file.Dispose() }
}

function New-ManifestForPayload([string]$PayloadPath, [string]$Destination) {
    $copy = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $item = Get-Item -LiteralPath $PayloadPath
    $copy.Payload.Path = [IO.Path]::GetFileName($PayloadPath)
    $copy.Payload.Bytes = [long]$item.Length
    $copy.Payload.Sha256 = Get-Sha256 $PayloadPath
    $copy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Destination -Encoding UTF8
}

function Get-RunValue {
    try {
        $value = (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name MyCapture -ErrorAction Stop).MyCapture
        return [string]$value
    }
    catch { return '<missing>' }
}

function Assert-NoTransactionArtifacts {
    $leftovers = @(Get-ChildItem -LiteralPath $tempRoot -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like '.MyCapture.stage.*' -or $_.Name -like '.MyCapture.backup.*' })
    Assert-Equal 0 $leftovers.Count 'Installer left a transaction directory behind.'
}

Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

foreach ($required in @($installerScript, $powerShellExe, $payload, $manifestPath, $publish)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required test input is missing: $required" }
}
[IO.Directory]::CreateDirectory($tempRoot) | Out-Null

$initialRunValue = Get-RunValue
# Build the Korean path segments from code points so Windows PowerShell 5.1 can
# read this UTF-8 source safely even when the file has no byte-order mark.
$installSegment = (-join [char[]](0xC124, 0xCE58)) + ' ' + (-join [char[]](0xACBD, 0xB85C)) + " $Version"
$userDataSegment = (-join [char[]](0xC0AC, 0xC6A9, 0xC790)) + ' ' + (-join [char[]](0xB370, 0xC774, 0xD130))
$installRoot = Join-Path $tempRoot $installSegment
$userDataRoot = Join-Path (Join-Path $tempRoot $userDataSegment) 'captures'
[IO.Directory]::CreateDirectory($userDataRoot) | Out-Null
$userDataSentinel = Join-Path $userDataRoot 'preserve-me.bin'
[IO.File]::WriteAllBytes($userDataSentinel, [byte[]](0..255))
$userDataHash = Get-Sha256 $userDataSentinel

try {
    # Contract: emitted manifest, publish tree, ZIP, bootstrap files, and source versions agree.
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert-Equal 1 ([int]$manifest.SchemaVersion) 'Unexpected installer manifest schema.'
    Assert-Equal 'MyCapture' ([string]$manifest.Product) 'Unexpected installer manifest product.'
    Assert-Equal $Version ([string]$manifest.Version) 'Installer manifest version mismatch.'
    Assert-Equal 'win-x64' ([string]$manifest.Runtime) 'Runtime mismatch.'
    Assert-True ([bool]$manifest.SelfContained) 'Manifest is not self-contained.'
    Assert-True ([bool]$manifest.Offline) 'Manifest is not offline.'

    $actualFiles = @{}
    Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($publish.Length + 1).Replace('\', '/')
        if ($actualFiles.ContainsKey($relative)) { throw "Duplicate publish path: $relative" }
        $actualFiles[$relative] = [pscustomobject]@{ Bytes = [long]$_.Length; Sha256 = Get-Sha256 $_.FullName }
    }
    Assert-Equal ([int]$manifest.FileCount) $actualFiles.Count 'Publish file count mismatch.'
    foreach ($file in @($manifest.Files)) {
        Assert-True ($actualFiles.ContainsKey([string]$file.Path)) "Manifest path missing from publish: $($file.Path)"
        $actual = $actualFiles[[string]$file.Path]
        Assert-Equal ([long]$file.Bytes) ([long]$actual.Bytes) "Publish size mismatch: $($file.Path)"
        Assert-Equal ([string]$file.Sha256) ([string]$actual.Sha256) "Publish hash mismatch: $($file.Path)"
    }

    $zipStream = [IO.File]::OpenRead($payload)
    $zip = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $seen = @{}
        foreach ($entry in $zip.Entries) {
            Assert-True (-not $entry.FullName.Contains('\')) "ZIP uses a backslash path: $($entry.FullName)"
            Assert-True (-not $seen.ContainsKey($entry.FullName)) "Duplicate ZIP path: $($entry.FullName)"
            Assert-True ($actualFiles.ContainsKey($entry.FullName)) "Unmanifested ZIP path: $($entry.FullName)"
            $seen[$entry.FullName] = $true
            $actual = $actualFiles[$entry.FullName]
            Assert-Equal ([long]$actual.Bytes) ([long]$entry.Length) "ZIP size mismatch: $($entry.FullName)"
            $entryStream = $entry.Open()
            try { $entryHash = Get-StreamSha256 $entryStream }
            finally { $entryStream.Dispose() }
            Assert-Equal ([string]$actual.Sha256) $entryHash "ZIP hash mismatch: $($entry.FullName)"
        }
        Assert-Equal $actualFiles.Count $seen.Count 'ZIP file count mismatch.'
    }
    finally { $zip.Dispose(); $zipStream.Dispose() }

    Assert-Equal (Get-Item -LiteralPath $payload).Length ([long]$manifest.Payload.Bytes) 'Payload bytes mismatch.'
    Assert-Equal (Get-Sha256 $payload) ([string]$manifest.Payload.Sha256) 'Payload hash mismatch.'
    foreach ($bootstrap in @($manifest.BootstrapFiles)) {
        $source = Join-Path $repo "build\installer\$($bootstrap.Path)"
        Assert-True (Test-Path -LiteralPath $source -PathType Leaf) "Bootstrap file missing: $($bootstrap.Path)"
        Assert-Equal ([long]$bootstrap.Bytes) (Get-Item -LiteralPath $source).Length "Bootstrap size mismatch: $($bootstrap.Path)"
        Assert-Equal ([string]$bootstrap.Sha256) (Get-Sha256 $source) "Bootstrap hash mismatch: $($bootstrap.Path)"
    }
    Assert-Equal 5 @($manifest.BootstrapFiles).Count 'Bootstrap file count mismatch.'

    [xml]$props = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
    $sourceVersion = [string]$props.SelectSingleNode('//Version').InnerText
    $sourceFileVersion = [string]$props.SelectSingleNode('//FileVersion').InnerText
    $appManifestText = Get-Content -LiteralPath (Join-Path $repo 'src\MyCapture.App\app.manifest') -Raw
    $dllVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'MyCapture.dll'))
    Assert-True (Test-Path -LiteralPath $releaseManifestPath -PathType Leaf) 'Release manifest is missing.'
    $releaseManifest = Get-Content -LiteralPath $releaseManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    $sourceCommit = [string]$releaseManifest.SourceCommit
    Assert-Equal $baseVersion $sourceVersion 'Directory.Build.props product version mismatch.'
    Assert-Equal $binaryVersion $sourceFileVersion 'Directory.Build.props file version mismatch.'
    Assert-True ($appManifestText -match ('assemblyIdentity version="' + [regex]::Escape($binaryVersion) + '"')) 'Embedded manifest source version mismatch.'
    Assert-Equal $Version (($dllVersionInfo.ProductVersion -split '\+')[0]) 'Published ProductVersion mismatch.'
    Assert-Equal $binaryVersion $dllVersionInfo.FileVersion 'Published FileVersion mismatch.'
    Assert-Equal 3 ([int]$releaseManifest.SchemaVersion) 'Release manifest schema mismatch.'
    Assert-True ($sourceCommit -cmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') 'Release source commit is not canonical lowercase hex.'
    Assert-Equal $true ([bool]$releaseManifest.SourceTreeClean) 'Release source tree was not clean.'
    Assert-Equal "$Version+$sourceCommit" $dllVersionInfo.ProductVersion 'Binary informational version does not match release source commit.'
    Write-Pass 'package-contract' "files=$($actualFiles.Count), bootstrap=5, forward-slash ZIP, version=$Version, commit=$sourceCommit"

    # VerifyOnly must fully hash/extract/validate while leaving no installation root.
    $verifyRoot = Join-Path $tempRoot 'verify-only'
    $code = Invoke-Installer $verifyRoot $payload $manifestPath @('-VerifyOnly')
    Assert-Equal 0 $code 'VerifyOnly should succeed.'
    Assert-True (-not (Test-Path -LiteralPath $verifyRoot)) 'VerifyOnly created an install root.'
    Assert-NoTransactionArtifacts
    Write-Pass 'verify-only' 'exit=0 and no install state'

    # Corruption must fail before extraction.
    $corruptPayload = Join-Path $tempRoot 'corrupt.zip'
    Copy-Item -LiteralPath $payload -Destination $corruptPayload
    $corruptStream = [IO.File]::Open($corruptPayload, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $corruptStream.Position = [Math]::Max(0, $corruptStream.Length - 17)
        $original = $corruptStream.ReadByte()
        $corruptStream.Position--
        $corruptStream.WriteByte([byte](($original -bxor 0x5A) -band 0xFF))
    }
    finally { $corruptStream.Dispose() }
    $code = Invoke-Installer (Join-Path $tempRoot 'corrupt-root') $corruptPayload $manifestPath @('-VerifyOnly')
    Assert-Equal 14 $code 'Corrupt payload should use integrity exit 14.'
    Write-Pass 'corrupt-payload' 'exit=14'

    # A traversal entry is rejected even when the outer payload hash is valid.
    $traversalZip = Join-Path $tempRoot 'traversal.zip'
    $traversalManifest = Join-Path $tempRoot 'traversal-manifest.json'
    New-ZipWithEntries $traversalZip @('../outside.txt')
    New-ManifestForPayload $traversalZip $traversalManifest
    $code = Invoke-Installer (Join-Path $tempRoot 'traversal-root') $traversalZip $traversalManifest @('-VerifyOnly')
    Assert-Equal 14 $code 'Traversal ZIP should use integrity exit 14.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $tempRoot 'outside.txt'))) 'Traversal wrote outside staging.'
    Write-Pass 'zip-traversal' 'exit=14 and no escaped file'

    # Windows extraction is case-insensitive, so case-only duplicate entries are rejected.
    $duplicateZip = Join-Path $tempRoot 'duplicate.zip'
    $duplicateManifest = Join-Path $tempRoot 'duplicate-manifest.json'
    New-ZipWithEntries $duplicateZip @('MyCapture.dll', 'mycapture.dll')
    New-ManifestForPayload $duplicateZip $duplicateManifest
    $code = Invoke-Installer (Join-Path $tempRoot 'duplicate-root') $duplicateZip $duplicateManifest @('-VerifyOnly')
    Assert-Equal 14 $code 'Duplicate ZIP should use integrity exit 14.'
    Write-Pass 'zip-duplicate' 'case-insensitive duplicate rejected with exit=14'

    # An unrelated populated directory must never be claimed or deleted.
    $unownedRoot = Join-Path $tempRoot 'unowned-existing'
    [IO.Directory]::CreateDirectory($unownedRoot) | Out-Null
    $unownedSentinel = Join-Path $unownedRoot 'keep.txt'
    [IO.File]::WriteAllText($unownedSentinel, 'do-not-touch')
    $code = Invoke-Installer $unownedRoot $payload $manifestPath @('-VerifyOnly')
    Assert-Equal 20 $code 'Unowned populated root should use exit 20.'
    Assert-Equal 'do-not-touch' ([IO.File]::ReadAllText($unownedSentinel)) 'Unowned root was modified.'
    Write-Pass 'unowned-root' 'exit=20 and sentinel preserved'

    # MinimumFreeBytes provides a deterministic insufficient-space path without filling a disk.
    $code = Invoke-Installer (Join-Path $tempRoot 'disk-root') $payload $manifestPath @('-VerifyOnly', '-MinimumFreeBytes', '9223372036854775807')
    Assert-Equal 13 $code 'Insufficient disk preflight should use exit 13.'
    Write-Pass 'disk-preflight' 'exit=13'

    # Installer mutex must reject a concurrent operation immediately.
    $mutex = New-Object Threading.Mutex($false, 'Local\MyCapture.InstallOrUninstall')
    $mutexHeld = $false
    try {
        try { $mutexHeld = $mutex.WaitOne(0, $false) }
        catch [Threading.AbandonedMutexException] { $mutexHeld = $true }
        Assert-True $mutexHeld 'Test could not acquire installer mutex.'
        $code = Invoke-Installer (Join-Path $tempRoot 'mutex-root') $payload $manifestPath @('-VerifyOnly')
        Assert-Equal 18 $code 'Concurrent installer should use exit 18.'
    }
    finally {
        if ($mutexHeld) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
    Write-Pass 'installer-mutex' 'exit=18'

    # Install to a Unicode path, then prove both injected failure points restore the old tree.
    $code = Invoke-Installer $installRoot $payload $manifestPath @()
    Assert-Equal 0 $code 'Initial Unicode-path install failed.'
    Assert-True (Test-Path -LiteralPath (Join-Path $installRoot 'MyCapture.exe')) 'Installed executable missing.'
    $rollbackSentinel = Join-Path $installRoot 'rollback-sentinel.bin'
    [IO.File]::WriteAllBytes($rollbackSentinel, [byte[]](255..0))
    $rollbackHash = Get-Sha256 $rollbackSentinel
    Write-Pass 'unicode-install' "exit=0 root=$installRoot"

    foreach ($fault in @('AfterBackup', 'AfterCommit')) {
        $code = Invoke-Installer $installRoot $payload $manifestPath @('-TestFault', $fault)
        Assert-Equal 17 $code "$fault should use commit/rollback exit 17."
        Assert-True (Test-Path -LiteralPath $rollbackSentinel -PathType Leaf) "$fault did not restore the previous installation."
        Assert-Equal $rollbackHash (Get-Sha256 $rollbackSentinel) "$fault changed the previous installation sentinel."
        Assert-True (Test-Path -LiteralPath (Join-Path $installRoot 'MyCapture.exe')) "$fault left no usable installation."
        Assert-NoTransactionArtifacts
        Write-Pass ("rollback-" + $fault.ToLowerInvariant()) 'exit=17 and previous tree restored exactly'
    }

    # Uninstaller mutex taxonomy is separate (31), then the normal deferred cleanup removes only
    # the verified install root while preserving external captures/settings data and Run state.
    $installedUninstaller = Join-Path $installRoot 'uninstall.ps1'
    $mutex = New-Object Threading.Mutex($false, 'Local\MyCapture.InstallOrUninstall')
    $mutexHeld = $false
    try {
        try { $mutexHeld = $mutex.WaitOne(0, $false) }
        catch [Threading.AbandonedMutexException] { $mutexHeld = $true }
        Assert-True $mutexHeld 'Test could not acquire uninstaller mutex.'
        $code = Invoke-Uninstaller $installedUninstaller (Join-Path $tempRoot 'uninstall-concurrent.log')
        Assert-Equal 31 $code 'Concurrent uninstaller should use exit 31.'
    }
    finally {
        if ($mutexHeld) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
    Write-Pass 'uninstaller-mutex' 'exit=31'

    $code = Invoke-Uninstaller $installedUninstaller (Join-Path $tempRoot 'uninstall.log')
    Assert-Equal 0 $code 'Verified uninstall launch failed.'
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ((Test-Path -LiteralPath $installRoot) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) 'Deferred cleanup did not remove the install root.'
    Assert-True (Test-Path -LiteralPath $userDataSentinel -PathType Leaf) 'External captures/settings sentinel was removed.'
    Assert-Equal $userDataHash (Get-Sha256 $userDataSentinel) 'External captures/settings sentinel changed.'
    Assert-Equal $initialRunValue (Get-RunValue) 'Run-key state changed during NoShellIntegration lifecycle.'
    Assert-NoTransactionArtifacts
    Write-Pass 'safe-uninstall' 'verified root removed; external data and Run state preserved'

    Write-Output "INSTALLER_HOSTILE_TESTS=PASS PASSES=$($script:PassCount) INVOCATIONS=$($script:InvocationCount)"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
