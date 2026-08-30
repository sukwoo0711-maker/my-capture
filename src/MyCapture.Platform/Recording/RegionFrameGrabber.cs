using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Grabs successive frames of a fixed screen region as BGRA32 byte buffers ready for
/// an encoder.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="ScreenCaptureEngine.CaptureRegion"/> — the same GDI
/// <c>BitBlt + CAPTUREBLT</c> path proven by the still-capture feature, including
/// layered-window and cursor handling — rather than standing up a second, subtly
/// different capture path with its own defects. At the recorder's default 15 fps and
/// the region sizes people record, BitBlt is comfortably fast enough and needs no
/// D3D device or duplication session, which is what keeps the dependency and failure
/// surface small on weak machines.
/// </para>
/// <para>
/// The region is captured at a fixed even width/height decided once at
/// <see cref="Open"/>, because H.264 requires even dimensions and a frame size that
/// never changes mid-clip.
/// </para>
/// </remarks>
public sealed class RegionFrameGrabber
{
    private readonly ScreenCaptureEngine _engine;
    private readonly bool _includeCursor;
    private RectD _region;
    private byte[] _buffer = [];

    public RegionFrameGrabber(ScreenCaptureEngine engine, bool includeCursor)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _includeCursor = includeCursor;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * 4;

    /// <summary>
    /// Fixes the capture region and frame size. Width/height are forced even and
    /// clamped to the region.
    /// </summary>
    public void Open(RectD screenRegion)
    {
        RectD pixels = screenRegion.Normalized().ToPixelBounds();
        int width = Math.Max(2, (int)pixels.Width);
        int height = Math.Max(2, (int)pixels.Height);

        // H.264 requires even dimensions; trim the odd last row/column rather than pad.
        width -= width % 2;
        height -= height % 2;

        Width = width;
        Height = height;
        _region = new RectD(pixels.Left, pixels.Top, width, height);
        _buffer = new byte[Stride * Height];
    }

    /// <summary>
    /// Captures the region now and copies its pixels into the reusable buffer.
    /// </summary>
    /// <remarks>
    /// The returned array is the grabber's own reusable buffer; the caller must hand
    /// it to the encoder synchronously (which the recorder does) rather than retain it.
    /// </remarks>
    public byte[] GrabInto()
    {
        BitmapSource frame = _engine.CaptureRegion(_region, _includeCursor);

        // Normalise to BGRA32 so the encoder always sees a known layout. Bgr32 from the
        // engine has the same byte width; converting is a cheap no-op when already 32bpp.
        BitmapSource bgra = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int copyWidth = Math.Min(Width, bgra.PixelWidth);
        int copyHeight = Math.Min(Height, bgra.PixelHeight);
        bgra.CopyPixels(
            new System.Windows.Int32Rect(0, 0, copyWidth, copyHeight),
            _buffer,
            Stride,
            0);

        return _buffer;
    }
}
