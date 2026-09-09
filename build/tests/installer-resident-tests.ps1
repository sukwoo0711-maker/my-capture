# Exercise resident-copy detection without stopping any real application.
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/install.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Installer parse failed.' }
$function = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-InstalledApplication' }, $true)
if (@($function).Count -ne 1) { throw 'Resident preflight function missing.' }
. ([scriptblock]::Create($function.Extent.Text))
$ExitProcessStop = 15
$script:UpdateSession = $null
function Throw-InstallerError($Code, $Message) { throw "$Code`: $Message" }
$portable = [pscustomobject]@{ MainModule = [pscustomobject]@{ FileName = 'C:\portable\MyCapture.exe' }; Disposed = $false }
$portable | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
try {
    Stop-InstalledApplication 'C:\installed\MyCapture.exe' @($portable)
    throw 'Preflight did not reject the other resident copy.'
}
catch {
    if ($_.Exception.Message -notlike '15: Another copy*') { throw }
}
if (-not $portable.Disposed) { throw 'Process handle was retained.' }
Stop-InstalledApplication 'C:\installed\MyCapture.exe' @()
Write-Output 'INSTALLER_RESIDENT_TESTS=PASS'
