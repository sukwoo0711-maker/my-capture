using System.Runtime.InteropServices;

namespace MyCapture.Platform.Interop;

/// <summary>
/// Direct3D 11, DXGI and WinRT activation declarations used only by the
/// Windows.Graphics.Capture recording spike.
/// </summary>
/// <remarks>
/// COM interfaces are called through their documented vtable slots with function
/// pointers instead of a wrapper package, keeping the "no new NuGet" rule. Each slot
/// number below is the method's position in the published d3d11.h / dxgi1_6.h /
/// windows.graphics.capture.interop.h declaration, counting IUnknown's three methods first.
/// </remarks>
internal static partial class NativeMethods
{
    internal const int D3D_DRIVER_TYPE_HARDWARE = 1;
    internal const int D3D_DRIVER_TYPE_WARP = 5;
    internal const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    internal const uint D3D11_SDK_VERSION = 7;
    internal const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    internal const uint D3D11_USAGE_STAGING = 3;
    internal const uint D3D11_CPU_ACCESS_READ = 0x20000;
    internal const int D3D11_MAP_READ = 1;
    internal const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
    internal const int DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 = 12;

    // IUnknown
    internal const int SlotRelease = 2;

    // ID3D11Device
    internal const int SlotDeviceCreateTexture2D = 5;
    internal const int SlotDeviceGetDeviceRemovedReason = 39;

    // ID3D11DeviceContext
    internal const int SlotContextMap = 14;
    internal const int SlotContextUnmap = 15;
    internal const int SlotContextCopySubresourceRegion = 46;

    // IGraphicsCaptureItemInterop (IUnknown-derived): CreateForWindow = 3, CreateForMonitor = 4
    internal const int SlotInteropCreateForMonitor = 4;

    // IDirect3DDxgiInterfaceAccess (IUnknown-derived)
    internal const int SlotDxgiAccessGetInterface = 3;

    // IDXGIFactory: IDXGIObject adds four methods after IUnknown, EnumAdapters is next.
    internal const int SlotFactoryEnumAdapters = 7;

    // IDXGIAdapter
    internal const int SlotAdapterEnumOutputs = 7;

    // IDXGIOutput6
    internal const int SlotOutput6GetDesc1 = 27;

    internal static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    internal static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    internal static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    internal static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    internal static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    internal static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    internal static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_BOX
    {
        public uint Left;
        public uint Top;
        public uint Front;
        public uint Right;
        public uint Bottom;
        public uint Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DXGI_OUTPUT_DESC1
    {
        public fixed char DeviceName[32];
        public RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float Primaries[8];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    [LibraryImport("d3d11.dll")]
    internal static partial int D3D11CreateDevice(
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

    [LibraryImport("d3d11.dll")]
    internal static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [LibraryImport("dxgi.dll")]
    internal static partial int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [LibraryImport("combase.dll")]
    internal static partial int WindowsDeleteString(IntPtr hstring);

    [LibraryImport("combase.dll")]
    internal static partial int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);

    /// <summary>Releases a COM pointer and clears it; tolerates <see cref="IntPtr.Zero"/>.</summary>
    internal static void ReleaseCom(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            _ = Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    /// <summary>Address of vtable slot <paramref name="slot"/> of a COM object.</summary>
    internal static unsafe void* VtableSlot(IntPtr comObject, int slot) => (*(void***)comObject)[slot];
}
