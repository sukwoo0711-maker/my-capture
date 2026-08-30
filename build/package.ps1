[CmdletBinding()]
param(
    [string]$Version = '0.4.0'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use numeric SemVer form (for example 0.4.0): $Version"
}

$binaryVersion = "$Version.0"
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\release')).TrimEnd('\')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot $Version)).TrimEnd('\')
if (-not $artifactRoot.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals((Split-Path -Leaf $artifactRoot), $Version, [StringComparison]::Ordinal)) {
    throw "Refusing to clean an unsafe artifact path: $artifactRoot"
}

$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path -LiteralPath (Join-Path $dotnetDir 'dotnet.exe')) {
    $env:Path = "$env:Path;$dotnetDir"
}
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$project = Join-Path $repo 'src\MyCapture.App\MyCapture.App.csproj'
$publish = Join-Path $artifactRoot 'publish-win-x64'
$stage = Join-Path $artifactRoot 'installer-stage'
$portable = Join-Path $artifactRoot "MyCapture-$Version-win-x64-portable.zip"
$setup = Join-Path $artifactRoot "MyCapture-$Version-win-x64-setup.exe"
$installerManifestOutput = Join-Path $artifactRoot 'installer-manifest.json'
$releaseManifestOutput = Join-Path $artifactRoot 'release-manifest.json'
$shaSumsOutput = Join-Path $artifactRoot 'SHA256SUMS.txt'
$readmeOutput = Join-Path $artifactRoot 'README-OFFLINE.txt'

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

function Get-RelativePackagePath([string]$Root, [string]$FullPath) {
    $relative = $FullPath.Substring($Root.Length + 1).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative.Contains('\')) {
        throw "Unable to create a safe package-relative path for: $FullPath"
    }
    foreach ($segment in $relative.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            throw "Unsafe package-relative path generated: $relative"
        }
    }
    return $relative
}

function New-FileRecord([string]$Path, [string]$RelativePath) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    return [pscustomobject][ordered]@{
        Path = $RelativePath
        Bytes = [long]$item.Length
        Sha256 = Get-Sha256 $item.FullName
    }
}

function New-InventoryZip([string]$SourceRoot, [string]$ZipPath, [object[]]$Inventory) {
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $zipStream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($record in $Inventory) {
            $entryName = [string]$record.Path
            if ($entryName.Contains('\')) { throw "Inventory path is not ZIP-normalized: $entryName" }
            $sourcePath = Join-Path $SourceRoot $entryName.Replace('/', '\')
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $sourceStream = [IO.File]::Open($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $entryStream = $entry.Open()
            try { $sourceStream.CopyTo($entryStream) }
            finally { $entryStream.Dispose(); $sourceStream.Dispose() }
        }
    }
    finally { $archive.Dispose(); $zipStream.Dispose() }
}

function Assert-ZipMatchesInventory([string]$ZipPath, [object[]]$Inventory) {
    $expected = @{}
    foreach ($record in $Inventory) {
        $path = [string]$record.Path
        if ($expected.ContainsKey($path)) { throw "Duplicate inventory path: $path" }
        $expected[$path] = $record
    }

    $stream = [IO.File]::Open($ZipPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $seen = @{}
        foreach ($entry in $archive.Entries) {
            $name = [string]$entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\') -or $name.StartsWith('/') -or $name.EndsWith('/')) {
                throw "ZIP contains a non-file or unsafe entry: $name"
            }
            foreach ($segment in $name.Split('/')) {
                if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
                    throw "ZIP contains an unsafe path segment: $name"
                }
            }
            if ($seen.ContainsKey($name)) { throw "ZIP contains a duplicate path: $name" }
            if (-not $expected.ContainsKey($name)) { throw "ZIP contains an unmanifested file: $name" }
            $seen[$name] = $true
            $record = $expected[$name]
            if ([long]$entry.Length -ne [long]$record.Bytes) {
                throw "ZIP size mismatch for ${name}: expected $($record.Bytes), found $($entry.Length)"
            }
            $entryStream = $entry.Open()
            try { $entryHash = Get-StreamSha256 $entryStream }
            finally { $entryStream.Dispose() }
            if (-not [string]::Equals($entryHash, [string]$record.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
                throw "ZIP SHA-256 mismatch for: $name"
            }
        }
        if ($seen.Count -ne $expected.Count) {
            $missing = @($expected.Keys | Where-Object { -not $seen.ContainsKey($_) } | Sort-Object)
            throw "ZIP inventory is incomplete. Missing: $($missing -join ', ')"
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
}

Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop

if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force -ErrorAction Stop
}
[IO.Directory]::CreateDirectory($publish) | Out-Null
[IO.Directory]::CreateDirectory($stage) | Out-Null

try {
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
        -p:Version=$Version -p:FileVersion=$binaryVersion -p:AssemblyVersion=$binaryVersion `
        -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

    $exe = Join-Path $publish 'MyCapture.exe'
    $dll = Join-Path $publish 'MyCapture.dll'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $dll)) {
        throw 'Published MyCapture.exe or MyCapture.dll is missing.'
    }
    foreach ($asset in @('tray-idle.ico', 'tray-capturing.ico', 'tray-busy.ico')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish "Assets\$asset"))) {
            throw "Published asset missing: Assets\$asset"
        }
    }

    $inventory = @(
        Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
            New-FileRecord $_.FullName (Get-RelativePackagePath $publish $_.FullName)
        } | Sort-Object -Property Path
    )
    if ($inventory.Count -lt 1 -or $inventory.Count -gt 10000) {
        throw "Published file count is outside the installer safety contract: $($inventory.Count)"
    }
    [long]$totalExpandedBytes = 0
    foreach ($record in $inventory) {
        $totalExpandedBytes += [long]$record.Bytes
        if ($totalExpandedBytes -gt 2147483648) { throw 'Published payload exceeds the 2 GiB installer safety limit.' }
    }

    New-InventoryZip $publish $portable $inventory
    Assert-ZipMatchesInventory $portable $inventory

    $payload = Join-Path $stage 'payload.zip'
    Copy-Item -LiteralPath $portable -Destination $payload -Force

    $bootstrapNames = @(
        'install.ps1',
        'install.cmd',
        'uninstall.ps1',
        'uninstall.cmd',
        'uninstall-cleanup.ps1'
    )
    $bootstrapInventory = @()
    foreach ($name in $bootstrapNames) {
        $sourcePath = Join-Path $PSScriptRoot "installer\$name"
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Installer bootstrap file is missing: $name" }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $stage $name) -Force
        $bootstrapInventory += New-FileRecord $sourcePath $name
    }

    $payloadItem = Get-Item -LiteralPath $payload
    $installerManifest = [pscustomobject][ordered]@{
        SchemaVersion = 1
        Product = 'MyCapture'
        Version = $Version
        Runtime = 'win-x64'
        Architecture = 'x64'
        MinimumWindowsBuild = 17763
        SelfContained = $true
        Offline = $true
        Payload = [pscustomobject][ordered]@{
            Path = 'payload.zip'
            Bytes = [long]$payloadItem.Length
            Sha256 = Get-Sha256 $payload
        }
        FileCount = [int]$inventory.Count
        TotalExpandedBytes = $totalExpandedBytes
        Files = $inventory
        BootstrapFiles = $bootstrapInventory
    }

    $stageManifest = Join-Path $stage 'installer-manifest.json'
    $installerManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stageManifest -Encoding UTF8
    Copy-Item -LiteralPath $stageManifest -Destination $installerManifestOutput -Force

    # Re-read the emitted JSON so serialization failures cannot be hidden by the in-memory object.
    $roundTrip = Get-Content -LiteralPath $stageManifest -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    if ([int]$roundTrip.SchemaVersion -ne 1 -or [string]$roundTrip.Version -ne $Version -or
        [int]$roundTrip.FileCount -ne $inventory.Count -or @($roundTrip.BootstrapFiles).Count -ne $bootstrapNames.Count) {
        throw 'Installer manifest failed its serialization round-trip contract.'
    }
    if ((Get-Item -LiteralPath $payload).Length -ne [long]$roundTrip.Payload.Bytes -or
        -not [string]::Equals((Get-Sha256 $payload), [string]$roundTrip.Payload.Sha256, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installer manifest payload record does not match payload.zip.'
    }

    $sed = Join-Path $stage 'package.sed'
    $source = $stage.TrimEnd('\') + '\'
    @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$setup
FriendlyName=MyCapture $Version Offline Setup
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=install.cmd /quiet
UserQuietInstCmd=install.cmd /quiet
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$source
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
%FILE4%=
%FILE5%=
%FILE6%=
[Strings]
FILE0=payload.zip
FILE1=installer-manifest.json
FILE2=install.ps1
FILE3=install.cmd
FILE4=uninstall.ps1
FILE5=uninstall.cmd
FILE6=uninstall-cleanup.ps1
"@ | Set-Content -LiteralPath $sed -Encoding ASCII

    $iexpressPath = Join-Path $env:SystemRoot 'System32\iexpress.exe'
    if (-not (Test-Path -LiteralPath $iexpressPath -PathType Leaf)) { throw "IExpress was not found: $iexpressPath" }
    $iexpress = Start-Process -FilePath $iexpressPath -ArgumentList '/N', '/Q', $sed -Wait -PassThru
    if ($iexpress.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $setup -PathType Leaf)) {
        throw "IExpress packaging failed: $($iexpress.ExitCode)"
    }

    $deliverables = @($setup, $portable) | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [pscustomobject][ordered]@{
            Path = $item.Name
            Bytes = [long]$item.Length
            Sha256 = Get-Sha256 $item.FullName
        }
    }

    $releaseManifest = [pscustomobject][ordered]@{
        SchemaVersion = 2
        Product = 'MyCapture'
        Version = $Version
        Runtime = 'win-x64'
        Architecture = 'x64'
        SelfContained = $true
        Offline = $true
        Unsigned = $true
        GeneratedUtc = [DateTime]::UtcNow.ToString('O')
        Compatibility = [pscustomobject][ordered]@{
            MinimumWindowsBuild = 17763
            MinimumWindowsRelease = 'Windows 10 version 1809'
            NativeArchitecture = 'x64'
            Arm64Support = 'Windows 11 x64 emulation'
            RequiresInternet = $false
            RequiresPreinstalledDotNet = $false
            SetupRequires = 'Windows PowerShell 5.1 FullLanguage and IExpress execution permitted by local policy'
            PortableFallback = $true
        }
        Installer = [pscustomobject][ordered]@{
            Format = 'IExpress self-extracting package'
            Manifest = 'installer-manifest.json'
            Payload = $installerManifest.Payload
            PayloadIntegrity = 'SHA-256 plus exact path, size, and file hash inventory'
            TransactionalInstall = $true
            Rollback = $true
            ConcurrentOperationMutex = 'Local\MyCapture.InstallOrUninstall'
            ZipTraversalProtection = $true
            BootstrapFileCount = $bootstrapNames.Count
        }
        Deliverables = $deliverables
        PublishInventory = $inventory
    }
    $releaseManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releaseManifestOutput -Encoding UTF8

    $sumLines = @($deliverables | ForEach-Object { "$($_.Sha256)  $($_.Path)" })
    $sumLines | Set-Content -LiteralPath $shaSumsOutput -Encoding ASCII

    @"
MyCapture $Version - OFFLINE RELEASE (win-x64)
================================================

CONTENTS
- MyCapture-$Version-win-x64-setup.exe
  Per-user transactional installer. Verifies payload and bootstrap SHA-256 hashes,
  rejects unsafe ZIP paths, and rolls back a failed update.
- MyCapture-$Version-win-x64-portable.zip
  Fully self-contained fallback. Extract to a normal local folder and run MyCapture.exe.

OFFLINE / RUNTIME
- No Internet connection is required.
- No preinstalled .NET runtime is required; CoreCLR and WPF are included.
- Supported baseline: Windows 10 version 1809 (build 17763) or later, x64.
- Windows 11 ARM64 can use Windows x64 emulation.
- Setup needs Windows PowerShell 5.1 FullLanguage and permission to execute IExpress/
  PowerShell scripts. If enterprise AppLocker or WDAC policy blocks that path, use the
  portable ZIP; the setup cannot and does not bypass local security policy.

INTEGRITY AND SIGNATURE NOTICE
- This release is NOT Authenticode-signed. Windows may show an unknown-publisher warning.
- Verify the SHA-256 values in SHA256SUMS.txt through a trusted distribution channel.
- SHA-256 detects accidental or malicious file changes, but without a signing certificate
  it does not independently prove publisher authenticity.

USER DATA
- Captures and settings under the user's application-data folders are not part of the
  installation payload and are preserved during update and uninstall.
"@ | Set-Content -LiteralPath $readmeOutput -Encoding UTF8

    Write-Output "SETUP=$setup"
    Write-Output "PORTABLE=$portable"
    Write-Output "FILES=$($inventory.Count)"
    Write-Output "INSTALLER_MANIFEST=$installerManifestOutput"
    Write-Output "RELEASE_MANIFEST=$releaseManifestOutput"
    Write-Output "SHA256SUMS=$shaSumsOutput"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
