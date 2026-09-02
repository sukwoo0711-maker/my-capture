# Generates multi-resolution .ico files for MyCapture.
#
# Each size is drawn independently so 16x16 tray icons stay crisp rather than
# being downscaled from one large bitmap.
#
# Small entries are written as uncompressed 32bpp DIBs (BITMAPINFOHEADER + BGRA +
# AND mask) for the widest compatibility; 128/256 are written as PNG because a
# 256x256 DIB entry alone is 256KB.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File build\generate-icons.ps1

param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\MyCapture.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$dibSizes = @(16, 20, 24, 32, 40, 48, 64)
$pngSizes = @(128, 256)

# Focus Portal palette. Tray state colours are deliberately far apart in hue;
# the tooltip/status text still carries the state so colour is never the only cue.
$AccentFocus   = [System.Drawing.Color]::FromArgb(255, 88, 199, 243)
$AccentAmber   = [System.Drawing.Color]::FromArgb(255, 245, 185, 66)
$AccentEmerald = [System.Drawing.Color]::FromArgb(255, 69, 214, 162)
$AccentCoral   = [System.Drawing.Color]::FromArgb(255, 255, 107, 116)
$GraphitePlate = [System.Drawing.Color]::FromArgb(255, 11, 15, 23)
$PortalWhite   = [System.Drawing.Color]::FromArgb(255, 246, 248, 252)

function New-RoundedPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    if ($d -le 0) {
        $p.AddRectangle((New-Object System.Drawing.RectangleF($X, $Y, $W, $H)))
        return $p
    }
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon {
    param(
        [int]$Size,
        [System.Drawing.Color]$Accent,

        # 'Plate' draws the two-pane brand mark on a dark rounded square. Used for the
        # application icon, where the host surface (Explorer, installer, Alt-Tab)
        # is unpredictable but always reasonably large.
        #
        # 'Glyph' draws the two panes over a dark halo. Used for tray icons: at
        # 16px a dark plate collapses into an indistinct blob on a dark taskbar,
        # whereas a haloed glyph stays readable on both light and dark themes.
        [ValidateSet('Plate', 'Glyph')]
        [string]$Mode = 'Plate'
    )

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$Size

    if ($Mode -eq 'Plate') {
        $pad = [Math]::Max(0.5, $s * 0.03)
        $plateSize = $s - ($pad * 2)
        $plate = New-RoundedPath -X $pad -Y $pad -W $plateSize -H $plateSize -R ($s * 0.21)
        $bg = New-Object System.Drawing.SolidBrush($GraphitePlate)
        $g.FillPath($bg, $plate)
        $bg.Dispose()
        $plate.Dispose()

        $margin = [Math]::Round($s * 0.17)
        $thickness = [Math]::Max(1.5, [Math]::Round($s * 0.072))
    }
    else {
        # No plate to sit inside, so the portal can use nearly the whole canvas and
        # a heavier stroke compensates for the missing backdrop.
        $margin = [Math]::Max(1.0, [Math]::Round($s * 0.08))
        $thickness = [Math]::Max(2.0, [Math]::Round($s * 0.10))
    }

    # Focus Portal: a captured source pane moves forward into a floating pane.
    # Both rectangles are kept complete at small sizes; that silhouette survives
    # Windows tray resampling much better than four disconnected crop corners.
    $span = $s - ($margin * 2)
    $paneSize = $span * 0.64
    $offset = $span * 0.26
    $radius = [Math]::Max(1.0, $s * 0.085)
    $frontPath = New-RoundedPath -X $margin -Y $margin -W $paneSize -H $paneSize -R $radius
    $rearPath = New-RoundedPath -X ($margin + $offset) -Y ($margin + $offset) -W $paneSize -H $paneSize -R $radius

    if ($Mode -eq 'Glyph') {
        $haloWidth = $thickness + [Math]::Max(1.5, $s * 0.055)
        $haloColor = [System.Drawing.Color]::FromArgb(190, 8, 12, 18)

        $halo = New-Object System.Drawing.Pen($haloColor, $haloWidth)
        $halo.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawPath($halo, $rearPath)
        $g.DrawPath($halo, $frontPath)
        $halo.Dispose()
    }

    $rearPen = New-Object System.Drawing.Pen($Accent, $thickness)
    $rearPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($rearPen, $rearPath)
    $rearPen.Dispose()

    $frontColor = if ($Mode -eq 'Plate') { $PortalWhite } else { $Accent }
    $frontPen = New-Object System.Drawing.Pen($frontColor, $thickness)
    $frontPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($frontPen, $frontPath)
    $frontPen.Dispose()
    $rearPath.Dispose()
    $frontPath.Dispose()

    $g.Dispose()
    return $bmp
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $raw = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. biHeight is doubled because an icon DIB stores the XOR
    # image followed by the AND mask.
    $bw.Write([UInt32]40)              # biSize
    $bw.Write([Int32]$w)               # biWidth
    $bw.Write([Int32]($h * 2))         # biHeight
    $bw.Write([UInt16]1)               # biPlanes
    $bw.Write([UInt16]32)              # biBitCount
    $bw.Write([UInt32]0)               # biCompression = BI_RGB
    $bw.Write([UInt32]($w * $h * 4))   # biSizeImage
    $bw.Write([Int32]0)                # biXPelsPerMeter
    $bw.Write([Int32]0)                # biYPelsPerMeter
    $bw.Write([UInt32]0)               # biClrUsed
    $bw.Write([UInt32]0)               # biClrImportant

    # XOR image, bottom-up.
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($raw, ($y * $stride), ($w * 4))
    }

    # AND mask: all zero. With a 32bpp XOR image Windows honours the alpha
    # channel, but the mask must still be present and correctly sized.
    $maskRowBytes = [int]([Math]::Floor(($w + 31) / 32)) * 4
    $maskRow = New-Object byte[] $maskRowBytes
    for ($y = 0; $y -lt $h; $y++) { $bw.Write($maskRow) }

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()

    # -NoEnumerate is required: a bare `return $bytes` makes PowerShell unroll the
    # byte[] into Object[], which then binds BinaryWriter.Write to the wrong
    # overload and silently emits a single byte.
    Write-Output -NoEnumerate $bytes
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    Write-Output -NoEnumerate $bytes
}

function Save-Ico {
    param(
        [string]$Path,
        [System.Drawing.Color]$Accent,
        [ValidateSet('Plate', 'Glyph')]
        [string]$Mode = 'Plate'
    )

    $entries = @()

    foreach ($size in $dibSizes) {
        $bmp = Draw-Icon -Size $size -Accent $Accent -Mode $Mode
        $entries += , @{ Size = $size; Bytes = [byte[]](Get-DibBytes -Bitmap $bmp) }
        $bmp.Dispose()
    }

    foreach ($size in $pngSizes) {
        $bmp = Draw-Icon -Size $size -Accent $Accent -Mode $Mode
        $entries += , @{ Size = $size; Bytes = [byte[]](Get-PngBytes -Bitmap $bmp) }
        $bmp.Dispose()
    }

    $fs = [System.IO.File]::Create($Path)
    try {
        $bw = New-Object System.IO.BinaryWriter($fs)

        # ICONDIR
        $bw.Write([UInt16]0)                 # idReserved
        $bw.Write([UInt16]1)                 # idType: 1 = icon
        $bw.Write([UInt16]$entries.Count)    # idCount

        $offset = 6 + (16 * $entries.Count)
        foreach ($e in $entries) {
            # 256 is encoded as 0 in the single-byte dimension fields.
            $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
            $bw.Write([Byte]$dim)            # bWidth
            $bw.Write([Byte]$dim)            # bHeight
            $bw.Write([Byte]0)               # bColorCount
            $bw.Write([Byte]0)               # bReserved
            $bw.Write([UInt16]1)             # wPlanes
            $bw.Write([UInt16]32)            # wBitCount
            $bw.Write([UInt32]$e.Bytes.Length)
            $bw.Write([UInt32]$offset)
            $offset += $e.Bytes.Length
        }

        foreach ($e in $entries) { $bw.Write([byte[]]$e.Bytes) }
        $bw.Flush()
    }
    finally {
        $fs.Dispose()
    }

    Write-Host ("  {0,-22} {1,9:N0} bytes  {2,-5} {3} sizes" -f `
        [System.IO.Path]::GetFileName($Path), (Get-Item $Path).Length, $Mode, $entries.Count)
}

Write-Host "Generating icons into $OutDir"

Save-Ico -Path (Join-Path $OutDir 'app.ico')            -Accent $AccentFocus   -Mode Plate
Save-Ico -Path (Join-Path $OutDir 'tray-idle.ico')      -Accent $AccentFocus   -Mode Glyph
Save-Ico -Path (Join-Path $OutDir 'tray-capturing.ico') -Accent $AccentAmber   -Mode Glyph
Save-Ico -Path (Join-Path $OutDir 'tray-busy.ico')      -Accent $AccentEmerald -Mode Glyph
Save-Ico -Path (Join-Path $OutDir 'tray-error.ico')     -Accent $AccentCoral   -Mode Glyph

Write-Host "Done."
