# Fake process boundaries only. Does not install, stop, or start MyCapture.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repository 'src\MyCapture.App\Updates\UpdateHelper.ps1') -FunctionsOnly
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-helper-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:Starts = 0
$script:Dies = $false
$script:BinaryVersion = '1.8.0'
function Get-InstalledBinaryVersion([string]$Path) { return $script:BinaryVersion }
function Start-UpdateProcess($Info) {
    if ($Info.UseShellExecute -or $Info.FileName -ne (Join-Path $testRoot 'MyCapture.exe')) { throw 'Unsafe restart boundary.' }
    $script:Starts++
    $fake = New-Object PSObject
    $fake | Add-Member ScriptMethod WaitForExit { param($Milliseconds) return $script:Dies }
    $fake | Add-Member ScriptMethod Dispose { }
    return $fake
}
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected restart rejection.' }
}
try {
    $files = @()
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) {
        $path = Join-Path $testRoot $name
        [IO.File]::WriteAllText($path, 'synthetic executable, never launched')
        $files += @{ Path = $name; Bytes = (Get-Item -LiteralPath $path).Length; Sha256 = Get-Hash $path }
    }
    $manifest = @{ Product = 'MyCapture'; Version = '1.8.0'; Files = $files }
    $manifestPath = Join-Path $testRoot 'install-manifest.json'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $config = [pscustomobject]@{ Token = 'session-token'; Version = '1.8.0'; InstallRoot = $testRoot }
    $receipt = [pscustomobject]@{ Token = 'session-token'; Version = '1.8.0'; InstallRoot = $testRoot; ExitCode = 17 }
    $targetConfig = [pscustomobject]@{ TargetMode = 'OwnedInstall'; SourceRoot = $testRoot; InstallRoot = $testRoot }
    Assert-SessionTarget $targetConfig
    $targetConfig.SourceRoot = $testRoot + '-other'
    Assert-Rejected { Assert-SessionTarget $targetConfig }
    $targetConfig.SourceRoot = $testRoot
    $targetConfig.TargetMode = 'InstallToDefault'
    $targetConfig.InstallRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\MyCapture'
    Assert-SessionTarget $targetConfig
    $targetConfig.InstallRoot = $testRoot
    Assert-Rejected { Assert-SessionTarget $targetConfig }
    $targetConfig.TargetMode = 'OwnedInstall'
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    $receipt.ExitCode = 0
    $receipt.Token = 'other-token'
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    $receipt.Token = $config.Token
    $receipt.Version = '1.7.1'
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    $receipt.Version = $config.Version
    $receipt.InstallRoot = $testRoot + '-other'
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    $receipt.InstallRoot = $testRoot
    $manifest.Version = '1.7.1'
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    $manifest.Version = $config.Version
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $script:BinaryVersion = '1.7.1'
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    $script:BinaryVersion = '1.8.0'
    $binaryPath = Join-Path $testRoot 'MyCapture.dll'
    [IO.File]::AppendAllText($binaryPath, 'tamper')
    Assert-Rejected { Assert-SessionTarget $targetConfig }
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    [IO.File]::WriteAllText($binaryPath, 'synthetic executable, never launched')
    if ($script:Starts -ne 0) { throw 'An unconfirmed update reached restart.' }
    Confirm-AndRestart $config $receipt
    if ($script:Starts -ne 1) { throw 'Confirmed update did not restart.' }
    $script:Dies = $true
    Assert-Rejected { Confirm-AndRestart $config $receipt }
    if ($script:Starts -ne 2) { throw 'Early exit fake was not exercised.' }
    Write-Output 'PASS: 14 target, receipt, manifest, hash, version, and fake restart cases.'
}
finally {
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll', 'install-manifest.json')) { Remove-Item -LiteralPath (Join-Path $testRoot $name) -Force -ErrorAction SilentlyContinue }
    [IO.Directory]::Delete($testRoot, $false)
}
