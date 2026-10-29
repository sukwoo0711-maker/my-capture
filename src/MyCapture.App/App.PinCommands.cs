using System.Windows;
using MyCapture.App.Capture;
using MyCapture.App.Editing;
using MyCapture.Platform.Shell;

namespace MyCapture.App;

public partial class App
{
    internal Func<nint> ReadPinForeground { get; set; } = ReceiptForeground.Get;

    private Action? ForegroundImageEditorPin()
    {
        nint foreground = ReadPinForeground();
        if (foreground == 0) return null;
        AnnotationEditorWindow? editor = PinCommandRouter.FindForegroundEditor(Windows.Cast<Window>(), foreground);
        return editor is null ? null : () =>
        {
            if (editor.IsEnabled && !editor.HasOpenDialog && !editor.IsCloseInProgress)
                editor.Editor.PinAndClose();
        };
    }

    private void HandlePinCommand() => PinCommandRouter.Execute(ForegroundImageEditorPin,
        () => _overlay?.IsSelecting == true || _recorder?.IsSelecting == true
            || Windows.OfType<CaptureOverlayWindow>().Any(window => window.IsVisible),
        () => _overlay?.ConfirmSelectionAsPin(), HandlePasteToScreen);

    private void NotifyPinFailed() => _shellPresenter?.ShowNotification(
        UiText.Get("QuickAccess.Pin"), UiText.Get("Onboard_PinFailed"), TrayBalloonKind.Error, answersCommand: true);
}
