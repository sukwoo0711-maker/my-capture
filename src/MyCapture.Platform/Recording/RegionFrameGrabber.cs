using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Grabs successive frames of a fixed screen region as BGRA32 byte buffers ready for
/// an encoder.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="ScreenCaptureEngine.CreateSession"/> — the same GDI
/// <c>BitBlt + CAPTUREBLT</c> path proven by the still-capture feature, including
/// layered-window and cursor handling — rather than standing up a second, subtly
/// different capture path with its own defects. At the recorder's default 30 fps and
/// ordinary region sizes, BitBlt avoids a D3D device or duplication session and keeps
/// the dependency and failure surface small. The optional 60 fps mode depends on the
/// selected area and machine; adaptive frame drop keeps playback real-time if capture
/// or encoding cannot sustain the requested rate.
/// </para>
/// <para>
/// The region is captured at a fixed even width/height decided once at
/// <see cref="Open"/>, because H.264 requires even dimensions and a frame size that
/// never changes mid-clip.
/// </para>
/// <para>
/// Recordings ask for <see cref="WgcRegionCaptureSession"/> unless the user chose compatibility
/// mode (<see cref="RecordingCaptureMode.Gdi"/>) or the diagnostic
/// <see cref="RecordingCaptureBackendSelection.EnvironmentVariable"/> switch says <c>gdi</c>
/// (ADR 0005/0006). A region that spans displays, an unsupported OS or any WGC failure falls
/// back to GDI for the rest of the recording without changing the frame contract. The WGC
/// session (D3D device, capture item, frame pool) opens on a worker thread from
/// <see cref="Open"/>, so its 70-300 ms setup overlaps encoder initialisation instead of
/// delaying the first recorded frame. Still captures never come through here and stay on GDI.
/// </para>
/// </remarks>
public sealed class RegionFrameGrabber : IDisposable
{
    private readonly ScreenCaptureEngine _engine;
    private readonly bool _includeCursor;
    private readonly bool _cursorHighlight;
    private readonly ILogger? _log;
    private RectD _region;
    private byte[] _buffer = [];
    private ScreenCaptureEngine.CaptureSession? _session;
    private WgcRegionCaptureSession? _wgc;
    private Task<WgcRegionCaptureSession>? _wgcOpening;
    private WgcCaptureDiagnostics? _wgcDiagnostics;

    public RegionFrameGrabber(
        ScreenCaptureEngine engine,
        bool includeCursor,
        bool cursorHighlight = false,
        RecordingCaptureBackend? backend = null,
        ILogger? log = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _includeCursor = includeCursor;
        _cursorHighlight = cursorHighlight;
        _log = log;
        RequestedBackend = backend ?? RecordingCaptureBackendSelection.FromEnvironment();
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int Stride => Width * 4;

    /// <summary>
    /// Backend asked for by the constructor argument, or else the product default (automatic,
    /// WGC first) unless the environment switch overrides it.
    /// </summary>
    public RecordingCaptureBackend RequestedBackend { get; }

    /// <summary>Backend that produced the latest frame; <see langword="null"/> before the first frame.</summary>
    public RecordingCaptureBackend? ActiveBackend { get; private set; }

    /// <summary>Why a requested WGC capture fell back to GDI, if it did.</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>
    /// Time the first <see cref="GrabInto"/> spent creating the backend session, or waiting for
    /// the WGC session that <see cref="Open"/> started.
    /// </summary>
    public double SessionOpenMs { get; private set; }

    /// <summary>Diagnostics of the WGC session, live while it runs and kept after it closes.</summary>
    public WgcCaptureDiagnostics? WgcDiagnostics => _wgc?.GetDiagnostics() ?? _wgcDiagnostics;

    /// <summary>
    /// Optional diagnostics sink that receives each <see cref="GrabInto"/> duration in
    /// milliseconds, on the capture thread. Unset in production.
    /// </summary>
    public Action<double>? FrameCaptureTimed { get; set; }

    /// <summary>
    /// Fixes the capture region and frame size. Width/height are forced even and
    /// clamped to the region.
    /// </summary>
    public void Open(RectD screenRegion)
    {
        if (_session is not null || _wgc is not null || _wgcOpening is not null)
        {
            throw new InvalidOperationException("The previous capture session is still open.");
        }

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
        if (RequestedBackend == RecordingCaptureBackend.WindowsGraphicsCapture)
        {
            RectD region = _region;
            bool includeCursor = _includeCursor;
            _wgcOpening = Task.Run(() => WgcRegionCaptureSession.Create(region, includeCursor));
        }
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
        long started = Stopwatch.GetTimestamp();
        if (_session is null && _wgc is null)
        {
            OpenBackend();
        }

        if (_wgc is not null)
        {
            try
            {
                _wgc.CaptureInto(_buffer, Stride);
                if (_includeCursor && _cursorHighlight)
                {
                    PaintCursorHighlight(_buffer, Stride, Width, Height, (int)_region.Left, (int)_region.Top);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or TimeoutException
                or NotSupportedException or ObjectDisposedException)
            {
                FallBackToGdi($"WGC capture failed: {ex.GetType().Name}: {ex.Message}", ex);
                _session!.CaptureInto(_buffer, Stride);
            }
        }
        else
        {
            _session!.CaptureInto(_buffer, Stride);
        }

        FrameCaptureTimed?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return _buffer;
    }

    private void OpenBackend()
    {
        long started = Stopwatch.GetTimestamp();
        if (_wgcOpening is { } opening)
        {
            _wgcOpening = null;
            try
            {
                if (!opening.Wait(WgcOpenTimeout))
                {
                    DisposeWhenOpened(opening);
                    throw new TimeoutException("Windows.Graphics.Capture did not open in time.");
                }

                _wgc = opening.Result;
                ActiveBackend = RecordingCaptureBackend.WindowsGraphicsCapture;
            }
            catch (Exception ex) when (ex is AggregateException or TimeoutException)
            {
                Exception cause = ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
                FallBackToGdi($"WGC unavailable: {cause.GetType().Name}: {cause.Message}", cause);
            }
        }
        else
        {
            _session = CreateSession();
            ActiveBackend = RecordingCaptureBackend.Gdi;
        }

        SessionOpenMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (_wgc?.GetDiagnostics() is { } wgc)
        {
            _log?.LogInformation(
                "Recording capture backend: requested {Requested}, active {Active}; session {CreateMs:0}ms, " +
                "first grab waited {WaitMs:0.0}ms, D3D driver {Driver}, borderless access {Access}, border required {Border}",
                RequestedBackend, ActiveBackend, wgc.CreateMs, SessionOpenMs, wgc.DriverType,
                wgc.BorderlessAccess ?? "not requested", wgc.BorderRequired?.ToString() ?? wgc.BorderSetError ?? "unknown");
        }
        else if (FallbackReason is null)
        {
            _log?.LogInformation("Recording capture backend: requested {Requested}, active {Active}", RequestedBackend, ActiveBackend);
        }
    }

    private static readonly TimeSpan WgcOpenTimeout = TimeSpan.FromSeconds(5);

    private static void DisposeWhenOpened(Task<WgcRegionCaptureSession> opening) =>
        _ = opening.ContinueWith(
            static task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    task.Result.Dispose();
                }
                else
                {
                    // Observe the failure; the recording already fell back or never started.
                    _ = task.Exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void FallBackToGdi(string reason, Exception cause)
    {
        FallbackReason = reason;
        if (cause is NotSupportedException && _wgc is null)
        {
            // Expected before the first frame: the region spans displays or the OS lacks WGC.
            _log?.LogInformation(
                "Recording capture backend: requested {Requested}, active {Active}; fallback: {Reason}",
                RequestedBackend, RecordingCaptureBackend.Gdi, reason);
        }
        else
        {
            _log?.LogWarning(
                cause,
                "Recording capture backend: requested {Requested}, active {Active}; fallback: {Reason}",
                RequestedBackend, RecordingCaptureBackend.Gdi, reason);
        }

        if (_wgc is not null)
        {
            _wgcDiagnostics = _wgc.GetDiagnostics();
            _wgc.Dispose();
            _wgc = null;
        }

        _session ??= CreateSession();
        ActiveBackend = RecordingCaptureBackend.Gdi;
    }

    private ScreenCaptureEngine.CaptureSession CreateSession()
    {
        ScreenCaptureEngine.CaptureSession session = _engine.CreateSession(_region, _includeCursor);
        // The emphasis ring is only meaningful on recordings; still captures never enable it.
        session.CursorHighlight = _cursorHighlight;
        return session;
    }

    /// <summary>
    /// WGC composites the pointer itself, so only the emphasis ring is drawn here, with the
    /// same radius, width and colours as the GDI path's <c>Ellipse</c>.
    /// </summary>
    private static void PaintCursorHighlight(byte[] pixels, int stride, int width, int height, int originX, int originY)
    {
        var info = new NativeMethods.CURSORINFO { cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref info) || (info.flags & NativeMethods.CURSOR_SHOWING) == 0)
        {
            return;
        }

        bool pressed = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;
        PaintRing(pixels, stride, width, height, info.ptScreenPos.X - originX, info.ptScreenPos.Y - originY, pressed);
    }

    /// <summary>Draws the highlight ring centred on (<paramref name="centerX"/>, <paramref name="centerY"/>) into BGRA pixels.</summary>
    internal static void PaintRing(byte[] pixels, int stride, int width, int height, int centerX, int centerY, bool pressed)
    {
        int radius = pressed ? 18 : 13;
        double halfPen = (pressed ? 5 : 3) / 2.0;
        // Amber (255,199,0) idle, coral red (255,64,64) while the button is down.
        byte red = 255, green = pressed ? (byte)64 : (byte)199, blue = pressed ? (byte)64 : (byte)0;
        int reach = radius + (int)Math.Ceiling(halfPen);
        int left = Math.Max(0, centerX - reach), right = Math.Min(width - 1, centerX + reach);
        int top = Math.Max(0, centerY - reach), bottom = Math.Min(height - 1, centerY + reach);
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                double distance = Math.Sqrt(((x - centerX) * (x - centerX)) + ((y - centerY) * (y - centerY)));
                if (Math.Abs(distance - radius) <= halfPen)
                {
                    int offset = (y * stride) + (x * 4);
                    pixels[offset] = blue;
                    pixels[offset + 1] = green;
                    pixels[offset + 2] = red;
                    pixels[offset + 3] = 255;
                }
            }
        }
    }

    /// <summary>Releases native resources on the recording thread that captured frames.</summary>
    public void Dispose()
    {
        if (_wgcOpening is { } opening)
        {
            // Opened but never captured: release the session whenever it finishes opening.
            _wgcOpening = null;
            DisposeWhenOpened(opening);
        }

        if (_wgc is not null)
        {
            _wgcDiagnostics = _wgc.GetDiagnostics();
            _wgc.Dispose();
            _wgc = null;
            _log?.LogInformation(
                "WGC recording session closed: {Arrived} frame(s) arrived, {Copied} copied, {Reused} reused, " +
                "{Superseded} superseded, {Recreated} pool recreation(s)",
                _wgcDiagnostics.FramesArrived, _wgcDiagnostics.FramesCopied, _wgcDiagnostics.ReusedCalls,
                _wgcDiagnostics.FramesSuperseded, _wgcDiagnostics.PoolRecreations);
        }

        _session?.Dispose();
        _session = null;
    }
}
