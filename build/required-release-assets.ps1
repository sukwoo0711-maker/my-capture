# Shared list of GitHub release files that must exist before a version is latest.
function Get-RequiredReleaseAssetNames {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version,
        [switch]$IncludeX86,
        [switch]$IncludeArm64
    )

    @(
        "MyCapture-$Version-win-x64-setup.exe",
        "MyCapture-$Version-win-x64-portable.zip",
        'installer-manifest.json',
        'release-manifest.json',
        'SHA256SUMS.txt',
        'README-OFFLINE.txt'
    )
    if ($IncludeX86) {
        @(
            "MyCapture-$Version-win-x86-setup.exe",
            "MyCapture-$Version-win-x86-portable.zip",
            'installer-manifest-win-x86.json',
            'release-manifest-win-x86.json',
            'SHA256SUMS-win-x86.txt',
            'README-OFFLINE-win-x86.txt'
        )
    }
    if ($IncludeArm64) {
        @(
            "MyCapture-$Version-win-arm64-setup.exe",
            "MyCapture-$Version-win-arm64-portable.zip",
            'installer-manifest-win-arm64.json',
            'release-manifest-win-arm64.json',
            'SHA256SUMS-win-arm64.txt',
            'README-OFFLINE-win-arm64.txt'
        )
    }
}
