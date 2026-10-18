using System.Runtime.InteropServices;
using MyCapture.Platform.Interop;

namespace MyCapture.Platform.Display;

/// <summary>Cheap DXGI inventory; does not create a graphics/decoder device or select an adapter.</summary>
public static unsafe class DisplayAdapterProbe
{
    public static IReadOnlyList<string> Describe()
    {
        var descriptions = new List<string>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDXGIFactory1(in NativeMethods.IID_IDXGIFactory1, out IntPtr factory));
        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
                NativeMethods.VtableSlot(factory, NativeMethods.SlotFactoryEnumAdapters);
            for (uint index = 0; ; index++)
            {
                IntPtr adapter;
                int hr = enumerate(factory, index, &adapter);
                if (hr == NativeMethods.DXGI_ERROR_NOT_FOUND) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    // IDXGIAdapter::GetDesc follows EnumOutputs at vtable slot 8.
                    var getDescription = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDescription*, int>)
                        NativeMethods.VtableSlot(adapter, 8);
                    AdapterDescription description;
                    Marshal.ThrowExceptionForHR(getDescription(adapter, &description));
                    descriptions.Add(new string(description.Description).TrimEnd('\0'));
                }
                finally { NativeMethods.ReleaseCom(ref adapter); }
            }
        }
        finally { NativeMethods.ReleaseCom(ref factory); }
        return descriptions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long AdapterLuid;
    }
}
