using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;

namespace MyCapture.App;

public partial class App
{
    private EditorPrewarmScheduler? _editorPrewarm;
    private DispatcherTimer? _editorPrewarmRetry;
    private bool _imageEditorOpened;

    private void StartEditorPrewarm()
    {
        _editorPrewarm ??= new EditorPrewarmScheduler(
            action => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, action),
            () => _overlay?.IsActive == true || _recorder?.IsActive == true || _recorder?.HasRecordingSession == true
                || _preparingAdvancedCapture || _activeCountdown is not null || _scrollCancellation is not null
                || _textCaptureCancellation is not null || !_capturePersistenceTask.IsCompleted
                || _preparingExit || _storeInstallationPending,
            () => _imageEditorOpened || _shutdownRequested || Dispatcher.HasShutdownStarted
                || s_residentProbe?.Has("no-editor-prewarm") == true,
            RetryEditorPrewarmLater,
            () =>
            {
                try { AnnotationEditorPrewarm.Run(); }
                catch (Exception ex) { _log?.LogDebug(ex, "Image editor prewarm failed"); }
            });
        // Independent of the selector callback: its early return must not lose this work.
        // A separate idle turn still lets the selector queue its reader prewarm first.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(RequestEditorPrewarm));
    }

    private void RequestEditorPrewarm()
    {
        _editorPrewarmRetry?.Stop();
        if (_shutdownRequested || Dispatcher.HasShutdownStarted) return;
        _editorPrewarm?.Request();
    }

    private void RetryEditorPrewarmLater()
    {
        if (_editorPrewarmRetry is null)
        {
            _editorPrewarmRetry = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _editorPrewarmRetry.Tick += (_, _) => RequestEditorPrewarm();
        }
        _editorPrewarmRetry.Start();
    }

    private void ObserveEditorPrewarmWindow(Window window)
    {
        if (window is not AnnotationEditorWindow) return;
        _imageEditorOpened = true;
        _editorPrewarmRetry?.Stop();
        _editorPrewarm?.EditorOpened();
    }
}
