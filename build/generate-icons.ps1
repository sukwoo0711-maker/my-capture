# Generates multi-resolution .ico files for MyCapture.
#
# The mark is the Corner Seal (design-system/mycapture/MASTER.md): an open selection frame
# whose release corner carries a square seal. The app icon and the idle tray icon use the
# Signal Magenta seal; the tray state icons recolour the seal and keep the same frame.
#
# Each size is drawn independently so 16x16 tray icons stay crisp rather than
# being downscaled from one large bitmap.
#
# Small entries are written as uncompressed 32bpp DIBs (BITMAPINFOHEADER + BGRA +
# AND mask) for the widest compatibility; 128/256 are written as PNG because a
# 256x256 DIB entry alone is 256KB.
#
# Usage:  powershell -NoProfile -ExecutionPolicy Bypass -File build\generate-icons.ps1
#
# Store mode (MSIX logo assets for the Microsoft Store package):
#   powershell -NoProfile -File build\generate-icons.ps1 -StoreAssetsDir <dir> [-StoreListingIconPath <file.png>]
# Writes only the PNG logo set (StoreLogo, Square44x44Logo, Square150x150Logo), each size
# drawn natively with the app.ico plate look, into <dir>, plus an optional 300x300
# Partner Center listing icon. No .ico files are written and the Assets folder is not touched.

param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\MyCapture.App\Assets'),

    # Store mode: when set, only the MSIX PNG logo set is written to this directory
    # and no .ico file is produced.
    [string]$StoreAssetsDir,

    # Store mode only: also write a 300x300 plate PNG to exactly this file path
    # (the Partner Center listing tile icon).
    [string]$StoreListingIconPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$StoreMode = -not [string]::IsNullOrWhiteSpace($StoreAssetsDir)
if (-not $StoreMode -and -not [string]::IsNullOrWhiteSpace($StoreListingIconPath)) {
    throw '-StoreListingIconPath requires -StoreAssetsDir.'
}

if (-not $StoreMode) {
    $OutDir = [System.IO.Path]::GetFullPath($OutDir)
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

$dibSizes = @(16, 20, 24, 32, 40, 48, 64)
$pngSizes = @(128, 256)

# Corner Seal palette. Tray states only recolour the seal (and capturing turns it round, the
# familiar REC dot); the tooltip/status text still carries the state so colour is never the
# only cue.
$SealMagenta  = [System.Drawing.Color]::FromArgb(255, 240, 54, 155)   # #F0369B idle / brand
$SealAmber    = [System.Drawing.Color]::FromArgb(255, 245, 185, 66)   # #F5B942 capturing
$SealEmerald  = [System.Drawing.Color]::FromArgb(255, 69, 214, 162)   # #45D6A2 busy / exporting
$SealRed      = [System.Drawing.Color]::FromArgb(255, 255, 107, 95)   # #FF6B5F error
$PlateTop     = [System.Drawing.Color]::FromArgb(255, 27, 32, 41)     # #1B2029
$PlateBottom  = [System.Drawing.Color]::FromArgb(255, 13, 16, 22)     # #0D1016
$PlateEdge    = [System.Drawing.Color]::FromArgb(40, 246, 248, 252)   # #F6F8FC at 16%
$FrameWhite   = [System.Drawing.Color]::FromArgb(255, 246, 248, 252)  # #F6F8FC

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
        [System.Drawing.Color]$Seal,

        # Square is the seal; Round is the capturing state's REC dot.
        [ValidateSet('Square', 'Round')]
        [string]$SealShape = 'Square'
    )

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    # Pixel (0,0) covers [0,1] so fractional geometry lands where the size table says.
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$Size

    # Plate: a graphite rounded square with a quiet top-to-bottom tone. The gradient brush spans
    # the whole plate and mirrors past its ends; a brush shorter than the plate tiles by default
    # and paints a hard band where the gradient restarts.
    $pad = [Math]::Max(0.5, $s * 0.03)
    $plateSize = $s - ($pad * 2)
    $plateRadius = $s * 0.22
    $plate = New-RoundedPath -X $pad -Y $pad -W $plateSize -H $plateSize -R $plateRadius
    $toneRect = New-Object System.Drawing.RectangleF([float]$pad, [float]$pad, [float]$plateSize, [float]$plateSize)
    $tone = New-Object System.Drawing.Drawing2D.LinearGradientBrush($toneRect, $PlateTop, $PlateBottom,
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $tone.WrapMode = [System.Drawing.Drawing2D.WrapMode]::TileFlipXY
    $g.FillPath($tone, $plate)
    $tone.Dispose()

    # Inner edge: drawn inside the plate so it lifts the silhouette on dark taskbars without
    # leaving a light halo outside the plate on light ones.
    $edgeWidth = [Math]::Max(1.0, $s * 0.018)
    $edgePath = New-RoundedPath -X ($pad + $edgeWidth / 2) -Y ($pad + $edgeWidth / 2) `
        -W ($plateSize - $edgeWidth) -H ($plateSize - $edgeWidth) -R ($plateRadius - $edgeWidth / 2)
    $edge = New-Object System.Drawing.Pen($PlateEdge, [float]$edgeWidth)
    $g.DrawPath($edge, $edgePath)
    $edge.Dispose()
    $edgePath.Dispose()
    $plate.Dispose()

    # Frame: the selection the user drew. It stays open at the release corner, where the
    # seal sits; small sizes stop the frame earlier so the gap survives resampling.
    $thickness = [Math]::Max(1.5, [Math]::Round($s * 0.092))
    $lo = $s * 0.233
    $hi = $s * 0.725
    $r = [Math]::Max(1.0, $s * 0.092)
    $d = $r * 2
    $openEnd = if ($Size -le 24) { $s * 0.43 } else { $s * 0.475 }

    $frame = New-Object System.Drawing.Drawing2D.GraphicsPath
    $frame.AddLine([float]$openEnd, [float]$hi, [float]($lo + $r), [float]$hi)
    $frame.AddArc([float]$lo, [float]($hi - $d), [float]$d, [float]$d, [float]90, [float]90)
    $frame.AddLine([float]$lo, [float]($hi - $r), [float]$lo, [float]($lo + $r))
    $frame.AddArc([float]$lo, [float]$lo, [float]$d, [float]$d, [float]180, [float]90)
    $frame.AddLine([float]($lo + $r), [float]$lo, [float]($hi - $r), [float]$lo)
    $frame.AddArc([float]($hi - $d), [float]$lo, [float]$d, [float]$d, [float]270, [float]90)
    $frame.AddLine([float]$hi, [float]($lo + $r), [float]$hi, [float]$openEnd)

    $pen = New-Object System.Drawing.Pen($FrameWhite, [float]$thickness)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $frame)
    $pen.Dispose()
    $frame.Dispose()

    # Seal: stamped where the drag was released, the moment the capture is kept.
    $sealLo = $s * 0.558
    $sealSize = ($s * 0.825) - $sealLo
    $sealBrush = New-Object System.Drawing.SolidBrush($Seal)
    if ($SealShape -eq 'Round') {
        $g.FillEllipse($sealBrush, [float]$sealLo, [float]$sealLo, [float]$sealSize, [float]$sealSize)
    }
    else {
        $sealPath = New-RoundedPath -X $sealLo -Y $sealLo -W $sealSize -H $sealSize -R ([Math]::Max(0.8, $s * 0.0625))
        $g.FillPath($sealBrush, $sealPath)
        $sealPath.Dispose()
    }
    $sealBrush.Dispose()

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
        [System.Drawing.Color]$Seal,
        [ValidateSet('Square', 'Round')]
        [string]$SealShape = 'Square'
    )

    $entries = @()

    foreach ($size in $dibSizes) {
        $bmp = Draw-Icon -Size $size -Seal $Seal -SealShape $SealShape
        $entries += , @{ Size = $size; Bytes = [byte[]](Get-DibBytes -Bitmap $bmp) }
        $bmp.Dispose()
    }

    foreach ($size in $pngSizes) {
        $bmp = Draw-Icon -Size $size -Seal $Seal -SealShape $SealShape
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

    Write-Host ("  {0,-22} {1,9:N0} bytes  {2,-6} {3} sizes" -f `
        [System.IO.Path]::GetFileName($Path), (Get-Item $Path).Length, $SealShape, $entries.Count)
}

function Resolve-OutputPath {
    param([string]$Path)
    # Resolve against the PowerShell location rather than the process working directory.
    $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

# Rounds half away from zero so 62.5 -> 63 and 187.5 -> 188 (Math.Round defaults to banker's rounding).
function Get-RoundedSize {
    param([double]$Value)
    [int][Math]::Round($Value, [MidpointRounding]::AwayFromZero)
}

# Draws the app.ico plate at its native pixel size. When -CanvasSize is larger, the plate is
# composited 1:1 (no resampling) at the centre of a transparent canvas, which gives the
# padding that tile logos need. The icon is never resized from another bitmap.
function Save-Png {
    param(
        [string]$Path,
        [int]$Size,
        [int]$CanvasSize = 0
    )

    $icon = $null
    $canvas = $null
    $g = $null
    try {
        $icon = Draw-Icon -Size $Size -Seal $SealMagenta
        $target = $icon
        $detail = ''

        if ($CanvasSize -gt $Size) {
            $canvas = New-Object System.Drawing.Bitmap($CanvasSize, $CanvasSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($canvas)
            # SourceCopy onto the transparent canvas keeps the plate's alpha channel exact;
            # SourceOver would re-blend anti-aliased edge pixels and shift their alpha by 1.
            $g.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)

            $offset = [int][Math]::Floor(($CanvasSize - $Size) / 2)
            $g.DrawImage($icon, $offset, $offset, $Size, $Size)

            $target = $canvas
            $detail = "  plate {0}px centered" -f $Size
        }

        $dir = [System.IO.Path]::GetDirectoryName($Path)
        if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [System.IO.File]::WriteAllBytes($Path, [byte[]](Get-PngBytes -Bitmap $target))

        Write-Host ("  {0,-56} {1,7:N0} bytes  {2,3}x{2,-3}{3}" -f `
            [System.IO.Path]::GetFileName($Path), (Get-Item $Path).Length, $target.Width, $detail)
    }
    finally {
        if ($g)      { $g.Dispose() }
        if ($canvas) { $canvas.Dispose() }
        if ($icon)   { $icon.Dispose() }
    }
}

# Writes an identical-content copy of an already generated PNG (altform variants).
function Copy-Png {
    param([string]$Source, [string]$Path)

    [System.IO.File]::Copy($Source, $Path, $true)
    $info = Get-Item $Path
    $dims = [System.Drawing.Image]::FromFile($Path)
    try { $px = $dims.Width } finally { $dims.Dispose() }
    Write-Host ("  {0,-56} {1,7:N0} bytes  {2,3}x{2,-3}  copy" -f $info.Name, $info.Length, $px)
}

function Save-StoreAssets {
    param([string]$Dir, [string]$ListingPath)

    New-Item -ItemType Directory -Force -Path $Dir | Out-Null

    $scales = @(100, 125, 150, 200, 400)

    # Plate fills the image.
    foreach ($scale in $scales) {
        $size = Get-RoundedSize (50.0 * $scale / 100)
        Save-Png -Path (Join-Path $Dir "StoreLogo.scale-$scale.png") -Size $size
    }

    foreach ($scale in $scales) {
        $size = Get-RoundedSize (44.0 * $scale / 100)
        Save-Png -Path (Join-Path $Dir "Square44x44Logo.scale-$scale.png") -Size $size
    }

    # Taskbar / Start list sizes. The unplated and light-unplated forms reuse the plated
    # image so the plate stays the brand mark on every system theme.
    foreach ($n in @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)) {
        $plated = Join-Path $Dir "Square44x44Logo.targetsize-$n.png"
        Save-Png -Path $plated -Size $n
        Copy-Png -Source $plated -Path (Join-Path $Dir "Square44x44Logo.targetsize-${n}_altform-unplated.png")
        Copy-Png -Source $plated -Path (Join-Path $Dir "Square44x44Logo.targetsize-${n}_altform-lightunplated.png")
    }

    # Medium tile: the plate is 60% of the tile and centred on a transparent canvas.
    foreach ($scale in $scales) {
        $canvasSize = Get-RoundedSize (150.0 * $scale / 100)
        $plateSize = Get-RoundedSize (0.6 * $canvasSize)
        Save-Png -Path (Join-Path $Dir "Square150x150Logo.scale-$scale.png") -Size $plateSize -CanvasSize $canvasSize
    }

    if ($ListingPath) {
        Save-Png -Path $ListingPath -Size 300
    }
}

if ($StoreMode) {
    $StoreAssetsDir = Resolve-OutputPath $StoreAssetsDir
    $listingPath = $null
    if (-not [string]::IsNullOrWhiteSpace($StoreListingIconPath)) {
        $listingPath = Resolve-OutputPath $StoreListingIconPath
    }

    Write-Host "Generating Microsoft Store assets into $StoreAssetsDir"
    Save-StoreAssets -Dir $StoreAssetsDir -ListingPath $listingPath
}
else {
    Write-Host "Generating icons into $OutDir"

    Save-Ico -Path (Join-Path $OutDir 'app.ico')            -Seal $SealMagenta
    Save-Ico -Path (Join-Path $OutDir 'tray-idle.ico')      -Seal $SealMagenta
    Save-Ico -Path (Join-Path $OutDir 'tray-capturing.ico') -Seal $SealAmber   -SealShape Round
    Save-Ico -Path (Join-Path $OutDir 'tray-busy.ico')      -Seal $SealEmerald
    Save-Ico -Path (Join-Path $OutDir 'tray-error.ico')     -Seal $SealRed
}

Write-Host "Done."
