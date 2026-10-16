# Artifact-free simulations only: never runs the real installer, Notepad, or changes policy.
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = Join-Path $repo 'build\installer'
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$tempRoot = Join-Path $tempParent ('MyCapture-bootstrap-tests-' + [guid]::NewGuid().ToString('N'))
$powerShell = Join-Path $PSHOME 'powershell.exe'
$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$script:PassCount = 0
$crlf = [string][char]13 + [char]10
$lf = [string][char]10

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "$Message Expected=[$Expected] Actual=[$Actual]" }
}
function Pass([string]$Name) {
    $script:PassCount++
    Write-Output "PASS $Name"
}
function Write-Fixture([string]$Path, [string]$Text) {
    Assert-True ([IO.Path]::IsPathRooted($Path)) 'Fixture path must be absolute.'
    [IO.File]::WriteAllText($Path, $Text.Replace($crlf, $lf).Replace($lf, $crlf), (New-Object Text.UTF8Encoding($false)))
}

$tokens = $null
$parseErrors = $null
$installerAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $source 'install.ps1'), [ref]$tokens, [ref]$parseErrors)
Assert-Equal 0 @($parseErrors).Count 'Installer parse errors.'
$first = $installerAst.EndBlock.Statements[0]
Assert-True ($first -is [Management.Automation.Language.IfStatementAst]) 'Marker guard must be the first executable statement.'
$markerCode = $first.Extent.Text
Assert-True ($first.Clauses[0].Item1.Extent.Text.Contains('$env:MYCAPTURE_SETUP_STARTED')) 'First statement must guard the optional start marker.'
Assert-True ($markerCode.Contains('[IO.FileMode]::CreateNew') -and $markerCode.Contains('$markerStream.WriteByte(1)')) 'First statement must create and write the marker.'
Assert-True ($markerCode.Contains('[IO.Path]::GetDirectoryName') -and $markerCode.Contains('\AMyCapture-Setup-')) 'First statement must validate the marker parent and name.'
Assert-True ($installerAst.EndBlock.Statements[1].Extent.Text.StartsWith('Set-StrictMode')) 'Marker must precede strict mode and all installer work.'
Pass 'marker is the first executable statement, with parent/name validation'

$helpBytes = [IO.File]::ReadAllBytes((Join-Path $source 'SETUP-HELP.txt'))
Assert-True ($helpBytes.Length -gt 3 -and $helpBytes[0] -eq 239 -and $helpBytes[1] -eq 187 -and $helpBytes[2] -eq 191) 'Help must have a UTF-8 BOM.'
Pass 'help has a UTF-8 BOM'

$bootstrapSource = [IO.File]::ReadAllText((Join-Path $source 'install.cmd'))
$hostAssignments = [regex]::Matches($bootstrapSource, '(?m)set "POWERSHELL_EXE=[^"\r\n]+"')
Assert-Equal 2 $hostAssignments.Count 'Expected only the two PowerShell host assignments.'
$notepadLaunch = 'start "" "%SystemRoot%\System32\notepad.exe" "%SETUP_HELP%"'
Assert-True ($bootstrapSource.Contains($notepadLaunch)) 'Expected the explicit system Notepad launch.'
# The stub host discards all incoming installer arguments (including the existing policy
# argument) and launches only the synthetic script with the machine's unchanged policy.
$startedHost = @'
@echo off
"%TEST_POWERSHELL_EXE%" -NoProfile -NonInteractive -File "%~dp0install.ps1"
exit /b %ERRORLEVEL%
'@

function New-Case([string]$Name, [string]$HostBody, [int]$ScriptExit = 0) {
    $directory = Join-Path $tempRoot $Name
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'install.cmd') -Destination (Join-Path $directory 'install.cmd')
    Copy-Item -LiteralPath (Join-Path $source 'SETUP-HELP.txt') -Destination (Join-Path $directory 'SETUP-HELP.txt')
    # The host must be an executable like powershell.exe: a batch host started without CALL from
    # inside install.cmd's CALL :RunInstaller replaces that batch context and cmd then fails to
    # find the label. host.exe runs this case's host.cmd in its own cmd.exe process instead.
    Copy-Item -LiteralPath $hostShim -Destination (Join-Path $directory 'host.exe')
    $bootstrap = $bootstrapSource
    foreach ($assignment in $hostAssignments) {
        $bootstrap = $bootstrap.Replace($assignment.Value, ('set "POWERSHELL_EXE=' + (Join-Path $directory 'host.exe') + '"'))
    }
    # Intercept the visible launch in every copy, so a /quiet regression cannot open a real UI.
    $bootstrap = $bootstrap.Replace($notepadLaunch,
        'start "" /wait /b "%SystemRoot%\System32\cmd.exe" /d /c ""%~dp0notepad-stub.cmd" "%SETUP_HELP%""')
    Write-Fixture (Join-Path $directory 'install.cmd') $bootstrap
    Write-Fixture (Join-Path $directory 'host.cmd') $HostBody
    Write-Fixture (Join-Path $directory 'notepad-stub.cmd') ('@echo off' + $crlf + '>"%~dp0notepad-opened.txt" echo %~1' + $crlf + 'exit /b 0' + $crlf)
    # Reuse just the verified first statement. No installer functions or work are executed.
    $script = $markerCode + $crlf +
        '[IO.File]::WriteAllText((Join-Path $PSScriptRoot ''script-ran.txt''), ''started'')' + $crlf +
        'Write-Output ''synthetic script output''' + $crlf +
        "exit $ScriptExit" + $crlf
    Write-Fixture (Join-Path $directory 'install.ps1') $script
    return $directory
}

function Invoke-Case([string]$Directory, [bool]$Quiet = $true, [bool]$DirectScript = $false) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $cmd
    # Absolute path: hosts with NoDefaultCurrentDirectoryInExePath do not run a bare name from
    # the working directory. /s keeps the outer quotes around the whole command.
    $bootstrapCommand = '"' + (Join-Path $Directory 'install.cmd') + '"'
    if ($Quiet) { $bootstrapCommand += ' /quiet' }
    $info.Arguments = '/d /s /c "' + $bootstrapCommand + '"'
    if ($DirectScript) {
        $info.FileName = $powerShell
        $info.Arguments = '-NoProfile -NonInteractive -File "' + (Join-Path $Directory 'install.ps1') + '"'
    }
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.EnvironmentVariables['TEMP'] = $Directory
    $info.EnvironmentVariables['TMP'] = $Directory
    $info.EnvironmentVariables['TEST_POWERSHELL_EXE'] = $powerShell
    $info.EnvironmentVariables.Remove('MYCAPTURE_SETUP_STARTED')
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        Assert-True ($process.WaitForExit(30000)) 'Synthetic bootstrap timed out.'
        $output = $stdout.Result + $stderr.Result
        Assert-Equal '' $output.Trim() 'Bootstrap must redirect its output to the log.'
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}
function Assert-NoMarkers([string]$Directory) {
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $Directory -Filter '*.started' -Force).Count 'Start marker was not removed.'
}
function Assert-HelpCopied([string]$Directory) {
    $copy = [IO.File]::ReadAllBytes((Join-Path $Directory 'MyCapture-Setup-Help.txt'))
    Assert-Equal ([Convert]::ToBase64String($helpBytes)) ([Convert]::ToBase64String($copy)) 'Copied help changed bytes/BOM.'
}
function Assert-NoHelp([string]$Directory) {
    Assert-True (-not [IO.File]::Exists((Join-Path $Directory 'MyCapture-Setup-Help.txt'))) 'Started script must not get bootstrap help.'
    Assert-True (-not [IO.File]::Exists((Join-Path $Directory 'notepad-opened.txt'))) 'Started script must not open help.'
}
function Latest-Evidence([string]$Directory) {
    $log = [IO.File]::ReadAllText((Join-Path $Directory 'MyCapture-Setup-bootstrap.log'))
    $lines = @($log -split '\r?\n' | Where-Object { $_ -match 'execution-policy evidence in this run:' })
    Assert-True ($lines.Count -gt 0) 'Missing explicit start-failure evidence in bootstrap log.'
    return $lines[-1]
}

[IO.Directory]::CreateDirectory($tempRoot) | Out-Null
try {
    $hostShim = Join-Path $tempRoot 'host-shim.exe'
    Add-Type -OutputType ConsoleApplication -OutputAssembly $hostShim -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;

public static class HostShim
{
    // Ignores the PowerShell arguments; the case's host.cmd decides the simulated outcome.
    public static int Main(string[] args)
    {
        string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "host.cmd");
        string cmd = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot"), @"System32\cmd.exe");
        var start = new ProcessStartInfo(cmd, "/d /s /c \"\"" + script + "\"\"") { UseShellExecute = false };
        using (Process process = Process.Start(start))
        {
            process.WaitForExit();
            return process.ExitCode;
        }
    }
}
'@
    $blockedHost = '@echo off' + $crlf + 'echo PSSecurityException: script execution is blocked by Group Policy. 1>&2' + $crlf + 'exit /b 1' + $crlf
    $blocked = New-Case 'blocked quiet' $blockedHost
    Assert-Equal 22 (Invoke-Case $blocked) 'Policy refusal must use the startup exit code.'
    Assert-HelpCopied $blocked
    Assert-True ((Latest-Evidence $blocked).Contains('evidence in this run: found.')) 'Policy evidence was not detected.'
    Assert-True (-not [IO.File]::Exists((Join-Path $blocked 'script-ran.txt'))) 'Refused host ran the script.'
    Assert-True (-not [IO.File]::Exists((Join-Path $blocked 'notepad-opened.txt'))) '/quiet launched guidance.'
    Assert-NoMarkers $blocked
    Pass 'policy refusal: exit 22, help copied, current-run evidence found, no script or UI'

    Write-Fixture (Join-Path $blocked 'host.cmd') ('@echo off' + $crlf + 'echo synthetic host failure' + $crlf + 'exit /b 7' + $crlf)
    Assert-Equal 22 (Invoke-Case $blocked) 'Non-policy startup failure must use the same startup code.'
    Assert-True ((Latest-Evidence $blocked).Contains('evidence in this run: not found.')) 'Earlier appended policy output leaked into this run.'
    Assert-Equal 0 @(Get-ChildItem -LiteralPath $blocked -Filter 'MyCapture-Setup-*-*-*.log').Count 'Per-run output logs must not be left behind.'
    Assert-Equal 2 @([IO.File]::ReadAllLines((Join-Path $blocked 'MyCapture-Setup-bootstrap.log')) | Where-Object { $_ -match 'Installer exit code' }).Count 'Both runs must be appended to the bootstrap log.'
    Assert-NoMarkers $blocked
    Pass 'generic startup failure ignores policy evidence from an earlier appended run'

    $textOnly = New-Case 'policy text' ('@echo off' + $crlf + 'echo The execution policy does not permit this script.' + $crlf + 'exit /b 1' + $crlf)
    Assert-Equal 22 (Invoke-Case $textOnly) 'Execution-policy text refusal exit.'
    Assert-True ((Latest-Evidence $textOnly).Contains('evidence in this run: found.')) 'Execution-policy error text was not detected.'
    Pass 'execution-policy text is detected without PSSecurityException'

    $success = New-Case 'started space & (parentheses)' $startedHost
    Assert-Equal 0 (Invoke-Case $success) 'Started successful script exit changed.'
    Assert-True ([IO.File]::Exists((Join-Path $success 'script-ran.txt'))) 'Successful stub did not run.'
    Assert-NoHelp $success
    Assert-NoMarkers $success
    Pass 'started success: exit 0, no help, marker removed, special-character path'

    $failed = New-Case 'started failed' $startedHost 17
    Assert-Equal 17 (Invoke-Case $failed) 'Started failing script exit changed.'
    Assert-True ([IO.File]::Exists((Join-Path $failed 'script-ran.txt'))) 'Failing stub did not run.'
    Assert-NoHelp $failed
    Assert-NoMarkers $failed
    Pass 'started failure: exit 17 preserved, no help, marker removed'

    $visible = New-Case 'visible guidance' $blockedHost
    Assert-Equal 22 (Invoke-Case $visible $false) 'Interactive refusal exit changed.'
    Assert-HelpCopied $visible
    $opened = [IO.File]::ReadAllText((Join-Path $visible 'notepad-opened.txt'), [Text.Encoding]::Default).Trim()
    Assert-Equal (Join-Path $visible 'MyCapture-Setup-Help.txt') $opened 'Notepad stub did not receive the durable help path.'
    Assert-NoMarkers $visible
    Pass 'non-quiet refusal opens copied guidance through the launch stub'

    # Boundary checks use only the same synthetic script, never the installer body.
    $direct = New-Case 'direct no marker' $startedHost
    Write-Fixture (Join-Path $direct 'install.ps1') ($markerCode + $crlf + 'exit 0' + $crlf)
    Assert-Equal 0 (Invoke-Case $direct $true $true) 'Absent marker must leave direct script calls unaffected.'
    Assert-NoMarkers $direct
    Pass 'direct script without the environment variable is unaffected'

    foreach ($invalid in @(
        @{ Name = 'nested'; Value = 'nested\MyCapture-Setup-1-2-3.started' },
        @{ Name = 'wrong name'; Value = 'unrelated.started' }
    )) {
        $directory = New-Case $invalid.Name $startedHost
        $invalidPath = Join-Path $directory $invalid.Value
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($invalidPath)) | Out-Null
        $invalidHost = '@echo off' + $crlf + 'set "MYCAPTURE_SETUP_STARTED=' + $invalidPath + '"' + $crlf + $startedHost
        Write-Fixture (Join-Path $directory 'host.cmd') $invalidHost
        Assert-Equal 22 (Invoke-Case $directory) 'Invalid marker must stop before script work.'
        Assert-True (-not [IO.File]::Exists($invalidPath)) 'Invalid marker was written.'
        Assert-True (-not [IO.File]::Exists((Join-Path $directory 'script-ran.txt'))) 'Invalid marker allowed later work.'
        Pass ("invalid marker rejected before work: " + $invalid.Name)
    }
    Write-Output ("RESULT: PASS ({0} checks)" -f $script:PassCount)
}
finally {
    # Remove only this test's own uniquely named fixture root, as installer-hostile-tests does.
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
