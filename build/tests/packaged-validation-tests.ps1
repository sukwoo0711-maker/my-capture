[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-test-common.ps1')
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'build\packaged-validation.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-pct-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
$previousData = $env:MYCAPTURE_DATA_ROOT
$env:MYCAPTURE_DATA_ROOT = Join-Path $fixture 'isolated-data'
try {
$runName = '20261012T000000Z-' + [guid]::NewGuid().ToString('N')
$publish = Join-Path $fixture 'artifacts\publish-win-x64'
$run = Join-Path $fixture "artifacts\packaged-self-tests\$runName"
[IO.Directory]::CreateDirectory($publish) | Out-Null
[IO.Directory]::CreateDirectory($run) | Out-Null
$checks = 0
function Expect-Rejection([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Gate accepted invalid fixture: $Name" }
    $script:checks++
    Write-Host "PASS rejection: $Name"
}
function Write-Result($Result) {
    $Result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $run 'result.json') -Encoding UTF8
}
$tests = @(Get-PackagedSelfTests)
if ($tests.Count -ne 8 -or @($tests | Select-Object -ExpandProperty Name -Unique).Count -ne 8 -or $tests[7].Name -cne 'hotfix-ux') {
    throw 'The mandatory eight-test release gate changed.'
}
$records = @($tests | ForEach-Object {
    $dir = Join-Path $run $_.Name
    [IO.Directory]::CreateDirectory($dir) | Out-Null
    $path = Join-Path $dir $_.Report
    'RESULT: PASS' | Set-Content -LiteralPath $path -Encoding UTF8
    [pscustomobject]@{ Name = $_.Name; Path = "$($_.Name)/$($_.Report)"; ExitCode = 0; Result = 'RESULT: PASS'; Sha256 = (Get-FileHash -LiteralPath $path).Hash }
})
$result = [pscustomobject]@{ SchemaVersion = 1; Result = 'PASS'; PassCount = 8; PublishRoot = $publish; Tests = $records }
Write-Result $result
$null = Assert-PackagedSelfTestResult $run
$checks++
$result.Tests = @($records | Select-Object -First 7)
Write-Result $result
Expect-Rejection 'missing hotfix-ux' { Assert-PackagedSelfTestResult $run }
$result.Tests = $records
$records[7].ExitCode = 1
Write-Result $result
Expect-Rejection 'nonzero process exit with PASS report' { Assert-PackagedSelfTestResult $run }
$records[7].ExitCode = $null
Write-Result $result
Expect-Rejection 'unobserved process exit' { Assert-PackagedSelfTestResult $run }
$records[7].ExitCode = 0
Write-Result $result
$report = Join-Path $run $records[7].Path
@('RESULT: PASS', 'RESULT: PASS') | Set-Content -LiteralPath $report -Encoding UTF8
Expect-Rejection 'duplicate RESULT lines' { Assert-PackagedSelfTestResult $run }
'RESULT: FAIL' | Set-Content -LiteralPath $report -Encoding UTF8
Expect-Rejection 'changed report' { Assert-PackagedSelfTestResult $run }
'RESULT: PASS' | Set-Content -LiteralPath $report -Encoding UTF8

# A local sparse clone provides real clean-source Git checks without staging or committing.
# Its object database reads the existing local repository; no network or source mutation occurs.
$sourceRepo = Join-Path $fixture 'source'
& git clone --quiet --shared --no-checkout $repo $sourceRepo
if ($LASTEXITCODE -ne 0) { throw 'Fixture local clone failed.' }
& git -C $sourceRepo config core.sparseCheckout true
if ($LASTEXITCODE -ne 0) { throw 'Fixture sparse configuration failed.' }
[IO.File]::WriteAllText((Join-Path $sourceRepo '.git\info\sparse-checkout'), "/README.md`n", [Text.Encoding]::ASCII)
& git -C $sourceRepo read-tree -mu HEAD
if ($LASTEXITCODE -ne 0) { throw 'Fixture sparse checkout failed.' }
$head = (& git -C $sourceRepo rev-parse HEAD | Out-String).Trim()
'synthetic payload' | Set-Content -LiteralPath (Join-Path $publish 'MyCapture.exe') -Encoding ASCII
$payload = Get-Item -LiteralPath (Join-Path $publish 'MyCapture.exe')
$manifest = [pscustomobject]@{
    SchemaVersion = 3; Product = 'MyCapture'; Version = '4.1.0'; SourceCommit = $head; SourceTreeClean = $true
    Runtime = 'win-x64'; Architecture = 'x64'; SelfContained = $true; StoreOnly = $true
    PublishInventory = @([pscustomobject]@{ Path = 'MyCapture.exe'; Bytes = $payload.Length; Sha256 = (Get-FileHash -LiteralPath $payload.FullName).Hash })
    PackagedSelfTestRun = $runName; PackagedSelfTestResultSha256 = (Get-FileHash -LiteralPath (Join-Path $run 'result.json')).Hash
}
function Write-Manifest { $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $fixture 'artifacts\release-manifest.json') -Encoding UTF8 }
Write-Manifest
$null = Assert-StorePublishProvenance $sourceRepo $publish '4.1.0'
$checks++
$manifest.PackagedSelfTestResultSha256 = '0' * 64
Write-Manifest
Expect-Rejection 'changed self-test JSON' { Assert-StorePublishProvenance $sourceRepo $publish '4.1.0' }
$manifest.PackagedSelfTestResultSha256 = (Get-FileHash -LiteralPath (Join-Path $run 'result.json')).Hash
$singleRecord = $manifest.PublishInventory[0]
$manifest.PublishInventory = @($singleRecord, $singleRecord)
Write-Manifest
Expect-Rejection 'duplicate publish inventory' { Assert-StorePublishProvenance $sourceRepo $publish '4.1.0' }
$manifest.PublishInventory = @($singleRecord)
$manifest.SourceCommit = '0' * 40
Write-Manifest
Expect-Rejection 'same version stale source SHA' { Assert-StorePublishProvenance $sourceRepo $publish '4.1.0' }
$manifest.SourceCommit = $head
$manifest.SourceTreeClean = $false
Write-Manifest
Expect-Rejection 'dirty provenance' { Assert-StorePublishProvenance $sourceRepo $publish '4.1.0' }
$manifest.SourceTreeClean = $true
$manifest.Architecture = 'arm64'
Write-Manifest
Expect-Rejection 'wrong architecture' { Assert-StorePublishProvenance $sourceRepo $publish '4.1.0' }
$manifest.Architecture = 'x64'
Write-Manifest
Expect-Rejection 'wrong version' { Assert-StorePublishProvenance $sourceRepo $publish '4.2.0' }
'changed payload' | Set-Content -LiteralPath $payload.FullName -Encoding ASCII
Expect-Rejection 'mutated payload hash' { Assert-StorePublishProvenance $sourceRepo $publish '4.1.0' }
'synthetic payload' | Set-Content -LiteralPath $payload.FullName -Encoding ASCII
'extra' | Set-Content -LiteralPath (Join-Path $publish 'extra.txt') -Encoding ASCII
Expect-Rejection 'unmanifested extra file' { Assert-StorePublishProvenance $sourceRepo $publish '4.1.0' }
$setupConfig = [IO.File]::ReadAllText((Get-SetupBootstrapSource 'MyCaptureSetup.exe.config'))
if ($setupConfig.Contains('sku=') -or -not $setupConfig.Contains('version="v4.0"')) { throw 'Setup CLR config contract changed.' }
if (@([MyCapture.Setup.PackageManifest]::BootstrapNames).Count -ne 3) { throw 'Unexpected setup executable bootstrap count.' }
foreach ($name in [MyCapture.Setup.PackageManifest]::BootstrapNames) {
    if (-not [IO.File]::Exists((Get-SetupBootstrapSource $name))) { throw "Missing setup bootstrap: $name" }
}
$checks += 2
Write-Host "CONTRACT_TESTS=PASS CHECKS=$checks"
}
catch { Write-Output ('TEST_FAILURE=' + $_.Exception.Message); throw }
finally {
    $env:MYCAPTURE_DATA_ROOT = $previousData
    Remove-TestItem $fixture
}
