using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MyCapture.App.Diagnostics;

namespace MyCapture.App.Capture;

/// <summary>At most one detached, bounded set of windows per UI dispatcher. Never retains a frame or owner.</summary>
internal sealed class NativeSelectorChromeCache
{
    [ThreadStatic] private static NativeSelectorChromeCache? _current;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _expiry;
    private NativeSelectorChrome? _hidden;
    private bool _keptOnIdle;
    internal static TimeSpan Retention => TimeSpan.FromMinutes(2);
    internal static int RetainedWindowCount => _current?._hidden?.Windows.Count ?? 0;
    // A surface-size guard, not a measurement of WPF/native private memory or GPU allocations.
    // Sized for the four session windows plus the deferred key panel and start guide at 250% scale.
    internal const long MaxRetainedSurfaceBytes = 16 * 1024 * 1024;
    internal const int MaxRetainedWindows = 8;

    private NativeSelectorChromeCache()
    {
        _expiry = new DispatcherTimer(DispatcherPriority.ContextIdle, _dispatcher) { Interval = Retention };
        _expiry.Tick += (_, _) => Clear();
        _dispatcher.ShutdownStarted += (_, _) => { Clear(); _current = null; };
    }

    internal static NativeSelectorChrome Acquire(NativeCaptureOverlay owner, bool magnifier, bool requireExclusion)
    {
        var cache = _current ??= new NativeSelectorChromeCache();
        cache._dispatcher.VerifyAccess();
        cache._expiry.Stop();
        NativeSelectorChrome? chrome = cache._hidden;
        bool keptOnIdle = cache._keptOnIdle;
        cache._hidden = null;
        cache._keptOnIdle = false;
        if (chrome is not null && chrome.CanReuse(owner, magnifier))
        {
            try
            {
                chrome.Bind(owner, requireExclusion);
                SelectorTimeline.Mark?.Invoke("native-chrome-cache-hit");
                if (keptOnIdle) SelectorTimeline.Mark?.Invoke("native-chrome-cache-hit-after-idle");
                return chrome;
            }
            catch { chrome.Dispose(); throw; }
        }
        chrome?.Dispose();
        SelectorTimeline.Mark?.Invoke("native-chrome-cache-miss");
        return new NativeSelectorChrome(owner, magnifier, requireExclusion);
    }

    internal static void Release(NativeSelectorChrome chrome)
    {
        var cache = _current ??= new NativeSelectorChromeCache();
        cache._dispatcher.VerifyAccess();
        // An incompletely rendered session may have queued frame notifications; never reuse it.
        if (cache._dispatcher.HasShutdownStarted || !CanRetainWithinBudget(chrome)) { chrome.Dispose(); return; }
        chrome.Detach();
        cache.Clear();
        cache._hidden = chrome;
        cache._expiry.Start();
        SelectorTimeline.Mark?.Invoke("native-chrome-cached");
    }

    internal static void TrimForIdle()
    {
        if (_current is not { } cache) return;
        cache._dispatcher.VerifyAccess();
        if (cache._hidden is not { } chrome) return;
        if (cache._dispatcher.HasShutdownStarted || !chrome.IsDetached
            || chrome.Windows.Any(window => window.IsVisible) || !CanRetainWithinBudget(chrome))
        {
            cache.Clear();
            return;
        }
        // Idle collection releases the completed capture's memory while this small cache keeps
        // its original expiry. Repeated idle notifications must not extend the two-minute lease.
        if (!cache._keptOnIdle) SelectorTimeline.Mark?.Invoke("native-chrome-cache-kept-on-idle");
        cache._keptOnIdle = true;
    }

    private static bool CanRetainWithinBudget(NativeSelectorChrome chrome)
    {
        if (!chrome.CanRetain || chrome.Windows.Count < 1 || chrome.Windows.Count > MaxRetainedWindows) return false;
        long bytes = 0;
        foreach (var window in chrome.Windows)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !NativeSelectorInterop.IsWindow(handle)) return false;
            var dpi = VisualTreeHelper.GetDpi(window);
            if (!TryAddSurfaceBytes(Math.Max(window.Width, window.ActualWidth), Math.Max(window.Height, window.ActualHeight),
                dpi.DpiScaleX, dpi.DpiScaleY, ref bytes)) return false;
        }
        return true;
    }

    internal static bool TryAddSurfaceBytes(double width, double height, double scaleX, double scaleY, ref long bytes)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(scaleX) || !double.IsFinite(scaleY)
            || width <= 0 || height <= 0 || scaleX <= 0 || scaleY <= 0 || bytes < 0 || bytes > MaxRetainedSurfaceBytes)
            return false;
        double physicalWidth = Math.Ceiling(width * scaleX), physicalHeight = Math.Ceiling(height * scaleY);
        long remainingPixels = (MaxRetainedSurfaceBytes - bytes) / 4;
        if (!double.IsFinite(physicalWidth) || !double.IsFinite(physicalHeight) || physicalWidth < 1 || physicalHeight < 1
            || physicalWidth > remainingPixels || physicalHeight > remainingPixels / physicalWidth) return false;
        bytes += (long)(physicalWidth * physicalHeight) * 4;
        return true;
    }

    internal static void Trim() => _current?.Clear();

    private void Clear()
    {
        _dispatcher.VerifyAccess();
        _expiry.Stop();
        NativeSelectorChrome? chrome = _hidden;
        _hidden = null;
        _keptOnIdle = false;
        chrome?.Dispose();
        if (chrome is not null) SelectorTimeline.Mark?.Invoke("native-chrome-cache-released");
    }
}
