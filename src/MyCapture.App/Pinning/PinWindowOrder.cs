using System.Runtime.InteropServices;

namespace MyCapture.App.Pinning;

/// <summary>Reads only the stacking order of the supplied pin handles.</summary>
internal static class PinWindowOrder
{
    // A changing desktop must not leave a synchronous exit snapshot traversing forever.
    private const int MaximumWindows = 8192;
    private const uint NextWindow = 2;

    internal static IReadOnlyList<IntPtr> BottomToTop(IReadOnlyList<IntPtr> handles) =>
        BottomToTop(handles, GetTopWindow(IntPtr.Zero), handle => GetWindow(handle, NextWindow));

    internal static IReadOnlyList<IntPtr> BottomToTop(
        IReadOnlyList<IntPtr> handles, IntPtr first, Func<IntPtr, IntPtr> next)
    {
        var remaining = handles.Where(handle => handle != IntPtr.Zero).ToHashSet();
        var visited = new HashSet<IntPtr>();
        var ordered = new List<IntPtr>(remaining.Count);
        for (IntPtr handle = first; handle != IntPtr.Zero && remaining.Count > 0
             && visited.Count < MaximumWindows && visited.Add(handle); handle = next(handle))
        {
            if (remaining.Remove(handle)) ordered.Add(handle);
        }
        ordered.Reverse();
        // A window can disappear during the native traversal. Keep its original relative
        // order as a fallback without reading any other window's title, image, or content.
        return handles.Where(remaining.Contains).Concat(ordered).ToArray();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);
}
