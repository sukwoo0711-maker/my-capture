# Prints every native thread's stack and the open file handles from the media probe's dumps
# (diagnostic only), with Microsoft public symbols, into "<dump>.stacks.txt" beside each dump.
# Uses the Windows SDK's cdb.exe when the runner has it and says so when it does not.

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Continue'

$dumps = @(Get-ChildItem -Path $Path -Filter '*.dmp' -ErrorAction SilentlyContinue)
if ($dumps.Count -eq 0) {
    Write-Host 'No dumps: every first open settled before the dump points.'
    exit 0
}

$cdb = @(
    "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\cdb.exe",
    "$env:ProgramFiles\Windows Kits\10\Debuggers\x64\cdb.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $cdb) {
    Write-Host 'cdb.exe is not installed on this runner; the dumps are uploaded for offline analysis.'
    exit 0
}

$cache = Join-Path $env:RUNNER_TEMP 'symbols'
$server = "srv*$cache*https://msdl.microsoft.com/download/symbols"
$images = @($server, "$env:WINDIR\System32")
foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
    $images += @(Get-ChildItem "$env:ProgramFiles\dotnet\shared\$framework" -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
}

foreach ($dump in $dumps) {
    $out = "$($dump.FullName).stacks.txt"
    Write-Host "==> $($dump.Name)"
    $arguments = @('-z', "`"$($dump.FullName)`"", '-y', "`"$server`"", '-i', "`"$($images -join ';')`"",
        '-logo', "`"$out`"", '-c', '"~*k 60; !handle 0 f File; q"')
    $process = Start-Process -FilePath $cdb -ArgumentList $arguments -NoNewWindow -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        Add-Content -Path $out -Value "cdb did not finish within $TimeoutSeconds s"
    }

    Write-Host "    $out"
}
