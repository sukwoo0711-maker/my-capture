using System.Diagnostics;
using System.Windows.Media;

namespace MyCapture.App.Pinning;

/// <summary>Uncomposited edge strips and immutable coverage, never a retained complete image.</summary>
internal sealed class NativePinEdgeCache
{
    private readonly NativePinRaster.Chrome _chrome;
    private readonly byte[] _raw;
    private readonly int _stride;
    internal long ByteLength => _raw.LongLength + _chrome.ByteLength;
    internal long PixelBytes => _raw.LongLength;
    // Only immutable byte/run arrays and numeric dimensions cross to the quality worker.
    // The live cache's raw pixels stay private to the STA and are never shared or mutated there.
    internal NativePinRaster.Chrome PreparedCoverage => _chrome;

    private NativePinEdgeCache(NativePinRaster.Chrome chrome, int stride)
    {
        _chrome = chrome; _stride = stride;
        _raw = new byte[chrome.RawByteLength];
    }

    internal static unsafe NativePinEdgeCache CaptureAndApply(IntPtr destination, int width, int height,
        int stride, double dpiScale, Color outline, double cornerRadiusDip, double contentScale = 1,
        CancellationToken cancellationToken = default) =>
        CaptureAndApply(destination, NativePinRaster.View.Whole(width, height), stride, dpiScale, outline,
            cornerRadiusDip, contentScale, cancellationToken);

    /// <summary>The destination holds only <paramref name="view"/> of the surface, at the view's size.</summary>
    internal static unsafe NativePinEdgeCache CaptureAndApply(IntPtr destination, NativePinRaster.View view,
        int stride, double dpiScale, Color outline, double cornerRadiusDip, double contentScale = 1,
        CancellationToken cancellationToken = default)
    {
        NativePinRaster.ValidateTarget(destination, view.Width, view.Height, stride);
        var chrome = new NativePinRaster.Chrome(view, stride, dpiScale, cornerRadiusDip, contentScale, cancellationToken);
        return CapturePreparedAndApply(destination, view.Width, view.Height, stride, chrome, outline, cancellationToken);
    }

    internal static unsafe NativePinEdgeCache CapturePreparedAndApply(IntPtr destination, int width, int height,
        int stride, NativePinRaster.Chrome chrome, Color outline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chrome);
        NativePinRaster.ValidateTarget(destination, width, height, stride);
        chrome.ValidateDimensions(width, height, stride);
        var cache = new NativePinEdgeCache(chrome, stride);
        foreach (NativePinRaster.EdgeRun run in chrome.Runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new ReadOnlySpan<byte>((byte*)destination + ((long)run.Y * stride) + (run.X * 4), run.Length * 4);
            row.CopyTo(cache._raw.AsSpan(run.RawOffset, row.Length));
        }
        chrome.Apply(destination, outline, cancellationToken);
        return cache;
    }

    internal unsafe double Redraw(IntPtr destination, Color outline)
    {
        if (destination == IntPtr.Zero) throw new ArgumentException("A destination buffer is required.", nameof(destination));
        long started = Stopwatch.GetTimestamp();
        foreach (NativePinRaster.EdgeRun run in _chrome.Runs)
        {
            var row = new Span<byte>((byte*)destination + ((long)run.Y * _stride) + (run.X * 4), run.Length * 4);
            _raw.AsSpan(run.RawOffset, row.Length).CopyTo(row);
        }
        _chrome.Apply(destination, outline);
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
}
