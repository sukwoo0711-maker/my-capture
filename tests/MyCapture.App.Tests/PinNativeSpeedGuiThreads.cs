using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// The threads of this process that have a message queue. Windows gives each of them USER and
/// kernel objects of its own, so a resource count taken before and after pin cycles is only a
/// statement about the pins if this set stayed the same. In a process that has not shown a window
/// before, a thread whose start routine is in ntdll.dll (a Windows thread-pool worker, started by
/// neither this code nor the runtime) joins the set 0.8 to 1 s after the first pin: seen as
/// USER +1 and kernel +3 or +4 in whichever fifty-cycle test ran first, six runs out of six.
/// </summary>
internal static class PinNativeSpeedGuiThreads
{
    internal const int Attempts = 3;

    internal static HashSet<uint> Capture(Process process)
    {
        var result = new HashSet<uint>();
        process.Refresh();
        foreach (ProcessThread thread in process.Threads)
            using (thread)
            {
                // Succeeds only for a thread with a message queue.
                var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
                if (GetGUIThreadInfo((uint)thread.Id, ref info)) result.Add((uint)thread.Id);
            }
        return result;
    }

    /// <summary>
    /// True when the set changed during a measurement that may still be repeated. The change is
    /// written out with each new thread's start module; the last attempt is always judged.
    /// </summary>
    internal static bool ChangedAndMayRepeat(Process process, HashSet<uint> before, int attempt, ITestOutputHelper output)
    {
        HashSet<uint> after = Capture(process);
        if (after.SetEquals(before)) return false;
        output.WriteLine($"Attempt {attempt}: the process's GUI threads changed during the cycles, so its USER and kernel counts say nothing about the pins.");
        foreach (uint thread in after.Except(before)) output.WriteLine("  new GUI thread " + Describe(process, thread));
        foreach (uint thread in before.Except(after)) output.WriteLine($"  GUI thread {thread} is gone");
        return attempt < Attempts;
    }

    private static string Describe(Process process, uint thread)
    {
        IntPtr handle = OpenThread(0x0040, false, thread); // THREAD_QUERY_INFORMATION; nothing is changed.
        if (handle == IntPtr.Zero) return $"{thread}: cannot be opened ({Marshal.GetLastWin32Error()})";
        try
        {
            int status = NtQueryInformationThread(handle, 9, out IntPtr start, IntPtr.Size, IntPtr.Zero);
            if (status < 0) return $"{thread}: start address unavailable (0x{status:X8})";
            foreach (ProcessModule module in process.Modules)
                using (module)
                {
                    long offset = start.ToInt64() - module.BaseAddress.ToInt64();
                    if (offset >= 0 && offset < module.ModuleMemorySize)
                        return $"{thread}: starts in {module.ModuleName}+0x{offset:X}";
                }
            return $"{thread}: starts outside every loaded module";
        }
        finally { _ = CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int informationClass, out IntPtr information,
        int length, IntPtr returnLength);
}
