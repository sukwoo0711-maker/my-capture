# Runs only the harness's preflight against synthetic files; never installs or starts MyCapture.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-test-common.ps1')
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$harness = Join-Path $PSScriptRoot 'update-restart-acceptance.ps1'
$powerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$parent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'MyCapture-restart-preflight-tests')).TrimEnd('\')
$root = [IO.Path]::GetFullPath((Join-Path $parent ([guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture root.' }
[IO.Directory]::CreateDirectory((Join-Path $root 'publish-win-x64')) | Out-Null
$sha = 'a' * 40
function Record([string]$Path, [string]$Name) {
    return @{ Path = $Name; Bytes = (Get-Item -LiteralPath $Path).Length; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Write-Json([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root $Name) -Encoding UTF8
}
function Write-Checksums {
    @('MyCapture-1.8.0-win-x64-setup.exe', 'MyCapture-1.8.0-win-x64-portable.zip', 'release-manifest.json') | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash, $_
    } | Set-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Encoding ASCII
}
function Invoke-Preflight([bool]$ExpectedSuccess, [string]$Commit = $sha) {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $powerShell -NoLogo -NoProfile -NonInteractive -File $harness -ArtifactRoot $root -ExpectedSourceCommit $Commit 2>&1
        $exit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $oldPreference }
    if (($exit -eq 0) -ne $ExpectedSuccess) { throw "Unexpected preflight exit ${exit}: $output" }
    if ($ExpectedSuccess -and (@($output) -join "`n") -notmatch 'PREFLIGHT PASS:') { throw 'Missing explicit preflight result.' }
}
try {
    $files = @()
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) {
        $file = Join-Path (Join-Path $root 'publish-win-x64') $name
        [IO.File]::WriteAllText($file, 'synthetic fixture, never executed')
        $files += Record $file $name
    }
    foreach ($name in @('MyCapture-1.8.0-win-x64-setup.exe', 'MyCapture-1.8.0-win-x64-portable.zip')) {
        [IO.File]::WriteAllText((Join-Path $root $name), 'synthetic package, never executed')
    }
    $bootstrap = @('MyCaptureSetup.exe', 'MyCaptureSetup.exe.config', 'SETUP-HELP.txt') | ForEach-Object {
        Record (Get-SetupBootstrapSource $_) $_
    }
    $manifest = @{ Product = 'MyCapture'; Version = '1.8.0'; Files = $files; BootstrapFiles = @($bootstrap)
        Payload = Record (Join-Path $root 'MyCapture-1.8.0-win-x64-portable.zip') 'payload.zip' }
    $release = @{ Product = 'MyCapture'; Version = '1.8.0'; SourceCommit = $sha; SourceTreeClean = $true }
    Write-Json 'installer-manifest.json' $manifest
    Write-Json 'release-manifest.json' $release
    Write-Checksums
    Invoke-Preflight $true
    Invoke-Preflight $false ('b' * 40)
    $release.SourceTreeClean = $false
    Write-Json 'release-manifest.json' $release
    Write-Checksums
    Invoke-Preflight $false
    $release.SourceTreeClean = $true
    Write-Json 'release-manifest.json' $release
    Write-Checksums
    $setup = Join-Path $root 'MyCapture-1.8.0-win-x64-setup.exe'
    [IO.File]::AppendAllText($setup, 'tamper')
    Invoke-Preflight $false
    [IO.File]::WriteAllText($setup, 'synthetic package, never executed')
    # A recomputed package checksum must not conceal a payload/manifest mismatch.
    $payload = Join-Path $root 'MyCapture-1.8.0-win-x64-portable.zip'
    [IO.File]::AppendAllText($payload, 'tamper')
    Write-Checksums
    Invoke-Preflight $false
    [IO.File]::WriteAllText($payload, 'synthetic package, never executed')
    Write-Checksums
    $binary = Join-Path (Join-Path $root 'publish-win-x64') 'MyCapture.dll'
    [IO.File]::AppendAllText($binary, 'tamper')
    Invoke-Preflight $false
    [IO.File]::WriteAllText($binary, 'synthetic fixture, never executed')
    $manifest.Files = @($files) + @($files[0])
    Write-Json 'installer-manifest.json' $manifest
    Invoke-Preflight $false
    $manifest.Files = @($files)
    $release.Version = '1.8.1'
    Write-Json 'release-manifest.json' $release
    Write-Checksums
    Write-Json 'installer-manifest.json' $manifest
    Invoke-Preflight $false
    $release.Version = '1.8.0'
    Write-Json 'release-manifest.json' $release
    Write-Checksums
    $manifest.BootstrapFiles[0].Sha256 = '0' * 64
    Write-Json 'installer-manifest.json' $manifest
    Invoke-Preflight $false
    $manifest.BootstrapFiles = @($bootstrap | ForEach-Object { Record (Get-SetupBootstrapSource $_.Path) $_.Path })
    Write-Json 'installer-manifest.json' $manifest
    Invoke-Preflight $true
    Write-Output 'PASS: valid preflight and wrong source, dirty source, tampered setup/payload/binary, duplicate inventory, version mismatch, and bootstrap mismatch rejection; no installation executed.'
}
finally {
    # Exact generated files only; no recursive caller-path deletion.
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) {
        Remove-TestItem -Path (Join-Path (Join-Path $root 'publish-win-x64') $name)
    }
    [IO.Directory]::Delete((Join-Path $root 'publish-win-x64'), $false)
    foreach ($name in @('MyCapture-1.8.0-win-x64-setup.exe', 'MyCapture-1.8.0-win-x64-portable.zip', 'installer-manifest.json', 'release-manifest.json', 'SHA256SUMS.txt')) {
        Remove-TestItem -Path (Join-Path $root $name)
    }
    [IO.Directory]::Delete($root, $false)
}
