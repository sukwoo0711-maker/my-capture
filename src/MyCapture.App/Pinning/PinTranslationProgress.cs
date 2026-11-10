using System.Diagnostics;
using System.Windows.Threading;
using MyCapture.Ocr.Translation;

namespace MyCapture.App.Pinning;

/// <summary>
/// Carries one pin translation's progress from the translator's threads to the pin. A report that arrives while an
/// earlier one is still on its way replaces it, and nothing reaches the pin once its translation is cancelled,
/// superseded or the pin closes. The pin itself shows at most ten updates a second.
/// </summary>
internal sealed class PinTranslationProgress(PinWindow pin, CancellationToken cancellation) : IProgress<TranslationProgress>
{
    private readonly object _gate = new();
    private TranslationProgress _latest;
    private long _latestAt;
    private bool _queued;

    public void Report(TranslationProgress value)
    {
        if (cancellation.IsCancellationRequested) return;
        lock (_gate)
        {
            // When it was reported, not when the pin gets to it: the time estimate needs the translator's pace.
            (_latest, _latestAt) = (value, Stopwatch.GetTimestamp());
            if (_queued) return;
            _queued = true;
        }
        _ = pin.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Deliver));
    }

    private void Deliver()
    {
        TranslationProgress value;
        long at;
        lock (_gate)
        {
            (value, at) = (_latest, _latestAt);
            _queued = false;
        }
        if (!cancellation.IsCancellationRequested) pin.ShowTranslationProgress(value.Completed, value.Total, at);
    }
}

/// <summary>
/// How long a pin translation still takes. The pace is the sentences finished since the first one did, over the time
/// since then, so the model's load and the reading before it never count; each new estimate moves only part of the
/// way from the previous one, aged by the time between them, so the number does not jump from report to report.
/// </summary>
internal sealed class PinTranslationTimeEstimate
{
    /// <summary>Below this nothing is shown: the work is about to end.</summary>
    internal static readonly TimeSpan ShortestShown = TimeSpan.FromSeconds(2);

    private const double Smoothing = 0.35;
    private (int Completed, TimeSpan At)? _first;
    private double? _seconds;
    private TimeSpan _estimatedAt;
    private (int Value, bool Minutes)? _shown;

    internal void Reset()
    {
        _first = null;
        _seconds = null;
        _shown = null;
    }

    /// <param name="at">When the translator reported <paramref name="completed"/>, on any clock that only moves forward.</param>
    internal void Observe(int completed, int total, TimeSpan at)
    {
        if (total <= 0 || completed >= total)
        {
            _seconds = null;
            return;
        }
        if (_first is not { } first)
        {
            if (completed > 0) _first = (completed, at);
            return;
        }
        int units = completed - first.Completed;
        double elapsed = (at - first.At).TotalSeconds;
        if (units <= 0 || elapsed <= 0) return;
        double estimate = (total - completed) * elapsed / units;
        if (_seconds is { } previous)
        {
            double aged = Math.Max(0, previous - (at - _estimatedAt).TotalSeconds);
            estimate = aged + Smoothing * (estimate - aged);
        }
        _seconds = estimate;
        _estimatedAt = at;
    }

    /// <summary>The time left at the latest report, or null until two sentences are done and after the last.</summary>
    internal TimeSpan? Remaining => _seconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>
    /// The progress line with the time left: whole seconds under ten, five-second steps under a minute, then minutes.
    /// Without an estimate or under two seconds it is the line alone. One step up from what is shown is not shown.
    /// </summary>
    internal string Describe(string progressLine)
    {
        if (Remaining is not { } remaining || remaining < ShortestShown)
        {
            _shown = null;
            return progressLine;
        }
        (int value, bool minutes) next = Round(remaining.TotalSeconds);
        if (_shown is { } shown && shown.Minutes == next.minutes && next.value > shown.Value
            && next.value - shown.Value <= Step(shown)) next = shown;
        _shown = next;
        return next.minutes ? UiText.Format("Pin_TranslationMinutesLeft", progressLine, next.value)
            : UiText.Format("Pin_TranslationSecondsLeft", progressLine, next.value);
    }

    private static (int Value, bool Minutes) Round(double seconds)
    {
        int rounded = seconds < 10 ? (int)Math.Round(seconds, MidpointRounding.AwayFromZero)
            : 5 * (int)Math.Round(seconds / 5, MidpointRounding.AwayFromZero);
        return rounded < 60 ? (Math.Max(2, rounded), false)
            : (Math.Max(1, (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero)), true);
    }

    private static int Step((int Value, bool Minutes) shown) => shown.Minutes ? 1 : shown.Value < 10 ? 1 : 5;
}
