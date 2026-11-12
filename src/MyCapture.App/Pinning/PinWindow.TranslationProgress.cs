using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MyCapture.App.Themes;

namespace MyCapture.App.Pinning;

internal sealed partial class PinWindow
{
    /// <summary>The least time between two progress lines on a pin, so a count stays readable.</summary>
    internal static readonly TimeSpan TranslationProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly PinProgressStrip _translationStrip = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly PinTranslationTimeEstimate _translationEstimate = new();
    // The message of the running translation step (installing the model, translating and its count); null when none runs.
    private string? _translationProgressLine;
    // The bottom message shows that line, so the bar belongs under it.
    private bool _feedbackShowsTranslation;
    private (int Completed, int Total)? _translationCount;
    private (int Completed, int Total, long ReportedAt)? _queuedTranslationCount;
    private long _translationCountShownAt;
    private DispatcherTimer? _translationCountTimer;

    /// <summary>
    /// Shows how many sentences of the running translation are done, at most once per
    /// <see cref="TranslationProgressInterval"/>; a newer count waiting for its turn replaces an older one.
    /// <paramref name="reportedAt"/> is the <see cref="Stopwatch"/> time at which the translator reported it.
    /// </summary>
    internal void ShowTranslationProgress(int completed, int total, long reportedAt)
    {
        if (_isClosed || TranslationState != PinTranslationState.Translating || _translationProgressLine is null || total <= 0) return;
        _queuedTranslationCount = (Math.Clamp(completed, 0, total), total, reportedAt);
        TimeSpan wait = _translationCountShownAt == 0 ? TimeSpan.Zero
            : TranslationProgressInterval - Stopwatch.GetElapsedTime(_translationCountShownAt);
        if (wait <= TimeSpan.Zero)
        {
            ShowQueuedTranslationCount();
            return;
        }
        if (_translationCountTimer is null)
        {
            _translationCountTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _translationCountTimer.Tick += OnTranslationCountTick;
        }
        if (_translationCountTimer.IsEnabled) return;
        _translationCountTimer.Interval = wait;
        _translationCountTimer.Start();
    }

    internal (string? Line, double? Value, bool BarShown, bool Moving) TranslationProgressForTest =>
        (_translationProgressLine, _translationStrip.Value, _translationStrip.Visibility == Visibility.Visible, _translationStrip.IsMoving);

    internal bool IsTranslationProgressQueuedForTest => _queuedTranslationCount is not null;
    internal bool IsTranslationProgressTimerRunningForTest => _translationCountTimer?.IsEnabled == true;
    internal void FlushTranslationProgressForTest() => ShowQueuedTranslationCount();
    internal PinProgressStrip TranslationStripForTest => _translationStrip;

    private void OnTranslationCountTick(object? sender, EventArgs e) => ShowQueuedTranslationCount();

    private void ShowQueuedTranslationCount()
    {
        _translationCountTimer?.Stop();
        if (_queuedTranslationCount is not { } count || _translationProgressLine is null || _isClosed) return;
        _queuedTranslationCount = null;
        _translationCountShownAt = Stopwatch.GetTimestamp();
        _translationCount = (count.Completed, count.Total);
        _translationEstimate.Observe(count.Completed, count.Total, Stopwatch.GetElapsedTime(0, count.ReportedAt));
        ReplaceTranslationLine(_translationEstimate.Describe(UiText.Format("Translate_Progress", count.Completed, count.Total)));
    }

    /// <summary>A translation step begins: its message replaces the previous step's in place, and its count starts over.</summary>
    private void BeginTranslationProgress(string text)
    {
        string? previous = _translationProgressLine;
        ResetTranslationCount();
        _translationProgressLine = text;
        if (previous is not null) MoveTranslationLine(previous, text);
    }

    /// <summary>
    /// The translation has stopped. A message showing its progress leaves at the next tick of the countdown unless the
    /// outcome replaces it first, which normally happens at once and in place.
    /// </summary>
    private void EndTranslationProgress()
    {
        if (_translationProgressLine is not { } line) return;
        _translationProgressLine = null;
        ResetTranslationCount();
        _translationStrip.Stop();
        if (_pendingProgress == line) CancelPendingProgress();
        if (_heldFeedback is { Kind: FeedbackKind.Progress } held && held.Text == line) _heldFeedback = null;
        if (!_feedbackShowsTranslation) return;
        _feedbackShowsTranslation = false;
        _feedbackCountdown.Start(TimeSpan.Zero);
    }

    /// <summary>The newest line of the running translation, wherever its message is: shown, held or about to appear.</summary>
    private void ReplaceTranslationLine(string line)
    {
        string? previous = _translationProgressLine;
        _translationProgressLine = line;
        if (previous is not null && MoveTranslationLine(previous, line)) return;
        // Another message replaced the progress and has gone while the translation still runs.
        if (_pendingProgress is null && (!IsFeedbackVisible || _feedbackHiding)) ShowFeedback(line, FeedbackKind.Progress);
    }

    /// <summary>Puts <paramref name="line"/> wherever <paramref name="previous"/> is; false when it is nowhere.</summary>
    private bool MoveTranslationLine(string previous, string line)
    {
        bool placed = false;
        if (_pendingProgress == previous)
        {
            _pendingProgress = line;
            placed = true;
        }
        if (_heldFeedback is { Kind: FeedbackKind.Progress } held && held.Text == previous)
        {
            _heldFeedback = (line, FeedbackKind.Progress);
            placed = true;
        }
        if (_feedbackShowsTranslation)
        {
            // In place: a count is not news to announce, and the message neither fades nor restarts its countdown.
            if (line != previous) KoreanWordWrap.Show(_feedbackText, line);
            SyncTranslationStrip();
            placed = true;
        }
        return placed;
    }

    private void SyncTranslationStrip()
    {
        if (!_feedbackShowsTranslation)
        {
            _translationStrip.Hide(keepSpace: false);
            return;
        }
        double? share = _translationCount is { Completed: > 0 } count ? (double)count.Completed / count.Total : TranslationDownloadShare;
        _translationStrip.Show(share, FluidMotion.AnimationsEnabled);
    }

    private void ResetTranslationCount()
    {
        _translationCountTimer?.Stop();
        _queuedTranslationCount = null;
        _translationCount = null;
        _translationCountShownAt = 0;
        _translationEstimate.Reset();
    }

    /// <summary>A closing pin keeps no timer, handler or animation of its translation progress.</summary>
    private void ReleaseTranslationProgress()
    {
        ResetTranslationCount();
        _translationProgressLine = null;
        _feedbackShowsTranslation = false;
        _translationStrip.Stop();
        if (_translationCountTimer is not { } timer) return;
        timer.Tick -= OnTranslationCountTick;
        _translationCountTimer = null;
    }
}
