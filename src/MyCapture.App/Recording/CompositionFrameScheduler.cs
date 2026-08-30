using System.Windows.Media;
using System.Windows.Threading;

namespace MyCapture.App.Recording;

/// <summary>
/// Coalesces any number of UI-thread invalidations into at most one callback on the next
/// WPF composition frame. The static <see cref="CompositionTarget.Rendering"/> event is
/// subscribed only while work is pending, so a closed editor cannot be retained by it.
/// </summary>
internal sealed class CompositionFrameScheduler : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _render;
    private bool _isPending;
    private bool _disposed;

    internal CompositionFrameScheduler(Dispatcher dispatcher, Action render)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _render = render ?? throw new ArgumentNullException(nameof(render));
    }

    internal long RequestCount { get; private set; }

    internal long CoalescedRequestCount { get; private set; }

    internal long RenderFrameCount { get; private set; }

    internal bool IsPending => _isPending;

    internal void Request()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(Request, DispatcherPriority.Render);
            return;
        }

        RequestCount++;
        if (_isPending)
        {
            CoalescedRequestCount++;
            return;
        }

        _isPending = true;
        CompositionTarget.Rendering += OnRendering;
    }

    internal void CancelPending()
    {
        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(CancelPending, DispatcherPriority.Send);
            return;
        }

        if (!_isPending)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _isPending = false;
    }

    /// <summary>Deterministic hook for STA tests; production rendering still uses composition.</summary>
    internal void FlushForTest()
    {
        _dispatcher.VerifyAccess();
        if (!_isPending || _disposed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _isPending = false;
        RenderFrameCount++;
        _render();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _ = _dispatcher.BeginInvoke(Dispose, DispatcherPriority.Send);
            return;
        }

        CancelPending();
        _disposed = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_isPending || _disposed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _isPending = false;
        RenderFrameCount++;
        _render();
    }
}
