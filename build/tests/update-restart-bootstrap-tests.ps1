# Candidate bootstrap integrity only: synthetic files are never installed or executed.
[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-test-common.ps1')
$harness = Join-Path $PSScriptRoot 'update-restart-acceptance.ps1'
$powerShell = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$root = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('MyCapture-staged-bootstrap-tests-' + [guid]::NewGuid().ToString('N'))))
if (-not $root.StartsWith($temporaryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture root.' }
$publish = Join-Path $root 'publish-win-x64'
$stage = Join-Path $root 'installer-stage'
$sha = 'a' * 40
$preserveFixture = $false
[IO.Directory]::CreateDirectory($publish) | Out-Null
[IO.Directory]::CreateDirectory($stage) | Out-Null
function Record([string]$Path, [string]$Name) {
    return @{ Path = $Name; Bytes = (Get-Item -LiteralPath $Path).Length; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Write-Manifest {
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'installer-manifest.json') -Encoding UTF8
}
function Assert-FixturePath([string]$Path) {
    if (-not [IO.Path]::GetFullPath($Path).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture move escaped its generated root: $Path"
    }
}
function Invoke-StagedPreflight([bool]$ExpectedSuccess, [string]$ExpectedFailure = '') {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $powerShell -NoLogo -NoProfile -NonInteractive -File $harness -ArtifactRoot $root -ExpectedSourceCommit $sha -StagedBootstrap 2>&1
        $exit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $oldPreference }
    $text = @($output) -join "`n"
    if (($exit -eq 0) -ne $ExpectedSuccess) { throw "Unexpected staged preflight exit ${exit}: $text" }
    if ($ExpectedSuccess -and $text -notmatch 'PREFLIGHT PASS:') { throw 'Missing explicit staged preflight result.' }
    if ($ExpectedFailure -and -not $text.Contains($ExpectedFailure)) { throw "Unexpected staged preflight failure: $text" }
}
try {
    $files = @('MyCapture.exe', 'MyCapture.dll') | ForEach-Object {
        $path = Join-Path $publish $_
        [IO.File]::WriteAllText($path, 'synthetic application, never executed')
        Record $path $_
    }
    foreach ($name in @('MyCapture-1.8.0-win-x64-setup.exe', 'MyCapture-1.8.0-win-x64-portable.zip')) {
        [IO.File]::WriteAllText((Join-Path $root $name), 'synthetic package, never executed')
    }
    # Deliberately differ from the independent solution build: its hashes must not be used.
    $bootstrap = @('MyCaptureSetup.exe', 'MyCaptureSetup.exe.config', 'SETUP-HELP.txt') | ForEach-Object {
        $path = Join-Path $stage $_
        [IO.File]::WriteAllText($path, 'candidate staged bootstrap, never executed: ' + $_)
        Record $path $_
    }
    $manifest = @{ Product = 'MyCapture'; Version = '1.8.0'; Files = @($files); BootstrapFiles = @($bootstrap)
        Payload = Record (Join-Path $root 'MyCapture-1.8.0-win-x64-portable.zip') 'payload.zip' }
    Write-Manifest
    @{ Product = 'MyCapture'; Version = '1.8.0'; SourceCommit = $sha; SourceTreeClean = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'release-manifest.json') -Encoding UTF8
    @('MyCapture-1.8.0-win-x64-setup.exe', 'MyCapture-1.8.0-win-x64-portable.zip', 'release-manifest.json') | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash, $_
    } | Set-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') -Encoding ASCII
    Invoke-StagedPreflight $true

    foreach ($entry in $bootstrap) {
        $path = Join-Path $stage $entry.Path
        $original = [IO.File]::ReadAllBytes($path)
        $changed = [byte[]]$original.Clone()
        $changed[0] = $changed[0] -bxor 1
        [IO.File]::WriteAllBytes($path, $changed)
        Invoke-StagedPreflight $false 'Seed installer source does not match the candidate bootstrap inventory.'
        [IO.File]::WriteAllBytes($path, $original)
    }
    # A correct hash must not excuse an incorrect byte count.
    $bootstrap[0].Bytes++
    Write-Manifest
    Invoke-StagedPreflight $false 'Seed installer source does not match the candidate bootstrap inventory.'
    $bootstrap[0].Bytes--
    Write-Manifest

    $manifest.BootstrapFiles = @($bootstrap[0], $bootstrap[0], $bootstrap[2])
    Write-Manifest
    Invoke-StagedPreflight $false 'Missing or duplicate bootstrap file.'
    $manifest.BootstrapFiles = @($bootstrap)
    Write-Manifest

    # A missing stage or file must fail even though the solution bootstrap still exists.
    $savedStage = Join-Path $root 'preserved-installer-stage'
    Assert-FixturePath $stage
    Assert-FixturePath $savedStage
    [IO.Directory]::Move($stage, $savedStage)
    Invoke-StagedPreflight $false
    Assert-FixturePath $savedStage
    Assert-FixturePath $stage
    [IO.Directory]::Move($savedStage, $stage)
    $config = Join-Path $stage 'MyCaptureSetup.exe.config'
    $savedConfig = Join-Path $stage 'preserved.config'
    Assert-FixturePath $config
    Assert-FixturePath $savedConfig
    [IO.File]::Move($config, $savedConfig)
    Invoke-StagedPreflight $false
    Assert-FixturePath $savedConfig
    Assert-FixturePath $config
    [IO.File]::Move($savedConfig, $config)

    # The exact candidate directory must not be a link, even to another owned fixture.
    Assert-FixturePath $stage
    Assert-FixturePath $savedStage
    [IO.Directory]::Move($stage, $savedStage)
    $preserveFixture = $true
    New-Item -ItemType Junction -Path $stage -Target $savedStage | Out-Null
    Invoke-StagedPreflight $false 'Acceptance paths cannot contain links.'
    Assert-FixturePath $stage
    Remove-TestItem -Path $stage
    foreach ($entry in $bootstrap) {
        $target = Join-Path $savedStage $entry.Path
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.Sha256 -or
            (Get-Item -LiteralPath $target).Length -ne $entry.Bytes) { throw 'Recycling the stage link changed its target.' }
    }
    Assert-FixturePath $savedStage
    Assert-FixturePath $stage
    [IO.Directory]::Move($savedStage, $stage)
    $preserveFixture = $false
    Invoke-StagedPreflight $true
    Write-Output 'UPDATE_RESTART_BOOTSTRAP_TESTS=PASS (candidate stage, three same-size tamper cases, exact size, duplicates, missing stage/file, owned junction rejection; no installation executed)'
}
finally {
    if ($preserveFixture) { Write-Warning "Preserved fixture after incomplete stage-link validation: $root" }
    elseif (Test-Path -LiteralPath $root) { Remove-TestItem -Path $root }
}
