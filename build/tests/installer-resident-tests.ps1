# Exercise the built setup executable's process selector through its net48 tests.
# No application process is started or stopped by this test.
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$previousData = $env:MYCAPTURE_DATA_ROOT
$env:MYCAPTURE_DATA_ROOT = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-resident-data-' + [guid]::NewGuid().ToString('N'))
try {
    & dotnet test (Join-Path $repo 'tests\MyCapture.Setup.Tests') -c Release --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~ExactProcessPathIsTheOnlyTerminationCandidate'
    if ($LASTEXITCODE -ne 0) { throw "Resident preflight test failed: $LASTEXITCODE" }
    Write-Output 'INSTALLER_RESIDENT_TESTS=PASS'
}
finally { $env:MYCAPTURE_DATA_ROOT = $previousData }