[CmdletBinding()]
param(
    [switch]$Quiet
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$globalJsonPath = Join-Path $repoRoot 'global.json'
if (-not (Test-Path -LiteralPath $globalJsonPath -PathType Leaf)) {
    throw "SDK configuration is missing: $globalJsonPath"
}

$sdkConfiguration = Get-Content -LiteralPath $globalJsonPath -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
$requiredText = [string]$sdkConfiguration.sdk.version
$requiredVersion = $null
if (-not [Version]::TryParse($requiredText, [ref]$requiredVersion)) {
    throw "global.json contains an invalid SDK version: $requiredText"
}

# Prefer the normal per-user installation used by the build scripts, but also try
# every dotnet host already on PATH. This lets a machine recover cleanly when one
# installation has only a runtime and another contains the required SDK.
$candidatePaths = New-Object 'System.Collections.Generic.List[string]'
if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $bootstrapDotnet = Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk\dotnet.exe'
    if (Test-Path -LiteralPath $bootstrapDotnet -PathType Leaf) {
        $candidatePaths.Add([IO.Path]::GetFullPath($bootstrapDotnet))
    }

    $perUserDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $perUserDotnet -PathType Leaf) {
        $candidatePaths.Add([IO.Path]::GetFullPath($perUserDotnet))
    }
}

Get-Command dotnet -CommandType Application -All -ErrorAction SilentlyContinue |
    ForEach-Object {
        $path = [IO.Path]::GetFullPath($_.Source)
        if (-not $candidatePaths.Contains($path)) {
            $candidatePaths.Add($path)
        }
    }

if ($candidatePaths.Count -eq 0) {
    throw @"
No .NET SDK installation was found.
Building MyCapture requires .NET 10 SDK $requiredText or newer; the runtime alone is not sufficient.
Run build\bootstrap-sdk.ps1, or install the SDK from https://dotnet.microsoft.com/download/dotnet/10.0
Then open a new PowerShell window and run build\doctor.ps1 again.
"@
}

$selectedPath = $null
$selectedVersion = $null
$diagnostics = New-Object 'System.Collections.Generic.List[string]'

Push-Location $repoRoot
try {
    foreach ($candidatePath in $candidatePaths) {
        $versionOutput = & $candidatePath --version 2>&1
        $exitCode = $LASTEXITCODE
        $versionText = (@($versionOutput) -join [Environment]::NewLine).Trim()

        if ($exitCode -ne 0) {
            $diagnostics.Add("$candidatePath -> exit $exitCode; $versionText")
            continue
        }

        $stableVersionLine = @($versionOutput |
            ForEach-Object { [string]$_ } |
            Where-Object { $_ -match '^\d+\.\d+\.\d+$' } |
            Select-Object -Last 1)
        if ($stableVersionLine.Count -eq 0) {
            $diagnostics.Add("$candidatePath -> could not parse the SDK version; $versionText")
            continue
        }

        $candidateVersion = [Version]$stableVersionLine[0]
        $compatible =
            $candidateVersion.Major -eq $requiredVersion.Major -and
            $candidateVersion.Minor -eq $requiredVersion.Minor -and
            $candidateVersion.Build -ge $requiredVersion.Build

        if ($compatible) {
            $selectedPath = $candidatePath
            $selectedVersion = $candidateVersion
            break
        }

        $diagnostics.Add("$candidatePath -> SDK $candidateVersion (required: $requiredText or newer in .NET $($requiredVersion.Major).$($requiredVersion.Minor))")
    }
}
finally {
    Pop-Location
}

if ($null -eq $selectedPath) {
    $detail = if ($diagnostics.Count -gt 0) {
        [Environment]::NewLine + 'Checked dotnet installations:' + [Environment]::NewLine +
            (($diagnostics | ForEach-Object { "  - $_" }) -join [Environment]::NewLine)
    }
    else {
        ''
    }

    throw @"
No compatible .NET SDK installation was found.
MyCapture requires .NET 10 SDK $requiredText or newer as pinned by global.json.
Run build\bootstrap-sdk.ps1, or install the SDK from https://dotnet.microsoft.com/download/dotnet/10.0$detail
"@
}

$selectedDirectory = Split-Path -Parent $selectedPath
$env:Path = "$selectedDirectory;$env:Path"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

if (-not $Quiet) {
    Write-Host 'MyCapture build doctor' -ForegroundColor Cyan
    Write-Host "  SDK:     $selectedVersion (required by global.json: $requiredText, rollForward: latestFeature)"
    Write-Host "  dotnet:  $selectedPath"
    Write-Host "  OS:      $([Environment]::OSVersion.VersionString)"
    Write-Host "  CPU:     $env:PROCESSOR_ARCHITECTURE (target: win-x64)"

    if ($env:OS -eq 'Windows_NT' -and [Environment]::OSVersion.Version.Build -lt 22000) {
        Write-Warning 'The build may succeed, but MyCapture requires Windows 11 21H2 (build 22000) or newer at runtime.'
    }

    Write-Host 'RESULT: READY' -ForegroundColor Green
}
