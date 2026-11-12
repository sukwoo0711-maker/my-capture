using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Platform.Capture;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace MyCapture.Platform.Shell;

public interface IOpenWithHandler
{
    string Name { get; }
    ImageSource? Icon { get; }
    Task InvokeAsync(string exportedPath);
}

public interface IOpenWithHandlerSet : IDisposable
{
    IReadOnlyList<IOpenWithHandler> Handlers { get; }
}

public interface IOpenWithHandlerSource
{
    Task<IOpenWithHandlerSet> QueryAsync(string extension, CancellationToken cancellationToken);
    Task ChooseAsync(string exportedPath, IntPtr owner);
}

/// <summary>
/// Shell handlers belong to one menu opening. Their RCWs are created, invoked and released on
/// their own STA, which pumps messages until the menu releases them. A hung enumeration holds
/// the shared gate even after its caller's deadline, so opening more menus cannot pile up probes.
/// </summary>
public sealed class OpenWithService : IOpenWithHandlerSource
{
    public static readonly TimeSpan QueryBudget = TimeSpan.FromMilliseconds(700);
    private static readonly SemaphoreSlim QueryGate = new(1, 1);
    private static readonly SemaphoreSlim IconGate = new(1, 1);

    public async Task<IOpenWithHandlerSet> QueryAsync(string extension, CancellationToken cancellationToken)
    {
        if (extension is not (".png" or ".mp4") || cancellationToken.IsCancellationRequested || !QueryGate.Wait(0))
            return EmptySet.Instance;
        var session = new HandlerSet(extension);
        long started = Stopwatch.GetTimestamp();
        try
        {
            session.Start();
            await session.Ready.Task.WaitAsync(QueryBudget, cancellationToken).ConfigureAwait(false);
            // Leave time for the caller's deadline continuation to receive the text-only result.
            TimeSpan remaining = QueryBudget - Stopwatch.GetElapsedTime(started) - TimeSpan.FromMilliseconds(25);
            if (remaining > TimeSpan.Zero && session.Handlers.Count > 0)
            {
                // Icon providers can hang too. Use independent COM instances on another STA, so a
                // hung icon cannot trap the selected handler's Invoke on its apartment. Names are
                // already available; an icon deadline degrades those rows to text only.
                var probe = new BoundedNativeProbe(IconGate, remaining, ApartmentState.STA, "MyCapture Open with icons");
                var icons = new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
                await probe.RunAsync(token => { ReadIcons(extension, icons, token); return true; }, false).ConfigureAwait(false);
                session.SetIcons(icons.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
            }
            if (cancellationToken.IsCancellationRequested) { session.Dispose(); return EmptySet.Instance; }
            return session;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Dispose also covers a result arriving just after WaitAsync lost its race.
            session.Dispose();
            return EmptySet.Instance;
        }
    }

    public Task ChooseAsync(string exportedPath, IntPtr owner)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // EXEC only: no ALLOW_REGISTRATION, FORCE_REGISTRATION or REGISTER_EXT.
                // Windows offers a one-time launch, without enabling its default-app checkbox.
                var info = new OpenAsInfo { File = exportedPath, Flags = 0x4 };
                int hr = SHOpenWithDialog(owner, ref info);
                if (hr == unchecked((int)0x800704C7)) throw new OperationCanceledException();
                Marshal.ThrowExceptionForHR(hr);
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "MyCapture Open with dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class EmptySet : IOpenWithHandlerSet
    {
        internal static readonly EmptySet Instance = new();
        public IReadOnlyList<IOpenWithHandler> Handlers => [];
        public void Dispose() { }
    }

    private sealed class HandlerSet(string extension) : IOpenWithHandlerSet
    {
        private readonly List<IOpenWithHandler> _handlers = [];
        private readonly List<IAssocHandler> _native = [];
        private Dispatcher? _dispatcher;
        private int _disposed;
        internal TaskCompletionSource<IOpenWithHandlerSet> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<IOpenWithHandler> Handlers => _handlers;

        internal void Start()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "MyCapture Open with handlers" };
            thread.SetApartmentState(ApartmentState.STA);
            try { thread.Start(); }
            catch { QueryGate.Release(); throw; }
        }

        private void Run()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                try
                {
                    IEnumAssocHandlers? enumeration = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(SHAssocEnumHandlers(extension, 1 /* ASSOC_FILTER_RECOMMENDED */, out enumeration));
                        while (Volatile.Read(ref _disposed) == 0 && enumeration.Next(1, out IAssocHandler handler, out uint fetched) == 0 && fetched == 1)
                        {
                            _native.Add(handler); // Retain even if its display name or icon fails.
                            Marshal.ThrowExceptionForHR(handler.GetUIName(out string name));
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            Marshal.ThrowExceptionForHR(handler.GetName(out string identity));
                            _handlers.Add(new Handler(name, identity, handler, this));
                        }
                    }
                    finally { if (enumeration is not null) Marshal.FinalReleaseComObject(enumeration); }
                }
                finally { QueryGate.Release(); }
                if (Volatile.Read(ref _disposed) != 0) return;
                Ready.TrySetResult(this);
                Dispatcher.Run();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Ready.TrySetException(error); }
            finally
            {
                foreach (IAssocHandler handler in _native) Marshal.FinalReleaseComObject(handler);
                _native.Clear();
                if (!_dispatcher.HasShutdownStarted) _dispatcher.InvokeShutdown();
                // No COM object escapes this apartment. Only frozen images and display names do.
            }
        }

        internal async Task InvokeAsync(IAssocHandler handler, string path)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _dispatcher!.InvokeAsync(() => Invoke(handler, path));
        }

        internal void SetIcons(IReadOnlyDictionary<string, ImageSource> icons)
        {
            foreach (Handler handler in _handlers.Cast<Handler>())
                handler.Icon = icons.GetValueOrDefault(handler.Identity);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    private sealed class Handler(string name, string identity, IAssocHandler native, HandlerSet owner) : IOpenWithHandler
    {
        public string Name { get; } = name;
        internal string Identity { get; } = identity;
        public ImageSource? Icon { get; internal set; }
        public Task InvokeAsync(string exportedPath) => owner.InvokeAsync(native, exportedPath);
    }

    private static void ReadIcons(string extension, ConcurrentDictionary<string, ImageSource> icons, CancellationToken token)
    {
        IEnumAssocHandlers? enumeration = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHAssocEnumHandlers(extension, 1, out enumeration));
            while (!token.IsCancellationRequested && enumeration.Next(1, out IAssocHandler handler, out uint fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (handler.GetName(out string identity) >= 0 && ReadIcon(handler) is { } icon && !token.IsCancellationRequested)
                        icons[identity] = icon;
                }
                finally { Marshal.FinalReleaseComObject(handler); }
            }
        }
        finally { if (enumeration is not null) Marshal.FinalReleaseComObject(enumeration); }
    }

    private static ImageSource? ReadIcon(IAssocHandler handler)
    {
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        try
        {
            if (handler.GetIconLocation(out string location, out int index) < 0 || string.IsNullOrWhiteSpace(location)) return null;
            if (SHDefExtractIconW(location, index, 0, out large, out small, (16u << 16) | 16u) < 0 || small == IntPtr.Zero) return null;
            BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(small, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        finally
        {
            if (large != IntPtr.Zero) DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);
        }
    }

    private static void Invoke(IAssocHandler handler, string path)
    {
        IShellItem? item = null;
        IntPtr data = IntPtr.Zero;
        try
        {
            Guid itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            Guid binding = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4"); // BHID_DataObject
            Guid dataId = new("0000010e-0000-0000-c000-000000000046"); // IDataObject
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref binding, ref dataId, out data));
            Marshal.ThrowExceptionForHR(handler.Invoke(data));
        }
        finally
        {
            if (data != IntPtr.Zero) Marshal.Release(data);
            if (item is not null) Marshal.FinalReleaseComObject(item);
        }
    }

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count, out IAssocHandler handler, out uint fetched);
    }

    [ComImport, Guid("f04061ac-1659-4a3f-a954-775aa57fc083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAssocHandler
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int Invoke(IntPtr data);
        [PreserveSig] int CreateInvoker(IntPtr data, out IntPtr invoker);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid binding, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public uint Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHAssocEnumHandlers(string extension, uint filter, out IEnumAssocHandlers handlers);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIconW(string path, int index, uint flags, out IntPtr large, out IntPtr small, uint sizes);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
