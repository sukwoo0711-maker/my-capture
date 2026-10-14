# Generates multi-resolution .ico files for MyCapture.
#
# Each size is drawn independently so 16x16 tray icons stay crisp rather than
# being downscaled from one large bitmap.
#
# Small entries are written as uncompressed 32bpp DIBs (BITMAPINFOHEADER + BGRA +
# AND mask) for the widest compatibility; 128/256 are written as PNG because a
# 256x256 DIB entry alone is 256KB.
#
# Usage:  powershell -NoProfile -File build\generate-icons.ps1
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

# M06: graphite #5B6571 -> #222933, warm white #FFF8F4,
# pin red #F35B4D -> #BB252D with #F87667 head highlight from 48px.
# Dedicated small drawings use flat #38424D / #E0453C and integer brackets.
# State signs replace only the pin: green circle, blue hourglass, amber warning.
Add-Type -AssemblyName PresentationCore, WindowsBase
$brandRoot = Join-Path $PSScriptRoot '../docs/design/brand'
$brandDrawings = @{}

function Get-BrandDrawing {
    param([int]$Size)
    $name = if ($Size -eq 16) { 'M06-16.svg' } elseif ($Size -eq 20) { 'M06-20.svg' } elseif ($Size -eq 24) { 'M06-24.svg' } elseif ($Size -lt 48) { 'M06-flat.svg' } else { 'M06.svg' }
    if ($brandDrawings.ContainsKey($name)) { return $brandDrawings[$name] }

    # These approved masters contain only filled paths and absolute linear gradients.
    # WPF parses the original path data directly, preserving curves and fill rules.
    [xml]$svg = [IO.File]::ReadAllText((Join-Path $brandRoot $name))
    $brushes = @{}
    foreach ($gradient in $svg.svg.defs.linearGradient) {
        if ($null -eq $gradient) { continue }
        $brush = [System.Windows.Media.LinearGradientBrush]::new()
        $brush.MappingMode = [System.Windows.Media.BrushMappingMode]::Absolute
        $brush.StartPoint = [System.Windows.Point]::new([double]$gradient.x1, [double]$gradient.y1)
        $brush.EndPoint = [System.Windows.Point]::new([double]$gradient.x2, [double]$gradient.y2)
        foreach ($stop in $gradient.stop) {
            $brush.GradientStops.Add([System.Windows.Media.GradientStop]::new(
                [System.Windows.Media.ColorConverter]::ConvertFromString($stop.'stop-color'), [double]$stop.offset))
        }
        $brush.Freeze()
        $brushes[$gradient.id] = $brush
    }
    $paths = @()
    foreach ($path in $svg.svg.path) {
        $rule = if ($path.'fill-rule' -eq 'evenodd') { 'F0' } else { 'F1' }
        $geometry = [System.Windows.Media.Geometry]::Parse("$rule $($path.d)")
        $geometry.Freeze()
        $brush = if ($path.fill.StartsWith('url(#')) {
            $brushes[$path.fill.Substring(5, $path.fill.Length - 6)]
        } else { [System.Windows.Media.BrushConverter]::new().ConvertFromString($path.fill) }
        $paths += @{ Geometry = $geometry; Brush = $brush }
    }
    $drawing = @{ Canvas = [double]$svg.svg.width; Paths = $paths }
    $brandDrawings[$name] = $drawing
    return $drawing
}

function Draw-Icon {
    param(
        [int]$Size,
        [ValidateSet('Idle', 'Capturing', 'Busy', 'Error')]
        [string]$State = 'Idle'
    )

    $drawing = Get-BrandDrawing -Size $Size
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    try {
        $scale = $Size / $drawing.Canvas
        $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($scale, $scale))
        $count = if ($State -eq 'Idle') { $drawing.Paths.Count } else { 2 }
        for ($i = 0; $i -lt $count; $i++) {
            $path = $drawing.Paths[$i]
            $dc.DrawGeometry($path.Brush, $null, $path.Geometry)
        }
        $dc.Pop()
        if ($State -ne 'Idle') {
            # Fixed 16-unit signs remain distinct without colour. No badge covers the frame.
            $dc.PushTransform([System.Windows.Media.ScaleTransform]::new($Size / 16.0, $Size / 16.0))
            $ink = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#222933')
            switch ($State) {
                'Capturing' {
                    $fill = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#69D5A2')
                    $centre = if ($Size -lt 32) { 11.75 } else { 12 }
                    $radius = if ($Size -lt 32) { 2.25 } else { 1.8 }
                    $dc.DrawEllipse($fill, $null, [System.Windows.Point]::new($centre, $centre), $radius, $radius)
                }
                'Busy' {
                    $fill = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#75BFFF')
                    $sign = if ($Size -lt 32) { 'M9.5 9 H13.5 V10 L12.25 11.5 L13.5 13 V14 H9.5 V13 L10.75 11.5 L9.5 10 Z' } else { 'M10.5 10.5 H13.5 V11.1 L12.6 12 L13.5 12.9 V13.5 H10.5 V12.9 L11.4 12 L10.5 11.1 Z' }
                    $dc.DrawGeometry($fill, $null, [System.Windows.Media.Geometry]::Parse($sign))
                }
                'Error' {
                    $fill = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#F5C451')
                    $sign = if ($Size -lt 32) { 'M11.5 8.5 L14 14 H9 Z' } else { 'M12 10.5 L13.75 13.5 H10.25 Z' }
                    $dc.DrawGeometry($fill, $null, [System.Windows.Media.Geometry]::Parse($sign))
                    if ($Size -lt 32) {
                        $dc.DrawRectangle($ink, $null, [System.Windows.Rect]::new(11, 10, 1, 2))
                        $dc.DrawRectangle($ink, $null, [System.Windows.Rect]::new(11, 13, 1, 0.75))
                    } else {
                        $dc.DrawRectangle($ink, $null, [System.Windows.Rect]::new(11.75, 11.5, 0.5, 0.9))
                        $dc.DrawRectangle($ink, $null, [System.Windows.Rect]::new(11.75, 12.75, 0.5, 0.4))
                    }
                }
            }
            $dc.Pop()
        }
    }
    finally { $dc.Close() }

    # Rasterize each target size directly. Only the Store canvas composition uses a bitmap.
    $target = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))
    $stream = [IO.MemoryStream]::new()
    try {
        $encoder.Save($stream)
        $stream.Position = 0
        $decoded = [System.Drawing.Bitmap]::new($stream)
        try { return $decoded.Clone([System.Drawing.Rectangle]::new(0, 0, $Size, $Size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb) }
        finally { $decoded.Dispose() }
    }
    finally { $stream.Dispose() }
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
        [ValidateSet('Idle', 'Capturing', 'Busy', 'Error')]
        [string]$State = 'Idle'
    )

    $entries = @()

    foreach ($size in $dibSizes) {
        $bmp = Draw-Icon -Size $size -State $State
        $entries += , @{ Size = $size; Bytes = [byte[]](Get-DibBytes -Bitmap $bmp) }
        $bmp.Dispose()
    }

    foreach ($size in $pngSizes) {
        $bmp = Draw-Icon -Size $size -State $State
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
        [System.IO.Path]::GetFileName($Path), (Get-Item $Path).Length, $State, $entries.Count)
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
        $icon = Draw-Icon -Size $Size
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

    Save-Ico -Path (Join-Path $OutDir 'app.ico')            -State Idle
    Save-Ico -Path (Join-Path $OutDir 'tray-idle.ico')      -State Idle
    Save-Ico -Path (Join-Path $OutDir 'tray-capturing.ico') -State Capturing
    Save-Ico -Path (Join-Path $OutDir 'tray-busy.ico')      -State Busy
    Save-Ico -Path (Join-Path $OutDir 'tray-error.ico')     -State Error
}

Write-Host "Done."
