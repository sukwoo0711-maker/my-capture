# Builds the Microsoft Store MSIX from the verified self-contained publish folder that
# build\package.ps1 produced, so the Store and GitHub releases ship identical binaries.
#
# Store package (identity values from Partner Center > Product identity):
#   .\build\package-store.ps1 -IdentityName 'Publisher.MyCapture' -Publisher 'CN=...' -PublisherDisplayName '...'
#
# Local sideload test (unsigned; install from an elevated prompt with Add-AppxPackage -AllowUnsigned):
#   .\build\package-store.ps1 -LocalTest
#
# The package is left unsigned: the Store re-signs it after certification.

[CmdletBinding()]
param(
    [string]$Version,
    [string]$IdentityName,
    [string]$Publisher,
    [string]$PublisherDisplayName,
    [string]$DisplayName = 'MyCapture',
    [string]$PublishDir,
    [string]$OutputRoot,
    [string]$MaxVersionTested = '10.0.26200.0',
    [switch]$LocalTest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')

[xml]$sourceProps = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw -Encoding UTF8
$propsVersion = [string]@($sourceProps.SelectNodes('/Project/PropertyGroup/Version'))[0].InnerText
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $propsVersion }
if (-not [string]::Equals($Version, $propsVersion, [StringComparison]::Ordinal)) {
    throw "Requested version $Version does not match Directory.Build.props ($propsVersion)."
}
if ($Version -cnotmatch '\A[1-9][0-9]{0,4}\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\z') {
    throw "The Store needs a stable numeric version with a non-zero major part: $Version"
}
# The fourth part is reserved for the Store and must stay 0.
$packageVersion = "$Version.0"

if ($LocalTest) {
    if ($IdentityName -or $Publisher -or $PublisherDisplayName) {
        throw '-LocalTest uses its own identity; do not combine it with Store identity values.'
    }
    # The OID marks an unsigned package that Windows 11 can sideload with -AllowUnsigned.
    $IdentityName = 'MyCapture.LocalTest'
    $Publisher = 'CN=MyCapture Local Test, OID.2.25.311729368913984317654407730594956997722=1'
    $PublisherDisplayName = 'MyCapture Local Test'
}
else {
    if ([string]::IsNullOrWhiteSpace($IdentityName) -or [string]::IsNullOrWhiteSpace($Publisher) -or
        [string]::IsNullOrWhiteSpace($PublisherDisplayName)) {
        throw 'Pass -IdentityName, -Publisher and -PublisherDisplayName exactly as Partner Center shows them, or use -LocalTest.'
    }
    if ($Publisher -match 'OID\.2\.25\.311729368913984317654407730594956997722') {
        throw 'The unsigned-package OID is for local tests only and is rejected by the Store.'
    }
}
if ($IdentityName -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9.\-]{2,49}\z') {
    throw "Package identity name must be 3-50 letters, digits, periods or dashes: $IdentityName"
}
if ($Publisher -cnotmatch '\ACN=.+') {
    throw "Publisher must be the distinguished name Partner Center shows, starting with CN=: $Publisher"
}
foreach ($text in @($PublisherDisplayName, $DisplayName)) {
    if ([string]::IsNullOrWhiteSpace($text) -or $text.Length -gt 256) {
        throw 'Display names must be 1-256 characters.'
    }
}
if ($MaxVersionTested -cnotmatch '\A10\.0\.[0-9]{5}\.0\z') {
    throw "MaxVersionTested must look like 10.0.26200.0: $MaxVersionTested"
}
[xml]$manifestTemplate = Get-Content -LiteralPath (Join-Path $repo 'build/store/AppxManifest.xml') -Raw -Encoding UTF8
$minimumVersion = [Version]$manifestTemplate.Package.Dependencies.TargetDeviceFamily.MinVersion
if ([Version]$MaxVersionTested -lt $minimumVersion) {
    throw "MaxVersionTested must be at least the package MinVersion ($minimumVersion)."
}

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $repo "artifacts\release\$Version\publish-win-x64"
}
$PublishDir = [IO.Path]::GetFullPath($PublishDir).TrimEnd('\')
if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish folder not found: $PublishDir. Run build\package.ps1 -Version $Version first."
}
. (Join-Path $PSScriptRoot 'packaged-validation.ps1')
$provenance = Assert-StorePublishProvenance $repo $PublishDir $Version
$provenanceHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $PublishDir) 'release-manifest.json') -Algorithm SHA256).Hash
# Legal notices remain mandatory in addition to the exact source and inventory gate.
foreach ($required in @('MyCapture.exe', 'MyCapture.dll', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md',
        'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt', 'Assets\tray-idle.ico')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $required) -PathType Leaf)) {
        throw "The publish folder is missing $required; use the folder build\package.ps1 produced."
    }
}
$fileVersion = [string](Get-Item -LiteralPath (Join-Path $PublishDir 'MyCapture.exe')).VersionInfo.FileVersion
if (-not [string]::Equals($fileVersion, $packageVersion, [StringComparison]::Ordinal)) {
    throw "MyCapture.exe is version $fileVersion, expected $packageVersion."
}
foreach ($reserved in @('AppxManifest.xml', 'AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml', 'resources.pri')) {
    if (Test-Path -LiteralPath (Join-Path $PublishDir $reserved)) {
        throw "The publish folder already contains package metadata: $reserved"
    }
}

$storeRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts\store')).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $storeRoot ($(if ($LocalTest) { "$Version-localtest" } else { $Version }))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot).TrimEnd('\')
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Preserving existing Store artifacts; move them to the Recycle Bin before repackaging: $OutputRoot"
}
$stage = Join-Path $OutputRoot 'stage'
$priRoot = Join-Path $OutputRoot 'pri-root'
[IO.Directory]::CreateDirectory($stage) | Out-Null
[IO.Directory]::CreateDirectory($priRoot) | Out-Null

if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    foreach ($candidateDir in @((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'), (Join-Path $env:LOCALAPPDATA 'MyCapture\dotnet-sdk'))) {
        if (Test-Path -LiteralPath (Join-Path $candidateDir 'dotnet.exe')) {
            $env:Path = "$candidateDir;$env:Path"
        }
    }
}
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-Tool {
    param([string]$FilePath, [string[]]$Arguments)
    # makeappx reports every payload file; keep the log to warnings, errors and the result.
    & $FilePath @Arguments | Where-Object { $_ -and $_ -notmatch '^Processing ".+" as a payload file' }
    if ($LASTEXITCODE -ne 0) {
        throw "$([IO.Path]::GetFileName($FilePath)) failed with exit code $LASTEXITCODE."
    }
}

# Pinned makeappx/makepri from NuGet, so packaging does not depend on an installed Windows SDK.
$toolsProject = Join-Path $repo 'build\tools\StoreTools\StoreTools.csproj'
Invoke-Tool 'dotnet' @('restore', $toolsProject, '--nologo')
$assetsJson = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $toolsProject) 'obj\project.assets.json') -Raw -Encoding UTF8 |
    ConvertFrom-Json
$packageFolder = [string]@($assetsJson.packageFolders.PSObject.Properties.Name)[0]
$buildTools = @($assetsJson.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Microsoft.Windows.SDK.BuildTools/*' })
if ($buildTools.Count -ne 1) { throw 'Microsoft.Windows.SDK.BuildTools was not restored exactly once.' }
$toolsVersion = ($buildTools[0].Name -split '/')[1]
$sdkBin = @(Get-ChildItem -LiteralPath (Join-Path (Join-Path $packageFolder $buildTools[0].Value.path) 'bin') -Directory |
    Where-Object { $_.Name -like '10.0.*' } | Sort-Object Name -Descending)
if ($sdkBin.Count -lt 1) { throw "No Windows SDK bin folder in Microsoft.Windows.SDK.BuildTools $toolsVersion." }
$makeappx = Join-Path $sdkBin[0].FullName 'x64\makeappx.exe'
$makepri = Join-Path $sdkBin[0].FullName 'x64\makepri.exe'
foreach ($tool in @($makeappx, $makepri)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Packaging tool not found: $tool" }
}

Write-Host "Staging $PublishDir"
Copy-Item -Path (Join-Path $PublishDir '*') -Destination $stage -Recurse

$listingIcon = Join-Path $OutputRoot 'StoreListingIcon-300.png'
& (Join-Path $PSScriptRoot 'generate-icons.ps1') -StoreAssetsDir (Join-Path $stage 'Assets') -StoreListingIconPath $listingIcon

$tokens = [ordered]@{
    '{IdentityName}' = $IdentityName
    '{Publisher}' = $Publisher
    '{PackageVersion}' = $packageVersion
    '{DisplayName}' = $DisplayName
    '{PublisherDisplayName}' = $PublisherDisplayName
    '{MaxVersionTested}' = $MaxVersionTested
}
$manifestText = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'store\AppxManifest.xml') -Raw -Encoding UTF8
foreach ($token in $tokens.Keys) {
    $manifestText = $manifestText.Replace($token, [Security.SecurityElement]::Escape([string]$tokens[$token]))
}
if ($manifestText -match '\{[A-Za-z]+\}') { throw "Unreplaced manifest token: $($Matches[0])" }
$manifestPath = Join-Path $stage 'AppxManifest.xml'
[IO.File]::WriteAllText($manifestPath, $manifestText, (New-Object System.Text.UTF8Encoding($false)))
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8

# Index only the logo assets. Indexing the whole folder would turn the satellite assembly
# folders (ko-KR\, en\) into language resources and bloat resources.pri with runtime files.
$priAssets = Join-Path $priRoot 'Assets'
New-Item -ItemType Directory -Path $priAssets -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $stage 'Assets') -File -Filter '*.png' |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $priAssets }
$priConfig = Join-Path $OutputRoot 'priconfig.xml'
Invoke-Tool $makepri @('createconfig', '/cf', $priConfig, '/dq', 'ko-KR', '/pv', '10.0.0', '/o')
# A single .msix carries every scale; the default config would describe split resource packs.
[xml]$priConfigXml = Get-Content -LiteralPath $priConfig -Raw
$packagingNode = $priConfigXml.SelectSingleNode('/resources/packaging')
if ($null -ne $packagingNode) { [void]$packagingNode.ParentNode.RemoveChild($packagingNode) }
$priConfigXml.Save($priConfig)
Invoke-Tool $makepri @('new', '/pr', $priRoot, '/cf', $priConfig, '/mn', $manifestPath,
    '/of', (Join-Path $stage 'resources.pri'), '/o')

$msixName = '{0}_{1}_x64.msix' -f $IdentityName, $packageVersion
$msix = Join-Path $OutputRoot $msixName
Invoke-Tool $makeappx @('pack', '/d', $stage, '/p', $msix, '/o')

$finalProvenance = Assert-StorePublishProvenance $repo $PublishDir $Version
if ($finalProvenance.SourceCommit -cne $provenance.SourceCommit -or
    (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $PublishDir) 'release-manifest.json') -Algorithm SHA256).Hash -cne $provenanceHash) { throw 'Source changed during Store packaging.' }

$hash = (Get-FileHash -LiteralPath $msix -Algorithm SHA256).Hash.ToLowerInvariant()
$summary = [ordered]@{
    Package = $msixName
    Sha256 = $hash
    Bytes = (Get-Item -LiteralPath $msix).Length
    PackageVersion = $packageVersion
    IdentityName = $IdentityName
    Publisher = $Publisher
    PublisherDisplayName = $PublisherDisplayName
    DisplayName = $DisplayName
    LocalTest = [bool]$LocalTest
    MaxVersionTested = $MaxVersionTested
    PublishDir = $PublishDir
    SourceCommit = $provenance.SourceCommit
    ReleaseManifestSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $PublishDir) 'release-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    BuildTools = "Microsoft.Windows.SDK.BuildTools $toolsVersion"
    StartupTaskId = [string]$manifest.Package.Applications.Application.Extensions.Extension.StartupTask.TaskId
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'store-package.json') -Encoding UTF8
"$hash  $msixName" | Set-Content -LiteralPath (Join-Path $OutputRoot 'SHA256SUMS.txt') -Encoding ASCII

Write-Host "Store package: $msix"
Write-Host "SHA-256:       $hash"
Write-Host "Listing icon:  $listingIcon"
