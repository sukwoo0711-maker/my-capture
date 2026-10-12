param(
    [string]$ReviewRoot = 'C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator',
    [string]$DestinationAssets = 'C:/GitRepositories/my-capture-edit-recovery/src/MyCapture.App/Assets/Tutorials',
    [string]$OutputDirectory = 'C:/GitRepositories/my-capture-edit-recovery/build/tools/TutorialGifGenerator/review-smooth-final',
    [string]$PinBatchName = 'review-attempt-batch-8',
    [switch]$CopyAssets
)
$ErrorActionPreference = 'Stop'
$taskReviewRoot = [IO.Path]::GetFullPath($ReviewRoot)
$taskDestination = [IO.Path]::GetFullPath($DestinationAssets)
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'Use a new output directory; preserve existing manifests and attempts.' }
if (-not (Test-Path -LiteralPath $taskDestination -PathType Container)) { throw 'Destination assets directory must already exist.' }
$catalog = Get-Content -LiteralPath (Join-Path $taskReviewRoot 'review-attempt-preflight-5/receipt.json') -Encoding UTF8 -Raw | ConvertFrom-Json
$featureBatches = @{}
@{
    'review-attempt-batch-2' = 'text-copy,capture-region'
    'review-attempt-batch-3' = 'record-pause,capture-scroll,capture-delay,color-copy,image-rectangle,image-ellipse,image-arrow,image-pen,image-highlighter'
    'review-attempt-batch-4' = 'image-text,image-step-number,image-crop,image-save,image-copy,video-play,video-frame-step'
    'review-attempt-batch-5' = 'capture-window,capture-fullscreen,library-open,quick-access'
    'review-attempt-batch-6' = 'record-region,image-mosaic,image-blur,image-rotate,video-trim,video-gif-export'
    'review-attempt-batch-7' = 'folder-create,folder-rename,library-move,library-delete'
}.GetEnumerator() | ForEach-Object {
    $batchName = $_.Key
    $_.Value.Split(',') | ForEach-Object { $featureBatches[$_] = $batchName }
}
'pin-clipboard,pin-hide-all,pin-click-through,pin-restore,pin-copy,pin-save'.Split(',') | ForEach-Object { $featureBatches[$_] = $PinBatchName }
if ($catalog.Features.Count -ne 38 -or $featureBatches.Count -ne 38) { throw 'Expected exactly 38 feature mappings.' }
$assets = foreach ($feature in $catalog.Features) {
    foreach ($language in @('ko', 'en')) {
        $fileName = "$feature.$language.gif"
        $batchDirectory = Join-Path $taskReviewRoot $featureBatches[$feature]
        $sourceGif = Join-Path $batchDirectory $fileName
        $sourceReceipt = Join-Path $batchDirectory "$feature.$language.receipt.json"
        $receipt = Get-Content -LiteralPath $sourceReceipt -Encoding UTF8 -Raw | ConvertFrom-Json
        $metrics = $receipt.Validation
        $sourceHash = (Get-FileHash -LiteralPath $sourceGif -Algorithm SHA256).Hash
        if ($sourceHash -ne $metrics.Sha256 -or (Get-Item -LiteralPath $sourceGif).Length -ne $metrics.Bytes) { throw "Source mismatch: $fileName" }
        if ($receipt.Feature -ne $feature -or $receipt.Language -ne $language -or $receipt.PayloadSha256 -ne $catalog.PayloadSha256) { throw "Provenance mismatch: $fileName" }
        if ($metrics.Frames -gt 160 -or $metrics.DurationMs -lt 10000 -or $metrics.DurationMs -gt 12000 -or $metrics.Bytes -gt 3MB -or $metrics.DecodedFrameBytes -gt 40MB -or $metrics.DecodedFrameBytes -ne $metrics.Frames * 640 * 360 -or $metrics.LoopCount -ne 0 -or $metrics.Disposal -ne 1 -or -not $metrics.FullOpaqueFrames) { throw "Receipt budget mismatch: $fileName" }
        [PSCustomObject]@{
            Feature = $feature; Language = $language; SourceBatch = $featureBatches[$feature]
            SourceGifPath = $sourceGif; SourceReceiptPath = $sourceReceipt
            SourceContactSheetPath = Join-Path $batchDirectory "$feature.$language.contact-sheet.png"
            SourceMotionSheetPath = Join-Path $batchDirectory "$feature.$language.motion-sheet.png"
            ReceiptSha256 = (Get-FileHash -LiteralPath $sourceReceipt -Algorithm SHA256).Hash
            Sha256 = $sourceHash; Bytes = [long]$metrics.Bytes; Frames = [int]$metrics.Frames
            DurationMs = [int]$metrics.DurationMs; DecodedBytes = [long]$metrics.DecodedFrameBytes
            DelaysCentiseconds = $metrics.DelaysCentiseconds
            DestinationGifPath = Join-Path $taskDestination $fileName
            CommittedReceiptRelativePath = "receipts/$feature.$language.receipt.json"
            GenerateMs = $receipt.GenerateMs; ValidateMs = $receipt.ValidateMs
            StartedUtc = $receipt.StartedUtc; CompletedUtc = $receipt.CompletedUtc
            DestinationSha256 = $null
        }
    }
}
if ($assets.Count -ne 76 -or @($assets | Group-Object Feature,Language | Where-Object Count -ne 1).Count -ne 0) { throw 'Expected 76 unique feature/language assets.' }
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$receiptOutput = Join-Path $taskOutput 'receipts'
New-Item -ItemType Directory -Path $receiptOutput | Out-Null
# Preserve physical AS-IS text files before an authorized content replacement.
if ($CopyAssets) {
    $asIsOutput = Join-Path $taskOutput 'as-is'
    New-Item -ItemType Directory -Path $asIsOutput | Out-Null
    foreach ($language in @('ko', 'en')) {
        $oldTextGif = Join-Path $taskDestination "text-copy.$language.gif"
        if (Test-Path -LiteralPath $oldTextGif) { Copy-Item -LiteralPath $oldTextGif -Destination (Join-Path $asIsOutput "text-copy.$language.gif") }
    }
}
foreach ($asset in $assets) {
    Copy-Item -LiteralPath $asset.SourceReceiptPath -Destination (Join-Path $receiptOutput ([IO.Path]::GetFileName($asset.SourceReceiptPath)))
    if ($CopyAssets) {
        Copy-Item -LiteralPath $asset.SourceGifPath -Destination $asset.DestinationGifPath
        $asset.DestinationSha256 = (Get-FileHash -LiteralPath $asset.DestinationGifPath -Algorithm SHA256).Hash
        if ($asset.DestinationSha256 -ne $asset.Sha256) { throw "Copied asset mismatch: $($asset.Feature).$($asset.Language)" }
    }
}
$manifest = [PSCustomObject]@{
    CreatedUtc = [DateTimeOffset]::UtcNow.ToString('o'); WorkingDirectory = [Environment]::CurrentDirectory
    SourceRevision = $catalog.SourceRevision; SourcePayload = $catalog.Payload; SourcePayloadSha256 = $catalog.PayloadSha256
    Framework = $catalog.Framework; Width = 640; Height = 360; Features = 38; Count = $assets.Count
    CopiedToProduct = [bool]$CopyAssets
    TotalBytes = [long]($assets | Measure-Object Bytes -Sum).Sum
    TotalFrames = [long]($assets | Measure-Object Frames -Sum).Sum
    TotalDecodedBytes = [long]($assets | Measure-Object DecodedBytes -Sum).Sum
    MaxBytes = [long]($assets | Measure-Object Bytes -Maximum).Maximum
    MaxFrames = [int]($assets | Measure-Object Frames -Maximum).Maximum
    MaxDecodedBytes = [long]($assets | Measure-Object DecodedBytes -Maximum).Maximum
    BufferPolicy = 'Aggregate decoded bytes is a sum of independent clips, not resident memory. Generation/actual decode verification is sequential per asset.'
    Validation = 'Each source receipt records completed actual product decoder/player validation. Assembly checks receipt provenance/budgets and SHA256 without loading all images.'
    Assets = @($assets)
}
$manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskOutput 'selected-source-manifest.json') -Encoding UTF8
$assets | Select-Object Feature,Language,SourceBatch,SourceGifPath,SourceReceiptPath,Sha256,Bytes,Frames,DurationMs,DecodedBytes,DestinationGifPath,DestinationSha256 | Export-Csv -LiteralPath (Join-Path $taskOutput 'selected-source-manifest.csv') -Encoding UTF8 -NoTypeInformation
Write-Output ("ASSEMBLY=PASS ASSETS={0} BYTES={1} FRAMES={2} MAX_DECODED_BYTES={3} COPIED={4}" -f $manifest.Count,$manifest.TotalBytes,$manifest.TotalFrames,$manifest.MaxDecodedBytes,$manifest.CopiedToProduct)
