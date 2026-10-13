using System.Runtime.InteropServices;
using MyCapture.Core.Capture;

namespace MyCapture.Platform.Capture;

/// <summary>One open File Explorer view (a tab on Windows 11) as the shell reports it.</summary>
/// <param name="TopLevelWindow">The Explorer frame window the view belongs to.</param>
/// <param name="TabWindow">The view's own host window (<c>ShellTabWindowClass</c> on tabbed Explorer).</param>
/// <param name="FolderPath">File-system path of the view's folder; empty for virtual folders.</param>
internal readonly record struct ExplorerTabSnapshot(IntPtr TopLevelWindow, IntPtr TabWindow, string FolderPath);

/// <summary>
/// Reads the current folder of File Explorer windows through the shell's own automation objects
/// (ShellWindows → IShellBrowser → active view → folder). Never reads the address bar, which can hold
/// unsubmitted text. Virtual locations such as This PC or Recycle Bin have no file-system path and
/// produce nothing, as does any window whose active tab cannot be identified unambiguously.
/// </summary>
public sealed class ExplorerFolderService
{
    internal const string CabinetWindowClass = "CabinetWClass";
    internal const string ExploreWindowClass = "ExploreWClass";
    internal const string TabWindowClass = "ShellTabWindowClass";

    /// <summary>
    /// Deadline for one shell read. The shell objects live in explorer.exe, so every call is
    /// cross-process; a responsive Explorer answers in tens of milliseconds, a hung one never does.
    /// </summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromMilliseconds(700);

    private static readonly SemaphoreSlim SharedGate = new(1, 1);
    private static readonly IReadOnlyDictionary<IntPtr, string> Empty = new Dictionary<IntPtr, string>();
    private readonly BoundedNativeProbe _probe;
    private readonly Func<CancellationToken, IReadOnlyList<ExplorerTabSnapshot>> _readTabs;
    private readonly Func<IntPtr, IntPtr> _activeTab;

    public ExplorerFolderService() : this(waitForBusyProbe: false) { }

    /// <param name="waitForBusyProbe">
    /// Wait, within the same deadline, for a shell read that is still running (the previous
    /// capture's) instead of returning nothing at once. For readers nothing on screen waits for.
    /// </param>
    public ExplorerFolderService(bool waitForBusyProbe)
        : this(ReadShellTabs, FindActiveTab, new BoundedNativeProbe(SharedGate, DefaultBudget, ApartmentState.STA,
            "MyCapture Explorer folder", waitForBusyProbe)) { }

    internal ExplorerFolderService(Func<CancellationToken, IReadOnlyList<ExplorerTabSnapshot>> readTabs,
        Func<IntPtr, IntPtr> activeTab, BoundedNativeProbe probe)
    {
        _readTabs = readTabs ?? throw new ArgumentNullException(nameof(readTabs));
        _activeTab = activeTab ?? throw new ArgumentNullException(nameof(activeTab));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <summary>File Explorer frame windows: <c>CabinetWClass</c>, and the legacy <c>ExploreWClass</c>.</summary>
    public static bool IsExplorerWindowClass(string? className) =>
        string.Equals(className, CabinetWindowClass, StringComparison.OrdinalIgnoreCase)
        || string.Equals(className, ExploreWindowClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the folder of each Explorer window that has one, keyed by top-level handle.</summary>
    public Task<IReadOnlyDictionary<IntPtr, string>> ReadAsync(IReadOnlyList<IntPtr> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        IntPtr[] targets = windows.Where(window => window != IntPtr.Zero).Distinct().ToArray();
        if (targets.Length == 0) return Task.FromResult(Empty);
        return _probe.RunAsync(cancellation =>
        {
            IReadOnlyList<ExplorerTabSnapshot> tabs = _readTabs(cancellation);
            var result = new Dictionary<IntPtr, string>();
            foreach (IntPtr window in targets)
            {
                cancellation.ThrowIfCancellationRequested();
                string path = ResolveFolder(window, _activeTab(window), tabs);
                if (path.Length > 0) result[window] = path;
            }

            return (IReadOnlyDictionary<IntPtr, string>)result;
        }, Empty);
    }

    /// <summary>
    /// Picks the folder shown by <paramref name="window"/>. Tabbed Explorer keeps every tab of a window
    /// under the same frame, so the view whose host is the visible tab wins; without a visible tab only
    /// a window with exactly one view qualifies. Anything ambiguous yields no path rather than a guess.
    /// </summary>
    internal static string ResolveFolder(IntPtr window, IntPtr activeTab, IReadOnlyList<ExplorerTabSnapshot> tabs)
    {
        if (window == IntPtr.Zero) return string.Empty;
        ExplorerTabSnapshot[] views = tabs.Where(tab => tab.TopLevelWindow == window).ToArray();
        ExplorerTabSnapshot[] chosen = activeTab == IntPtr.Zero
            ? views
            : views.Where(tab => tab.TabWindow == activeTab).ToArray();
        // A shell that reports the frame itself as the view's window has no per-tab hosts to compare.
        if (chosen.Length == 0 && views.Length == 1 && views[0].TabWindow == window) chosen = views;
        return chosen.Length == 1 ? CaptureSourceText.NormalizeLocalPath(chosen[0].FolderPath) : string.Empty;
    }

    /// <summary>The visible tab host of a tabbed Explorer window; zero for an untabbed window.</summary>
    internal static IntPtr FindActiveTab(IntPtr window)
    {
        IntPtr first = IntPtr.Zero;
        IntPtr child = IntPtr.Zero;
        for (int guard = 0; guard < 256; guard++)
        {
            child = FindWindowEx(window, child, TabWindowClass, null);
            if (child == IntPtr.Zero) break;
            if (first == IntPtr.Zero) first = child;
            if (IsWindowVisible(child)) return child;
        }

        return first;
    }

    private static IReadOnlyList<ExplorerTabSnapshot> ReadShellTabs(CancellationToken cancellation)
    {
        var tabs = new List<ExplorerTabSnapshot>();
        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            Type type = Type.GetTypeFromCLSID(ClsidShellWindows, throwOnError: true)!;
            var windows = Own(owned, (IShellWindows)Activator.CreateInstance(type)!);
            int count = Math.Min(windows.Count, 256);
            for (int index = 0; index < count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    object? item = windows.Item(index);
                    if (item is null) continue;
                    Own(owned, item);
                    if (TryReadView(item, owned, out ExplorerTabSnapshot tab)) tabs.Add(tab);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidComObjectException)
                {
                    // A view closing during the walk contributes nothing.
                }
            }
        }
        finally
        {
            foreach (object item in owned)
            {
                try
                {
                    if (Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item);
                }
                catch (Exception ex) when (ex is InvalidComObjectException or ArgumentException)
                {
                    // Already released through another reference to the same object.
                }
            }
        }

        return tabs;
    }

    private static bool TryReadView(object item, HashSet<object> owned, out ExplorerTabSnapshot tab)
    {
        tab = default;
        if (item is not IServiceProvider provider) return false;
        Guid service = SidTopLevelBrowser, iid = IidShellBrowser;
        if (provider.QueryService(ref service, ref iid, out IntPtr browserPointer) < 0 || browserPointer == IntPtr.Zero)
            return false;
        var browser = (IShellBrowser)Own(owned, TakeObject(browserPointer));
        if (browser.GetWindow(out IntPtr tabWindow) < 0 || tabWindow == IntPtr.Zero) return false;
        IntPtr topLevel = GetAncestor(tabWindow, GaRoot);
        if (topLevel == IntPtr.Zero) topLevel = tabWindow;

        string path = string.Empty;
        if (browser.QueryActiveShellView(out IntPtr viewPointer) >= 0 && viewPointer != IntPtr.Zero)
        {
            object view = Own(owned, TakeObject(viewPointer));
            if (view is IFolderView folderView)
            {
                Guid persist = IidPersistFolder2;
                if (folderView.GetFolder(ref persist, out IntPtr folderPointer) >= 0 && folderPointer != IntPtr.Zero)
                {
                    var folder = (IPersistFolder2)Own(owned, TakeObject(folderPointer));
                    if (folder.GetCurFolder(out IntPtr pidl) >= 0 && pidl != IntPtr.Zero)
                    {
                        try { path = ReadFileSystemPath(pidl); }
                        finally { Marshal.FreeCoTaskMem(pidl); }
                    }
                }
            }
        }

        tab = new ExplorerTabSnapshot(topLevel, tabWindow, path);
        return true;
    }

    private static string ReadFileSystemPath(IntPtr pidl)
    {
        if (SHGetNameFromIDList(pidl, SigdnFileSystemPath, out IntPtr name) < 0 || name == IntPtr.Zero) return string.Empty;
        try { return Marshal.PtrToStringUni(name) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(name); }
    }

    private static object TakeObject(IntPtr pointer)
    {
        try { return Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static T Own<T>(HashSet<object> owned, T value) where T : class
    {
        owned.Add(value);
        return value;
    }

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFolder2 = new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");
    private const uint SigdnFileSystemPath = 0x80058000;
    private const uint GaRoot = 2;

    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr name);

    // Only the vtable prefix through the last called member is declared; earlier unused members keep
    // their slots and are never invoked.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? Item([In, MarshalAs(UnmanagedType.Struct)] object index);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();
        [PreserveSig]
        int QueryActiveShellView(out IntPtr shellView);
    }

    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        [PreserveSig]
        int GetFolder(ref Guid riid, out IntPtr folder);
    }

    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFolder2
    {
        void GetClassID();
        void Initialize();
        [PreserveSig]
        int GetCurFolder(out IntPtr pidl);
    }
}
