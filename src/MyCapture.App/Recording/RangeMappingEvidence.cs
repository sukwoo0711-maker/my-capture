using System.Globalization;
using System.Text;

namespace MyCapture.App.Recording;

// Public properties intentionally survive the diagnostic child's System.Text.Json round trip.
// Bin is shown / 4; X is the observed median, except aligned fits use the full fit's X.
internal sealed record RangeAnchorEvidence(int Bin, double X, double? BeforePav, double? AfterPav, int Count);
internal sealed record RangeSplitHalf(double Value, RangeAnchorEvidence? Left, RangeAnchorEvidence? Right,
    double NearestAnchorDistance);
internal sealed record RangeSplitLocation(string Channel, int Level, string Classification,
    RangeSplitHalf First, RangeSplitHalf Second);
internal sealed record RangeResidualBand(int Band, double? Error, int Count, int RetainedCount);
internal sealed record RangeReferenceCluster(double Median, int Count);
internal sealed record RangeMultiClusterBin(int Bin, double Shown, RangeReferenceCluster[] Clusters);
internal sealed record RangePatchContributor(double Shown, double Reference, int Count, double Error);
internal sealed record RangeClusterEvidence(double Shown, double Reference, int Count, int FirstCount,
    int SecondCount, bool Repeated, string Decision, double? CorrectedError);
internal sealed record RangeChannelEvidence(string Channel, RangeAnchorEvidence[] FullAnchors,
    RangeAnchorEvidence[] FirstAnchors, RangeAnchorEvidence[] SecondAnchors,
    RangeAnchorEvidence[] AlignedFirstAnchors, RangeAnchorEvidence[] AlignedSecondAnchors,
    RangeResidualBand[] Bands, RangeMultiClusterBin[] MultiClusterBins, RangePatchContributor[] TopContributors)
{
    public RangeResidualBand? WorstBand => Bands.OrderByDescending(b => b.Error).FirstOrDefault();
    public RangeClusterEvidence[] Clusters { get; init; } = [];
    public string[] AnchorDecisions { get; init; } = [];
}

internal static class RangeMappingEvidence
{
    internal static string Format(RangeMappingDiagnostics diagnostics)
    {
        var text = new StringBuilder();
        text.AppendLine(diagnostics.Summary);
        if (diagnostics.MaximumSplit is { } peak)
        {
            text.AppendLine(FormattableString.Invariant($"maximum split {peak.Channel}@{peak.Level} {peak.Classification}"));
            Half("first", peak.First); Half("second", peak.Second);
        }
        foreach (var channel in diagnostics.Channels)
        {
            text.AppendLine(channel.Channel);
            Anchors("full", channel.FullAnchors);
            Anchors("first", channel.FirstAnchors); Anchors("second", channel.SecondAnchors);
            Anchors("aligned-first", channel.AlignedFirstAnchors); Anchors("aligned-second", channel.AlignedSecondAnchors);
            text.AppendLine("  anchor decisions: " + string.Join("; ", channel.AnchorDecisions));
            text.AppendLine("  clusters (shown,reference,n,first/second,repeated,decision,corrected median error): "
                + string.Join("; ", channel.Clusters.Select(p => $"({Number(p.Shown)},{Number(p.Reference)},{p.Count},"
                    + $"{p.FirstCount}/{p.SecondCount},{p.Repeated},{p.Decision},{Number(p.CorrectedError)})")));
            text.AppendLine("  bands (reference/32, trimmed mean absolute error, n, retained): "
                + string.Join("; ", channel.Bands.Select(b => $"{b.Band}:{Number(b.Error)},n={b.Count},kept={b.RetainedCount}")));
            text.AppendLine("  multi-cluster shown bins: " + string.Join("; ", channel.MultiClusterBins.Select(b =>
                $"bin={b.Bin},x={Number(b.Shown)} <- " + string.Join("/", b.Clusters.Select(c => $"{Number(c.Median)}(n={c.Count})")))));
            text.AppendLine("  top patch contributors (shown,reference,n,trimmed mean absolute error): "
                + string.Join("; ", channel.TopContributors.Select(p => $"({Number(p.Shown)},{Number(p.Reference)},{p.Count},{Number(p.Error)})")));
        }
        return text.ToString();

        void Anchors(string label, RangeAnchorEvidence[] anchors) => text.AppendLine($"  {label} (bin:x,y before PAV,y after PAV,n): "
            + string.Join("; ", anchors.Select(Anchor)));
        void Half(string label, RangeSplitHalf half) => text.AppendLine($"  {label}: value={Number(half.Value)}, "
            + $"left={Anchor(half.Left)}, right={Anchor(half.Right)}, nearest={Number(half.NearestAnchorDistance)}");
    }

    private static string Number(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "missing";
    private static string Anchor(RangeAnchorEvidence? a) => a is null ? "none"
        : $"{a.Bin}:({Number(a.X)},{Number(a.BeforePav)},{Number(a.AfterPav)},n={a.Count})";
}

internal static partial class MeasuredRangeCurveFitter
{
    // Observations preserve the residual/coverage/split gates. Only the explicitly added
    // supported-patch maximum participates in the additional per-patch acceptance gate.
    private static RangeMappingDiagnostics BuildEvidence(List<RangeValuePair>[] channels, FitChannel[] full,
        List<RangeValuePair>[] first, List<RangeValuePair>[] second, FitChannel[] a, FitChannel[] b, AnchorPlan[] plans)
    {
        string[] names = ["B", "G", "R"];
        var evidence = new RangeChannelEvidence[3];
        RangeSplitLocation? peak = null;
        double maximum = -1;
        double? supported = null, gap = null, aligned = null;
        for (int c = 0; c < 3; c++)
        {
            var alignedA = Align(a[c].Anchors, full[c].Anchors, plans[c].Clusters, firstHalf: true);
            var alignedB = Align(b[c].Anchors, full[c].Anchors, plans[c].Clusters, firstHalf: false);
            for (int i = 0; i < alignedA.Length; i++)
                if (alignedA[i].AfterPav is { } ya && alignedB[i].AfterPav is { } yb)
                    aligned = Math.Max(aligned ?? 0, Math.Abs(ya - yb));

            var groupedBands = channels[c].GroupBy(p => p.Reference / 32).ToDictionary(g => g.Key, g => g.ToArray());
            var bands = Enumerable.Range(0, 8).Select(band => groupedBands.TryGetValue(band, out var values)
                ? new RangeResidualBand(band, full[c].HasCurve ? TrimmedError(values, full[c].Curve) : null, values.Length, Retained(values.Length))
                : new RangeResidualBand(band, null, 0, 0)).ToArray();
            var merged = new List<RangeMultiClusterBin>();
            var contributors = new List<RangePatchContributor>();
            foreach (var bin in channels[c].GroupBy(p => p.Shown / 4).OrderBy(g => g.Key))
            {
                // Retain the original bin view alongside the repeated-cluster decisions;
                // support is global in each half, not a percentage of this bin's area.
                var clusters = SplitReferenceClusters(bin.ToArray()).Where(g =>
                    g.Count(p => p.Split == 0) >= Support(first[c].Count)
                    && g.Count(p => p.Split != 0) >= Support(second[c].Count)).ToArray();
                if (clusters.Length >= 2)
                    merged.Add(new(bin.Key, Median(bin.Select(p => (double)p.Shown)), clusters.Select(g =>
                        new RangeReferenceCluster(Median(g.Select(p => (double)p.Reference)), g.Length)).ToArray()));
                if (full[c].HasCurve) contributors.AddRange(clusters.Select(g => new RangePatchContributor(
                    Median(g.Select(p => (double)p.Shown)), Median(g.Select(p => (double)p.Reference)),
                    g.Length, TrimmedError(g, full[c].Curve))));
            }
            var patchEvidence = plans[c].Clusters.Select(p => p with
            {
                CorrectedError = full[c].HasCurve
                    ? Math.Abs(full[c].Curve[(int)Math.Floor(p.Shown + 0.5)] - p.Reference) : null,
                Decision = p.Repeated ? plans[c].Groups.FirstOrDefault(g => g.Clusters.Any(values =>
                    Median(values.Select(v => (double)v.Shown)) == p.Shown
                    && Median(values.Select(v => (double)v.Reference)) == p.Reference))?.Decision ?? p.Decision : p.Decision,
            }).ToArray();
            evidence[c] = new(names[c], full[c].Anchors, a[c].Anchors, b[c].Anchors, alignedA, alignedB,
                bands, merged.ToArray(), contributors.OrderByDescending(p => p.Error)
                    .ThenByDescending(p => p.Count).ThenBy(p => p.Shown).ThenBy(p => p.Reference).Take(3).ToArray())
            { Clusters = patchEvidence, AnchorDecisions = plans[c].Groups.Select(g => g.Decision).ToArray() };
            if (!a[c].Coverage.Sufficient || !b[c].Coverage.Sufficient || !a[c].HasCurve || !b[c].HasCurve) continue;
            int low = Math.Max(a[c].Coverage.Minimum, b[c].Coverage.Minimum);
            int high = Math.Min(a[c].Coverage.Maximum, b[c].Coverage.Maximum);
            for (int level = low; level <= high; level++)
            {
                var left = DescribeHalf(a[c], level); var right = DescribeHalf(b[c], level);
                double difference = Math.Abs(left.Value - right.Value);
                bool hasSupport = left.NearestAnchorDistance <= 4 && right.NearestAnchorDistance <= 4;
                if (hasSupport) supported = Math.Max(supported ?? 0, difference);
                else gap = Math.Max(gap ?? 0, difference);
                // Deterministic ties: B/G/R, then lowest input level, matching the gate's scan.
                if (difference <= maximum) continue;
                maximum = difference;
                peak = new(names[c], level, hasSupport ? "supported" : "gap", left, right);
            }
        }
        return new("evidence", full.Select(f => f.Coverage).ToArray())
        {
            Channels = evidence, MaximumSplit = peak, SupportedSplit = supported, GapSplit = gap, AlignedSplit = aligned,
            MaximumPatchError = evidence.SelectMany(c => c.Clusters).Where(p => p.Repeated)
                .Select(p => p.CorrectedError).Max(),
        };
    }

    private static RangeSplitHalf DescribeHalf(FitChannel fit, int level) => new(fit.Curve[level],
        fit.Anchors.LastOrDefault(a => a.X <= level), fit.Anchors.FirstOrDefault(a => a.X >= level),
        fit.Anchors.Min(a => Math.Abs(a.X - level)));

    private static RangeAnchorEvidence[] Align(RangeAnchorEvidence[] half, RangeAnchorEvidence[] full,
        RangeClusterEvidence[] clusters, bool firstHalf)
    {
        // IDs are plan positions, not bins: an adjacent plateau can have two anchors in
        // one bin. Only align X for the diagnostic view; independent fit X stays measured.
        // Unsupported clusters stay explicit in the diagnostic view, including a missing
        // half's zero count. They never become anchors in any of the three actual fits.
        return half.Select((a, i) => a with { X = full[i].X, Bin = full[i].Bin })
            .Concat(clusters.Where(c => !c.Repeated).Select(c =>
            {
                int count = firstHalf ? c.FirstCount : c.SecondCount;
                return new RangeAnchorEvidence((int)c.Shown / 4, c.Shown, count >= 3 ? c.Reference : null, null, count);
            })).ToArray();
    }

    private static int Retained(int count) => Math.Max(1, count - (int)(count * 0.2));
    private static double TrimmedError(IEnumerable<RangeValuePair> pairs, byte[] curve)
    {
        int[] errors = pairs.Select(p => Math.Abs(curve[p.Shown] - p.Reference)).Order().ToArray();
        return errors.Take(Retained(errors.Length)).Average();
    }
}
