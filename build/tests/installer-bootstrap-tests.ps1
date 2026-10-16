# IExpress -> native managed executable contract. No policy changes or real install targets.
[CmdletBinding()]
param()
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup-test-common.ps1')
$root = Join-Path ([IO.Path]::GetTempPath()) ('MyCapture-bootstrap-tests-' + [guid]::NewGuid().ToString('N') + ' & (space)')
[IO.Directory]::CreateDirectory($root) | Out-Null
$previousData = $env:MYCAPTURE_DATA_ROOT
$env:MYCAPTURE_DATA_ROOT = Join-Path $root 'data'
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
try {
    $package = [IO.File]::ReadAllText((Join-Path $setupTestRepo 'build\package.ps1'))
    foreach ($line in @('AppLaunched=MyCaptureSetup.exe', 'AdminQuietInstCmd=MyCaptureSetup.exe /quiet', 'UserQuietInstCmd=MyCaptureSetup.exe /quiet', 'FILE2=MyCaptureSetup.exe', 'FILE3=MyCaptureSetup.exe.config', 'FILE4=SETUP-HELP.txt')) {
        Assert-True ($package.Contains($line)) "Missing IExpress executable bootstrap contract: $line"
    }
    foreach ($old in @('install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'uninstall-cleanup.ps1')) {
        Assert-True (-not $package.Contains("FILE2=$old") -and -not $package.Contains("AppLaunched=$old")) "Old bootstrap remains: $old"
    }
    $help = [IO.File]::ReadAllBytes((Get-SetupBootstrapSource 'SETUP-HELP.txt'))
    Assert-True ($help[0] -eq 239 -and $help[1] -eq 187 -and $help[2] -eq 191) 'Bilingual help needs a UTF-8 BOM.'
    $config = [IO.File]::ReadAllText((Get-SetupBootstrapSource 'MyCaptureSetup.exe.config'))
    Assert-True ($config.Contains('<supportedRuntime version="v4.0"') -and -not $config.Contains('sku=')) 'CLR loader config must not pin a Framework SKU.'
    foreach ($name in @('MyCaptureSetup.exe', 'MyCaptureSetup.exe.config', 'SETUP-HELP.txt')) { Copy-Item -LiteralPath (Get-SetupBootstrapSource $name) -Destination (Join-Path $root $name) }
    $exe = Join-Path $root 'MyCaptureSetup.exe'
    $log = Join-Path $root 'missing-payload.log'
    & $exe /quiet --install-root (Join-Path $root 'installed') --no-shell-integration --log $log
    Assert-True ($LASTEXITCODE -eq 14) 'Direct quiet failure must retain exit 14.'
    Assert-True ([IO.File]::ReadAllText($log).Contains('Installation failed (exit 14)')) 'Missing durable native failure log.'
    Assert-True (-not [IO.Directory]::Exists((Join-Path $root 'installed'))) 'Missing payload changed install state.'
    & $exe /quiet --unknown
    Assert-True ($LASTEXITCODE -eq 21) 'Unknown executable option must return exit 21.'
    $rejected = $false
    try { [MyCapture.Setup.WindowsHost]::RequireRuntime([Func[int]]{ 528039 }, [Action]{ throw 'Compression must not load' }) }
    catch { $rejected = $_.Exception.InnerException.Code -eq 19 }
    Assert-True $rejected 'Missing Framework release must return component-unavailable exit 19.'
    # Script-start marker, per-run policy-text detection and Notepad interception are retired:
    # no script host exists. Their replacement checks above prove direct launch, durable errors,
    # quiet failure, special-character paths, help bytes, loader config and runtime probe.
    Write-Output 'INSTALLER_BOOTSTRAP_TESTS=PASS (IExpress direct exe contract and native startup failures)'
}
finally {
    $env:MYCAPTURE_DATA_ROOT = $previousData
    # Remove only this test's own uniquely named fixture root, as installer-hostile-tests does.
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
}