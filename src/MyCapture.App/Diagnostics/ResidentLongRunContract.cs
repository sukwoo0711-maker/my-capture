namespace MyCapture.App.Diagnostics;

/// <summary>Fixed long-run rules, independent from smoke/preflight execution status.</summary>
internal static class ResidentLongRunContract
{
    internal const string FullScenario = "soak-8h";
    internal const string PreflightScenario = "soak-8h-preflight";
    internal const string SmokeScenario = "soak-8h-smoke";
    internal const string Revision = "resident-soak-8h-v2";
    internal const int TargetSeconds = 28_800;
    internal const int ActionCount = 1_000;
    internal const int ComparisonCount = 100;
    internal const int Seed = 42;
    internal static bool FullWorkloadImplemented => true;
    internal static readonly string[] Cycle =
        ["capture", "edit", "pin", "capture", "edit", "pin", "library", "ocr", "recording", "translation"];
    internal static readonly string[] MissingAdapters = [];

    internal static bool IsRequested(IReadOnlyList<string> scenarios) =>
        scenarios.Any(value => string.Equals(value, FullScenario, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, PreflightScenario, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, SmokeScenario, StringComparison.OrdinalIgnoreCase));

    internal static void ValidateOptions(IReadOnlyList<string> scenarios, bool quick, bool audit,
        IReadOnlySet<string> variants, bool noIndexing, bool software)
    {
        if (!IsRequested(scenarios)) return;
        if (scenarios.Count != 1 || quick || audit || variants.Count != 0 || noIndexing || software)
            throw new ArgumentException("The long-run scenario requires one scenario and production options without diagnostic variants.");
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("The long-run contract requires an x64 process.");
    }

    internal static string Kind(int index)
    {
        if (index is < 1 or > ActionCount) throw new ArgumentOutOfRangeException(nameof(index));
        return Cycle[(index - 1) % Cycle.Length];
    }

    internal static double ScheduledSeconds(int index)
    {
        _ = Kind(index);
        return (index - 1) * ((double)TargetSeconds / ActionCount);
    }

    internal sealed record State(string WindowState, int QueueCount, int ThumbnailCount, long ThumbnailBytes,
        int ClosedPinCount, long ClosedPinBytes, string ModelState, int PendingWork);

    internal sealed record ActionSample(int Index, string Kind, double StartedSeconds, double CompletedSeconds,
        bool OraclePassed, long PrivateBytes, State State);

    internal sealed record AwakeSample(bool ReadSucceeded, bool PowerEventsSubscribed,
        ulong StartTicks100ns, ulong EndTicks100ns, double ElapsedSeconds,
        int SuspendCount, int ResumeCount, long FirstPowerEventTimestamp, long LastPowerEventTimestamp,
        long ObservationTimestamp, DateTimeOffset ObservedUtc);

    internal static bool IsAwakeIntervalValid(AwakeSample? sample) => sample is
        { ReadSucceeded: true, PowerEventsSubscribed: true, SuspendCount: 0, ResumeCount: 0,
          FirstPowerEventTimestamp: 0, LastPowerEventTimestamp: 0 }
        && sample.EndTicks100ns >= sample.StartTicks100ns
        && double.IsFinite(sample.ElapsedSeconds)
        && sample.ElapsedSeconds == (sample.EndTicks100ns - sample.StartTicks100ns) / 10_000_000.0
        && sample.ElapsedSeconds >= TargetSeconds;

    internal sealed record MemoryEvaluation(bool EvidenceValid, bool? BudgetPassed, double? FirstMedianBytes,
        double? LastMedianBytes, double? AllowedIncreaseBytes, string[] Issues);

    /// <summary>A numeric subgate, never sufficient to declare the whole commercial gate passed.</summary>
    internal static MemoryEvaluation Evaluate(IReadOnlyList<ActionSample> samples, double observedSeconds)
    {
        var issues = new List<string>();
        if (!double.IsFinite(observedSeconds) || observedSeconds < TargetSeconds) issues.Add("DURATION_INCOMPLETE");
        if (samples.Count != ActionCount) issues.Add("ACTION_COUNT_INCOMPLETE");
        double previousEnd = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            ActionSample sample = samples[i];
            if (i >= ActionCount || sample.Index != i + 1 || sample.Kind != Kind(i + 1))
                issues.Add("ACTION_ORDER_OR_KIND");
            if (!double.IsFinite(sample.StartedSeconds) || !double.IsFinite(sample.CompletedSeconds)
                || sample.StartedSeconds < previousEnd || sample.CompletedSeconds < sample.StartedSeconds
                || sample.CompletedSeconds > observedSeconds
                || (i < ActionCount && sample.StartedSeconds < ScheduledSeconds(i + 1)))
                issues.Add("ACTION_CHRONOLOGY");
            if (!sample.OraclePassed || sample.PrivateBytes <= 0) issues.Add("ACTION_ORACLE_OR_BYTES");
            if (sample.State.PendingWork != 0 || sample.State.QueueCount < 0 || sample.State.ThumbnailCount < 0
                || sample.State.ThumbnailBytes < 0 || sample.State.ClosedPinCount < 0 || sample.State.ClosedPinBytes < 0
                || string.IsNullOrWhiteSpace(sample.State.WindowState) || string.IsNullOrWhiteSpace(sample.State.ModelState))
                issues.Add("CHECKPOINT_STATE_INCOMPLETE");
            previousEnd = sample.CompletedSeconds;
        }
        if (samples.Count == ActionCount)
            for (int i = 0; i < ComparisonCount; i++)
                if (samples[i].State != samples[ActionCount - ComparisonCount + i].State)
                    issues.Add("COMPARISON_STATE_DIFFERS");
        if (issues.Count != 0) return new(false, null, null, null, null, [.. issues.Distinct()]);
        double first = Median(samples.Take(ComparisonCount).Select(value => value.PrivateBytes));
        double last = Median(samples.Skip(ActionCount - ComparisonCount).Select(value => value.PrivateBytes));
        double allowed = Math.Max(first * 0.10, 32L * 1024 * 1024);
        return new(true, last - first <= allowed, first, last, allowed, []);
    }

    private static double Median(IEnumerable<long> values)
    {
        long[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? ordered[middle - 1] / 2.0 + ordered[middle] / 2.0 : ordered[middle];
    }
}
