using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Ocr;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

/// <summary>
/// Shell wiring for the capture overlay's window auto-detect, colour copy and text capture.
/// </summary>
/// <remarks>
/// Kept in its own partial so the resident shell's main file only carries the hooks.
/// Text capture never writes the selection to the library: the frozen pixels are recognised
/// locally, the text replaces the clipboard, and the image lives only as long as the
/// notification's "open advanced extraction" action can still use it.
/// </remarks>
public partial class App
{
    private TextCaptureService? _textCapture;
    private CancellationTokenSource? _textCaptureCancellation;

    private void InitializeTextCapture()
    {
        if (_services is null || _overlay is null || _ocrService is null)
        {
            throw new InvalidOperationException("Capture services are unavailable.");
        }

        _textCapture = new TextCaptureService(
            _ocrService,
            () => _settings?.Ocr ?? new MyCapture.Core.Settings.OcrSettings(),
            ClipboardImageService.CopyTextAsync,
            _services.GetRequiredService<ILogger<TextCaptureService>>());
        _overlay.AutoDetectWindows = () => _settings?.Capture.AutoDetectWindows == true;
        _overlay.ShowShortcutHints = () => _settings?.General.ShowShortcutHints != false;
        _overlay.CopyColorText = ClipboardImageService.CopyTextAsync;
        _overlay.ReadColorFormat = () => _settings?.Capture.ColorFormat ?? MyCapture.Core.Settings.ColorFormat.Hex;
        _overlay.TextCaptureRequested = OnTextCaptureSelected;
        if (_tray is not null) _tray.CaptureTextRequested += (_, _) => HandleCaptureTextRequested();
    }

    /// <summary>
    /// Opens the region selector directly in text capture mode. Uses the same guards as region
    /// capture, so it never interrupts a recording, countdown or scrolling capture.
    /// </summary>
    private void HandleCaptureTextRequested()
    {
        DismissQuickAccessForCapture();
        _log?.LogInformation("Text capture requested");
        if (_overlay is null || _settings is null)
        {
            return;
        }

        try
        {
            if (!TryPrepareCapture(_overlay.CanRetake,
                allowRetake => GuardStillCapture(UiText.Get("TextCapture_Title"), allowRetake),
                _overlay.CloseEditorForRetake)) return;
            _tray?.SetState(TrayIconState.Capturing);
            // The pointer would only add noise to recognition, so text capture never draws it.
            _overlay.Start(
                includeCursor: false,
                _settings.Capture.AbortOnFocusLoss,
                _settings.Capture.ShowMagnifier,
                _settings.Capture.ShowSelectionGrid,
                textCapture: true);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Could not open the text capture overlay");
            _tray?.SetState(TrayIconState.Error);
            _tray?.ShowBalloon(
                UiText.Get("Text_70EECBC7C211"),
                MyCapture.App.Themes.ThemedDialog.RecoveryMessage(ex),
                TrayBalloonKind.Error);
        }
    }

    /// <summary>Receives a text-mode selection while the selector is still closing.</summary>
    private void OnTextCaptureSelected(CaptureSelectionCompletedEventArgs e)
    {
        BitmapSource image = e.SelectedBitmap;
        string context = string.IsNullOrWhiteSpace(e.SourceTitle) ? UiText.Get("TextCapture_Title") : e.SourceTitle;
        // Start after the selector has closed and the session-end handler has restored the tray,
        // so the busy indicator below is not immediately reset.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _ = RunTextCaptureAsync(image, context)));
    }

    private async Task RunTextCaptureAsync(BitmapSource image, string context)
    {
        if (_textCapture is null || Dispatcher.HasShutdownStarted) return;

        // The newest text capture wins; an older recognition must not overwrite its clipboard.
        try { _textCaptureCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        var cancellation = new CancellationTokenSource();
        _textCaptureCancellation = cancellation;
        SetOcrBusy(true);

        TextCaptureOutcome outcome;
        try
        {
            outcome = await _textCapture.RecognizeAndCopyAsync(image, cancellation.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.LogError(ex, "Text capture failed unexpectedly");
            outcome = new TextCaptureOutcome(TextCaptureStatus.Failed, null, 0);
        }
        finally
        {
            if (ReferenceEquals(_textCaptureCancellation, cancellation))
            {
                _textCaptureCancellation = null;
                SetOcrBusy(false);
            }

            cancellation.Dispose();
        }

        if (outcome.Status == TextCaptureStatus.Cancelled || Dispatcher.HasShutdownStarted) return;
        ShowTextCaptureOutcome(outcome, image, context);
    }

    private void ShowTextCaptureOutcome(TextCaptureOutcome outcome, BitmapSource image, string context)
    {
        string title = UiText.Get("TextCapture_Title");
        (string heading, string message, TrayBalloonKind kind) = outcome.Status switch
        {
            TextCaptureStatus.Copied => (UiText.Get("TextCapture_Copied"),
                UiText.Format("TextCapture_CopiedDetail", outcome.CharacterCount), TrayBalloonKind.Information),
            TextCaptureStatus.NoText => (title, UiText.Get("TextCapture_NoText"), TrayBalloonKind.Information),
            TextCaptureStatus.ClipboardFailed => (title, UiText.Get("TextCapture_ClipboardFailed"), TrayBalloonKind.Warning),
            TextCaptureStatus.Unavailable => (title,
                string.IsNullOrWhiteSpace(outcome.Result?.Message) ? UiText.Get("TextCapture_Unavailable") : outcome.Result.Message,
                TrayBalloonKind.Warning),
            _ => (title, UiText.Get("TextCapture_Failed"), TrayBalloonKind.Error),
        };

        // The recognised result and the in-memory image open the advanced extraction window
        // without re-running OCR. Unavailable and failed engines have nothing to show there.
        if (outcome.Result is { } result && _ocrPresenter is not null && _shellPresenter is not null &&
            outcome.Status is TextCaptureStatus.Copied or TextCaptureStatus.NoText or TextCaptureStatus.ClipboardFailed)
        {
            _shellPresenter.ShowActionNotification(heading, message, UiText.Get("TextCapture_OpenAdvanced"),
                () => _ocrPresenter?.ShowCached(result, context,
                    () => OcrRequestFactory.FromBitmap(image, _settings?.Ocr ?? new MyCapture.Core.Settings.OcrSettings())));
            return;
        }

        _tray?.ShowBalloon(heading, message, kind, playSound: false);
    }
}
