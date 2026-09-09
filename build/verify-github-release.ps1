[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Repository = 'sukwoo0711-maker/my-capture'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$env:NO_COLOR = '1'
$env:GH_FORCE_TTY = '0'

. (Join-Path $PSScriptRoot 'required-release-assets.ps1')

if ($Tag -notmatch '^v(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$') {
    throw "Tag must look like v1.8.2: $Tag"
}

$version = $Matches['version']
$required = @(Get-RequiredReleaseAssetNames -Version $version)

$draft = (gh api "repos/$Repository/releases/tags/$Tag" --jq '.draft').Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not read GitHub release $Tag."
}

$prerelease = (gh api "repos/$Repository/releases/tags/$Tag" --jq '.prerelease').Trim()
$names = @(gh api "repos/$Repository/releases/tags/$Tag" --jq '.assets[].name' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$sizes = @(gh api "repos/$Repository/releases/tags/$Tag" --jq '.assets[].size')

if ($draft -eq 'true') {
    throw "Release $Tag is still a draft; required assets are not published."
}

if ($prerelease -eq 'true') {
    throw "Release $Tag is a prerelease; latest deliverables must be a stable release."
}

$missing = @($required | Where-Object { $names -notcontains $_ })
if ($missing.Count -gt 0) {
    throw "Release $Tag is missing required assets: $($missing -join ', ')"
}

for ($index = 0; $index -lt $names.Count; $index++) {
    if ($required -contains $names[$index] -and [int64]$sizes[$index] -le 0) {
        throw "Release $Tag has empty required assets: $($names[$index])"
    }
}

Write-Output "RELEASE_ASSETS=PASS TAG=$Tag COUNT=$($required.Count)"
