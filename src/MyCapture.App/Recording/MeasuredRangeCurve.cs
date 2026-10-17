namespace MyCapture.App.Recording;

internal readonly record struct RangeValuePair(byte Shown, byte Reference, int Split);

// Immutable, lossless 256-entry B/G/R curves. Base64 also keeps JSON and Information logs compact.
internal sealed record MeasuredRangeCurve(string Blue, string Green, string Red)
{
    public string Compact => $"B:{Blue};G:{Green};R:{Red}";
    internal byte[][] Channels() => [Convert.FromBase64String(Blue), Convert.FromBase64String(Green), Convert.FromBase64String(Red)];
    internal const int TextureWidth = 1024, TextureHeight = 256;
    internal byte[] Texture()
    {
        byte[][] channels = Channels();
        if (channels.Any(c => c.Length != 256)) throw new ArgumentException("A range curve must have 256 entries per channel.");
        byte[] pixels = new byte[TextureWidth * TextureHeight * 4];
        for (int y = 0; y < TextureHeight; y++)
        for (int x = 0; x < TextureWidth; x++)
        {
            int offset = (y * TextureWidth + x) * 4;
            for (int c = 0; c < 3; c++) pixels[offset + c] = channels[c][x / 4];
            pixels[offset + 3] = 255;
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
    public RangeChannelEvidence[] Channels { get; init; } = [];
    public RangeSplitLocation? MaximumSplit { get; init; }
    public double? SupportedSplit { get; init; }
    public double? GapSplit { get; init; }
    public double? AlignedSplit { get; init; }
    public double? MaximumPatchError { get; init; }
    public string Summary => $"{Status}, residual={Residual:F3}, held-out={HeldOutResidual:F3}, split={SplitDisagreement:F3}, "
        + "coverage=" + string.Join("/", Coverage.Select(c => $"{c.Levels}@{c.Minimum}-{c.Maximum}:{c.Bands}bands:gap{c.LargestGap}"))
        + $", peak={MaximumSplit?.Channel}@{MaximumSplit?.Level}:{MaximumSplit?.Classification}, "
        + $"supported/gap/aligned={SupportedSplit:F3}/{GapSplit:F3}/{AlignedSplit:F3}, worst="
        + string.Join("/", Channels.Select(c => $"{c.Channel}:{c.WorstBand?.Band}@{c.WorstBand?.Error:F3}"))
        + $", merged={Channels.Sum(c => c.MultiClusterBins.Length)}, patch={MaximumPatchError:F3}";

    internal string Evidence => RangeMappingEvidence.Format(this);
}

/// <summary>Robust inverse of the observed presenter, fitted only to flat decoded/presented pairs.</summary>
internal static partial class MeasuredRangeCurveFitter
{
    private sealed record Anchor(double X, double Y, int Bin, int Count);
    private sealed record FitChannel(byte[] Curve, RangeMappingCoverage Coverage, RangeAnchorEvidence[] Anchors, bool HasCurve);
    internal static (MeasuredRangeCurve? Curve, RangeMappingDiagnostics Diagnostics) Fit(List<RangeValuePair>[] channels)
    {
        if (channels.Length != 3) throw new ArgumentException("B/G/R pairs are required.");
        var firstHalves = channels.Select(p => p.Where(v => v.Split == 0).ToList()).ToArray();
        var secondHalves = channels.Select(p => p.Where(v => v.Split != 0).ToList()).ToArray();
        var plans = channels.Select(PlanAnchors).ToArray();
        var full = plans.Select((p, c) => FitChannelCurve(p.Groups.Select(g => g.At(null)).ToArray(), SupportedCoverage(channels[c]))).ToArray();
        var firstFits = plans.Select((p, c) => FitChannelCurve(p.Groups.Select(g => g.At(0)).ToArray(), SupportedCoverage(firstHalves[c]))).ToArray();
        var secondFits = plans.Select((p, c) => FitChannelCurve(p.Groups.Select(g => g.At(1)).ToArray(), SupportedCoverage(secondHalves[c]))).ToArray();
        var evidence = BuildEvidence(channels, full, firstHalves, secondHalves, firstFits, secondFits, plans);
        // Preserve the original coverage rule, including each half's supported range. A
        // second anchor in one plateau bin does not count as another covered shown bin.
        if (full.Any(f => !f.Coverage.Sufficient))
            return (null, evidence with { Status = "mapping-low-coverage" });
        if (firstFits.Concat(secondFits).Any(f => !f.Coverage.Sufficient))
            return (null, evidence with { Status = "mapping-split-low-coverage" });
        if (full.Concat(firstFits).Concat(secondFits).Any(f => !f.HasCurve))
            return (null, evidence with { Status = "mapping-low-coverage" });
        double residual = 0, heldOut = 0, disagreement = 0;
        for (int c = 0; c < 3; c++)
        {
            var first = firstHalves[c]; var second = secondHalves[c];
            var a = firstFits[c]; var b = secondFits[c];
            if (!a.Coverage.Sufficient || !b.Coverage.Sufficient)
                return (null, evidence with { Status = "mapping-split-low-coverage" });
            residual = Math.Max(residual, Residual(channels[c], full[c].Curve));
            heldOut = Math.Max(heldOut, Math.Max(Residual(second, a.Curve), Residual(first, b.Curve)));
            int low = Math.Max(a.Coverage.Minimum, b.Coverage.Minimum);
            int high = Math.Min(a.Coverage.Maximum, b.Coverage.Maximum);
            for (int i = low; i <= high; i++)
                disagreement = Math.Max(disagreement, Math.Abs(a.Curve[i] - b.Curve[i]));
        }
        string status = residual > PresentedFrameRangeModel.MaximumError ? "mapping-residual"
            : heldOut > PresentedFrameRangeModel.MaximumError ? "mapping-held-out"
            : disagreement > 4 ? "mapping-split-disagreement"
            : evidence.MaximumPatchError > 12 ? "mapping-patch-error" : "verified";
        var diagnostics = evidence with { Status = status, Residual = residual,
            HeldOutResidual = heldOut, SplitDisagreement = disagreement };
        return (status == "verified" ? new(Convert.ToBase64String(full[0].Curve),
            Convert.ToBase64String(full[1].Curve), Convert.ToBase64String(full[2].Curve)) : null, diagnostics);
    }

    private static RangeMappingCoverage SupportedCoverage(List<RangeValuePair> pairs) => CoverageOf(pairs
        .GroupBy(p => p.Shown / 4).Where(g => g.Count() >= Support(pairs.Count))
        .Select(g => new Anchor(Median(g.Select(p => (double)p.Shown)), 0, g.Key, g.Count())).OrderBy(a => a.X).ToArray());

    private static RangeMappingCoverage CoverageOf(Anchor[] anchors)
    {
        int minimum = anchors.Length == 0 ? 0 : (int)anchors[0].X;
        int maximum = anchors.Length == 0 ? 0 : (int)anchors[^1].X;
        int gap = anchors.Length < 2 ? 255 : (int)Math.Ceiling(anchors.Zip(anchors.Skip(1), (a, b) => b.X - a.X).Max());
        return new(anchors.Select(a => a.Bin).Distinct().Count(), minimum, maximum,
            anchors.Select(a => (int)a.X / 32).Distinct().Count(), gap);
    }

    private static FitChannel FitChannelCurve(Anchor[] anchors, RangeMappingCoverage coverage)
    {
        if (!CoverageOf(anchors).Sufficient || !anchors.Zip(anchors.Skip(1), (a, b) => a.X < b.X).All(v => v))
            return new(new byte[256], coverage,
                anchors.Select(a => new RangeAnchorEvidence(a.Bin, a.X, a.Y, null, a.Count)).ToArray(), false);

        double[] fitted = FitMonotone(anchors);
        byte[] curve = new byte[256];
        for (int value = 0, segment = 0; value < 256; value++)
        {
            while (segment < anchors.Length - 2 && value > anchors[segment + 1].X) segment++;
            // The first/last supported secant extends to the endpoints; never invent (0,0)
            // or (255,255) anchors. Saturation clamps the extrapolation, preserving monotonicity.
            double slope = (fitted[segment + 1] - fitted[segment]) / (anchors[segment + 1].X - anchors[segment].X);
            curve[value] = (byte)Math.Clamp(Math.Round(fitted[segment] + (value - anchors[segment].X) * slope), 0, 255);
        }
        return new(curve, coverage, anchors.Select((a, i) =>
            new RangeAnchorEvidence(a.Bin, a.X, a.Y, fitted[i], a.Count)).ToArray(), true);
    }

    private static double[] FitMonotone(Anchor[] anchors)
    {
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
        return fitted;
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
