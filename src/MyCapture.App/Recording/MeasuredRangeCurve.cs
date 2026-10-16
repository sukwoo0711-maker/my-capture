namespace MyCapture.App.Recording;

internal readonly record struct RangeValuePair(byte Shown, byte Reference, int Split);

// Immutable, lossless 256-entry B/G/R curves. Base64 also keeps JSON and Information logs compact.
internal sealed record MeasuredRangeCurve(string Blue, string Green, string Red)
{
    public string Compact => $"B:{Blue};G:{Green};R:{Red}";
    internal byte[][] Channels() => [Convert.FromBase64String(Blue), Convert.FromBase64String(Green), Convert.FromBase64String(Red)];
    internal byte[] Texture()
    {
        byte[][] channels = Channels();
        if (channels.Any(c => c.Length != 256)) throw new ArgumentException("A range curve must have 256 entries per channel.");
        byte[] pixels = new byte[256 * 4];
        for (int i = 0; i < 256; i++)
        {
            for (int c = 0; c < 3; c++) pixels[i * 4 + c] = channels[c][i];
            pixels[i * 4 + 3] = 255;
        }
        return pixels;
    }
}

internal sealed record RangeMappingCoverage(int Levels, int Minimum, int Maximum, int Bands, int LargestGap)
{
    public bool Sufficient => Levels >= 6 && Minimum <= 32 && Maximum >= 223
        && Maximum - Minimum >= 192 && Bands >= 5 && LargestGap <= 112;
}

internal sealed record RangeMappingDiagnostics(string Status, RangeMappingCoverage[] Coverage,
    double? Residual = null, double? HeldOutResidual = null, double? SplitDisagreement = null)
{
    public string Summary => $"{Status}, residual={Residual:F3}, held-out={HeldOutResidual:F3}, split={SplitDisagreement:F3}, "
        + "coverage=" + string.Join("/", Coverage.Select(c => $"{c.Levels}@{c.Minimum}-{c.Maximum}:{c.Bands}bands:gap{c.LargestGap}"));
}

/// <summary>Robust inverse of the observed presenter, fitted only to flat decoded/presented pairs.</summary>
internal static class MeasuredRangeCurveFitter
{
    private sealed record Anchor(double X, double Y);
    private sealed record FitChannel(byte[] Curve, RangeMappingCoverage Coverage);
    internal static (MeasuredRangeCurve? Curve, RangeMappingDiagnostics Diagnostics) Fit(List<RangeValuePair>[] channels)
    {
        if (channels.Length != 3) throw new ArgumentException("B/G/R pairs are required.");
        var full = channels.Select(FitChannelCurve).ToArray();
        var coverage = full.Select(f => f.Coverage).ToArray();
        if (coverage.Any(c => !c.Sufficient)) return (null, new("mapping-low-coverage", coverage));
        double residual = 0, heldOut = 0, disagreement = 0;
        for (int c = 0; c < 3; c++)
        {
            var first = channels[c].Where(p => p.Split == 0).ToList();
            var second = channels[c].Where(p => p.Split != 0).ToList();
            var a = FitChannelCurve(first); var b = FitChannelCurve(second);
            if (!a.Coverage.Sufficient || !b.Coverage.Sufficient)
                return (null, new("mapping-split-low-coverage", coverage));
            residual = Math.Max(residual, Residual(channels[c], full[c].Curve));
            heldOut = Math.Max(heldOut, Math.Max(Residual(second, a.Curve), Residual(first, b.Curve)));
            int low = Math.Max(a.Coverage.Minimum, b.Coverage.Minimum);
            int high = Math.Min(a.Coverage.Maximum, b.Coverage.Maximum);
            for (int i = low; i <= high; i++)
                disagreement = Math.Max(disagreement, Math.Abs(a.Curve[i] - b.Curve[i]));
        }
        string status = residual > PresentedFrameRangeModel.MaximumError ? "mapping-residual"
            : heldOut > PresentedFrameRangeModel.MaximumError ? "mapping-held-out"
            : disagreement > 4 ? "mapping-split-disagreement" : "verified";
        var diagnostics = new RangeMappingDiagnostics(status, coverage, residual, heldOut, disagreement);
        return (status == "verified" ? new(Convert.ToBase64String(full[0].Curve),
            Convert.ToBase64String(full[1].Curve), Convert.ToBase64String(full[2].Curve)) : null, diagnostics);
    }

    private static FitChannel FitChannelCurve(List<RangeValuePair> pairs)
    {
        // Four-step bins absorb H.264/8-bit rounding. Sparse bins cannot make random outliers
        // look like coverage. Both independent halves must support the entire usable range.
        int support = Math.Max(4, (int)Math.Ceiling(pairs.Count * 0.01));
        var anchors = pairs.GroupBy(p => p.Shown / 4).Where(g => g.Count() >= support)
            .Select(g => new Anchor(Median(g.Select(p => (double)p.Shown)), ReferenceMedian(g.ToArray())))
            .OrderBy(p => p.X).ToArray();
        int minimum = anchors.Length == 0 ? 0 : (int)anchors[0].X;
        int maximum = anchors.Length == 0 ? 0 : (int)anchors[^1].X;
        int gap = anchors.Length < 2 ? 255 : (int)Math.Ceiling(anchors.Zip(anchors.Skip(1), (a, b) => b.X - a.X).Max());
        var coverage = new RangeMappingCoverage(anchors.Length, minimum, maximum,
            anchors.Select(a => (int)a.X / 32).Distinct().Count(), gap);
        if (!coverage.Sufficient) return new(new byte[256], coverage);

        // Pool adjacent violators, equal weight per supported bin (not per pixel area).
        var blocks = new List<(int Start, int End, double Sum, int Count)>();
        for (int i = 0; i < anchors.Length; i++)
        {
            blocks.Add((i, i, anchors[i].Y, 1));
            while (blocks.Count >= 2 && blocks[^2].Sum / blocks[^2].Count > blocks[^1].Sum / blocks[^1].Count)
            {
                var a = blocks[^2]; var b = blocks[^1];
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = (a.Start, b.End, a.Sum + b.Sum, a.Count + b.Count);
            }
        }
        double[] fitted = new double[anchors.Length];
        foreach (var block in blocks)
            for (int i = block.Start; i <= block.End; i++) fitted[i] = block.Sum / block.Count;
        byte[] curve = new byte[256];
        for (int value = 0, segment = 0; value < 256; value++)
        {
            while (segment < anchors.Length - 2 && value > anchors[segment + 1].X) segment++;
            // The first/last supported secant extends to the endpoints; never invent (0,0)
            // or (255,255) anchors. Saturation clamps the extrapolation, preserving monotonicity.
            double slope = (fitted[segment + 1] - fitted[segment]) / (anchors[segment + 1].X - anchors[segment].X);
            curve[value] = (byte)Math.Clamp(Math.Round(fitted[segment] + (value - anchors[segment].X) * slope), 0, 255);
        }
        return new(curve, coverage);
    }

    private static double ReferenceMedian(RangeValuePair[] bin)
    {
        // At a clipped endpoint several decoded levels can share one shown level. Balance
        // supported reference clusters so a large black patch cannot erase a smaller grey16
        // patch. Median within each cluster rejects codec noise; sparse outliers are excluded.
        double[] sorted = bin.Select(p => (double)p.Reference).Order().ToArray();
        var clusters = new List<List<double>> { new() { sorted[0] } };
        foreach (double value in sorted.Skip(1))
        {
            if (value - clusters[^1][^1] > 6) clusters.Add([]);
            clusters[^1].Add(value);
        }
        var supported = clusters.Where(c => c.Count >= Math.Max(3, (int)Math.Ceiling(bin.Length * 0.1)))
            .Select(Median).ToArray();
        return supported.Length == 0 ? Median(sorted) : Median(supported);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
    }

    private static double Residual(List<RangeValuePair> pairs, byte[] curve) => pairs.GroupBy(p => p.Reference / 32)
        .Select(g =>
        {
            var errors = g.Select(p => Math.Abs(curve[p.Shown] - p.Reference)).Order().ToArray();
            return errors.Take(Math.Max(1, errors.Length - (int)(errors.Length * 0.2))).Average();
        }).DefaultIfEmpty(255).Average();
}
