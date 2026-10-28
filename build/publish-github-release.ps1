[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$NotesFile,

    [string]$Repository = 'sukwoo0711-maker/my-capture',

    [switch]$Latest,
    # A test build stays a stable release but leaves 'Latest', which the in-app update check
    # reads, on the previous version.
    [switch]$NotLatest,
    [switch]$IncludeX86
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'required-release-assets.ps1')

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = Join-Path $repoRoot "artifacts\release\$Version"
$tag = "v$Version"
$required = @(Get-RequiredReleaseAssetNames -Version $Version -IncludeX86:$IncludeX86)
$files = @($required | ForEach-Object {
    $assetRoot = if ($_.Contains('win-x86')) { Join-Path $artifactRoot 'win-x86' } else { $artifactRoot }
    $path = Join-Path $assetRoot $_
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) {
        throw "Refusing to publish $tag without required local asset: $_"
    }

    $path
})

if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) {
    throw "Release notes file was not found: $NotesFile"
}

$env:NO_COLOR = '1'
$env:GH_FORCE_TTY = '0'
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
gh api "repos/$Repository/releases/tags/$tag" --jq '.tag_name' | Out-Null
$releaseExists = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $previousPreference
if ($releaseExists) {
    throw "Release $tag already exists. Refusing to overwrite it."
}

if ($Latest -and $NotLatest) {
    throw 'Choose either -Latest or -NotLatest.'
}

$latestArgs = @()
if ($Latest) {
    $latestArgs += '--latest'
}
elseif ($NotLatest) {
    $latestArgs += '--latest=false'
}

gh release create $tag --repo $Repository --title "MyCapture $Version" --notes-file $NotesFile @latestArgs @files
if ($LASTEXITCODE -ne 0) {
    throw "GitHub release create failed for $tag."
}

& (Join-Path $PSScriptRoot 'verify-github-release.ps1') -Tag $tag -Repository $Repository -IncludeX86:$IncludeX86
Write-Output "PUBLISHED=$tag"
