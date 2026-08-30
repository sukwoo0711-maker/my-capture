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

# Accent palette. Tray state colours are deliberately far apart in hue so the
# state is readable at 16px without relying on shape differences.
$AccentBlue    = [System.Drawing.Color]::FromArgb(255, 96, 165, 250)
$AccentAmber   = [System.Drawing.Color]::FromArgb(255, 251, 191, 36)
$AccentEmerald = [System.Drawing.Color]::FromArgb(255, 52, 211, 153)

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

        # 'Plate' draws the accent frame on a dark rounded square. Used for the
        # application icon, where the host surface (Explorer, installer, Alt-Tab)
        # is unpredictable but always reasonably large.
        #
        # 'Glyph' draws the frame alone over a dark halo. Used for tray icons: at
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
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF(0, 0)),
            (New-Object System.Drawing.PointF($s, $s)),
            [System.Drawing.Color]::FromArgb(255, 44, 55, 72),
            [System.Drawing.Color]::FromArgb(255, 20, 26, 36))
        $g.FillPath($bg, $plate)
        $bg.Dispose()
        $plate.Dispose()

        $margin = [Math]::Round($s * 0.20)
        $thickness = [Math]::Max(2.0, [Math]::Round($s * 0.075))
    }
    else {
        # No plate to sit inside, so the frame can use nearly the whole canvas and
        # a heavier stroke compensates for the missing backdrop.
        $margin = [Math]::Max(1.0, [Math]::Round($s * 0.10))
        $thickness = [Math]::Max(2.0, [Math]::Round($s * 0.105))
    }

    # Capture frame: four corner brackets.
    #
    # Arm length is derived as 35% of the frame span rather than hand-tuned, which
    # leaves a 30% gap in the middle of each edge at every size. Longer arms make
    # the brackets merge into a closed ring and the icon stops reading as a crop
    # marquee.
    $span = $s - ($margin * 2)
    $arm = [Math]::Max(2.0, [Math]::Round($span * 0.35))

    $l = [float]$margin
    $r = [float]($s - $margin)
    $t = [float]$margin
    $b = [float]($s - $margin)

    $corners = @(
        @{ Cx = $l; Cy = $t; Dx = 1;  Dy = 1 },
        @{ Cx = $r; Cy = $t; Dx = -1; Dy = 1 },
        @{ Cx = $l; Cy = $b; Dx = 1;  Dy = -1 },
        @{ Cx = $r; Cy = $b; Dx = -1; Dy = -1 }
    )

    $paths = @()
    foreach ($corner in $corners) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddLine(($corner.Cx + ($arm * $corner.Dx)), $corner.Cy, $corner.Cx, $corner.Cy)
        $path.AddLine($corner.Cx, $corner.Cy, $corner.Cx, ($corner.Cy + ($arm * $corner.Dy)))
        $paths += , $path
    }

    $dotR = $s * 0.10
    $c = $s / 2.0

    # In plate mode the dot is skipped below 24px: the frame gap is only a few
    # pixels there and a dot would visually close it up. Glyph mode uses a larger
    # frame so the dot always fits.
    $drawDot = ($Size -ge 24) -or ($Mode -eq 'Glyph')

    if ($Mode -eq 'Glyph') {
        $haloWidth = $thickness + [Math]::Max(1.5, $s * 0.055)
        $haloColor = [System.Drawing.Color]::FromArgb(160, 12, 16, 22)

        $halo = New-Object System.Drawing.Pen($haloColor, $haloWidth)
        $halo.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
        $halo.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
        $halo.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        foreach ($p in $paths) { $g.DrawPath($halo, $p) }
        $halo.Dispose()

        if ($drawDot) {
            $haloBrush = New-Object System.Drawing.SolidBrush($haloColor)
            $hr = $dotR + (($haloWidth - $thickness) / 2.0)
            $g.FillEllipse($haloBrush, ($c - $hr), ($c - $hr), ($hr * 2), ($hr * 2))
            $haloBrush.Dispose()
        }
    }

    # Flat caps keep the arm ends exactly at the computed coordinates so the gap
    # stays predictable; a round join still softens the corner itself.
    $pen = New-Object System.Drawing.Pen($Accent, $thickness)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    foreach ($p in $paths) { $g.DrawPath($pen, $p) }
    $pen.Dispose()

    foreach ($p in $paths) { $p.Dispose() }

    if ($drawDot) {
        $dotBrush = New-Object System.Drawing.SolidBrush($Accent)
        $g.FillEllipse($dotBrush, ($c - $dotR), ($c - $dotR), ($dotR * 2), ($dotR * 2))
        $dotBrush.Dispose()
    }

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

Save-Ico -Path (Join-Path $OutDir 'app.ico')            -Accent $AccentBlue    -Mode Plate
Save-Ico -Path (Join-Path $OutDir 'tray-idle.ico')      -Accent $AccentBlue    -Mode Glyph
Save-Ico -Path (Join-Path $OutDir 'tray-capturing.ico') -Accent $AccentAmber   -Mode Glyph
Save-Ico -Path (Join-Path $OutDir 'tray-busy.ico')      -Accent $AccentEmerald -Mode Glyph

Write-Host "Done."
