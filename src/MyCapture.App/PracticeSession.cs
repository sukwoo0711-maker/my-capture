namespace MyCapture.App;

/// <summary>No UI, clipboard reads, timers, or simulated success. Only correlated result events complete steps.</summary>
internal sealed class PracticeSession
{
    internal static readonly PracticeStep[] Steps = Enum.GetValues<PracticeStep>()
        .Where(step => step is not (PracticeStep.Groups or PracticeStep.Results)).ToArray();
    private readonly Dictionary<PracticeStep, PracticeStepResult> _results = Steps.ToDictionary(step => step, _ => PracticeStepResult.Pending);
    internal Guid Id { get; } = Guid.NewGuid();
    internal PracticeStep Step { get; private set; }
    internal Guid CaptureRequestId { get; private set; }
    internal Guid? CaptureId { get; private set; }
    internal Guid? EditorId { get; private set; }
    internal Guid? LogicalPinId { get; private set; }
    internal Guid? WindowPinId { get; private set; }
    internal uint ClipboardSequence { get; private set; }
    internal bool PinRestorable { get; private set; }
    internal bool PinRestorePending { get; private set; }
    private Guid? _closedWindowPin;
    internal bool Ended { get; private set; }
    internal bool CapturePending { get; private set; }
    internal PracticeStep? LastCompleted { get; private set; }
    internal string? NoticeKey { get; private set; }
    internal IReadOnlyDictionary<PracticeStep, PracticeStepResult> Results => _results;
    internal event Action? Changed;

    internal PracticeSession(bool pinOnly = false) => Step = pinOnly ? PracticeStep.B0 : PracticeStep.A1;
    internal Guid BeginCapture()
    {
        if (Ended || CapturePending) return Guid.Empty;
        CaptureRequestId = Guid.NewGuid();
        CaptureId = null; EditorId = null;
        CapturePending = true;
        // The drag instruction and sample are rendered before the desktop freezes.
        Step = PracticeStep.A2;
        LastCompleted = null; NoticeKey = null;
        Changed?.Invoke();
        return CaptureRequestId;
    }

    internal void StartGroup(PracticeStep first)
    {
        if (Ended || first is not (PracticeStep.A1 or PracticeStep.B0 or PracticeStep.C1)) return;
        Navigate(first);
    }
    internal void Back()
    {
        if (Ended) return;
        int index = Array.IndexOf(Steps, Step);
        Navigate(index <= 0 ? PracticeStep.Groups : Steps[index - 1]);
    }
    internal void RepeatPinClose()
    {
        if (!Ended && Step == PracticeStep.B6 && WindowPinId is not null) Navigate(PracticeStep.B5);
    }
    internal void Skip()
    {
        if (Ended || !_results.ContainsKey(Step)) return;
        if (_results[Step] != PracticeStepResult.Completed) _results[Step] = PracticeStepResult.Skipped;
        Navigate(Next(Step));
    }
    internal void End() { Ended = true; CapturePending = false; Changed?.Invoke(); }
    internal void ClearFeedback() { LastCompleted = null; NoticeKey = null; Changed?.Invoke(); }
    internal void Notice(string key) { LastCompleted = null; NoticeKey = key; Changed?.Invoke(); }
    internal void CannotRestorePin()
    {
        PinRestorable = false; PinRestorePending = false;
        Notice("Journey.PinUnavailable");
    }
    internal void ForgetCapture()
    {
        CaptureId = null; EditorId = null;
        Notice("Journey.NoCapture");
    }
    private void Navigate(PracticeStep step)
    {
        CapturePending = false; CaptureRequestId = Guid.Empty;
        Step = step; LastCompleted = null; NoticeKey = null;
        Changed?.Invoke();
    }
    internal static PracticeStep Next(PracticeStep step) => step == PracticeStep.C2 ? PracticeStep.Results : step + 1;
    private void Complete(PracticeStep step, bool advance = true)
    {
        _results[step] = PracticeStepResult.Completed;
        LastCompleted = step; NoticeKey = null;
        if (advance) Step = Next(step);
    }
    internal bool Accept(PracticeEvent signal)
    {
        if (Ended || signal.SessionId != Id) return false;
        bool editor = CaptureId is { } capture && signal.RecordId == capture
            && EditorId is { } editing && signal.EditorId == editing;
        bool pin = LogicalPinId is { } logical && signal.LogicalPinId == logical
            && WindowPinId is { } window && signal.WindowPinId == window;
        bool changed = true;
        switch (signal.Kind)
        {
            case PracticeEventKind.SelectorReady when CapturePending && signal.RequestId == CaptureRequestId && Step == PracticeStep.A2:
                Complete(PracticeStep.A1, advance: false); break;
            case PracticeEventKind.CaptureSaved when CapturePending && signal.RequestId == CaptureRequestId && Step == PracticeStep.A2 && signal.RecordId != Guid.Empty:
                CaptureId = signal.RecordId; CapturePending = false; Complete(PracticeStep.A2); break;
            case PracticeEventKind.CaptureCancelled when CapturePending && signal.RequestId == CaptureRequestId:
                CapturePending = false; NoticeKey = "Journey.CaptureCancelled"; LastCompleted = null; break;
            case PracticeEventKind.EditorReady when CaptureId == signal.RecordId && signal.RecordId != Guid.Empty && signal.EditorId != Guid.Empty
                && (EditorId is null || EditorId == signal.EditorId) && Step is >= PracticeStep.A3 and <= PracticeStep.A6:
                EditorId = signal.EditorId;
                if (Step == PracticeStep.A3) Complete(Step);
                break;
            case PracticeEventKind.EditorClosed when editor:
                EditorId = null; NoticeKey = "Journey.EditorClosed"; LastCompleted = null; break;
            case PracticeEventKind.ArrowSelected when editor && Step == PracticeStep.A4: Complete(Step); break;
            case PracticeEventKind.ArrowCommitted when editor && Step == PracticeStep.A5: Complete(Step); break;
            case PracticeEventKind.EditorCopiedAndClosed when editor:
                EditorId = null;
                if (Step == PracticeStep.A6) Complete(Step);
                else { NoticeKey = "Journey.EditorClosed"; LastCompleted = null; }
                break;
            case PracticeEventKind.PictureCopied when Step is PracticeStep.B0 or PracticeStep.B1 && signal.ClipboardSequence != 0:
                ClipboardSequence = signal.ClipboardSequence;
                LogicalPinId = null; WindowPinId = null; PinRestorable = false; PinRestorePending = false; _closedWindowPin = null;
                Complete(PracticeStep.B0); break;
            case PracticeEventKind.ClipboardChanged when ClipboardSequence != 0 && signal.ClipboardSequence != ClipboardSequence:
                ClipboardSequence = 0;
                if (Step == PracticeStep.B1) { LastCompleted = null; NoticeKey = "Journey.CopyAgain"; }
                break;
            case PracticeEventKind.PinOpened when Step == PracticeStep.B1 && ClipboardSequence != 0 && signal.ClipboardSequence == ClipboardSequence
                && signal.LogicalPinId != Guid.Empty && signal.WindowPinId != Guid.Empty:
                LogicalPinId = signal.LogicalPinId; WindowPinId = signal.WindowPinId; PinRestorable = false; Complete(Step); break;
            case PracticeEventKind.PinResized when pin && Step == PracticeStep.B2: Complete(Step); break;
            case PracticeEventKind.OpacityMenuOpened when pin && Step == PracticeStep.B3: Complete(Step); break;
            case PracticeEventKind.OpacityMenuClosed when pin && Step == PracticeStep.B4:
                Navigate(PracticeStep.B3); break;
            case PracticeEventKind.OpacityChanged when pin && Step == PracticeStep.B4 && Math.Abs(signal.Value - .6) < .0001: Complete(Step); break;
            case PracticeEventKind.PinClosing when pin && Step is >= PracticeStep.B2 and <= PracticeStep.B6:
                _closedWindowPin = WindowPinId; WindowPinId = null; PinRestorable = false; PinRestorePending = true;
                LastCompleted = null; NoticeKey = "Journey.PinClosed";
                break;
            case PracticeEventKind.PinClosed when pin || (WindowPinId is null && _closedWindowPin == signal.WindowPinId && LogicalPinId == signal.LogicalPinId):
                _closedWindowPin = signal.WindowPinId; PinRestorePending = false;
                WindowPinId = null; PinRestorable = signal.Restorable;
                if (Step == PracticeStep.B5 && signal.Restorable) Complete(Step);
                else { LastCompleted = null; NoticeKey = "Journey.PinClosed"; }
                break;
            case PracticeEventKind.PinRestored when LogicalPinId == signal.LogicalPinId && signal.LogicalPinId != Guid.Empty && WindowPinId is null && _closedWindowPin is not null && signal.WindowPinId != Guid.Empty:
                WindowPinId = signal.WindowPinId; PinRestorable = false; PinRestorePending = false; _closedWindowPin = null;
                if (Step == PracticeStep.B6) Complete(Step);
                else { LastCompleted = null; NoticeKey = null; }
                break;
            case PracticeEventKind.LibraryReady when Step == PracticeStep.C1: Complete(Step); break;
            case PracticeEventKind.LibraryEditorOpened when Step == PracticeStep.C2 && CaptureId == signal.RecordId && signal.RecordId != Guid.Empty: Complete(Step); break;
            default: changed = false; break;
        }
        if (changed) Changed?.Invoke();
        return changed;
    }
}
