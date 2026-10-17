namespace MyCapture.App.Recording;

internal static partial class MeasuredRangeCurveFitter
{
    private static AnchorPlan PlanWideFlatEndpoints(List<RangeValuePair> pairs, AnchorPlan primary)
    {
        int firstSupport = Support(pairs.Count(p => p.Split == 0));
        int secondSupport = Support(pairs.Count(p => p.Split != 0));
        var groups = new List<AnchorGroup>();
        bool changed = false;
        foreach (var group in primary.Groups)
        {
            // Expand only an existing ordinary flat island, after the original plateau plan.
            // Keep black/highlight clipping, broad reference data and shared-bin fallbacks intact.
            if (group.Decision != "repeated" || group.Clusters.Length != 1)
            {
                groups.Add(group);
                continue;
            }
            var values = group.Clusters[0];
            var supported = values.GroupBy(p => p.Shown / 4)
                .Where(g => g.Count(p => p.Split == 0) >= firstSupport
                    && g.Count(p => p.Split != 0) >= secondSupport)
                .OrderBy(g => g.Key).ToArray();
            if (values.Max(p => p.Reference) - values.Min(p => p.Reference) > PresentedFrameRangeModel.FlatRange
                || supported.Length < 3 || supported[0].Key == 0 || supported[^1].Key == 63)
            {
                groups.Add(group);
                continue;
            }
            // Shared endpoint slots, independently measured X/Y in full/first/second fits.
            // Keep plan order: an endpoint crossing its neighbour fails the existing strict-X check.
            groups.Add(new([supported[0].ToArray()], "wide-flat-endpoint", group.ReferenceId));
            groups.Add(new([supported[^1].ToArray()], "wide-flat-endpoint", group.ReferenceId));
            changed = true;
        }
        // Preserve every original cluster/patch and all raw pairs for the unchanged gates.
        return changed ? new(groups.ToArray(), primary.Clusters) : primary;
    }
}
