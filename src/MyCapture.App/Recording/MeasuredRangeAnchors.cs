namespace MyCapture.App.Recording;

internal static partial class MeasuredRangeCurveFitter
{
    private sealed record AnchorGroup(RangeValuePair[][] Clusters, string Decision, int ReferenceId = -1)
    {
        internal Anchor At(int? split)
        {
            var clusters = Clusters.Select(c => c.Where(p => split is null || (p.Split == 0 ? 0 : 1) == split).ToArray()).ToArray();
            var pairs = clusters.SelectMany(c => c).ToArray();
            double x = Median(pairs.Select(p => (double)p.Shown));
            return new(x, Median(clusters.Select(c => Median(c.Select(p => (double)p.Reference)))), (int)x / 4, pairs.Length);
        }
    }

    private sealed record AnchorPlan(AnchorGroup[] Groups, RangeClusterEvidence[] Clusters);
    private static int Support(int count) => Math.Max(4, (int)Math.Ceiling(count * 0.01));

    private static AnchorPlan PlanAnchors(List<RangeValuePair> pairs)
    {
        int firstSupport = Support(pairs.Count(p => p.Split == 0));
        int secondSupport = Support(pairs.Count(p => p.Split != 0));
        bool Repeated(RangeValuePair[] values) => values.Count(p => p.Split == 0) >= firstSupport
            && values.Count(p => p.Split != 0) >= secondSupport;
        var groups = new List<AnchorGroup>();
        var evidence = new List<RangeClusterEvidence>();
        var coveredBins = new HashSet<int>();
        // Identify flat reference clusters before quantising shown values. This keeps codec
        // noise straddling a four-level bin boundary from changing the two anchor sets.
        // Broad/continuous reference data retains the ordinary four-level bin estimator.
        int referenceId = 0;
        foreach (var reference in SplitReferenceClusters(pairs.ToArray()))
        {
            int id = referenceId++;
            bool flatReference = reference[^1].Reference - reference[0].Reference <= PresentedFrameRangeModel.FlatRange;
            var bins = reference.GroupBy(p => p.Shown / 4).Where(g => g.Count() >= 4).OrderBy(g => g.Key).ToArray();
            var islands = new List<List<RangeValuePair>>();
            int previous = -2;
            foreach (var bin in bins)
            {
                if (!flatReference || bin.Key > previous + 1) islands.Add([]);
                islands[^1].AddRange(bin);
                previous = bin.Key;
            }
            foreach (var island in islands)
            {
                var values = island.ToArray();
                bool repeated = Repeated(values);
                evidence.Add(ClusterEvidence(values, repeated ? "repeated" : "insufficient-repeat", repeated));
                if (!repeated) continue;
                groups.Add(new([values], flatReference ? "repeated" : "shared-reference-bin", id));
                foreach (var p in values) coveredBins.Add(p.Shown / 4);
            }
        }

        // A bin with no repeated flat reference cluster must still expose row differences
        // (e.g. reference 128 in one half and 136 in the other). Both halves support this
        // same bin; retain their independent statistics rather than synthesising a cluster.
        foreach (var bin in pairs.GroupBy(p => p.Shown / 4).OrderBy(g => g.Key))
        {
            var values = bin.ToArray();
            if (coveredBins.Contains(bin.Key) || !Repeated(values)) continue;
            groups.Add(new([values], "shared-bin"));
            evidence.AddRange(SplitReferenceClusters(values).Where(Repeated)
                .Select(cluster => ClusterEvidence(cluster, "shared-bin", true)));
        }
        groups.Sort((a, b) => a.At(null).X.CompareTo(b.At(null).X));
        var result = new List<AnchorGroup>();
        for (int i = 0; i < groups.Count;)
        {
            int end = i + 1;
            int bin = groups[i].At(null).Bin;
            // A plateau candidate is confined to ONE bin or TWO adjacent bins. Do not
            // chain a run of adjacent bins into an unbounded alternative estimator.
            while (end < groups.Count && groups[end].At(null).Bin <= bin + 1) end++;
            var nearby = groups.GetRange(i, end - i);
            bool sameReference = nearby.Select(g => g.ReferenceId).Distinct().Count() == 1;
            bool separate = nearby.Count > 1 && !sameReference && nearby.All(g => g.ReferenceId >= 0)
                && new int?[] { null, 0, 1 }.All(split =>
                {
                    var anchors = nearby.Select(g => g.At(split)).ToArray();
                    return anchors.Zip(anchors.Skip(1), (a, b) => a.X < b.X && a.Y < b.Y).All(v => v)
                        && (i == 0 || groups[i - 1].At(split).Y <= anchors[0].Y)
                        && (end == groups.Count || anchors[^1].Y <= groups[end].At(split).Y);
                });
            if (nearby.Count == 1 || sameReference) result.AddRange(nearby);
            else if (separate) result.AddRange(nearby.Select(g => g with { Decision = "adjacent-plateau" }));
            else result.Add(new(nearby.SelectMany(g => g.Clusters).ToArray(), "balanced-plateau"));
            i = end;
        }
        // The plan is shared, but observed coordinates and reference medians stay independent.
        // In particular, equal shown medians are never split into fictitious distinct levels.
        return new(result.ToArray(), evidence.ToArray());
    }

    private static RangeClusterEvidence ClusterEvidence(RangeValuePair[] values, string decision, bool repeated) => new(
        Median(values.Select(p => (double)p.Shown)), Median(values.Select(p => (double)p.Reference)),
        values.Length, values.Count(p => p.Split == 0), values.Count(p => p.Split != 0), repeated, decision, null);

    private static RangeValuePair[][] SplitReferenceClusters(RangeValuePair[] pairs)
    {
        if (pairs.Length == 0) return [];
        var sorted = pairs.OrderBy(p => p.Reference).ToArray();
        var clusters = new List<List<RangeValuePair>> { new() { sorted[0] } };
        foreach (var pair in sorted.Skip(1))
        {
            if (pair.Reference - clusters[^1][^1].Reference > 6) clusters.Add([]);
            clusters[^1].Add(pair);
        }
        return clusters.Select(g => g.ToArray()).ToArray();
    }
}
