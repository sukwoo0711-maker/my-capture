using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MyCapture.Core.Capture;

namespace MyCapture.Platform.Capture;

/// <summary>The application that owned a captured window.</summary>
/// <param name="Name">Friendly name, for example <c>Microsoft Edge</c>.</param>
/// <param name="Path">Full path of the executable.</param>
public sealed record SourceAppIdentity(string Name, string Path);

/// <summary>
/// Resolves a process to its executable and a friendly name. The name comes from the executable's
/// version resource (FileDescription) because that is what Windows itself shows in Task Manager;
/// a missing or blank description falls back to the file name without extension.
/// </summary>
public sealed partial class SourceAppResolver
{
    /// <summary>Window class of the frame that hosts a Store (UWP) app's content window.</summary>
    internal const string ApplicationFrameClass = "ApplicationFrameWindow";

    private const int MaxCachedNames = 256;
    private readonly Func<uint, string?> _imagePath;
    private readonly Func<string, string?> _fileDescription;
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public SourceAppResolver() : this(ReadImagePath, ReadFileDescription) { }

    internal SourceAppResolver(Func<uint, string?> imagePath, Func<string, string?> fileDescription)
    {
        _imagePath = imagePath ?? throw new ArgumentNullException(nameof(imagePath));
        _fileDescription = fileDescription ?? throw new ArgumentNullException(nameof(fileDescription));
    }

    /// <summary>Returns null when the process has exited, is protected, or reports no usable path.</summary>
    public SourceAppIdentity? Resolve(uint processId)
    {
        if (processId == 0) return null;
        string? image;
        try { image = _imagePath(processId); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        string path = CaptureSourceText.NormalizeLocalPath(image);
        if (path.Length == 0) return null;

        if (!_names.TryGetValue(path, out string? name))
        {
            string? description;
            try { description = _fileDescription(path); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { description = null; }
            name = FriendlyName(path, description);
            if (_names.Count >= MaxCachedNames) _names.Clear();
            _names[path] = name;
        }

        return name.Length == 0 ? null : new SourceAppIdentity(name, path);
    }

    internal static string FriendlyName(string path, string? description)
    {
        string name = CaptureSourceText.NormalizeAppName(description);
        return name.Length > 0
            ? name
            : CaptureSourceText.NormalizeAppName(System.IO.Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// A Store app's top-level window belongs to ApplicationFrameHost, which only draws the frame; the
    /// app's content is a child window owned by the app's own process. Naming the frame host would be
    /// wrong for every Store app, so return the first child process instead, or 0 when the content is
    /// not attached (a suspended app), which leaves the capture without an application.
    /// </summary>
    internal static uint ResolveContentProcess(string className, uint windowProcessId, Func<IEnumerable<uint>> childProcessIds)
    {
        if (!string.Equals(className, ApplicationFrameClass, StringComparison.OrdinalIgnoreCase)) return windowProcessId;
        foreach (uint child in childProcessIds())
        {
            if (child != 0 && child != windowProcessId) return child;
        }

        return 0;
    }

    internal static IEnumerable<uint> ReadChildProcessIds(IntPtr window)
    {
        var processIds = new List<uint>();
        if (window == IntPtr.Zero) return processIds;
        bool Callback(IntPtr child, IntPtr data)
        {
            _ = GetWindowThreadProcessId(child, out uint processId);
            if (processId != 0) processIds.Add(processId);
            return processIds.Count < 64;
        }

        _ = EnumChildWindows(window, Callback, IntPtr.Zero);
        return processIds;
    }

    private static string? ReadImagePath(uint processId)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            foreach (int capacity in new[] { 1024, CaptureSourceText.MaxPathLength + 1 })
            {
                char[] buffer = new char[capacity];
                int length = buffer.Length;
                if (QueryFullProcessImageName(process, 0, buffer, ref length))
                    return length > 0 ? new string(buffer, 0, length) : null;
                if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer) return null;
            }

            return null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private static string? ReadFileDescription(string path) => FileVersionInfo.GetVersionInfo(path).FileDescription;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, uint flags,
        [Out] char[] exeName, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
