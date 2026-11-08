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
    internal static TimeSpan Retention => TimeSpan.FromMinutes(2);
    internal static int RetainedWindowCount => _current?._hidden?.Windows.Count ?? 0;

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
        cache._hidden = null;
        if (chrome is not null && chrome.CanReuse(owner, magnifier))
        {
            try
            {
                chrome.Bind(owner, requireExclusion);
                SelectorTimeline.Mark?.Invoke("native-chrome-cache-hit");
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
        if (!chrome.CanRetain || cache._dispatcher.HasShutdownStarted) { chrome.Dispose(); return; }
        chrome.Detach();
        cache.Clear();
        cache._hidden = chrome;
        cache._expiry.Start();
        SelectorTimeline.Mark?.Invoke("native-chrome-cached");
    }

    internal static void Trim() => _current?.Clear();

    private void Clear()
    {
        _dispatcher.VerifyAccess();
        _expiry.Stop();
        NativeSelectorChrome? chrome = _hidden;
        _hidden = null;
        chrome?.Dispose();
        if (chrome is not null) SelectorTimeline.Mark?.Invoke("native-chrome-cache-released");
    }
}
