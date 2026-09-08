# Runs only the harness's preflight against synthetic files; never installs or starts MyCapture.
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$harness = Join-Path $PSScriptRoot 'update-restart-acceptance.ps1'
$powerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$parent = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts\validation\restart-preflight-tests')).TrimEnd('\')
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
    $bootstrap = @('install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'uninstall-cleanup.ps1') | ForEach-Object {
        Record (Join-Path (Join-Path $repository 'build\installer') $_) $_
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
    $manifest.BootstrapFiles[0].Sha256 = '0' * 64
    Write-Json 'installer-manifest.json' $manifest
    Invoke-Preflight $false
    Write-Output 'PASS: valid preflight and wrong source, dirty source, tampered setup, and bootstrap mismatch rejection; no installation executed.'
}
finally {
    # Exact generated files only; no recursive caller-path deletion.
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) {
        Remove-Item -LiteralPath (Join-Path (Join-Path $root 'publish-win-x64') $name) -Force -ErrorAction SilentlyContinue
    }
    [IO.Directory]::Delete((Join-Path $root 'publish-win-x64'), $false)
    foreach ($name in @('MyCapture-1.8.0-win-x64-setup.exe', 'MyCapture-1.8.0-win-x64-portable.zip', 'installer-manifest.json', 'release-manifest.json', 'SHA256SUMS.txt')) {
        Remove-Item -LiteralPath (Join-Path $root $name) -Force -ErrorAction SilentlyContinue
    }
    [IO.Directory]::Delete($root, $false)
}
