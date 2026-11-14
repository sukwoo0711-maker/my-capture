# Shared package gate: every PE image in a publish folder must suit the package architecture.
# Dot-source. It reads file headers only and starts nothing, so it also judges a cross-built
# package that the build PC cannot run.
function Get-PublishImageKind {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { return $null }
        $stream.Position = 0x3C
        $header = $reader.ReadInt32()
        if ($header -lt 0 -or [long]$header + 24 -gt $stream.Length) { return $null }
        $stream.Position = $header
        if ($reader.ReadUInt32() -ne 0x00004550) { return $null }
        $machine = [int]$reader.ReadUInt16()
        $sectionCount = [int]$reader.ReadUInt16()
        $stream.Position = $header + 20
        $optionalSize = [int]$reader.ReadUInt16()
        $kind = [pscustomobject]@{ Machine = $machine; Managed = $false; ILOnly = $false; Requires32Bit = $false; ReadyToRun = $false }

        $optional = [long]$header + 24
        if ($optionalSize -lt 2 -or $optional + $optionalSize -gt $stream.Length) { return $kind }
        $stream.Position = $optional
        $magic = [int]$reader.ReadUInt16()
        $directoryCountOffset = if ($magic -eq 0x20B) { 108 } elseif ($magic -eq 0x10B) { 92 } else { 0 }
        # Data directory 14 is the CLI header; a native image has none.
        if ($directoryCountOffset -eq 0 -or $optionalSize -lt $directoryCountOffset + 4 + (15 * 8)) { return $kind }
        $stream.Position = $optional + $directoryCountOffset
        if ($reader.ReadUInt32() -lt 15) { return $kind }
        $stream.Position = $optional + $directoryCountOffset + 4 + (14 * 8)
        $cliAddress = [long]$reader.ReadUInt32()
        $cliSize = [long]$reader.ReadUInt32()
        if ($cliAddress -eq 0 -or $cliSize -eq 0) { return $kind }

        $cliOffset = [long]-1
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $stream.Position = $optional + $optionalSize + ($index * 40) + 8
            $virtualSize = [long]$reader.ReadUInt32()
            $virtualAddress = [long]$reader.ReadUInt32()
            $rawSize = [long]$reader.ReadUInt32()
            $rawOffset = [long]$reader.ReadUInt32()
            if ($cliAddress -ge $virtualAddress -and $cliAddress -lt $virtualAddress + [Math]::Max($virtualSize, $rawSize)) {
                $cliOffset = $cliAddress - $virtualAddress + $rawOffset
                break
            }
        }
        if ($cliSize -lt 72 -or $cliOffset -lt 0 -or $cliOffset + 72 -gt $stream.Length) {
            throw "A PE image has a CLI header outside its sections: $Path"
        }

        # IMAGE_COR20_HEADER: Flags at 16, ManagedNativeHeader (the ReadyToRun header) at 64.
        $stream.Position = $cliOffset + 16
        $flags = [long]$reader.ReadUInt32()
        $stream.Position = $cliOffset + 64
        $nativeHeaderAddress = [long]$reader.ReadUInt32()
        $nativeHeaderSize = [long]$reader.ReadUInt32()
        $kind.Managed = $true
        $kind.ILOnly = ($flags -band 0x1) -ne 0
        $kind.Requires32Bit = ($flags -band 0x2) -ne 0
        $kind.ReadyToRun = ($flags -band 0x4) -ne 0 -and $nativeHeaderAddress -ne 0 -and $nativeHeaderSize -ne 0
        return $kind
    }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-PublishArchitecture {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][ValidateSet('x64', 'x86', 'arm64')][string]$Architecture
    )

    $machines = @{ 'x64' = 0x8664; 'x86' = 0x014C; 'arm64' = 0xAA64 }
    $expected = [int]$machines[$Architecture]
    if (-not (Test-Path -LiteralPath $PublishRoot -PathType Container)) { throw "Release publish folder is missing: $PublishRoot" }
    $root = [IO.Path]::GetFullPath($PublishRoot).TrimEnd('\')
    $counts = [ordered]@{ Native = 0; ReadyToRun = 0; ArchitectureIL = 0; NeutralIL = 0; Other = 0 }
    $unsuitable = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse -Force | Sort-Object -Property FullName)) {
        $kind = Get-PublishImageKind -Path $file.FullName
        if ($null -eq $kind) { $counts.Other++; continue }
        # Compiled code must be for this architecture: native images, ReadyToRun images (which no
        # longer carry the IL-only flag) and mixed-mode assemblies. IL without a 32-bit requirement
        # runs on every architecture.
        $suitable = $kind.Machine -eq $expected
        if (-not $kind.Managed) { $category = 'Native' }
        elseif ($kind.ReadyToRun) { $category = 'ReadyToRun' }
        elseif (-not $kind.ILOnly) { $category = 'Native' }
        elseif ($kind.Machine -eq 0x014C -and -not $kind.Requires32Bit) { $category = 'NeutralIL'; $suitable = $true }
        else { $category = 'ArchitectureIL' }
        $counts[$category]++
        if (-not $suitable) {
            $unsuitable.Add(('{0} ({1}, machine 0x{2:X4})' -f $file.FullName.Substring($root.Length + 1).Replace('\', '/'), $category, $kind.Machine))
        }
    }

    $applicationHost = Join-Path $root 'MyCapture.exe'
    $hostKind = if (Test-Path -LiteralPath $applicationHost -PathType Leaf) { Get-PublishImageKind -Path $applicationHost } else { $null }
    if ($null -eq $hostKind -or $hostKind.Managed -or $hostKind.Machine -ne $expected) {
        $unsuitable.Add("MyCapture.exe is not a native win-$Architecture application host")
    }
    if ($counts.ReadyToRun -lt 1) { $unsuitable.Add('no ReadyToRun image was published') }
    if ($unsuitable.Count -gt 0) {
        throw ("Release publish has files that do not suit win-${Architecture}: " + ($unsuitable.ToArray() -join '; '))
    }
    Write-Host ("Release architecture passed: win-$Architecture (machine 0x{0:X4}), native={1}, ReadyToRun={2}, architecture IL={3}, neutral IL={4}, other files={5}." -f `
        $expected, $counts.Native, $counts.ReadyToRun, $counts.ArchitectureIL, $counts.NeutralIL, $counts.Other)
}
