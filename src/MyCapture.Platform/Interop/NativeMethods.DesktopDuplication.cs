using System.Runtime.InteropServices;

namespace MyCapture.Platform.Interop;

/// <summary>
/// DisplayConfig colour-state queries and DXGI Desktop Duplication declarations for the scRGB
/// read of HDR displays (still capture). D3D11 and DXGI pieces shared with recording are in
/// <c>NativeMethods.GraphicsCapture.cs</c>.
/// </summary>
/// <remarks>
/// Slot numbers are positions in the published dxgi1_2.h / dxgi1_3.h / dxgi1_5.h / d3d11.h
/// declarations, counting IUnknown's three methods first. Structure layouts follow wingdi.h and
/// dxgi1_2.h; every field is fixed-size or pointer-size, so the same declarations serve x64 and x86.
/// </remarks>
internal static partial class NativeMethods
{
    internal const int D3D_DRIVER_TYPE_UNKNOWN = 0;
    internal const uint DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
    internal const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    internal const int DXGI_MODE_ROTATION_IDENTITY = 1;
    internal const int DXGI_MODE_ROTATION_ROTATE270 = 4;

    internal const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    internal const int ERROR_INSUFFICIENT_BUFFER = 122;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

    /// <summary>Windows 11 24H2 and later; earlier builds answer ERROR_INVALID_PARAMETER.</summary>
    internal const uint DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 = 15;

    /// <summary>Bytes of one DISPLAYCONFIG_MODE_INFO (a 16-byte header and a 48-byte union).</summary>
    internal const int DisplayConfigModeInfoSize = 64;

    // IDXGIAdapter: EnumOutputs = 7, GetDesc = 8.
    internal const int SlotAdapterGetDesc = 8;

    // IDXGIOutput5 (IDXGIOutput6 derives from it): DuplicateOutput1 follows IDXGIOutput4's slot 25.
    internal const int SlotOutput5DuplicateOutput1 = 26;

    // IDXGIOutputDuplication: IDXGIObject's four methods, then GetDesc.
    internal const int SlotDuplicationGetDesc = 7;
    internal const int SlotDuplicationAcquireNextFrame = 8;
    internal const int SlotDuplicationReleaseFrame = 14;

    // ID3D11Texture2D: ID3D11DeviceChild 3..6, ID3D11Resource 7..9, GetDesc.
    internal const int SlotTexture2DGetDesc = 10;

    // ID3D11DeviceContext: ClearState = 110, Flush = 111.
    internal const int SlotContextFlush = 111;

    // IDXGIDevice3: IDXGIDevice 7..11, IDXGIDevice1 12..13, IDXGIDevice2 14..16, Trim.
    internal const int SlotDxgiDevice3Trim = 17;

    internal static readonly Guid IID_IDXGIDevice3 = new("6007896c-3244-4afd-bf18-a6d3beda5023");

    /// <summary>Two 32-bit halves, so the structure keeps the 4-byte alignment LUID has in the Windows ABI.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        public uint LowPart;
        public int HighPart;

        public readonly bool Matches(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DXGI_ADAPTER_DESC
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public LUID AdapterLuid;
    }

    /// <summary>DXGI_OUTDUPL_DESC with its DXGI_MODE_DESC written out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_DESC
    {
        public uint Width;
        public uint Height;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
        public int Rotation;
        public int DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DXGI_OUTDUPL_FRAME_INFO
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public POINT PointerPosition;
        public int PointerVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public LUID SourceAdapterId;
        public uint SourceId;
        public uint SourceModeInfoIdx;
        public uint SourceStatusFlags;
        public LUID TargetAdapterId;
        public uint TargetId;
        public uint TargetModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint TargetStatusFlags;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public fixed char ViewGdiDeviceName[32];
    }

    /// <summary>
    /// DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO. Flags: bit 0 advanced colour supported, bit 1 enabled
    /// (HDR or wide colour gamut), bit 2 wide colour enforced, bit 3 force-disabled.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Flags;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    /// <summary>
    /// DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2: the same leading fields, then the active colour
    /// mode (0 SDR, 1 wide colour gamut, 2 HDR).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Flags;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public uint ActiveColorMode;
    }

    /// <summary>SDRWhiteLevel is the SDR white as a multiple of 80 nits, times 1000.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_SDR_WHITE_LEVEL
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint SDRWhiteLevel;
    }

    [LibraryImport("user32.dll")]
    internal static unsafe partial int GetDisplayConfigBufferSizes(uint flags, uint* pathCount, uint* modeCount);

    [LibraryImport("user32.dll")]
    internal static unsafe partial int QueryDisplayConfig(
        uint flags, uint* pathCount, DISPLAYCONFIG_PATH_INFO* paths, uint* modeCount, byte* modes, void* currentTopologyId);

    /// <summary>The packet starts with a DISPLAYCONFIG_DEVICE_INFO_HEADER naming its type and size.</summary>
    [LibraryImport("user32.dll")]
    internal static unsafe partial int DisplayConfigGetDeviceInfo(void* requestPacket);
}
