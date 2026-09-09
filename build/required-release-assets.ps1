# Shared list of GitHub release files that must exist before a version is latest.
function Get-RequiredReleaseAssetNames {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    @(
        "MyCapture-$Version-win-x64-setup.exe",
        "MyCapture-$Version-win-x64-portable.zip",
        'installer-manifest.json',
        'release-manifest.json',
        'SHA256SUMS.txt',
        'README-OFFLINE.txt'
    )
}
