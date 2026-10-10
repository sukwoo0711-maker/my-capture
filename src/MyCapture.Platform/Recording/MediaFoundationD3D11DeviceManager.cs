using System.IO;
using System.Runtime.InteropServices;

namespace MyCapture.Platform.Recording;

/// <summary>
/// Owns one hardware D3D11 device with video support, the Media Foundation DXGI device manager
/// that lets a Source Reader decode, colour-convert and resize on the GPU, and a reusable
/// staging texture for reading decoded surfaces back.
/// </summary>
/// <remarks>
/// <para>
/// Only raw COM pointers and vtable calls are used: the handful of D3D11 calls needed here do
/// not justify full interface declarations, and no NuGet or native package is added. Every
/// setup failure is reported as an exception so the reader can fall back to software decode.
/// </para>
/// <para>
/// Reading a DXGI sample through <c>IMF2DBuffer::Lock2D</c> makes Media Foundation build and
/// synchronise a fresh staging copy on every lock (about 7 ms per frame measured on an RTX
/// 5080, independent of size). Copying into one persistent staging texture instead costs a
/// small GPU copy plus a single map. Instances are used from one reader thread only.
/// </para>
/// </remarks>
internal sealed unsafe class MediaFoundationD3D11DeviceManager : IDisposable
{
    private const int DriverTypeHardware = 1;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint CreateDeviceVideoSupport = 0x800;
    private const uint SdkVersion = 7;
    private const int UsageStaging = 3;
    private const uint CpuAccessRead = 0x20000;
    private const int MapRead = 1;

    // ID3D10Multithread: IUnknown (3) + Enter, Leave, SetMultithreadProtected.
    private const int SetMultithreadProtectedSlot = 5;

    // IMFDXGIDeviceManager: IUnknown (3) + CloseDeviceHandle, GetVideoService, LockDevice,
    // OpenDeviceHandle, ResetDevice.
    private const int ResetDeviceSlot = 7;

    // ID3D11Device: IUnknown (3) + CreateBuffer, CreateTexture1D, CreateTexture2D.
    private const int CreateTexture2DSlot = 5;

    // ID3D11Texture2D: IUnknown (3) + ID3D11DeviceChild (4) + ID3D11Resource (3) + GetDesc.
    private const int GetDescriptionSlot = 10;

    // ID3D11DeviceContext: IUnknown (3) + ID3D11DeviceChild (4) + 7 draw/state slots, then
    // Map and Unmap; CopySubresourceRegion follows the pipeline-state setters.
    private const int MapSlot = 14;
    private const int UnmapSlot = 15;
    private const int CopySubresourceRegionSlot = 46;

    private static readonly Guid MultithreadInterface = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _manager;
    private IntPtr _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private int _stagingFormat;

    private MediaFoundationD3D11DeviceManager()
    {
    }

    /// <summary>The <c>IMFDXGIDeviceManager</c> pointer, valid until disposal.</summary>
    internal IntPtr Manager
    {
        get
        {
            ObjectDisposedException.ThrowIf(_manager == IntPtr.Zero, this);
            return _manager;
        }
    }

    internal static MediaFoundationD3D11DeviceManager Create()
    {
        var owner = new MediaFoundationD3D11DeviceManager();
        try
        {
            Check(
                D3D11CreateDevice(
                    IntPtr.Zero,
                    DriverTypeHardware,
                    IntPtr.Zero,
                    CreateDeviceBgraSupport | CreateDeviceVideoSupport,
                    IntPtr.Zero,
                    0,
                    SdkVersion,
                    out owner._device,
                    out _,
                    out owner._context),
                nameof(D3D11CreateDevice));

            // Media Foundation calls the device from its own worker threads; D3D11 requires the
            // multithread guard to be enabled before the device is shared with a DXGI manager.
            Check(
                Marshal.QueryInterface(owner._device, in MultithreadInterface, out IntPtr multithread),
                "ID3D11Device.QueryInterface(ID3D10Multithread)");
            try
            {
                IntPtr* vtable = *(IntPtr**)multithread;
                _ = ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)vtable[SetMultithreadProtectedSlot])(multithread, 1);
            }
            finally
            {
                _ = Marshal.Release(multithread);
            }

            Check(MFCreateDXGIDeviceManager(out uint resetToken, out owner._manager), nameof(MFCreateDXGIDeviceManager));
            IntPtr* managerTable = *(IntPtr**)owner._manager;
            Check(
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)managerTable[ResetDeviceSlot])(
                    owner._manager,
                    owner._device,
                    resetToken),
                "IMFDXGIDeviceManager.ResetDevice");
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies the <paramref name="width"/> x <paramref name="height"/> rectangle at
    /// (<paramref name="x"/>, <paramref name="y"/>) of one texture subresource into a new,
    /// tightly packed top-down array: BGRA for 32-bit BGRA surfaces, or the packed NV12 layout
    /// of <see cref="DecodedVideoPixelFormat.Nv12"/> when <paramref name="nv12"/> is set. Returns
    /// null when the surface has another format or does not contain the rectangle, so the
    /// caller can use the generic lock path.
    /// </summary>
    internal byte[]? TryReadSurface(IntPtr texture, uint subresource, int x, int y, int width, int height, bool nv12 = false)
    {
        ObjectDisposedException.ThrowIf(_context == IntPtr.Zero, this);
        Texture2DDescription source;
        ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDescription*, void>)(*(IntPtr**)texture)[GetDescriptionSlot])(
            texture,
            &source);

        // Planar copies must start and end on the 2x2 chroma grid.
        int regionWidth = nv12 ? (width + 1) & ~1 : width;
        int regionHeight = nv12 ? (height + 1) & ~1 : height;
        if (!(nv12 ? source.Format == FormatNv12 : IsBgra32(source.Format))
            || source.SampleCount != 1
            || x < 0
            || y < 0
            || (nv12 && ((x | y) & 1) != 0)
            || width < 1
            || height < 1
            || (long)x + regionWidth > source.Width
            || (long)y + regionHeight > source.Height)
        {
            return null;
        }

        EnsureStaging(regionWidth, regionHeight, source.Format);
        IntPtr* context = *(IntPtr**)_context;
        var region = new Box((uint)x, (uint)y, 0, (uint)(x + regionWidth), (uint)(y + regionHeight), 1);
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)context[CopySubresourceRegionSlot])(
            _context,
            _staging,
            0,
            0,
            0,
            0,
            texture,
            subresource,
            &region);

        MappedSubresource mapped;
        Check(
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSubresource*, int>)context[MapSlot])(
                _context,
                _staging,
                0,
                MapRead,
                0,
                &mapped),
            "ID3D11DeviceContext.Map");
        try
        {
            if (nv12)
            {
                return PackNv12((byte*)mapped.Data, mapped.RowPitch, regionHeight, width, height);
            }

            int rowBytes = checked(width * 4);
            if (mapped.Data == IntPtr.Zero || mapped.RowPitch < rowBytes)
            {
                throw new InvalidDataException("The mapped video surface is smaller than its frame.");
            }

            // Every byte is overwritten by the row copies below.
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(rowBytes * height));
            fixed (byte* destination = pixels)
            {
                for (int row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        (byte*)mapped.Data + ((long)row * mapped.RowPitch),
                        destination + ((long)row * rowBytes),
                        rowBytes,
                        rowBytes);
                }
            }

            return pixels;
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)context[UnmapSlot])(_context, _staging, 0);
        }
    }

    /// <summary>
    /// Packs a mapped NV12 staging surface: luma rows, then the interleaved chroma plane that a
    /// D3D11 NV12 resource stores directly after <paramref name="planeHeight"/> luma rows.
    /// </summary>
    private static byte[] PackNv12(byte* data, int rowPitch, int planeHeight, int width, int height)
    {
        int chromaRowBytes = ((width + 1) / 2) * 2;
        int chromaRows = (height + 1) / 2;
        if (data == null || rowPitch < chromaRowBytes || rowPitch < width)
        {
            throw new InvalidDataException("The mapped NV12 surface is smaller than its frame.");
        }

        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked((width * height) + (chromaRowBytes * chromaRows)));
        fixed (byte* destination = pixels)
        {
            for (int row = 0; row < height; row++)
            {
                Buffer.MemoryCopy(data + ((long)row * rowPitch), destination + ((long)row * width), width, width);
            }

            byte* chroma = data + ((long)rowPitch * planeHeight);
            byte* chromaDestination = destination + ((long)width * height);
            for (int row = 0; row < chromaRows; row++)
            {
                Buffer.MemoryCopy(
                    chroma + ((long)row * rowPitch),
                    chromaDestination + ((long)row * chromaRowBytes),
                    chromaRowBytes,
                    chromaRowBytes);
            }
        }

        return pixels;
    }

    public void Dispose()
    {
        Release(ref _staging);
        Release(ref _manager);
        Release(ref _context);
        Release(ref _device);
    }

    private const int FormatNv12 = 103; // DXGI_FORMAT_NV12

    private static bool IsBgra32(int format) =>
        format is 87 // DXGI_FORMAT_B8G8R8A8_UNORM
            or 88 // DXGI_FORMAT_B8G8R8X8_UNORM
            or 91 // DXGI_FORMAT_B8G8R8A8_UNORM_SRGB
            or 93; // DXGI_FORMAT_B8G8R8X8_UNORM_SRGB

    private void EnsureStaging(int width, int height, int format)
    {
        if (_staging != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height && _stagingFormat == format)
        {
            return;
        }

        Release(ref _staging);
        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = UsageStaging,
            BindFlags = 0,
            CpuAccessFlags = CpuAccessRead,
            MiscFlags = 0,
        };
        IntPtr staging;
        Check(
            ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDescription*, IntPtr, IntPtr*, int>)(*(IntPtr**)_device)[CreateTexture2DSlot])(
                _device,
                &description,
                IntPtr.Zero,
                &staging),
            "ID3D11Device.CreateTexture2D(staging)");
        _staging = staging;
        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;
    }

    private static void Release(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            _ = Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Direct3D/Media Foundation call '{operation}' failed with HRESULT 0x{result:X8}.");
        }
    }

    internal static int NativeTextureDescriptionSize => sizeof(Texture2DDescription);

    internal static int NativeMappedSubresourceSize => sizeof(MappedSubresource);

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Box(uint Left, uint Top, uint Front, uint Right, uint Bottom, uint Back);

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public IntPtr Data;
        public int RowPitch;
        public int DepthPitch;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr deviceManager);
}
