using System.Runtime.InteropServices;

namespace MyCapture.App.Diagnostics;

/// <summary>Resident, nonshared pages of this process; unlike PrivateMemorySize64 this is RAM, not commit.</summary>
internal sealed class ResidentProbeWorkingSet
{
    private nuint[] _buffer = [];

    internal unsafe (long? Bytes, string? Error) Read(IntPtr process, long workingSetBytes)
    {
        int capacity = checked((int)Math.Min(int.MaxValue - 1L,
            Math.Max(4096, workingSetBytes / Environment.SystemPageSize + 4096)));
        if (_buffer.Length < capacity + 1) _buffer = new nuint[capacity + 1];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool success;
            fixed (nuint* buffer = _buffer)
                success = QueryWorkingSet(process, (IntPtr)buffer, checked(_buffer.Length * IntPtr.Size));
            if (success)
            {
                nuint count = _buffer[0];
                if (count > (nuint)(_buffer.Length - 1)) return (null, "QueryWorkingSet returned an invalid page count");
                return (CountPrivateBytes(_buffer.AsSpan(1, (int)count), Environment.SystemPageSize), null);
            }
            int error = Marshal.GetLastWin32Error();
            if (error != 24) return (null, "QueryWorkingSet Win32 error " + error); // ERROR_BAD_LENGTH
            // The working set can grow between sizing and the query. Retry with a larger buffer.
            if (_buffer.Length > 8 * 1024 * 1024) break;
            _buffer = new nuint[checked(_buffer.Length * 2)];
        }
        return (null, "QueryWorkingSet buffer changed during three attempts");
    }

    internal static long CountPrivateBytes(ReadOnlySpan<nuint> entries, int pageSize)
    {
        long pages = 0;
        // PSAPI_WORKING_SET_BLOCK.Shared is bit 8 in both the 32-bit and 64-bit layouts.
        foreach (nuint entry in entries)
            if ((entry & (1u << 8)) == 0) pages++;
        return checked(pages * pageSize);
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSet(IntPtr process, IntPtr buffer, int bytes);
}
