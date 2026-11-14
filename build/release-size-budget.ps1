# Shared package gates. The sole budget values are in ReleaseSizeBudgets.props.
function Assert-ReleaseSizeBudget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][ValidateSet('x64', 'x86', 'arm64')][string]$Architecture,
        [string]$PortableZip,
        [string]$BudgetPath = (Join-Path $PSScriptRoot 'ReleaseSizeBudgets.props')
    )
    [xml]$budgetDocument = Get-Content -LiteralPath $BudgetPath -Raw -Encoding UTF8
    $condition = "'`$(RuntimeIdentifier)' == 'win-$Architecture'"
    $groups = @($budgetDocument.Project.PropertyGroup | Where-Object { $_.Condition -ceq $condition })
    if ($groups.Count -ne 1) { throw "Expected exactly one release size budget for win-$Architecture in $BudgetPath." }
    $limits = @{}
    foreach ($name in @('MyCaptureInstalledSizeBudgetBytes', 'MyCapturePortableSizeBudgetBytes', 'MyCaptureProjectionSizeBudgetBytes')) {
        $nodes = @($groups[0].SelectNodes($name))
        [long]$limit = 0
        if ($nodes.Count -ne 1 -or -not [long]::TryParse([string]$nodes[0].InnerText, [ref]$limit) -or $limit -le 0) {
            throw "Invalid positive release size budget: $name in $BudgetPath."
        }
        $limits[$name] = $limit
    }
    if (-not (Test-Path -LiteralPath $PublishRoot -PathType Container)) { throw "Release publish folder is missing: $PublishRoot" }
    $files = @(Get-ChildItem -LiteralPath $PublishRoot -File -Recurse -Force)
    [long]$installed = 0
    foreach ($file in $files) {
        $installed += $file.Length
        if ($file.Name -match '^(mscordaccore(?:_.*)?\.dll|mscordbi\.dll|Microsoft\.DiaSymReader\.Native\..+\.dll)$') {
            throw "Debugger-only file remains in release publish: $($file.FullName). Use a fresh publish directory."
        }
    }
    $projection = Get-Item -LiteralPath (Join-Path $PublishRoot 'Microsoft.Windows.SDK.NET.dll') -ErrorAction Stop
    if ($projection.PSIsContainer -or $projection.Length -le 0) { throw 'Release publish is missing the Windows projection.' }
    $advice = 'Review the per-file size report and measured ZIP, then deliberately update build/ReleaseSizeBudgets.props for this architecture.'
    foreach ($measurement in @(
        @{ Name = 'installed'; Bytes = $installed; Limit = $limits.MyCaptureInstalledSizeBudgetBytes },
        @{ Name = 'Windows projection'; Bytes = $projection.Length; Limit = $limits.MyCaptureProjectionSizeBudgetBytes }
    )) {
        if ($measurement.Bytes -gt $measurement.Limit) {
            throw "Release $($measurement.Name) budget exceeded for win-${Architecture}: $($measurement.Bytes) > $($measurement.Limit) bytes. $advice"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($PortableZip)) {
        $zip = Get-Item -LiteralPath $PortableZip -ErrorAction Stop
        if ($zip.PSIsContainer -or $zip.Length -le 0) { throw "Portable ZIP is missing or empty: $PortableZip" }
        if ($zip.Length -gt $limits.MyCapturePortableSizeBudgetBytes) {
            throw "Release portable ZIP budget exceeded for win-${Architecture}: $($zip.Length) > $($limits.MyCapturePortableSizeBudgetBytes) bytes. $advice"
        }
    }
    Write-Host "Release size budget passed: win-$Architecture, installed=$installed, projection=$($projection.Length) bytes."
}
