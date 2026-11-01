namespace MyCapture.App;

internal enum PracticeStep { Groups, A1, A2, A3, A4, A5, A6, B0, B1, B2, B3, B4, B5, B6, C1, C2, Results }
internal enum PracticeStepResult { Pending, Completed, Skipped }
internal enum PracticeEventKind
{
    SelectorReady, CaptureSaved, CaptureCancelled, EditorReady, ArrowSelected, ArrowCommitted,
    EditorCopiedAndClosed, EditorClosed, PictureCopied, ClipboardChanged, PinOpened,
    PinResized, OpacityMenuOpened, OpacityMenuClosed, OpacityChanged, PinClosing, PinClosed, PinRestored, LibraryReady, PictureOpened, LibraryEditorOpened,
}

/// <summary>Identity accompanies the result, including across asynchronous work and reopened windows.</summary>
internal sealed record PracticeEvent(PracticeEventKind Kind, Guid SessionId,
    Guid RequestId = default, Guid RecordId = default, Guid EditorId = default,
    Guid LogicalPinId = default, Guid WindowPinId = default, uint ClipboardSequence = 0,
    double Value = 0, bool Restorable = false);

internal sealed record PracticeEditorTarget(Guid SessionId, Guid RecordId, Guid EditorId);
internal sealed record PracticeCaptureTarget(Guid SessionId, Guid RequestId);
