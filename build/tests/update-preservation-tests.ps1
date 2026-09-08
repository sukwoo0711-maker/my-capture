# Exercises actual preservation/root guard functions with synthetic files, without invoking an installer.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scriptPath = Join-Path $repo 'build\installer\install.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer parse failed.' }
foreach ($name in @('Assert-UpdateRootBoundary', 'Preserve-UserInstallFiles')) {
    $function = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (@($function).Count -ne 1) { throw "Missing function: $name" }
    . ([scriptblock]::Create($function.Extent.Text))
}
function Throw-InstallerError($Code, $Message) { throw $Message }
function Read-And-ValidateManifest($Path) { return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
$ExitUnsafePath = 12; $ExitExistingInstall = 20
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-preserve-tests-' + [guid]::NewGuid().ToString('N'))
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
    @{ Files = @(@{ Path = 'MyCapture.exe' }) } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $old 'install-manifest.json') -Encoding UTF8
    [IO.File]::WriteAllText((Join-Path $old 'MyCapture.exe'), 'old owned binary')
    [IO.File]::WriteAllText((Join-Path $stage 'MyCapture.exe'), 'new owned binary')
    [IO.File]::WriteAllText((Join-Path $old 'notes.txt'), 'user notes')
    Preserve-UserInstallFiles $old $stage
    if ([IO.File]::ReadAllText((Join-Path $stage 'MyCapture.exe')) -ne 'new owned binary') { throw 'Owned binary was overwritten.' }
    if ([IO.File]::ReadAllText((Join-Path $stage 'notes.txt')) -ne 'user notes') { throw 'User data was not copied.' }
    Assert-Rejected { Preserve-UserInstallFiles $old $stage }
    if ([IO.File]::ReadAllText((Join-Path $old 'notes.txt')) -ne 'user notes') { throw 'Source data changed on conflict.' }
    Assert-Rejected { Assert-UpdateRootBoundary ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) }
    Assert-UpdateRootBoundary $old
    $outside = Join-Path $testRoot 'outside'
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'linked user data')
    $link = Join-Path $old 'linked'
    New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
    try {
        Remove-Item -LiteralPath (Join-Path $stage 'notes.txt') -Force
        Assert-Rejected { Preserve-UserInstallFiles $old $stage }
        if ([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -ne 'linked user data') { throw 'Preservation followed the junction.' }
    }
    finally {
        [IO.Directory]::Delete($link, $false)
        Remove-Item -LiteralPath (Join-Path $outside 'keep.txt') -Force
        [IO.Directory]::Delete($outside, $false)
    }
    Write-Output 'PASS: user-file preservation, collision refusal, source preservation, root guards, and junction refusal.'
}
finally {
    foreach ($root in @($old, $stage)) {
        foreach ($name in @('MyCapture.exe', 'install-manifest.json', 'notes.txt')) {
            $owned = Join-Path $root $name
            if (Test-Path -LiteralPath $owned) { Remove-Item -LiteralPath $owned -Force }
        }
        [IO.Directory]::Delete($root, $false)
    }
    [IO.Directory]::Delete($testRoot, $false)
}
