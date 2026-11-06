using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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

internal readonly record struct ScrollOverlapSearchStatistics(
    int CandidatesConsidered,
    long RowPairChecks,
    int PixelRowComparisons,
    int VerificationRowComparisons,
    int HeaderRowComparisons,
    int DistinctRowClasses,
    int WorkLimit,
    bool StoppedAtWorkLimit);

/// <summary>
/// Deterministically stitches successive scroll frames by matching each frame's leading
/// content against the current canvas tail, longest overlap first. Exact row classes,
/// memoized tolerant comparisons and recent mismatches avoid repeated pixel work. A bounded
/// search accepts only directly verified overlaps; unmatched or work-limited frames are
/// never appended.
/// </summary>
public sealed class ScrollStitcher
{
    internal const int WorkLimitPerOverlapRow = 16;
    internal const int WorkLimitBase = 256;
    internal const int KillerCapacity = 4;
    internal const int DenseMemoMaxClasses = 1024;

    private readonly ScrollStitchOptions _options;
    private readonly int _width;
    private readonly int _stride;
    private readonly int _maxMismatchBytes;
    private readonly MemoryStream _canvas;
    private readonly Dictionary<ulong, int> _firstClassByHash = new();
    private readonly Dictionary<long, bool> _sparseMemo = new();
    private int[] _rowClasses = [];
    private int[] _classRepresentatives = [];
    private int[] _nextClassWithHash = [];
    private byte[] _denseMemo = [];

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
        _maxMismatchBytes = (int)Math.Floor(_stride * _options.MaxRowMismatchRatio);
        _canvas = new MemoryStream(capacity: Math.Min((int)options.MaxOutputBytes, 4 * 1024 * 1024));
        _effectiveHeader = options.FixedHeaderHeight;
        _headerResolved = options.FixedHeaderHeight > 0 || !options.AutoDetectFixedHeader;
    }

    public int Height => _canvasHeight;

    public int Width => _width;

    public int EffectiveHeaderHeight => _effectiveHeader;

    public bool HasContent => _seeded && _canvasHeight > 0;

    /// <summary>The most recent append's search, or zeroes when no search ran.</summary>
    internal ScrollOverlapSearchStatistics LastOverlapSearch { get; private set; }

    public ScrollAppendResult Append(ScrollFrame frame)
    {
        LastOverlapSearch = default;
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

        int headerRowComparisons = ResolveHeaderIfNeeded(frame);
        int header = Math.Min(_effectiveHeader, Math.Max(0, frame.Height - _options.MinOverlapRows));
        int overlap = FindOverlap(frame, header, headerRowComparisons);
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
    private int ResolveHeaderIfNeeded(ScrollFrame frame)
    {
        if (_headerResolved)
        {
            return 0;
        }

        _headerResolved = true;
        int contentFloor = _options.MinOverlapRows;
        int ratioCap = (int)Math.Floor(frame.Height * _options.MaxAutoHeaderRatio);
        int maxRows = Math.Min(ratioCap, Math.Min(frame.Height, _canvasHeight) - contentFloor);
        if (maxRows <= 0)
        {
            _effectiveHeader = 0;
            return 0;
        }

        byte[] canvas = _canvas.GetBuffer();
        int equalPrefix = 0;
        int comparisons = 0;
        for (int row = 0; row < maxRows; row++)
        {
            comparisons++;
            if (!RowsMatch(frame.Pixels.AsSpan(row * _stride, _stride),
                canvas.AsSpan(row * _stride, _stride), _maxMismatchBytes))
            {
                break;
            }

            equalPrefix++;
        }

        _effectiveHeader = equalPrefix;
        return comparisons;
    }

    /// <summary>
    /// Searches exact row classes from longest to shortest, rejecting recent mismatch rows
    /// first. Only uncached comparisons of different classes consume the work limit;
    /// header detection and direct verification of a successful candidate are separate.
    /// </summary>
    private int FindOverlap(ScrollFrame frame, int header, int headerRowComparisons)
    {
        int frameContent = frame.Height - header;
        int maxOverlap = Math.Min(frameContent, _canvasHeight - header);
        if (maxOverlap < _options.MinOverlapRows)
        {
            return -1;
        }

        ReadOnlySpan<byte> frameRows = frame.Pixels.AsSpan(header * _stride, maxOverlap * _stride);
        ReadOnlySpan<byte> canvasRows = _canvas.GetBuffer().AsSpan(
            (_canvasHeight - maxOverlap) * _stride, maxOverlap * _stride);
        int classCount = BuildRowClasses(frameRows, canvasRows, maxOverlap);
        _sparseMemo.Clear();
        if (classCount <= DenseMemoMaxClasses)
        {
            int usedLength = classCount * classCount;
            if (_denseMemo.Length < usedLength)
            {
                _denseMemo = new byte[usedLength];
            }

            Array.Clear(_denseMemo, 0, usedLength);
        }

        var counters = new SearchCounters
        {
            HeaderRowComparisons = headerRowComparisons,
            DistinctRowClasses = classCount,
            WorkLimit = WorkLimitPerOverlapRow * maxOverlap + WorkLimitBase,
        };
        Span<int> frameKillers = stackalloc int[KillerCapacity];
        Span<int> canvasKillers = stackalloc int[KillerCapacity];
        int frameKillerCount = 0;
        int canvasKillerCount = 0;

        // Longest trustworthy overlap wins, preventing a short coincidental row pattern from
        // duplicating content. Killers change comparison order only, never acceptance.
        for (int start = 0; start <= maxOverlap - _options.MinOverlapRows; start++)
        {
            int overlap = maxOverlap - start;
            counters.CandidatesConsidered++;
            if (CandidateMatches(frameRows, canvasRows, maxOverlap, start,
                frameKillers, ref frameKillerCount, canvasKillers, ref canvasKillerCount,
                ref counters) && VerifyOverlap(frameRows, canvasRows, start, overlap, ref counters))
            {
                return FinishSearch(overlap, counters);
            }

            if (counters.StoppedAtWorkLimit)
            {
                return FinishSearch(-1, counters);
            }
        }

        return FinishSearch(-1, counters);
    }

    private bool CandidateMatches(ReadOnlySpan<byte> frameRows, ReadOnlySpan<byte> canvasRows,
        int maxOverlap, int start, Span<int> frameKillers, ref int frameKillerCount,
        Span<int> canvasKillers, ref int canvasKillerCount, ref SearchCounters counters)
    {
        int overlap = maxOverlap - start;
        for (int index = 0; index < frameKillerCount; index++)
        {
            int row = frameKillers[index];
            if (row < overlap && !PairMatches(frameRows, canvasRows, maxOverlap,
                row, start + row, ref counters))
            {
                PromoteKiller(frameKillers, ref frameKillerCount, row);
                return false;
            }
        }

        for (int index = 0; index < canvasKillerCount; index++)
        {
            int row = canvasKillers[index];
            if (row >= start && !PairMatches(frameRows, canvasRows, maxOverlap,
                row - start, row, ref counters))
            {
                PromoteKiller(canvasKillers, ref canvasKillerCount, row);
                return false;
            }
        }

        for (int row = 0; row < overlap; row++)
        {
            if (!PairMatches(frameRows, canvasRows, maxOverlap, row, start + row, ref counters))
            {
                if (!counters.StoppedAtWorkLimit)
                {
                    PromoteKiller(frameKillers, ref frameKillerCount, row);
                    PromoteKiller(canvasKillers, ref canvasKillerCount, start + row);
                }

                return false;
            }
        }

        return true;
    }

    private static void PromoteKiller(Span<int> killers, ref int count, int row)
    {
        int index = 0;
        while (index < count && killers[index] != row)
        {
            index++;
        }

        if (index == count && count < killers.Length)
        {
            count++;
        }

        for (index = Math.Min(index, killers.Length - 1); index > 0; index--)
        {
            killers[index] = killers[index - 1];
        }

        killers[0] = row;
    }

    private int BuildRowClasses(ReadOnlySpan<byte> frameRows, ReadOnlySpan<byte> canvasRows, int rowCount)
    {
        int totalRows = rowCount * 2;
        if (_rowClasses.Length < totalRows)
        {
            int capacity = Math.Max(totalRows, _rowClasses.Length * 2);
            _rowClasses = new int[capacity];
            _classRepresentatives = new int[capacity];
            _nextClassWithHash = new int[capacity];
        }

        _firstClassByHash.Clear();
        int classCount = 0;
        for (int row = 0; row < totalRows; row++)
        {
            ReadOnlySpan<byte> pixels = SearchRow(frameRows, canvasRows, rowCount, row);
            ulong hash = HashRow(pixels);
            int firstClass = _firstClassByHash.TryGetValue(hash, out int first) ? first : -1;
            int matchingClass = firstClass;
            while (matchingClass >= 0)
            {
                if (pixels.SequenceEqual(SearchRow(frameRows, canvasRows, rowCount,
                    _classRepresentatives[matchingClass])))
                {
                    break;
                }

                matchingClass = _nextClassWithHash[matchingClass];
            }

            if (matchingClass < 0)
            {
                matchingClass = classCount++;
                _classRepresentatives[matchingClass] = row;
                _nextClassWithHash[matchingClass] = firstClass;
                _firstClassByHash[hash] = matchingClass;
            }

            _rowClasses[row] = matchingClass;
        }

        return classCount;
    }

    private ReadOnlySpan<byte> SearchRow(ReadOnlySpan<byte> frameRows, ReadOnlySpan<byte> canvasRows,
        int rowCount, int row) => row < rowCount
            ? frameRows.Slice(row * _stride, _stride)
            : canvasRows.Slice((row - rowCount) * _stride, _stride);

    private bool PairMatches(ReadOnlySpan<byte> frameRows, ReadOnlySpan<byte> canvasRows,
        int rowCount, int frameRow, int canvasRow, ref SearchCounters counters)
    {
        counters.RowPairChecks++;
        int leftClass = _rowClasses[frameRow];
        int rightClass = _rowClasses[rowCount + canvasRow];
        if (leftClass == rightClass)
        {
            return true;
        }

        if (leftClass > rightClass)
        {
            (leftClass, rightClass) = (rightClass, leftClass);
        }

        bool dense = counters.DistinctRowClasses <= DenseMemoMaxClasses;
        int denseIndex = dense ? leftClass * counters.DistinctRowClasses + rightClass : 0;
        long key = ((long)leftClass << 32) | (uint)rightClass;
        if (dense)
        {
            byte memo = _denseMemo[denseIndex];
            if (memo != 0)
            {
                return memo == 1;
            }
        }
        else if (_sparseMemo.TryGetValue(key, out bool memo))
        {
            return memo;
        }

        if (counters.PixelRowComparisons == counters.WorkLimit)
        {
            counters.StoppedAtWorkLimit = true;
            return false;
        }

        counters.PixelRowComparisons++;
        bool matches = RowsMatch(
            SearchRow(frameRows, canvasRows, rowCount, _classRepresentatives[leftClass]),
            SearchRow(frameRows, canvasRows, rowCount, _classRepresentatives[rightClass]),
            _maxMismatchBytes);
        if (dense)
        {
            _denseMemo[denseIndex] = matches ? (byte)1 : (byte)2;
        }
        else
        {
            _sparseMemo[key] = matches;
        }

        return matches;
    }

    private bool VerifyOverlap(ReadOnlySpan<byte> frameRows, ReadOnlySpan<byte> canvasRows,
        int start, int overlap, ref SearchCounters counters)
    {
        for (int row = 0; row < overlap; row++)
        {
            counters.VerificationRowComparisons++;
            if (!RowsMatch(frameRows.Slice(row * _stride, _stride),
                canvasRows.Slice((start + row) * _stride, _stride), _maxMismatchBytes))
            {
                return false;
            }
        }

        return true;
    }

    private int FinishSearch(int overlap, SearchCounters counters)
    {
        LastOverlapSearch = new ScrollOverlapSearchStatistics(
            counters.CandidatesConsidered, counters.RowPairChecks, counters.PixelRowComparisons,
            counters.VerificationRowComparisons, counters.HeaderRowComparisons,
            counters.DistinctRowClasses, counters.WorkLimit, counters.StoppedAtWorkLimit);
        return overlap;
    }

    private struct SearchCounters
    {
        public int CandidatesConsidered;
        public long RowPairChecks;
        public int PixelRowComparisons;
        public int VerificationRowComparisons;
        public int HeaderRowComparisons;
        public int DistinctRowClasses;
        public int WorkLimit;
        public bool StoppedAtWorkLimit;
    }

    /// <summary>
    /// Compares equal-length rows with the original differing-byte tolerance. Equal prefixes
    /// are skipped; remaining bytes use hardware vectors when available and a scalar tail.
    /// </summary>
    internal static bool RowsMatch(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int maxMismatchBytes)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        int offset = left.CommonPrefixLength(right);
        if (offset == left.Length)
        {
            return true;
        }

        // A negative limit can result from the existing NaN option conversion. As in the
        // original loop, identical rows match, but their first differing byte rejects them.
        if (maxMismatchBytes <= 0)
        {
            return false;
        }

        int mismatch = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; offset <= left.Length - Vector256<byte>.Count; offset += Vector256<byte>.Count)
            {
                Vector256<byte> equal = Vector256.Equals(
                    MemoryMarshal.Read<Vector256<byte>>(left[offset..]),
                    MemoryMarshal.Read<Vector256<byte>>(right[offset..]));
                mismatch += Vector256<byte>.Count - BitOperations.PopCount(equal.ExtractMostSignificantBits());
                if (mismatch > maxMismatchBytes)
                {
                    return false;
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            for (; offset <= left.Length - Vector128<byte>.Count; offset += Vector128<byte>.Count)
            {
                Vector128<byte> equal = Vector128.Equals(
                    MemoryMarshal.Read<Vector128<byte>>(left[offset..]),
                    MemoryMarshal.Read<Vector128<byte>>(right[offset..]));
                mismatch += Vector128<byte>.Count - BitOperations.PopCount(equal.ExtractMostSignificantBits());
                if (mismatch > maxMismatchBytes)
                {
                    return false;
                }
            }
        }

        for (; offset < left.Length; offset++)
        {
            if (left[offset] != right[offset] && ++mismatch > maxMismatchBytes)
            {
                return false;
            }
        }

        return true;
    }

    private static ulong HashRow(ReadOnlySpan<byte> row)
    {
        const ulong prime1 = 11400714785074694791UL;
        const ulong prime2 = 14029467366897019727UL;
        const ulong prime3 = 1609587929392839161UL;
        const ulong prime4 = 9650029242287828579UL;
        const ulong prime5 = 2870177450012600261UL;

        static ulong Round(ulong lane, ulong value) =>
            unchecked(BitOperations.RotateLeft(lane + value * prime2, 31) * prime1);

        unchecked
        {
            int offset = 0;
            ulong hash;
            if (row.Length >= 32)
            {
                ulong lane1 = prime1 + prime2;
                ulong lane2 = prime2;
                ulong lane3 = 0;
                ulong lane4 = 0UL - prime1;
                for (; offset <= row.Length - 32; offset += 32)
                {
                    lane1 = Round(lane1, BinaryPrimitives.ReadUInt64LittleEndian(row[offset..]));
                    lane2 = Round(lane2, BinaryPrimitives.ReadUInt64LittleEndian(row[(offset + 8)..]));
                    lane3 = Round(lane3, BinaryPrimitives.ReadUInt64LittleEndian(row[(offset + 16)..]));
                    lane4 = Round(lane4, BinaryPrimitives.ReadUInt64LittleEndian(row[(offset + 24)..]));
                }

                hash = BitOperations.RotateLeft(lane1, 1) + BitOperations.RotateLeft(lane2, 7)
                    + BitOperations.RotateLeft(lane3, 12) + BitOperations.RotateLeft(lane4, 18);
                hash = (hash ^ Round(0, lane1)) * prime1 + prime4;
                hash = (hash ^ Round(0, lane2)) * prime1 + prime4;
                hash = (hash ^ Round(0, lane3)) * prime1 + prime4;
                hash = (hash ^ Round(0, lane4)) * prime1 + prime4;
            }
            else
            {
                hash = prime5;
            }

            hash += (ulong)row.Length;
            for (; offset <= row.Length - 8; offset += 8)
            {
                hash ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(row[offset..]));
                hash = BitOperations.RotateLeft(hash, 27) * prime1 + prime4;
            }

            for (; offset < row.Length; offset++)
            {
                hash ^= row[offset] * prime5;
                hash = BitOperations.RotateLeft(hash, 11) * prime1;
            }

            hash ^= hash >> 33;
            hash *= prime2;
            hash ^= hash >> 29;
            hash *= prime3;
            return hash ^ (hash >> 32);
        }
    }

    private void AppendRows(ScrollFrame frame, int firstRow, int rowCount)
    {
        int addedBytes = checked(rowCount * _stride);
        _canvas.Write(frame.Pixels, checked(firstRow * _stride), addedBytes);
        _canvasHeight = checked(_canvasHeight + rowCount);
    }
}
