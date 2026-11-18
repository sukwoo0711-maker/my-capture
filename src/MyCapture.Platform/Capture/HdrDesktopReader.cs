using System.Diagnostics;
using System.Runtime.InteropServices;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Capture;

/// <summary>How the scRGB read of one display ended.</summary>
public enum HdrReadOutcome
{
    /// <summary>The converted pixels replaced the GDI copy.</summary>
    Applied = 0,

    /// <summary>The display turned out not to be composed for HDR; its GDI copy is already right.</summary>
    NotNeeded = 1,

    /// <summary>The read failed; the display keeps the GDI copy.</summary>
    Failed = 2,

    /// <summary>The read did not finish inside the capture's budget; the display keeps the GDI copy.</summary>
    TimedOut = 3,
}

/// <summary>What the scRGB read did for one display's part of a still capture.</summary>
/// <param name="DeviceName">GDI device name of the display.</param>
/// <param name="ScreenBounds">The part of the capture on this display, in virtual-desktop pixels.</param>
/// <param name="Outcome">Whether the converted pixels replaced the GDI copy, and if not, why not.</param>
/// <param name="Detail">The reason the GDI copy was kept; <see langword="null"/> when applied.</param>
/// <param name="SdrWhiteNits">SDR white level the pixels were divided by.</param>
/// <param name="ColorSpace">DXGI_COLOR_SPACE_TYPE of the output (12 is HDR10), or -1 before it was read.</param>
/// <param name="MaxLuminance">Peak luminance the output reports, in nits.</param>
/// <param name="SurfaceFormat">DXGI_FORMAT of the duplicated surface (10 is R16G16B16A16_FLOAT), or 0 before it was read.</param>
/// <param name="Rotation">DXGI_MODE_ROTATION of the surface.</param>
/// <param name="DeviceReused">The D3D device kept from an earlier capture was used.</param>
/// <param name="FindOutputMs">DXGI factory creation and output lookup.</param>
/// <param name="DeviceMs">D3D device creation; near zero when reused.</param>
/// <param name="DuplicateMs"><c>DuplicateOutput1</c>.</param>
/// <param name="AcquireMs">Waiting for the desktop frame.</param>
/// <param name="AcquireCalls">Calls to <c>AcquireNextFrame</c>.</param>
/// <param name="CopyMs">Copy to a staging texture and mapping it.</param>
/// <param name="ConvertMs">scRGB to sRGB conversion.</param>
/// <param name="PixelsAboveSdrWhite">Pixels brighter than SDR white, which the tone operator scaled.</param>
public sealed record HdrOutputRead(
    string DeviceName,
    RectD ScreenBounds,
    HdrReadOutcome Outcome,
    string? Detail,
    double SdrWhiteNits,
    int ColorSpace,
    float MaxLuminance,
    uint SurfaceFormat,
    int Rotation,
    bool DeviceReused,
    double FindOutputMs,
    double DeviceMs,
    double DuplicateMs,
    double AcquireMs,
    int AcquireCalls,
    double CopyMs,
    double ConvertMs,
    long PixelsAboveSdrWhite)
{
    public bool Applied => Outcome == HdrReadOutcome.Applied;
}

/// <param name="DeviceName">GDI device name of the display.</param>
/// <param name="MonitorBounds">The whole display, in this process's virtual-desktop pixels.</param>
/// <param name="ScreenBounds">The part of the capture on the display.</param>
/// <param name="SdrWhiteNits">The display's SDR white level; used when the output is in an HDR colour space.</param>
/// <param name="ConfirmHdr">Read only when the DXGI output reports an HDR colour space.</param>
internal readonly record struct HdrReadRequest(
    string DeviceName, RectD MonitorBounds, RectD ScreenBounds, double SdrWhiteNits, bool ConfirmHdr);

/// <summary>Converted BGRA rows of one display's part of a capture, in native memory.</summary>
/// <remarks>
/// One buffer is kept as a spare when a tile is disposed, and the next tile that fits takes it:
/// a burst of captures then converts into memory that is already committed instead of faulting
/// fresh pages in for every frame. The spare is freed when the reader trims, half a second after
/// the last read.
/// </remarks>
internal sealed unsafe class HdrTile : IDisposable
{
    /// <summary>Largest buffer kept as a spare: a 7680x4320 display.</summary>
    private const long LargestSpareBytes = 7680L * 4320 * 4;

    private static readonly object SpareGate = new();
    private static IntPtr s_spare;
    private static long s_spareBytes;

    private readonly long _capacity;
    private IntPtr _pixels;

    private HdrTile(IntPtr pixels, long capacity, int left, int top, int width, int height)
    {
        _pixels = pixels;
        _capacity = capacity;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    ~HdrTile() => Dispose();

    internal IntPtr Pixels => _pixels;

    /// <summary>Left edge in virtual-desktop pixels.</summary>
    internal int Left { get; }

    /// <summary>Top edge in virtual-desktop pixels.</summary>
    internal int Top { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal int Stride => Width * 4;

    internal static HdrTile? TryAllocate(int left, int top, int width, int height)
    {
        long bytes = (long)width * height * 4;
        if (width <= 0 || height <= 0 || bytes > int.MaxValue)
        {
            return null;
        }

        lock (SpareGate)
        {
            if (s_spare != IntPtr.Zero && s_spareBytes >= bytes)
            {
                var reused = new HdrTile(s_spare, s_spareBytes, left, top, width, height);
                s_spare = IntPtr.Zero;
                s_spareBytes = 0;
                return reused;
            }
        }

        try
        {
            return new HdrTile((IntPtr)NativeMemory.Alloc((nuint)bytes), bytes, left, top, width, height);
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Frees the spare buffer, if one is kept.</summary>
    internal static void ReleaseSpare()
    {
        IntPtr spare;
        lock (SpareGate)
        {
            spare = s_spare;
            s_spare = IntPtr.Zero;
            s_spareBytes = 0;
        }

        if (spare != IntPtr.Zero)
        {
            NativeMemory.Free((void*)spare);
        }
    }

    public void Dispose()
    {
        IntPtr pixels = Interlocked.Exchange(ref _pixels, IntPtr.Zero);
        if (pixels != IntPtr.Zero)
        {
            lock (SpareGate)
            {
                // The larger of the two stays: it serves both sizes.
                if (_capacity <= LargestSpareBytes && _capacity > s_spareBytes)
                {
                    (pixels, s_spare) = (s_spare, pixels);
                    s_spareBytes = _capacity;
                }
            }

            if (pixels != IntPtr.Zero)
            {
                NativeMemory.Free((void*)pixels);
            }
        }

        GC.SuppressFinalize(this);
    }
}

internal sealed class HdrReadResult : IDisposable
{
    internal List<HdrOutputRead> Outputs { get; } = [];

    internal List<HdrTile> Tiles { get; } = [];

    public void Dispose()
    {
        foreach (HdrTile tile in Tiles)
        {
            tile.Dispose();
        }

        Tiles.Clear();
    }
}

/// <summary>
/// Reads displays in scRGB (R16G16B16A16_FLOAT) through DXGI Desktop Duplication and converts
/// them to SDR BGRA.
/// </summary>
/// <remarks>
/// <para>
/// Desktop Duplication rather than Windows.Graphics.Capture: it shows no capture border on
/// Windows 10 and needs no consent in a packaged app, the first <c>AcquireNextFrame</c> of a new
/// duplication returned the composed desktop in 2-10 ms on a static screen (34 of 34 reads), and
/// its floating-point picture of an SDR desktop converts back to the GDI picture, which the
/// Windows.Graphics.Capture picture does not over the taskbar (docs/adr/0009).
/// </para>
/// <para>
/// A duplication is created for one read and released with it, so no session has to survive a
/// lock, a mode change or a static desktop. The D3D device is kept between captures: creating
/// one took 72-137 ms on the measured PC (NVIDIA), most of a read that otherwise takes about
/// 10 ms at 3440x1440. It costs about 31 MB of private memory while it lives, so it is released
/// after <see cref="DeviceIdleRelease"/> without a capture, and the staging pool the driver keeps
/// is trimmed half a second after the last read.
/// </para>
/// <para>
/// One read runs at a time, on its own thread, under the caller's deadline. A caller whose
/// deadline passes stops waiting and keeps the GDI copy; the abandoned read still owns the gate
/// until it ends, and while it does, later captures keep the GDI copy at once: they neither
/// queue behind a stuck driver call nor start another.
/// </para>
/// </remarks>
internal static unsafe class HdrDesktopReader
{
    private const int AcquireCallTimeoutMs = 50;
    private const int AcquireBudgetMs = 200;
    private const int RowsPerParallelBand = 64;
    private const long SmallestParallelPixelCount = 256 * 256;

    /// <summary>
    /// Quiet time after a read before the driver's staging pool is trimmed, so a burst of captures
    /// (a scrolling capture) does not trim between its frames.
    /// </summary>
    private static readonly TimeSpan TrimDelay = TimeSpan.FromMilliseconds(500);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Reads whose capture stopped waiting and that have not ended yet.
    private static int s_abandonedReads;

    // What the last such read reported when it did end, until somebody asks.
    private static IReadOnlyList<HdrOutputRead>? s_lateRead;

    // Everything below is touched only while Gate is held.
    private static IntPtr s_device;
    private static IntPtr s_context;
    private static NativeMethods.LUID s_deviceAdapter;
    private static long s_lastUse;
    private static bool s_trimPending;
    private static Timer? s_housekeeping;
    private static HdrToneMap? s_toneMap;
    private static (double White, HdrToneOperator Operator, double Peak) s_toneMapKey;

    /// <summary>How long the D3D device outlives the last read.</summary>
    internal static TimeSpan DeviceIdleRelease { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Runs on the reading thread before the first display; tests use it to outlast a deadline.</summary>
    internal static Action? BeforeReadForTest { get; set; }

    internal static bool HasKeptDevice => Volatile.Read(ref s_device) != IntPtr.Zero;

    /// <summary>
    /// Reads every requested display within <paramref name="budget"/>. The result lists how each
    /// display ended and owns the converted tiles; the caller disposes it.
    /// </summary>
    internal static HdrReadResult Read(
        IReadOnlyList<HdrReadRequest> requests, bool forceFloat, HdrToneOperator toneOperator, TimeSpan budget)
    {
        long started = Stopwatch.GetTimestamp();
        // A read that already outlasted its capture may never return; nothing waits behind it.
        // Any other holder of the gate is a read in progress or its bookkeeping, both short.
        if (Volatile.Read(ref s_abandonedReads) != 0 || !Gate.Wait(budget))
        {
            return Skipped(requests, HdrReadOutcome.TimedOut, "an earlier HDR read is still running");
        }

        TimeSpan remaining = budget - Stopwatch.GetElapsedTime(started);
        var job = new Job(requests, forceFloat, toneOperator);
        bool running = false;
        try
        {
            if (remaining > TimeSpan.Zero)
            {
                new Thread(job.Run) { IsBackground = true, Name = "MyCapture HDR read" }.Start();
                running = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reported below as a read that could not start.
        }

        if (!running)
        {
            Gate.Release();
            return remaining <= TimeSpan.Zero
                ? Skipped(requests, HdrReadOutcome.TimedOut, "an earlier HDR read used up the budget")
                : Skipped(requests, HdrReadOutcome.Failed, "the HDR read could not start");
        }

        _ = job.Done.Wait(remaining);
        return job.TakeOrAbandon()
            ?? Skipped(requests, HdrReadOutcome.TimedOut, $"the HDR read took longer than {budget.TotalMilliseconds:0} ms");
    }

    /// <summary>
    /// The step timings of the last read that ended after its capture had stopped waiting, once;
    /// <see langword="null"/> when none ended since the last call. They say which step was slow.
    /// </summary>
    internal static IReadOnlyList<HdrOutputRead>? TakeLateRead() => Interlocked.Exchange(ref s_lateRead, null);

    /// <summary>Releases the kept D3D device now. Returns false when a read still holds it after two seconds.</summary>
    internal static bool ReleaseKeptDevice()
    {
        if (!Gate.Wait(TimeSpan.FromSeconds(2)))
        {
            return false;
        }

        try
        {
            ReleaseDevice();
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static HdrReadResult Skipped(IReadOnlyList<HdrReadRequest> requests, HdrReadOutcome outcome, string reason)
    {
        var result = new HdrReadResult();
        foreach (HdrReadRequest request in requests)
        {
            result.Outputs.Add(NotRead(request, outcome, reason));
        }

        return result;
    }

    private static HdrOutputRead NotRead(HdrReadRequest request, HdrReadOutcome outcome, string reason) =>
        new(request.DeviceName, request.ScreenBounds, outcome, reason, request.SdrWhiteNits, -1, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>One read on its own thread, with a hand-over the waiting capture can give up on.</summary>
    private sealed class Job(IReadOnlyList<HdrReadRequest> requests, bool forceFloat, HdrToneOperator toneOperator)
    {
        private readonly object _sync = new();
        private HdrReadResult? _result;
        private bool _abandoned;

        internal ManualResetEventSlim Done { get; } = new(false);

        private bool IsAbandoned
        {
            get
            {
                lock (_sync)
                {
                    return _abandoned;
                }
            }
        }

        internal void Run()
        {
            var result = new HdrReadResult();
            try
            {
                BeforeReadForTest?.Invoke();
                foreach (HdrReadRequest request in requests)
                {
                    result.Outputs.Add(IsAbandoned
                        ? NotRead(request, HdrReadOutcome.TimedOut, "the capture stopped waiting")
                        : ReadOutput(request, forceFloat, toneOperator, result.Tiles));
                }
            }
            catch (Exception ex)
            {
                // Nothing may leave this thread; the displays not reached keep the GDI copy.
                for (int index = result.Outputs.Count; index < requests.Count; index++)
                {
                    result.Outputs.Add(NotRead(requests[index], HdrReadOutcome.Failed, ex.GetType().Name));
                }
            }
            finally
            {
                bool abandoned = Complete(result);
                try
                {
                    AfterRead();
                }
                catch (Exception)
                {
                    // Housekeeping only; the next read creates what is missing.
                }

                if (abandoned)
                {
                    _ = Interlocked.Decrement(ref s_abandonedReads);
                }

                Gate.Release();
            }
        }

        /// <summary>The waiting capture takes the result, or gives the read up so it frees its own tiles.</summary>
        internal HdrReadResult? TakeOrAbandon()
        {
            lock (_sync)
            {
                if (_result is null && !_abandoned)
                {
                    _abandoned = true;
                    _ = Interlocked.Increment(ref s_abandonedReads);
                }

                return _result;
            }
        }

        /// <summary>Hands the result over, or frees it when the capture gave up. Returns whether it gave up.</summary>
        private bool Complete(HdrReadResult result)
        {
            bool abandoned;
            lock (_sync)
            {
                abandoned = _abandoned;
                if (abandoned)
                {
                    Volatile.Write(ref s_lateRead, result.Outputs.ToArray());
                    result.Dispose();
                }
                else
                {
                    _result = result;
                }
            }

            Done.Set();
            return abandoned;
        }
    }

    private static HdrOutputRead ReadOutput(
        HdrReadRequest request, bool forceFloat, HdrToneOperator toneOperator, List<HdrTile> tiles)
    {
        HdrOutputRead read = ReadOutputOnce(request, forceFloat, toneOperator, tiles, out bool deviceSuspect);
        if (read.Applied || !deviceSuspect)
        {
            return read;
        }

        // A kept device can outlive a driver reset or its adapter: one more attempt with a new one.
        ReleaseDevice();
        return ReadOutputOnce(request, forceFloat, toneOperator, tiles, out _);
    }

    private static HdrOutputRead ReadOutputOnce(
        HdrReadRequest request, bool forceFloat, HdrToneOperator toneOperator, List<HdrTile> tiles, out bool deviceSuspect)
    {
        deviceSuspect = false;
        long mark = Stopwatch.GetTimestamp();
        double findMs = 0, deviceMs = 0, duplicateMs = 0, acquireMs = 0, copyMs = 0, convertMs = 0;
        int acquireCalls = 0, rotation = 0, colorSpace = -1;
        uint format = 0;
        float maxLuminance = 0;
        bool deviceReused = false;
        double whiteNits = request.SdrWhiteNits;
        RectD bounds = request.ScreenBounds;

        double Lap()
        {
            long now = Stopwatch.GetTimestamp();
            double milliseconds = Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds;
            mark = now;
            return milliseconds;
        }

        HdrOutputRead Result(HdrReadOutcome outcome, string? detail, long aboveWhite = 0) => new(
            request.DeviceName, bounds, outcome, detail, whiteNits, colorSpace, maxLuminance, format, rotation,
            deviceReused, findMs, deviceMs, duplicateMs, acquireMs, acquireCalls, copyMs, convertMs, aboveWhite);

        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero, output = IntPtr.Zero, duplication = IntPtr.Zero,
            resource = IntPtr.Zero, texture = IntPtr.Zero, staging = IntPtr.Zero;
        bool frameHeld = false, mapped = false;
        HdrTile? tile = null;
        try
        {
            int hr = NativeMethods.CreateDXGIFactory1(in NativeMethods.IID_IDXGIFactory1, out factory);
            if (hr < 0)
            {
                return Result(HdrReadOutcome.Failed, $"CreateDXGIFactory1 failed (0x{hr:X8})");
            }

            if (!FindOutput(factory, request.DeviceName, out adapter, out output, out NativeMethods.DXGI_OUTPUT_DESC1 description))
            {
                return Result(HdrReadOutcome.Failed, "the display has no DXGI output");
            }

            colorSpace = description.ColorSpace;
            maxLuminance = description.MaxLuminance;
            findMs = Lap();
            bool hdrOutput = colorSpace == NativeMethods.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            // SDR white sits at the display's white level only on an HDR desktop. A forced read of an
            // SDR desktop has it at scRGB 1.0, whatever level is stored for the display.
            whiteNits = hdrOutput ? request.SdrWhiteNits : HdrToneMap.NitsPerScRgbUnit;
            if (request.ConfirmHdr && !hdrOutput)
            {
                // Advanced colour without HDR (a wide-gamut SDR display): the GDI copy is already right.
                return Result(HdrReadOutcome.NotNeeded, "the output is not in an HDR colour space");
            }

            NativeMethods.RECT desktop = description.DesktopCoordinates;
            RectD monitor = request.MonitorBounds;
            if (desktop.Left != (int)monitor.Left || desktop.Top != (int)monitor.Top
                || desktop.Width != (int)monitor.Width || desktop.Height != (int)monitor.Height)
            {
                // The capture rectangle is in this process's coordinates; a mismatch means they are
                // not the output's physical pixels (a process without per-monitor DPI awareness).
                return Result(HdrReadOutcome.Failed, "the output's desktop coordinates differ from the monitor's");
            }

            int left = Math.Max((int)bounds.Left, desktop.Left), top = Math.Max((int)bounds.Top, desktop.Top);
            int right = Math.Min((int)bounds.Right, desktop.Right), bottom = Math.Min((int)bounds.Bottom, desktop.Bottom);
            if (right <= left || bottom <= top)
            {
                return Result(HdrReadOutcome.NotNeeded, "the capture does not touch the output");
            }

            bounds = new RectD(left, top, right - left, bottom - top);

            NativeMethods.DXGI_ADAPTER_DESC adapterDescription;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, NativeMethods.DXGI_ADAPTER_DESC*, int>)
                NativeMethods.VtableSlot(adapter, NativeMethods.SlotAdapterGetDesc))(adapter, &adapterDescription);
            if (hr < 0)
            {
                return Result(HdrReadOutcome.Failed, $"IDXGIAdapter::GetDesc failed (0x{hr:X8})");
            }

            deviceReused = KeptDeviceServes(adapterDescription.AdapterLuid);
            if (!deviceReused)
            {
                ReleaseDevice();
                // The device must be created on the adapter that drives the output.
                hr = NativeMethods.D3D11CreateDevice(adapter, NativeMethods.D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, 0,
                    IntPtr.Zero, 0, NativeMethods.D3D11_SDK_VERSION, out s_device, out _, out s_context);
                if (hr < 0)
                {
                    s_device = IntPtr.Zero;
                    s_context = IntPtr.Zero;
                    return Result(HdrReadOutcome.Failed, $"D3D11CreateDevice failed (0x{hr:X8})");
                }

                s_deviceAdapter = adapterDescription.AdapterLuid;
            }

            deviceMs = Lap();

            // An HDR desktop is composed in the first format and arrives unconverted. BGRA8 is listed
            // so that a desktop that is not floating point (HDR was just turned off) arrives as it is
            // and is left to GDI; the forced read lists only the float format, so DWM converts.
            uint* formats = stackalloc uint[2] { NativeMethods.DXGI_FORMAT_R16G16B16A16_FLOAT, NativeMethods.DXGI_FORMAT_B8G8R8A8_UNORM };
            IntPtr duplicated;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint*, IntPtr*, int>)
                NativeMethods.VtableSlot(output, NativeMethods.SlotOutput5DuplicateOutput1))(
                    output, s_device, 0, forceFloat ? 1u : 2u, formats, &duplicated);
            duplicateMs = Lap();
            if (hr < 0)
            {
                deviceSuspect = deviceReused;
                return Result(HdrReadOutcome.Failed, $"DuplicateOutput1 failed (0x{hr:X8})");
            }

            duplication = duplicated;
            NativeMethods.DXGI_OUTDUPL_DESC duplicationDescription;
            ((delegate* unmanaged[Stdcall]<IntPtr, NativeMethods.DXGI_OUTDUPL_DESC*, void>)
                NativeMethods.VtableSlot(duplication, NativeMethods.SlotDuplicationGetDesc))(duplication, &duplicationDescription);
            rotation = duplicationDescription.Rotation is >= NativeMethods.DXGI_MODE_ROTATION_IDENTITY and <= NativeMethods.DXGI_MODE_ROTATION_ROTATE270
                ? duplicationDescription.Rotation
                : NativeMethods.DXGI_MODE_ROTATION_IDENTITY;

            hr = AcquireDesktopFrame(duplication, out resource, ref acquireCalls);
            frameHeld = resource != IntPtr.Zero;
            acquireMs = Lap();
            if (!frameHeld)
            {
                if (hr == NativeMethods.DXGI_ERROR_WAIT_TIMEOUT)
                {
                    return Result(HdrReadOutcome.Failed, $"no desktop frame arrived in {AcquireBudgetMs} ms");
                }

                deviceSuspect = deviceReused;
                return Result(HdrReadOutcome.Failed, $"AcquireNextFrame failed (0x{hr:X8})");
            }

            hr = Marshal.QueryInterface(resource, in NativeMethods.IID_ID3D11Texture2D, out texture);
            if (hr < 0)
            {
                return Result(HdrReadOutcome.Failed, $"the desktop frame is not a texture (0x{hr:X8})");
            }

            // The texture, not the duplication's mode description, has the surface size: a portrait
            // display reports its desktop mode there and delivers a landscape surface.
            NativeMethods.D3D11_TEXTURE2D_DESC textureDescription;
            ((delegate* unmanaged[Stdcall]<IntPtr, NativeMethods.D3D11_TEXTURE2D_DESC*, void>)
                NativeMethods.VtableSlot(texture, NativeMethods.SlotTexture2DGetDesc))(texture, &textureDescription);
            format = textureDescription.Format;
            if (format != NativeMethods.DXGI_FORMAT_R16G16B16A16_FLOAT)
            {
                return Result(HdrReadOutcome.NotNeeded, "the desktop is not composed in floating point");
            }

            var surfaceRotation = (HdrSurfaceRotation)rotation;
            (int desktopWidth, int desktopHeight) = HdrSurfaceGeometry.DesktopSize(
                (int)textureDescription.Width, (int)textureDescription.Height, surfaceRotation);
            if (desktopWidth != desktop.Width || desktopHeight != desktop.Height)
            {
                return Result(HdrReadOutcome.Failed, "the duplicated surface does not match the output size");
            }

            int width = right - left, height = bottom - top;
            HdrSurfaceBox box = HdrSurfaceGeometry.ToSurface(
                left - desktop.Left, top - desktop.Top, width, height, desktopWidth, desktopHeight, surfaceRotation);
            var stagingDescription = new NativeMethods.D3D11_TEXTURE2D_DESC
            {
                Width = (uint)box.Width,
                Height = (uint)box.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = NativeMethods.DXGI_FORMAT_R16G16B16A16_FLOAT,
                SampleCount = 1,
                Usage = NativeMethods.D3D11_USAGE_STAGING,
                CPUAccessFlags = NativeMethods.D3D11_CPU_ACCESS_READ,
            };
            IntPtr stagingTexture;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, NativeMethods.D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)
                NativeMethods.VtableSlot(s_device, NativeMethods.SlotDeviceCreateTexture2D))(s_device, &stagingDescription, IntPtr.Zero, &stagingTexture);
            if (hr < 0)
            {
                deviceSuspect = deviceReused;
                return Result(HdrReadOutcome.Failed, $"the staging texture could not be created (0x{hr:X8})");
            }

            staging = stagingTexture;
            var source = new NativeMethods.D3D11_BOX
            {
                Left = (uint)box.Left, Top = (uint)box.Top, Front = 0, Right = (uint)box.Right, Bottom = (uint)box.Bottom, Back = 1,
            };
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, NativeMethods.D3D11_BOX*, void>)
                NativeMethods.VtableSlot(s_context, NativeMethods.SlotContextCopySubresourceRegion))(s_context, staging, 0, 0, 0, 0, texture, 0, &source);
            NativeMethods.D3D11_MAPPED_SUBRESOURCE mapping;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, NativeMethods.D3D11_MAPPED_SUBRESOURCE*, int>)
                NativeMethods.VtableSlot(s_context, NativeMethods.SlotContextMap))(s_context, staging, 0, NativeMethods.D3D11_MAP_READ, 0, &mapping);
            copyMs = Lap();
            if (hr < 0)
            {
                deviceSuspect = deviceReused;
                return Result(HdrReadOutcome.Failed, $"the staging texture could not be mapped (0x{hr:X8})");
            }

            mapped = true;
            long surfaceBytes = ((long)mapping.RowPitch * (box.Height - 1)) + ((long)box.Width * 8);
            tile = surfaceBytes <= int.MaxValue ? HdrTile.TryAllocate(left, top, width, height) : null;
            if (tile is null)
            {
                return Result(HdrReadOutcome.Failed, "no memory for the converted pixels");
            }

            HdrToneMap toneMap = ToneMapFor(whiteNits, toneOperator, maxLuminance);
            long aboveWhite = Convert(toneMap, mapping.Data, (int)mapping.RowPitch, (int)surfaceBytes, box.Width, box.Height, surfaceRotation, tile);
            convertMs = Lap();
            tiles.Add(tile);
            tile = null;
            return Result(HdrReadOutcome.Applied, null, aboveWhite);
        }
        finally
        {
            tile?.Dispose();
            if (mapped)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
                    NativeMethods.VtableSlot(s_context, NativeMethods.SlotContextUnmap))(s_context, staging, 0);
            }

            NativeMethods.ReleaseCom(ref staging);
            NativeMethods.ReleaseCom(ref texture);
            NativeMethods.ReleaseCom(ref resource);
            if (frameHeld)
            {
                _ = ((delegate* unmanaged[Stdcall]<IntPtr, int>)
                    NativeMethods.VtableSlot(duplication, NativeMethods.SlotDuplicationReleaseFrame))(duplication);
            }

            NativeMethods.ReleaseCom(ref duplication);
            NativeMethods.ReleaseCom(ref output);
            NativeMethods.ReleaseCom(ref adapter);
            NativeMethods.ReleaseCom(ref factory);
        }
    }

    /// <summary>
    /// Waits for the first frame that carries a desktop image. A new duplication delivers the whole
    /// composed desktop on its first frame; a frame without a present time is a pointer update.
    /// </summary>
    private static int AcquireDesktopFrame(IntPtr duplication, out IntPtr resource, ref int calls)
    {
        resource = IntPtr.Zero;
        var acquire = (delegate* unmanaged[Stdcall]<IntPtr, uint, NativeMethods.DXGI_OUTDUPL_FRAME_INFO*, IntPtr*, int>)
            NativeMethods.VtableSlot(duplication, NativeMethods.SlotDuplicationAcquireNextFrame);
        var release = (delegate* unmanaged[Stdcall]<IntPtr, int>)
            NativeMethods.VtableSlot(duplication, NativeMethods.SlotDuplicationReleaseFrame);
        long started = Stopwatch.GetTimestamp();
        do
        {
            NativeMethods.DXGI_OUTDUPL_FRAME_INFO info;
            IntPtr acquired;
            calls++;
            int hr = acquire(duplication, AcquireCallTimeoutMs, &info, &acquired);
            if (hr == NativeMethods.DXGI_ERROR_WAIT_TIMEOUT)
            {
                continue;
            }

            if (hr < 0)
            {
                return hr;
            }

            if (info.LastPresentTime != 0)
            {
                resource = acquired;
                return 0;
            }

            NativeMethods.ReleaseCom(ref acquired);
            _ = release(duplication);
        }
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < AcquireBudgetMs);

        return NativeMethods.DXGI_ERROR_WAIT_TIMEOUT;
    }

    private static bool FindOutput(
        IntPtr factory, string deviceName, out IntPtr adapter, out IntPtr output, out NativeMethods.DXGI_OUTPUT_DESC1 description)
    {
        adapter = IntPtr.Zero;
        output = IntPtr.Zero;
        description = default;
        var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
            NativeMethods.VtableSlot(factory, NativeMethods.SlotFactoryEnumAdapters);
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            IntPtr candidateAdapter;
            if (enumAdapters(factory, adapterIndex, &candidateAdapter) < 0)
            {
                return false;
            }

            var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
                NativeMethods.VtableSlot(candidateAdapter, NativeMethods.SlotAdapterEnumOutputs);
            for (uint outputIndex = 0; ; outputIndex++)
            {
                IntPtr candidateOutput;
                if (enumOutputs(candidateAdapter, outputIndex, &candidateOutput) < 0)
                {
                    break;
                }

                int hr = Marshal.QueryInterface(candidateOutput, in NativeMethods.IID_IDXGIOutput6, out IntPtr output6);
                NativeMethods.ReleaseCom(ref candidateOutput);
                if (hr < 0)
                {
                    continue;
                }

                NativeMethods.DXGI_OUTPUT_DESC1 candidate;
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, NativeMethods.DXGI_OUTPUT_DESC1*, int>)
                    NativeMethods.VtableSlot(output6, NativeMethods.SlotOutput6GetDesc1);
                if (getDesc1(output6, &candidate) >= 0 && candidate.AttachedToDesktop != 0
                    && NameOf(in candidate).Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    adapter = candidateAdapter;
                    output = output6;
                    description = candidate;
                    return true;
                }

                NativeMethods.ReleaseCom(ref output6);
            }

            NativeMethods.ReleaseCom(ref candidateAdapter);
        }
    }

    private static string NameOf(in NativeMethods.DXGI_OUTPUT_DESC1 description)
    {
        fixed (char* name = description.DeviceName)
        {
            var characters = new ReadOnlySpan<char>(name, 32);
            int terminator = characters.IndexOf('\0');
            return new string(terminator >= 0 ? characters[..terminator] : characters);
        }
    }

    private static bool KeptDeviceServes(NativeMethods.LUID adapter)
    {
        if (s_device == IntPtr.Zero || !s_deviceAdapter.Matches(adapter))
        {
            return false;
        }

        int removed = ((delegate* unmanaged[Stdcall]<IntPtr, int>)
            NativeMethods.VtableSlot(s_device, NativeMethods.SlotDeviceGetDeviceRemovedReason))(s_device);
        return removed == 0;
    }

    private static void ReleaseDevice()
    {
        NativeMethods.ReleaseCom(ref s_context);
        NativeMethods.ReleaseCom(ref s_device);
        s_deviceAdapter = default;
    }

    /// <summary>
    /// Lets the driver reuse the staging memory for the next read, and starts the countdown to the
    /// trim and, later, to the release of the kept device.
    /// </summary>
    private static void AfterRead()
    {
        if (s_device != IntPtr.Zero)
        {
            // Destruction of the released staging texture is deferred until a flush. Without one the
            // next read cannot reuse its memory and allocates the same amount again.
            ((delegate* unmanaged[Stdcall]<IntPtr, void>)
                NativeMethods.VtableSlot(s_context, NativeMethods.SlotContextFlush))(s_context);
            s_lastUse = Stopwatch.GetTimestamp();
            s_trimPending = true;
        }

        s_housekeeping ??= new Timer(static _ => KeepHouse(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _ = s_housekeeping.Change(TrimDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Shortly after the last read: gives the spare tile and the staging memory back. A released
    /// staging texture stays in the driver's pool (66 MB for a 3840x2160 read stayed committed after
    /// Release and Flush) until the DXGI device is trimmed. After the idle period: releases the device.
    /// </summary>
    private static void KeepHouse()
    {
        // A running read starts the countdown again when it ends.
        if (!Gate.Wait(0))
        {
            return;
        }

        try
        {
            HdrTile.ReleaseSpare();
            if (s_device == IntPtr.Zero)
            {
                return;
            }

            if (s_trimPending)
            {
                s_trimPending = false;
                if (Marshal.QueryInterface(s_device, in NativeMethods.IID_IDXGIDevice3, out IntPtr dxgiDevice) >= 0)
                {
                    ((delegate* unmanaged[Stdcall]<IntPtr, void>)
                        NativeMethods.VtableSlot(dxgiDevice, NativeMethods.SlotDxgiDevice3Trim))(dxgiDevice);
                    NativeMethods.ReleaseCom(ref dxgiDevice);
                }
            }

            TimeSpan idle = Stopwatch.GetElapsedTime(s_lastUse);
            if (idle + TimeSpan.FromMilliseconds(100) >= DeviceIdleRelease)
            {
                ReleaseDevice();
            }
            else
            {
                _ = s_housekeeping?.Change(DeviceIdleRelease - idle, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception)
        {
            // A timer callback has nobody to report to.
        }
        finally
        {
            Gate.Release();
        }
    }

    private static HdrToneMap ToneMapFor(double sdrWhiteNits, HdrToneOperator toneOperator, float maxLuminance)
    {
        double white = double.IsFinite(sdrWhiteNits) ? Math.Clamp(sdrWhiteNits, 10.0, 10000.0) : HdrToneMap.NitsPerScRgbUnit;
        // Only the soft knee uses the peak; one table serves every display otherwise.
        double peak = toneOperator == HdrToneOperator.SoftKnee && float.IsFinite(maxLuminance) && maxLuminance > 0
            ? maxLuminance
            : HdrToneMap.DefaultPeakNits;
        var key = (white, toneOperator, peak);
        if (s_toneMap is { } kept && s_toneMapKey == key)
        {
            return kept;
        }

        s_toneMapKey = key;
        return s_toneMap = HdrToneMap.Create(white, toneOperator, peak);
    }

    private static long Convert(
        HdrToneMap toneMap, IntPtr surface, int surfaceStride, int surfaceBytes, int surfaceWidth, int surfaceHeight,
        HdrSurfaceRotation rotation, HdrTile tile)
    {
        IntPtr target = tile.Pixels;
        int stride = tile.Stride, rows = tile.Height, targetBytes = stride * rows;
        int bands = Math.Min(Environment.ProcessorCount, rows / RowsPerParallelBand);
        if (bands <= 1 || (long)tile.Width * rows < SmallestParallelPixelCount)
        {
            return toneMap.ConvertRows(new ReadOnlySpan<byte>((void*)surface, surfaceBytes), surfaceStride, surfaceWidth, surfaceHeight,
                rotation, new Span<byte>((void*)target, targetBytes), stride, 0, rows);
        }

        long aboveWhite = 0;
        Parallel.For(0, bands, band =>
        {
            int first = (int)((long)rows * band / bands), end = (int)((long)rows * (band + 1) / bands);
            long scaled = toneMap.ConvertRows(new ReadOnlySpan<byte>((void*)surface, surfaceBytes), surfaceStride, surfaceWidth,
                surfaceHeight, rotation, new Span<byte>((void*)target, targetBytes), stride, first, end - first);
            Interlocked.Add(ref aboveWhite, scaled);
        });
        return aboveWhite;
    }
}
