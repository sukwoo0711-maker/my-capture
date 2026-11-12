using System.Windows.Threading;

namespace MyCapture.App.Capture;

/// <summary>Runs the first input turn before optional chrome work, and cancels both on close.</summary>
internal sealed class NativeSelectorStartup : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private DispatcherOperation? _inputOperation, _chromeOperation;
    private bool _closed;

    internal NativeSelectorStartup(Dispatcher dispatcher, Action inputReady, Action showChrome)
    {
        _dispatcher = dispatcher;
        _dispatcher.VerifyAccess();
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
