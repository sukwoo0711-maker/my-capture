[CmdletBinding()]
param(
    [string]$Version = '4.1.0'
)

# Produces the verified self-contained publish folder and its release manifest under
# artifacts\release\<Version>. build\package-store.ps1 builds the Microsoft Store package from
# this folder, and build\run-packaged-self-tests.ps1 runs the native self-tests against it.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$semVerPattern = '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?$'
$versionMatch = [regex]::Match($Version, $semVerPattern)
if (-not $versionMatch.Success) {
    throw "Version must use a SemVer core with an optional prerelease (for example 1.7.0 or 1.7.0-rc.1): $Version"
}

$baseVersion = '{0}.{1}.{2}' -f $versionMatch.Groups['major'].Value, $versionMatch.Groups['minor'].Value, $versionMatch.Groups['patch'].Value
$binaryVersion = "$baseVersion.0"
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')

[xml]$sourceProps = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw -Encoding UTF8
$productVersionNodes = @($sourceProps.SelectNodes('/Project/PropertyGroup/Version'))
$fileVersionNodes = @($sourceProps.SelectNodes('/Project/PropertyGroup/FileVersion'))
$assemblyVersionNodes = @($sourceProps.SelectNodes('/Project/PropertyGroup/AssemblyVersion'))
if ($productVersionNodes.Count -ne 1 -or $fileVersionNodes.Count -ne 1 -or $assemblyVersionNodes.Count -ne 1) {
    throw 'Directory.Build.props must declare exactly one Version, FileVersion, and AssemblyVersion.'
}
if (-not [string]::Equals([string]$productVersionNodes[0].InnerText, $baseVersion, [StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$fileVersionNodes[0].InnerText, $binaryVersion, [StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$assemblyVersionNodes[0].InnerText, $binaryVersion, [StringComparison]::Ordinal)) {
    throw "Requested package version $Version does not match the release version declared in Directory.Build.props."
}

$releaseRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\release')).TrimEnd('\')
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $releaseRoot $Version)).TrimEnd('\')
if (-not $artifactRoot.StartsWith($releaseRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals((Split-Path -Leaf $artifactRoot), $Version, [StringComparison]::Ordinal)) {
    throw "Refusing to clean an unsafe artifact path: $artifactRoot"
}

if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    $bootstrapDotnetDir = Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk'
    $dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
    foreach ($candidateDir in @($dotnetDir, $bootstrapDotnetDir)) {
        if (Test-Path -LiteralPath (Join-Path $candidateDir 'dotnet.exe')) {
            $env:Path = "$candidateDir;$env:Path"
        }
    }
}
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$project = Join-Path $repo 'src\MyCapture.App\MyCapture.App.csproj'
$publish = Join-Path $artifactRoot 'publish-win-x64'
$releaseManifestOutput = Join-Path $artifactRoot 'release-manifest.json'
$projectLicense = Join-Path $repo 'LICENSE'
$thirdPartyIndex = Join-Path $repo 'THIRD-PARTY-NOTICES.md'

function Get-Sha256([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-GitSourceState {
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $git) {
        throw 'Git is required to record release source provenance.'
    }

    $gitPath = [string]$git.Source
    $commitOutput = & $gitPath -C $repo rev-parse --verify 'HEAD^{commit}' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to resolve the release source commit: $(@($commitOutput) -join ' ')"
    }

    $commit = (@($commitOutput) -join '').Trim().ToLowerInvariant()
    if ($commit -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
        throw "Git returned an invalid source commit: $commit"
    }

    $statusOutput = & $gitPath -C $repo status --porcelain=v1 --untracked-files=all 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to inspect the release source tree: $(@($statusOutput) -join ' ')"
    }

    $status = (@($statusOutput) -join [Environment]::NewLine).Trim()
    return [pscustomobject][ordered]@{
        Commit = $commit
        WorkingTreeClean = [string]::IsNullOrEmpty($status)
    }
}

function Get-RelativePackagePath([string]$Root, [string]$FullPath) {
    $relative = $FullPath.Substring($Root.Length + 1).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative.Contains('\')) {
        throw "Unable to create a safe package-relative path for: $FullPath"
    }
    foreach ($segment in $relative.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            throw "Unsafe package-relative path generated: $relative"
        }
    }
    return $relative
}

function New-FileRecord([string]$Path, [string]$RelativePath) {
    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    return [pscustomobject][ordered]@{
        Path = $RelativePath
        Bytes = [long]$item.Length
        Sha256 = Get-Sha256 $item.FullName
    }
}

function Assert-SelfContainedPublish([string]$PublishRoot) {
    $requiredRuntimeFiles = @(
        'MyCapture.exe',
        'MyCapture.dll',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'System.Private.CoreLib.dll',
        'PresentationCore.dll',
        'PresentationFramework.dll'
    )
    foreach ($name in $requiredRuntimeFiles) {
        $path = Join-Path $PublishRoot $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) {
            throw "Self-contained publish is missing required runtime file: $name"
        }
    }

    $runtimeConfigPath = Join-Path $PublishRoot 'MyCapture.runtimeconfig.json'
    $depsPath = Join-Path $PublishRoot 'MyCapture.deps.json'
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw 'Self-contained publish is missing runtimeconfig or deps metadata.'
    }

    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    if ($null -ne $runtimeConfig.runtimeOptions.PSObject.Properties['framework'] -or
        $null -ne $runtimeConfig.runtimeOptions.PSObject.Properties['frameworks']) {
        throw 'Published runtimeconfig requests a machine-wide .NET shared framework.'
    }
    $includedFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    $includedNames = @($includedFrameworks | ForEach-Object { [string]$_.name })
    foreach ($frameworkName in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if ($includedNames -notcontains $frameworkName) {
            throw "Published runtimeconfig does not embed required framework: $frameworkName"
        }
    }

    $deps = Get-Content -LiteralPath $depsPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    if ([string]$deps.runtimeTarget.name -notmatch '/win-x64$') {
        throw "Published deps runtime target is not win-x64: $($deps.runtimeTarget.name)"
    }
}

$sourceState = Get-GitSourceState
$sourceCommit = [string]$sourceState.Commit
$sourceTreeClean = [bool]$sourceState.WorkingTreeClean
if (-not $sourceTreeClean) {
    throw 'Refusing to package a dirty Git worktree; commit the exact release source first.'
}

foreach ($requiredLegalFile in @($projectLicense, $thirdPartyIndex)) {
    if (-not (Test-Path -LiteralPath $requiredLegalFile -PathType Leaf) -or
        (Get-Item -LiteralPath $requiredLegalFile).Length -le 0) {
        throw "Required release legal notice is missing or empty: $requiredLegalFile"
    }
}

$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
$dotnetRoot = Split-Path -Parent ([string]$dotnetCommand.Source)
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
$dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
foreach ($requiredDotnetNotice in @($dotnetLicense, $dotnetNotices)) {
    if (-not (Test-Path -LiteralPath $requiredDotnetNotice -PathType Leaf) -or
        (Get-Item -LiteralPath $requiredDotnetNotice).Length -le 0) {
        throw "The selected .NET SDK is missing a required distribution notice: $requiredDotnetNotice"
    }
}

if (Test-Path -LiteralPath $artifactRoot) {
    throw "Preserving existing release artifacts; move them to the Recycle Bin before repackaging: $artifactRoot"
}
[IO.Directory]::CreateDirectory($publish) | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishProfile=win-x64-self-contained -o $publish `
    -p:Version=$Version -p:FileVersion=$binaryVersion -p:AssemblyVersion=$binaryVersion `
    -p:SourceRevisionId=$sourceCommit
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }

Assert-SelfContainedPublish $publish

$exe = Join-Path $publish 'MyCapture.exe'
$dll = Join-Path $publish 'MyCapture.dll'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $dll)) {
    throw 'Published MyCapture.exe or MyCapture.dll is missing.'
}

# build\package-store.ps1 requires these notices; their presence ties a publish folder to this script.
Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $publish 'LICENSE.txt') -Force
Copy-Item -LiteralPath $thirdPartyIndex -Destination (Join-Path $publish 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $publish 'DOTNET-LICENSE.txt') -Force
Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $publish 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force

foreach ($asset in @('tray-idle.ico', 'tray-capturing.ico', 'tray-busy.ico', 'tray-error.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish "Assets\$asset"))) {
        throw "Published asset missing: Assets\$asset"
    }
}

$inventory = @(
    Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
        New-FileRecord $_.FullName (Get-RelativePackagePath $publish $_.FullName)
    } | Sort-Object -Property Path
)
if ($inventory.Count -lt 1) {
    throw 'Published folder contains no files.'
}

# Packaging can be long-running. Recheck immediately before writing provenance so a source
# edit made during publish can never be mislabeled as the commit captured at startup.
$finalSourceState = Get-GitSourceState
if (-not [bool]$finalSourceState.WorkingTreeClean -or
    -not [string]::Equals([string]$finalSourceState.Commit, $sourceCommit, [StringComparison]::Ordinal)) {
    throw 'Release source changed during packaging; refusing to emit an inexact manifest.'
}

$releaseManifest = [pscustomobject][ordered]@{
    SchemaVersion = 4
    Product = 'MyCapture'
    Version = $Version
    SourceCommit = $sourceCommit
    SourceTreeClean = $sourceTreeClean
    Runtime = 'win-x64'
    Architecture = 'x64'
    SelfContained = $true
    GeneratedUtc = [DateTime]::UtcNow.ToString('O')
    Distribution = 'Microsoft Store package built by build\package-store.ps1 from this publish folder'
    Compatibility = [pscustomobject][ordered]@{
        MinimumWindowsBuild = 22000
        MinimumWindowsRelease = 'Windows 11 version 21H2'
        NativeArchitecture = 'x64'
        Arm64Support = 'Windows 11 ARM64 via x64 emulation'
        RequiresInternet = $false
        RequiresPreinstalledDotNet = $false
    }
    PublishInventory = $inventory
}
$releaseManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $releaseManifestOutput -Encoding UTF8

# Re-read the emitted JSON so serialization failures cannot be hidden by the in-memory object.
$releaseRoundTrip = Get-Content -LiteralPath $releaseManifestOutput -Raw -Encoding UTF8 |
    ConvertFrom-Json -ErrorAction Stop
if ([int]$releaseRoundTrip.SchemaVersion -ne 4 -or
    -not [string]::Equals([string]$releaseRoundTrip.Version, $Version, [StringComparison]::Ordinal) -or
    -not [string]::Equals([string]$releaseRoundTrip.SourceCommit, $sourceCommit, [StringComparison]::Ordinal) -or
    [bool]$releaseRoundTrip.SourceTreeClean -ne $sourceTreeClean -or
    @($releaseRoundTrip.PublishInventory).Count -ne $inventory.Count) {
    throw 'Release manifest failed its serialization round-trip contract.'
}

Write-Output "PUBLISH=$publish"
Write-Output "FILES=$($inventory.Count)"
Write-Output "RELEASE_MANIFEST=$releaseManifestOutput"
