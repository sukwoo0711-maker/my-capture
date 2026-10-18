# Real preservation/root guards from the built setup executable, with synthetic files only.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-test-common.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-preserve-tests-' + [guid]::NewGuid().ToString('N'))
$previousData = $env:MYCAPTURE_DATA_ROOT
$env:MYCAPTURE_DATA_ROOT = Join-Path $testRoot 'data'
$old = Join-Path $testRoot 'old'
$stage = Join-Path $testRoot 'stage'
[IO.Directory]::CreateDirectory($old) | Out-Null
[IO.Directory]::CreateDirectory($stage) | Out-Null
function Assert-Rejected([scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected rejection.' }
}
try {
    New-SyntheticSetupPackage (Join-Path $testRoot 'package')
    Copy-Item -LiteralPath (Join-Path $testRoot 'package\installer-manifest.json') -Destination (Join-Path $old 'install-manifest.json')
    [IO.File]::WriteAllText((Join-Path $old 'MyCapture.exe'), 'old owned binary')
    [IO.File]::WriteAllText((Join-Path $stage 'MyCapture.exe'), 'new owned binary')
    [IO.File]::WriteAllText((Join-Path $old 'notes.txt'), 'user notes')
    [MyCapture.Setup.PathSafety]::PreserveUserFiles($old, $stage)
    if ([IO.File]::ReadAllText((Join-Path $stage 'MyCapture.exe')) -ne 'new owned binary') { throw 'Owned binary was overwritten.' }
    if ([IO.File]::ReadAllText((Join-Path $stage 'notes.txt')) -ne 'user notes') { throw 'User data was not copied.' }
    Assert-Rejected { [MyCapture.Setup.PathSafety]::PreserveUserFiles($old, $stage) }
    if ([IO.File]::ReadAllText((Join-Path $old 'notes.txt')) -ne 'user notes') { throw 'Source data changed on conflict.' }
    Assert-Rejected { [MyCapture.Setup.PathSafety]::UpdateBoundary([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) }
    [MyCapture.Setup.PathSafety]::UpdateBoundary($old)
    $outside = Join-Path $testRoot 'outside'
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'linked user data')
    $link = Join-Path $old 'linked'
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    $secondStage = Join-Path $testRoot 'second-stage'
    [IO.Directory]::CreateDirectory($secondStage) | Out-Null
    Assert-Rejected { [MyCapture.Setup.PathSafety]::PreserveUserFiles($old, $secondStage) }
    Assert-Rejected { [MyCapture.Setup.PathSafety]::NoLinks($link) }
    Assert-Rejected { [MyCapture.Setup.PathSafety]::NoLinks((Join-Path $link 'child')) }
    if ([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -ne 'linked user data') { throw 'Preservation followed the junction.' }
    Write-Output 'PASS: user-file preservation, collision refusal, source preservation, root guards, and junction/ancestor refusal.'
}
finally {
    $env:MYCAPTURE_DATA_ROOT = $previousData
    # Delete the junction itself first, never its target, then this test's own fixture root.
    if ($link -and (Test-Path -LiteralPath $link)) { [IO.Directory]::Delete($link, $false) }
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}