namespace MyCapture.Core.Capture;

/// <summary>A rectangular, tightly packed, top-down BGRA32 frame.</summary>
public sealed class ScrollFrame
{
    public const int BytesPerPixel = 4;

    public ScrollFrame(int width, int height, byte[] pixels)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        ArgumentNullException.ThrowIfNull(pixels);

        long expected = checked((long)width * height * BytesPerPixel);
        if (expected > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The frame is too large for a managed byte array.");
        }

        if (pixels.LongLength < expected)
        {
            throw new ArgumentException(
                $"Pixel buffer is {pixels.LongLength} bytes but {expected} were expected for {width}x{height} BGRA32.",
                nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => checked(Width * BytesPerPixel);

    public byte[] Pixels { get; }
}

/// <summary>Pure pixel-space settings for deterministic scrolling-image stitching.</summary>
public readonly record struct ScrollStitchOptions(
    int FixedHeaderHeight = 0,
    double MaxRowMismatchRatio = 0.02,
    int MinOverlapRows = 8,
    bool AutoDetectFixedHeader = true,
    double MaxAutoHeaderRatio = 0.35,
    long MaxOutputBytes = 256L * 1024 * 1024,
    int MaxOutputHeight = 32760)
{
    // Parameterless construction of a record struct zeroes fields instead of applying primary
    // constructor defaults, so callers use this explicit production-safe value.
    public static ScrollStitchOptions Default => new(
        FixedHeaderHeight: 0,
        MaxRowMismatchRatio: 0.02,
        MinOverlapRows: 8,
        AutoDetectFixedHeader: true,
        MaxAutoHeaderRatio: 0.35,
        MaxOutputBytes: 256L * 1024 * 1024,
        MaxOutputHeight: 32760);
}

public enum ScrollAppendKind
{
    Seeded,
    Appended,
    NoNewContent,
    NoOverlap,
    /// <summary>The verified next rows would exceed the configured byte or height bound.</summary>
    LimitReached,
}

public readonly record struct ScrollAppendResult(
    ScrollAppendKind Kind,
    int OverlapRows,
    int AppendedRows);

/// <summary>
/// Deterministically stitches successive scroll frames by matching each frame's leading
/// content against the current canvas tail. Unmatched frames are never appended.
/// </summary>
public sealed class ScrollStitcher
{
    private readonly ScrollStitchOptions _options;
    private readonly int _width;
    private readonly int _stride;
    private readonly MemoryStream _canvas;

    private int _canvasHeight;
    private bool _seeded;
    private bool _headerResolved;
    private int _effectiveHeader;

    public ScrollStitcher(int frameWidth, ScrollStitchOptions options)
    {
        if (frameWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameWidth));
        }

        if (options.FixedHeaderHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Fixed header height cannot be negative.");
        }

        if (options.MinOverlapRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum overlap rows must be at least one.");
        }

        if (options.MaxRowMismatchRatio is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Row mismatch ratio must be between zero and one.");
        }

        if (options.MaxAutoHeaderRatio is < 0 or > 0.8)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Auto-header ratio must be between zero and 0.8.");
        }

        if (options.MaxOutputBytes < 1 || options.MaxOutputBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Output byte limit is outside managed-array bounds.");
        }

        if (options.MaxOutputHeight < 1 || options.MaxOutputHeight > 32760)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Output height must be within WPF's safe bitmap range.");
        }

        _width = frameWidth;
        _stride = checked(frameWidth * ScrollFrame.BytesPerPixel);
        _options = options;
        _canvas = new MemoryStream(capacity: Math.Min((int)options.MaxOutputBytes, 4 * 1024 * 1024));
        _effectiveHeader = options.FixedHeaderHeight;
        _headerResolved = options.FixedHeaderHeight > 0 || !options.AutoDetectFixedHeader;
    }

    public int Height => _canvasHeight;

    public int Width => _width;

    public int EffectiveHeaderHeight => _effectiveHeader;

    public bool HasContent => _seeded && _canvasHeight > 0;

    public ScrollAppendResult Append(ScrollFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != _width)
        {
            throw new ArgumentException(
                $"Frame width {frame.Width} does not match the stitch width {_width}.",
                nameof(frame));
        }

        if (!_seeded)
        {
            if (!CanGrowTo(frame.Height))
            {
                return new ScrollAppendResult(ScrollAppendKind.LimitReached, 0, 0);
            }

            Seed(frame);
            return new ScrollAppendResult(ScrollAppendKind.Seeded, 0, _canvasHeight);
        }

        ResolveHeaderIfNeeded(frame);
        int header = Math.Min(_effectiveHeader, Math.Max(0, frame.Height - _options.MinOverlapRows));
        int overlap = FindOverlap(frame, header);
        if (overlap < 0)
        {
            return new ScrollAppendResult(ScrollAppendKind.NoOverlap, 0, 0);
        }

        int firstNewRow = header + overlap;
        int newRows = frame.Height - firstNewRow;
        if (newRows <= 0)
        {
            return new ScrollAppendResult(ScrollAppendKind.NoNewContent, overlap, 0);
        }

        if (!CanGrowTo(checked(_canvasHeight + newRows)))
        {
            return new ScrollAppendResult(ScrollAppendKind.LimitReached, overlap, 0);
        }

        AppendRows(frame, firstNewRow, newRows);
        return new ScrollAppendResult(ScrollAppendKind.Appended, overlap, newRows);
    }

    public ScrollFrame ToImage()
    {
        if (!HasContent)
        {
            throw new InvalidOperationException("No frames have been stitched yet.");
        }

        return new ScrollFrame(_width, _canvasHeight, _canvas.ToArray());
    }

    private bool CanGrowTo(int height)
    {
        if (height <= 0 || height > _options.MaxOutputHeight)
        {
            return false;
        }

        long bytes = checked((long)_stride * height);
        return bytes <= _options.MaxOutputBytes && bytes <= Array.MaxLength;
    }

    private void Seed(ScrollFrame frame)
    {
        int bytes = checked(_stride * frame.Height);
        _canvas.Write(frame.Pixels, 0, bytes);
        _canvasHeight = frame.Height;
        _seeded = true;
        _effectiveHeader = Math.Min(_effectiveHeader, Math.Max(0, frame.Height - _options.MinOverlapRows));
    }

    /// <summary>
    /// Detects an unchanged leading prefix between the seed and second frame. The search is
    /// capped to a fraction of the frame so a duplicate end frame cannot be mistaken for an
    /// all-page header; the remaining content still resolves as a full overlap.
    /// </summary>
    private void ResolveHeaderIfNeeded(ScrollFrame frame)
    {
        if (_headerResolved)
        {
            return;
        }

        _headerResolved = true;
        int contentFloor = _options.MinOverlapRows;
        int ratioCap = (int)Math.Floor(frame.Height * _options.MaxAutoHeaderRatio);
        int maxRows = Math.Min(ratioCap, Math.Min(frame.Height, _canvasHeight) - contentFloor);
        if (maxRows <= 0)
        {
            _effectiveHeader = 0;
            return;
        }

        byte[] canvas = _canvas.GetBuffer();
        int equalPrefix = 0;
        for (int row = 0; row < maxRows; row++)
        {
            if (!RowsMatch(frame.Pixels, row * _stride, canvas, row * _stride))
            {
                break;
            }

            equalPrefix++;
        }

        _effectiveHeader = equalPrefix;
    }

    private int FindOverlap(ScrollFrame frame, int header)
    {
        int frameContent = frame.Height - header;
        int maxOverlap = Math.Min(frameContent, _canvasHeight - header);
        if (maxOverlap < _options.MinOverlapRows)
        {
            return -1;
        }

        // Longest trustworthy overlap wins, preventing a short coincidental row pattern from
        // duplicating content.
        for (int overlap = maxOverlap; overlap >= _options.MinOverlapRows; overlap--)
        {
            if (Matches(frame, header, overlap))
            {
                return overlap;
            }
        }

        return -1;
    }

    private bool Matches(ScrollFrame frame, int header, int overlap)
    {
        byte[] canvas = _canvas.GetBuffer();
        int canvasStartRow = _canvasHeight - overlap;
        for (int row = 0; row < overlap; row++)
        {
            int frameOffset = (header + row) * _stride;
            int canvasOffset = (canvasStartRow + row) * _stride;
            if (!RowsMatch(frame.Pixels, frameOffset, canvas, canvasOffset))
            {
                return false;
            }
        }

        return true;
    }

    private bool RowsMatch(byte[] left, int leftOffset, byte[] right, int rightOffset)
    {
        int maxMismatchBytes = (int)Math.Floor(_stride * _options.MaxRowMismatchRatio);
        int mismatch = 0;
        for (int b = 0; b < _stride; b++)
        {
            if (left[leftOffset + b] == right[rightOffset + b])
            {
                continue;
            }

            mismatch++;
            if (mismatch > maxMismatchBytes)
            {
                return false;
            }
        }

        return true;
    }

    private void AppendRows(ScrollFrame frame, int firstRow, int rowCount)
    {
        int addedBytes = checked(rowCount * _stride);
        _canvas.Write(frame.Pixels, checked(firstRow * _stride), addedBytes);
        _canvasHeight = checked(_canvasHeight + rowCount);
    }
}
