[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,

    [string]$Repository = 'sukwoo0711-maker/my-capture'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'required-release-assets.ps1')

if ($Tag -notmatch '^v(?<version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*))$') {
    throw "Tag must look like v1.8.2: $Tag"
}

$version = $Matches['version']
$required = @(Get-RequiredReleaseAssetNames -Version $version)

$releaseJson = gh release view $Tag --repo $Repository --json tagName,isDraft,isPrerelease,assets
if ($LASTEXITCODE -ne 0) {
    throw "Could not read GitHub release $Tag."
}

$release = $releaseJson | ConvertFrom-Json
if ($release.isDraft) {
    throw "Release $Tag is still a draft; required assets are not published."
}

if ($release.isPrerelease) {
    throw "Release $Tag is a prerelease; latest deliverables must be a stable release."
}

$names = @($release.assets | ForEach-Object { [string]$_.name })
$missing = @($required | Where-Object { $names -notcontains $_ })
if ($missing.Count -gt 0) {
    throw "Release $Tag is missing required assets: $($missing -join ', ')"
}

$empty = @($release.assets | Where-Object { $required -contains $_.name -and $_.size -le 0 })
if ($empty.Count -gt 0) {
    throw "Release $Tag has empty required assets: $((@($empty | ForEach-Object { $_.name })) -join ', ')"
}

Write-Output "RELEASE_ASSETS=PASS TAG=$Tag COUNT=$($required.Count)"
