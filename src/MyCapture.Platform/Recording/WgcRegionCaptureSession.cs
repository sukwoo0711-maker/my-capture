using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Interop;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;

namespace MyCapture.Platform.Recording;

/// <summary>Point-in-time observations of one Windows.Graphics.Capture session.</summary>
/// <param name="DriverType">D3D driver type actually created (1 hardware, 5 WARP).</param>
/// <param name="FeatureLevel">D3D feature level as the D3D_FEATURE_LEVEL value.</param>
/// <param name="BorderlessAccess">Result of <c>GraphicsCaptureAccess.RequestAccessAsync(Borderless)</c>, or the error.</param>
/// <param name="BorderRequired">The session's <c>IsBorderRequired</c> read back after it was set.</param>
/// <param name="BorderSetError">Exception raised by setting <c>IsBorderRequired = false</c>, if any.</param>
/// <param name="CursorCaptureEnabled">The session's <c>IsCursorCaptureEnabled</c> read back.</param>
/// <param name="CreateMs">Device, item, pool and session creation plus <c>StartCapture</c>.</param>
/// <param name="FirstFrameWaitMs">Time the first <see cref="WgcRegionCaptureSession.CaptureInto"/> waited for DWM's first frame.</param>
/// <param name="FramesArrived">Frames DWM delivered to the pool.</param>
/// <param name="FramesSuperseded">Delivered frames replaced by a newer one before the recorder asked.</param>
/// <param name="FramesCopied">Frames cropped into the staging texture.</param>
/// <param name="ReusedCalls">Captures answered from the previous frame because nothing new was composed.</param>
/// <param name="PoolRecreations">Frame pool recreations after a display size change.</param>
public sealed record WgcCaptureDiagnostics(
    int DriverType,
    int FeatureLevel,
    string? BorderlessAccess,
    bool? BorderRequired,
    string? BorderSetError,
    bool CursorCaptureEnabled,
    double CreateMs,
    double FirstFrameWaitMs,
    long FramesArrived,
    long FramesSuperseded,
    long FramesCopied,
    long ReusedCalls,
    long PoolRecreations);

/// <summary>
/// Fixed-region recording capture through Windows.Graphics.Capture (spike).
/// </summary>
/// <remarks>
/// <para>
/// A monitor <see cref="GraphicsCaptureItem"/> is created with
/// <c>IGraphicsCaptureItemInterop.CreateForMonitor</c> on the display that fully contains
/// the region. A free-threaded frame pool keeps only the newest DWM frame: every arrival
/// replaces and returns the previous one, so the pool never fills while the recorder is
/// paused or slow, and the newest frame always matches what is on screen. When the recorder
/// asks for a frame, only the region is copied on the GPU into a CPU-readable staging
/// texture, which is mapped and copied row by row into the caller's BGRA buffer.
/// </para>
/// <para>
/// DWM only produces a frame when the display content changes. When nothing new arrived
/// since the previous capture the caller's buffer, which already holds that frame, is left
/// as is. Windows excluded with <c>WDA_EXCLUDEFROMCAPTURE</c> are omitted by DWM exactly as
/// for GDI capture.
/// </para>
/// <para>
/// The session may be created on a worker thread (so it opens while the encoder initialises),
/// but <see cref="CaptureInto"/> must then be called from one thread at a time: the D3D
/// immediate context is not thread-safe, and only the frame hand-off is synchronised. No
/// wrapper package is used: D3D11 and the interop interfaces are called through vtable slots
/// and WinRT objects come from the Windows SDK projection.
/// </para>
/// </remarks>
public sealed unsafe class WgcRegionCaptureSession : IDisposable
{
    private const string CaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private const int PoolBuffers = 3;
    private const int FirstFrameTimeoutMs = 2000;
    private static readonly TimeSpan BorderlessRequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly object BorderlessGate = new();
    private static string? s_borderlessAccess;

    private readonly object _frameGate = new();
    private readonly ManualResetEventSlim _frameArrived = new(false);
    private readonly int _offsetX;
    private readonly int _offsetY;
    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _staging;
    private IDirect3DDevice? _winrtDevice;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Direct3D11CaptureFrame? _latest;
    private GraphicsCaptureItem? _item;
    private volatile bool _itemClosed;
    private SizeInt32 _poolSize;
    private byte[]? _lastDestination;
    private int _lastStride;
    private bool _hasStagedFrame;
    private bool _disposed;

    private int _driverType;
    private int _featureLevel;
    private string? _borderlessAccess;
    private bool? _borderRequired;
    private string? _borderSetError;
    private bool _cursorCaptureEnabled;
    private double _createMs;
    private double _firstFrameWaitMs;
    private long _framesArrived;
    private long _framesSuperseded;
    private long _framesCopied;
    private long _reusedCalls;
    private long _poolRecreations;

    private WgcRegionCaptureSession(int offsetX, int offsetY, int width, int height, RectD monitorBounds)
    {
        _offsetX = offsetX;
        _offsetY = offsetY;
        Width = width;
        Height = height;
        MonitorBounds = monitorBounds;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Physical bounds of the captured display.</summary>
    public RectD MonitorBounds { get; }

    /// <summary>True when the OS reports Windows.Graphics.Capture support.</summary>
    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a monitor capture for <paramref name="screenRegion"/> (physical virtual-screen
    /// pixels). Throws <see cref="NotSupportedException"/> when the region is not inside one display.
    /// </summary>
    /// <param name="requestBorderless">
    /// Request borderless access and set <c>IsBorderRequired = false</c>. Diagnostics pass
    /// <see langword="false"/> to observe the default yellow border.
    /// </param>
    public static WgcRegionCaptureSession Create(RectD screenRegion, bool includeCursor, bool requestBorderless = true)
    {
        RectD pixels = screenRegion.Normalized().ToPixelBounds();
        if (!IsSupported())
        {
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this device.");
        }

        (IntPtr monitor, RectD bounds) = FindContainingMonitor(pixels);
        var session = new WgcRegionCaptureSession(
            (int)(pixels.Left - bounds.Left),
            (int)(pixels.Top - bounds.Top),
            Math.Max(1, (int)pixels.Width),
            Math.Max(1, (int)pixels.Height),
            bounds);
        long started = Stopwatch.GetTimestamp();
        try
        {
            session.Initialize(monitor, includeCursor, requestBorderless);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        session._createMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return session;
    }

    public WgcCaptureDiagnostics GetDiagnostics()
    {
        lock (_frameGate)
        {
            return new WgcCaptureDiagnostics(_driverType, _featureLevel, _borderlessAccess, _borderRequired,
                _borderSetError, _cursorCaptureEnabled, _createMs, _firstFrameWaitMs, _framesArrived,
                _framesSuperseded, _framesCopied, _reusedCalls, _poolRecreations);
        }
    }

    /// <summary>
    /// Copies the newest composed region into <paramref name="destination"/> as top-down BGRA.
    /// </summary>
    public void CaptureInto(byte[] destination, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_itemClosed)
        {
            // The display was disconnected or the session ended: frames would silently freeze.
            throw new InvalidOperationException("The captured display is no longer available.");
        }

        ArgumentNullException.ThrowIfNull(destination);
        if (stride < checked(Width * 4)) throw new ArgumentOutOfRangeException(nameof(stride));
        if (destination.Length < checked(stride * Height)) throw new ArgumentException("Buffer is too small.", nameof(destination));

        Direct3D11CaptureFrame? frame = TakeLatest();
        if (frame is null && !_hasStagedFrame)
        {
            // A new session's first frame arrives asynchronously after StartCapture.
            long waitStarted = Stopwatch.GetTimestamp();
            while (frame is null && Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds < FirstFrameTimeoutMs)
            {
                _ = _frameArrived.Wait(20);
                frame = TakeLatest();
            }

            _firstFrameWaitMs = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
            if (frame is null)
            {
                throw new TimeoutException("Windows.Graphics.Capture delivered no frame.");
            }
        }

        if (frame is not null)
        {
            using (frame)
            {
                StageRegion(frame);
            }

            Interlocked.Increment(ref _framesCopied);
        }
        else if (ReferenceEquals(destination, _lastDestination) && stride == _lastStride)
        {
            // Nothing was composed since the previous capture; the buffer already holds it.
            Interlocked.Increment(ref _reusedCalls);
            return;
        }

        ReadStaged(destination, stride);
        _lastDestination = destination;
        _lastStride = stride;
    }

    private void Initialize(IntPtr monitor, bool includeCursor, bool requestBorderless)
    {
        CreateDevice();
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_device, in NativeMethods.IID_IDXGIDevice, out IntPtr dxgiDevice));
        IntPtr inspectable;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable));
        }
        finally
        {
            NativeMethods.ReleaseCom(ref dxgiDevice);
        }

        try
        {
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            NativeMethods.ReleaseCom(ref inspectable);
        }

        GraphicsCaptureItem item = CreateItemForMonitor(monitor);
        _item = item;
        item.Closed += OnItemClosed;
        _poolSize = item.Size;
        if (_offsetX < 0 || _offsetY < 0 || _offsetX + Width > _poolSize.Width || _offsetY + Height > _poolSize.Height)
        {
            throw new NotSupportedException(
                $"The region does not fit the captured display surface ({_poolSize.Width}x{_poolSize.Height}).");
        }

        _staging = CreateStagingTexture(Width, Height);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = includeCursor;
        _cursorCaptureEnabled = _session.IsCursorCaptureEnabled;
        if (requestBorderless)
        {
            _borderlessAccess = RequestBorderlessAccess();
            try
            {
                _session.IsBorderRequired = false;
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                _borderSetError = $"{ex.GetType().Name} 0x{ex.HResult:X8}";
            }
        }

        try
        {
            _borderRequired = _session.IsBorderRequired;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _borderSetError ??= $"read {ex.GetType().Name} 0x{ex.HResult:X8}";
        }

        _session.StartCapture();
    }

    private void CreateDevice()
    {
        int lastError = 0;
        foreach (int driverType in new[] { NativeMethods.D3D_DRIVER_TYPE_HARDWARE, NativeMethods.D3D_DRIVER_TYPE_WARP })
        {
            int hr = NativeMethods.D3D11CreateDevice(IntPtr.Zero, driverType, IntPtr.Zero,
                NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, NativeMethods.D3D11_SDK_VERSION,
                out _device, out _featureLevel, out _context);
            if (hr >= 0)
            {
                _driverType = driverType;
                return;
            }

            lastError = hr;
        }

        Marshal.ThrowExceptionForHR(lastError);
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(CaptureItemClass, CaptureItemClass.Length, out IntPtr className));
        IntPtr factory;
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(
                className, in NativeMethods.IID_IGraphicsCaptureItemInterop, out factory));
        }
        finally
        {
            _ = NativeMethods.WindowsDeleteString(className);
        }

        try
        {
            Guid iid = NativeMethods.IID_IGraphicsCaptureItem;
            IntPtr itemPointer;
            var createForMonitor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)
                NativeMethods.VtableSlot(factory, NativeMethods.SlotInteropCreateForMonitor);
            Marshal.ThrowExceptionForHR(createForMonitor(factory, monitor, &iid, &itemPointer));
            try
            {
                return GraphicsCaptureItem.FromAbi(itemPointer);
            }
            finally
            {
                NativeMethods.ReleaseCom(ref itemPointer);
            }
        }
        finally
        {
            NativeMethods.ReleaseCom(ref factory);
        }
    }

    private IntPtr CreateStagingTexture(int width, int height)
    {
        var description = new NativeMethods.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = NativeMethods.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            Usage = NativeMethods.D3D11_USAGE_STAGING,
            CPUAccessFlags = NativeMethods.D3D11_CPU_ACCESS_READ,
        };
        IntPtr texture;
        var createTexture2D = (delegate* unmanaged[Stdcall]<IntPtr, NativeMethods.D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)
            NativeMethods.VtableSlot(_device, NativeMethods.SlotDeviceCreateTexture2D);
        Marshal.ThrowExceptionForHR(createTexture2D(_device, &description, IntPtr.Zero, &texture));
        return texture;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? next;
        try
        {
            next = sender.TryGetNextFrame();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or COMException)
        {
            return;
        }

        if (next is null)
        {
            return;
        }

        Direct3D11CaptureFrame? released;
        lock (_frameGate)
        {
            if (_disposed)
            {
                released = next;
            }
            else
            {
                released = _latest;
                _latest = next;
                _framesArrived++;
                if (released is not null)
                {
                    _framesSuperseded++;
                }

                _frameArrived.Set();
            }
        }

        // Returning the older buffer immediately keeps the pool from filling up, so DWM never
        // drops a newer frame while the recorder is paused or busy encoding.
        released?.Dispose();
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => _itemClosed = true;

    private Direct3D11CaptureFrame? TakeLatest()
    {
        lock (_frameGate)
        {
            Direct3D11CaptureFrame? frame = _latest;
            _latest = null;
            _frameArrived.Reset();
            return frame;
        }
    }

    private void StageRegion(Direct3D11CaptureFrame frame)
    {
        SizeInt32 content = frame.ContentSize;
        if (content.Width != _poolSize.Width || content.Height != _poolSize.Height)
        {
            // The display mode changed: later frames need surfaces of the new size.
            _pool!.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, content);
            _poolSize = content;
            Interlocked.Increment(ref _poolRecreations);
            if (_offsetX + Width > content.Width || _offsetY + Height > content.Height)
            {
                throw new InvalidOperationException("The recording region no longer fits the captured display.");
            }
        }

        IntPtr texture = GetTexture(frame.Surface);
        try
        {
            var box = new NativeMethods.D3D11_BOX
            {
                Left = (uint)_offsetX,
                Top = (uint)_offsetY,
                Front = 0,
                Right = (uint)(_offsetX + Width),
                Bottom = (uint)(_offsetY + Height),
                Back = 1,
            };
            var copySubresourceRegion = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, NativeMethods.D3D11_BOX*, void>)
                NativeMethods.VtableSlot(_context, NativeMethods.SlotContextCopySubresourceRegion);
            copySubresourceRegion(_context, _staging, 0, 0, 0, 0, texture, 0, &box);
            _hasStagedFrame = true;
        }
        finally
        {
            NativeMethods.ReleaseCom(ref texture);
        }
    }

    private static IntPtr GetTexture(IDirect3DSurface surface)
    {
        // Borrowed pointer: valid while the projected surface object is alive.
        IntPtr surfacePointer = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
            surfacePointer, in NativeMethods.IID_IDirect3DDxgiInterfaceAccess, out IntPtr access));
        try
        {
            Guid iid = NativeMethods.IID_ID3D11Texture2D;
            IntPtr texture;
            var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                NativeMethods.VtableSlot(access, NativeMethods.SlotDxgiAccessGetInterface);
            Marshal.ThrowExceptionForHR(getInterface(access, &iid, &texture));
            return texture;
        }
        finally
        {
            NativeMethods.ReleaseCom(ref access);
            GC.KeepAlive(surface);
        }
    }

    private void ReadStaged(byte[] destination, int stride)
    {
        NativeMethods.D3D11_MAPPED_SUBRESOURCE mapped;
        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, NativeMethods.D3D11_MAPPED_SUBRESOURCE*, int>)
            NativeMethods.VtableSlot(_context, NativeMethods.SlotContextMap);
        int hr = map(_context, _staging, 0, NativeMethods.D3D11_MAP_READ, 0, &mapped);
        if (hr < 0)
        {
            var removedReason = (delegate* unmanaged[Stdcall]<IntPtr, int>)
                NativeMethods.VtableSlot(_device, NativeMethods.SlotDeviceGetDeviceRemovedReason);
            throw new COMException(
                $"Staging texture Map failed (0x{hr:X8}, device removed reason 0x{removedReason(_device):X8}).", hr);
        }

        try
        {
            int rowBytes = Width * 4;
            byte* source = (byte*)mapped.Data;
            fixed (byte* target = destination)
            {
                for (int row = 0; row < Height; row++)
                {
                    Buffer.MemoryCopy(source + (long)row * mapped.RowPitch, target + (long)row * stride, rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
                NativeMethods.VtableSlot(_context, NativeMethods.SlotContextUnmap);
            unmap(_context, _staging, 0);
        }
    }

    private static string RequestBorderlessAccess()
    {
        lock (BorderlessGate)
        {
            if (s_borderlessAccess is not null)
            {
                return s_borderlessAccess;
            }

            try
            {
                Task<AppCapabilityAccessStatus> request = GraphicsCaptureAccess
                    .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                    .AsTask();
                s_borderlessAccess = request.Wait(BorderlessRequestTimeout) ? request.Result.ToString() : "Timeout";
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or AggregateException
                or InvalidOperationException or TypeLoadException)
            {
                Exception root = ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
                s_borderlessAccess = $"{root.GetType().Name} 0x{root.HResult:X8}";
            }

            return s_borderlessAccess;
        }
    }

    private static (IntPtr Monitor, RectD Bounds) FindContainingMonitor(RectD pixels)
    {
        var handles = new List<IntPtr>();
        var bounds = new List<RectD>();
        bool Callback(IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT clip, IntPtr data)
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                handles.Add(monitor);
                bounds.Add(new RectD(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height));
            }

            return true;
        }

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        int index = RecordingCaptureBackendSelection.FindContainingMonitor(pixels, bounds);
        if (index < 0)
        {
            throw new NotSupportedException("The recording region is not inside a single display.");
        }

        return (handles[index], bounds[index]);
    }

    /// <summary>Stops the capture and releases GPU resources. Safe from any thread, once or more.</summary>
    public void Dispose()
    {
        Direct3D11CaptureFrame? latest;
        lock (_frameGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            latest = _latest;
            _latest = null;
        }

        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
        }

        latest?.Dispose();
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
            _item = null;
        }

        _winrtDevice?.Dispose();
        _winrtDevice = null;
        NativeMethods.ReleaseCom(ref _staging);
        NativeMethods.ReleaseCom(ref _context);
        NativeMethods.ReleaseCom(ref _device);
        _frameArrived.Dispose();
    }
}

/// <summary>Colour state of one desktop output, for recording diagnostics.</summary>
/// <param name="AdvancedColor">True when the output runs in HDR (ST.2084 / BT.2020) colour space.</param>
public sealed record DisplayColorInfo(
    string DeviceName,
    RectD Bounds,
    uint BitsPerColor,
    int ColorSpace,
    bool AdvancedColor,
    float MaxLuminance);

/// <summary>Reads each DXGI output's colour space to record whether HDR is active.</summary>
public static unsafe class DisplayColorProbe
{
    public static IReadOnlyList<DisplayColorInfo> Describe()
    {
        var displays = new List<DisplayColorInfo>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDXGIFactory1(in NativeMethods.IID_IDXGIFactory1, out IntPtr factory));
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
                NativeMethods.VtableSlot(factory, NativeMethods.SlotFactoryEnumAdapters);
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                IntPtr adapter;
                if (enumAdapters(factory, adapterIndex, &adapter) < 0)
                {
                    break;
                }

                try
                {
                    AddOutputs(adapter, displays);
                }
                finally
                {
                    NativeMethods.ReleaseCom(ref adapter);
                }
            }
        }
        finally
        {
            NativeMethods.ReleaseCom(ref factory);
        }

        return displays;
    }

    private static void AddOutputs(IntPtr adapter, List<DisplayColorInfo> displays)
    {
        var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
            NativeMethods.VtableSlot(adapter, NativeMethods.SlotAdapterEnumOutputs);
        for (uint outputIndex = 0; ; outputIndex++)
        {
            IntPtr output;
            if (enumOutputs(adapter, outputIndex, &output) < 0)
            {
                return;
            }

            try
            {
                if (Marshal.QueryInterface(output, in NativeMethods.IID_IDXGIOutput6, out IntPtr output6) < 0)
                {
                    continue;
                }

                try
                {
                    NativeMethods.DXGI_OUTPUT_DESC1 description;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, NativeMethods.DXGI_OUTPUT_DESC1*, int>)
                        NativeMethods.VtableSlot(output6, NativeMethods.SlotOutput6GetDesc1);
                    if (getDesc1(output6, &description) >= 0)
                    {
                        string name = new string(description.DeviceName).TrimEnd('\0');
                        NativeMethods.RECT r = description.DesktopCoordinates;
                        displays.Add(new DisplayColorInfo(name, new RectD(r.Left, r.Top, r.Width, r.Height),
                            description.BitsPerColor, description.ColorSpace,
                            description.ColorSpace == NativeMethods.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,
                            description.MaxLuminance));
                    }
                }
                finally
                {
                    NativeMethods.ReleaseCom(ref output6);
                }
            }
            finally
            {
                NativeMethods.ReleaseCom(ref output);
            }
        }
    }
}
