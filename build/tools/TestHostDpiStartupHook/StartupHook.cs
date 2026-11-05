using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

// The runtime requires this exact global type and method name. No WPF or product
// assembly may be referenced here: DPI must be fixed before their initialization.
internal static class StartupHook
{
    private const string ModeVariable = "MYCAPTURE_TESTHOST_DPI_MODE";
    private const string ReceiptVariable = "MYCAPTURE_TESTHOST_DPI_INITIALIZED_PID";
    private static readonly nint PerMonitorV2 = -4;

    public static void Initialize()
    {
        if (Environment.GetEnvironmentVariable(ModeVariable) != "PerMonitorV2") return;

        Assembly? entry = Assembly.GetEntryAssembly();
        string? entryName = entry?.GetName().Name;
        string? executable = Path.GetFileName(Environment.ProcessPath);
        // Child processes inherit environment variables. In particular MyCapture,
        // MyCapture.Setup and the dotnet CLI must never get their DPI changed here.
        if (!string.Equals(entryName, "testhost", StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(Path.GetFileName(entry!.Location), "testhost.dll", StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(executable, "testhost.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(executable, "dotnet.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The opt-in DPI startup hook requires the standard .NET testhost entry point and process.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("The PMv2 testhost hook requires Windows 10 version 1809 or later.");

        try
        {
            nint before = GetDpiAwarenessContextForProcess(GetCurrentProcess());
            if (before == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect testhost process DPI awareness.");
            bool alreadyConfigured = AreDpiAwarenessContextsEqual(before, PerMonitorV2);
            if (!alreadyConfigured && !SetProcessDpiAwarenessContext(PerMonitorV2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set testhost PMv2 before Main. Its awareness may already be fixed.");

            nint after = GetDpiAwarenessContextForProcess(GetCurrentProcess());
            nint thread = GetThreadDpiAwarenessContext();
            if (after == 0 || thread == 0 || !AreDpiAwarenessContextsEqual(after, PerMonitorV2)
                || !AreDpiAwarenessContextsEqual(thread, PerMonitorV2))
                throw new InvalidOperationException($"PMv2 verification failed: process=0x{after:X}, thread=0x{thread:X}.");

            Environment.SetEnvironmentVariable(ReceiptVariable, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            Console.Error.WriteLine($"[MyCapture.TestHostDpi] VERIFIED pid={Environment.ProcessId}; entry={entryName}; " +
                $"process={executable}; before=0x{before:X}; after=0x{after:X}; thread=0x{thread:X}; alreadyConfigured={alreadyConfigured}.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[MyCapture.TestHostDpi] FAIL pid={Environment.ProcessId}: {error}");
            throw;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetDpiAwarenessContextForProcess(nint process);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
}
