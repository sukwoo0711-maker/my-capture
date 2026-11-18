using System.Windows.Threading;

namespace MyCapture.App.Capture;

/// <summary>Runs the first input turn before optional chrome work, and cancels both on close.</summary>
internal sealed class NativeSelectorStartup : IDisposable
{
    // QS_KEY | QS_MOUSEMOVE | QS_MOUSEBUTTON | QS_RAWINPUT | QS_TOUCH | QS_POINTER (QS_INPUT), and QS_PAINT.
    private const uint QueuedInputOrPaint = 0x1C07 | 0x0020;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _hasQueuedInputOrPaint;
    private DispatcherOperation? _inputOperation, _chromeOperation;
    private bool _closed;

    internal NativeSelectorStartup(Dispatcher dispatcher, Action inputReady, Action showChrome, Func<bool>? hasQueuedInputOrPaint = null)
    {
        _dispatcher = dispatcher;
        _dispatcher.VerifyAccess();
        _hasQueuedInputOrPaint = hasQueuedInputOrPaint ?? HasQueuedInputOrPaint;
        _inputOperation = dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _inputOperation = null;
            if (_closed) return;
            inputReady();
            if (_closed) return;
            _chromeOperation = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _chromeOperation = null;
                if (!_closed) showChrome();
            }));
        }));
    }

    /// <summary>
    /// Called after the selector's window procedure handled a message while startup work is queued.
    /// WPF holds Input- and Background-priority work while Win32 input is queued, and then waits for
    /// its background timer (at least USER_TIMER_MINIMUM, 10 ms) even when that input was handled at
    /// once. A selector that just appeared under the pointer receives such input, so without this the
    /// first input turn and the mode bar waited a timer tick or two. Once neither input nor paint is
    /// queued, the next startup step is raised to Loaded, the lowest priority WPF runs without
    /// checking for input, so it runs on the next dispatcher turn after any queued layout and render
    /// work. The order is unchanged: queued input and the hover paint are handled first, and chrome
    /// still follows the input turn.
    /// </summary>
    internal void QueueDrained()
    {
        if (_closed) return;
        DispatcherOperation? next = _inputOperation ?? _chromeOperation;
        if (next is not { Status: DispatcherOperationStatus.Pending } || next.Priority >= DispatcherPriority.Loaded) return;
        if (_hasQueuedInputOrPaint()) return;
        next.Priority = DispatcherPriority.Loaded;
    }

    /// <summary>Test hook: the priority of the startup step still queued, if any.</summary>
    internal DispatcherPriority? PendingPriorityForTest => (_inputOperation ?? _chromeOperation)?.Priority;

    private static bool HasQueuedInputOrPaint() =>
        ((NativeSelectorInterop.GetQueueStatus(QueuedInputOrPaint) >> 16) & QueuedInputOrPaint) != 0;

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_closed) return;
        _closed = true;
        _inputOperation?.Abort();
        _inputOperation = null;
        _chromeOperation?.Abort();
        _chromeOperation = null;
    }
}
