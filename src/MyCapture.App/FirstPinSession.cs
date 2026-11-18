namespace MyCapture.App;

internal enum FirstPinState { Ready, Pinned, Skipped }

/// <summary>The first pin is complete only after the armed clipboard operation opens a real pin.</summary>
internal sealed class FirstPinSession(Guid sessionId)
{
    internal FirstPinState State { get; private set; }
    internal uint ExpectedClipboardSequence { get; private set; }
    internal Guid? LogicalPinId { get; private set; }
    internal Guid? WindowPinId { get; private set; }
    internal bool Ended { get; private set; }

    internal void BeginAttempt(uint clipboardSequence)
    {
        if (Ended || State != FirstPinState.Ready) return;
        ExpectedClipboardSequence = clipboardSequence;
    }

    internal bool Accept(PracticeEvent result)
    {
        if (Ended || result.SessionId != sessionId) return false;
        if (State == FirstPinState.Pinned
            && result.Kind is PracticeEventKind.PinClosing or PracticeEventKind.PinClosed
            && result.LogicalPinId == LogicalPinId && result.WindowPinId == WindowPinId)
        {
            LogicalPinId = null;
            WindowPinId = null;
            State = FirstPinState.Ready;
            return true;
        }
        if (State != FirstPinState.Ready
            || result.Kind != PracticeEventKind.PinOpened || ExpectedClipboardSequence == 0
            || result.ClipboardSequence != ExpectedClipboardSequence
            || result.LogicalPinId == Guid.Empty || result.WindowPinId == Guid.Empty) return false;
        LogicalPinId = result.LogicalPinId;
        WindowPinId = result.WindowPinId;
        ExpectedClipboardSequence = 0;
        State = FirstPinState.Pinned;
        return true;
    }

    internal void Skip()
    {
        if (Ended || State != FirstPinState.Ready) return;
        ExpectedClipboardSequence = 0;
        State = FirstPinState.Skipped;
    }

    internal void End()
    {
        Ended = true;
        ExpectedClipboardSequence = 0;
    }
}
