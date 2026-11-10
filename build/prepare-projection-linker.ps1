[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# This is build tooling, not an application PackageReference: PublishTrimmed must remain
# false so application compilation and its trim analyzers are completely unchanged.
[xml]$targets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'WindowsProjectionTrim.targets') -Raw -Encoding UTF8
$version = [string]$targets.Project.PropertyGroup.MyCaptureProjectionLinkerVersion.'#text'
$expectedSha512 = [string]$targets.Project.PropertyGroup.MyCaptureProjectionLinkerPackageSha512
if ($version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or [Convert]::FromBase64String($expectedSha512).Length -ne 64) {
    throw 'The Windows projection linker version or package SHA512 pin is invalid.'
}

if ($env:MYCAPTURE_PROJECTION_LINKER_PATH) {
    if (-not (Test-Path -LiteralPath $env:MYCAPTURE_PROJECTION_LINKER_PATH -PathType Leaf)) {
        throw 'MYCAPTURE_PROJECTION_LINKER_PATH does not name an existing linker.'
    }
    Write-Host "Using prepared projection linker: $env:MYCAPTURE_PROJECTION_LINKER_PATH"
    return
}

$cacheLines = @(& dotnet nuget locals global-packages --list --force-english-output)
if ($LASTEXITCODE -ne 0) { throw 'The NuGet package cache could not be located.' }
$cacheLine = @($cacheLines | Where-Object { $_ -cmatch '^global-packages: .+$' })
if ($cacheLine.Count -ne 1) { throw 'The NuGet package cache response was ambiguous.' }
$cache = $cacheLine[0].Substring('global-packages: '.Length).Trim()
$cachedLinker = Join-Path $cache "microsoft.net.illink.tasks\$version\tools\net\illink.dll"
if (Test-Path -LiteralPath $cachedLinker -PathType Leaf) {
    $env:MYCAPTURE_PROJECTION_LINKER_PATH = $cachedLinker
    Write-Host "Using cached projection linker: $cachedLinker"
    return
}

$toolRoot = Join-Path ([IO.Path]::GetTempPath()) ('mycapture-linker-' + $version + '-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($toolRoot) | Out-Null
$archive = Join-Path $toolRoot 'linker.zip'
$uri = "https://api.nuget.org/v3-flatcontainer/microsoft.net.illink.tasks/$version/microsoft.net.illink.tasks.$version.nupkg"
Invoke-WebRequest -Uri $uri -OutFile $archive -UseBasicParsing
$algorithm = [Security.Cryptography.SHA512]::Create()
$inputStream = [IO.File]::OpenRead($archive)
try { $actualSha512 = [Convert]::ToBase64String($algorithm.ComputeHash($inputStream)) }
finally { $inputStream.Dispose(); $algorithm.Dispose() }
if ($actualSha512 -cne $expectedSha512) { throw 'Downloaded projection linker package failed its pinned SHA512 verification.' }
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolRoot 'pack')
$linker = Join-Path $toolRoot 'pack\tools\net\illink.dll'
if (-not (Test-Path -LiteralPath $linker -PathType Leaf)) { throw 'The verified linker package has no tools/net/illink.dll.' }
$env:MYCAPTURE_PROJECTION_LINKER_PATH = $linker
Write-Host "Prepared pinned projection linker $version at $linker"
