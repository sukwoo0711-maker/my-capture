namespace MyCapture.App.Recording;

internal sealed record RangeVerificationFrame(int Width, int Height, byte[] Pixels, double PositionMs);
internal sealed record RangeModelErrors(double Identity, double StudioRetained, double ExpandedAsStudio);
internal sealed record RangeVerificationResult(VideoRangePresentation? Model, RangeModelErrors Errors,
    RangeModelErrors[] ChannelErrors, int Points, int DynamicRange, int Attempts, double PositionMs, string Status, string? Failure = null)
{
    public VideoRangeCorrection? Correction => Model switch
    {
        VideoRangePresentation.Correct => VideoRangeCorrection.None,
        VideoRangePresentation.StudioRetained => VideoRangeCorrection.Expand,
        VideoRangePresentation.ExpandedAsStudio => VideoRangeCorrection.Compress,
        _ => null,
    };
}

/// <summary>Compares uncorrected presentation to decoded RGB; input range is deliberately absent.</summary>
internal static class PresentedFrameRangeModel
{
    internal const int MinimumPoints = 64;
    internal const int MinimumDynamicRange = 96;
    internal const int FlatRange = 6;
    internal const double MaximumError = 6;
    internal const double WinnerRatio = 0.5;

    internal static RangeVerificationResult Compare(RangeVerificationFrame shown, RangeVerificationFrame reference)
    {
        if (shown.Width != reference.Width || shown.Height != reference.Height
            || shown.Pixels.Length != reference.Pixels.Length
            || shown.Pixels.Length != checked(shown.Width * shown.Height * 4))
            throw new ArgumentException("Verification frame sizes differ.");
        // Balance intensity bands so abundant clipped black/white cannot conceal the midtone
        // difference between identity and expansion. Trim within each band, never across bands.
        var errors = new List<double>[3, 3, 8];
        for (int m = 0; m < 3; m++)
        for (int c = 0; c < 3; c++)
        for (int b = 0; b < 8; b++) errors[m, c, b] = [];
        int[] minimum = [255, 255, 255], maximum = [0, 0, 0];
        int points = 0, width = shown.Width, height = shown.Height;
        byte[] source = reference.Pixels, pixels = shown.Pixels;
        for (int y = 2; y < height - 2; y += 4)
        for (int x = 2; x < width - 2; x += 4)
        {
            int offset = (y * width + x) * 4;
            bool flat = pixels[offset + 3] >= 250;
            for (int c = 0; c < 3 && flat; c++)
            {
                int low = 255, high = 0;
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int i = ((y + dy) * width + x + dx) * 4;
                    low = Math.Min(low, source[i + c]); high = Math.Max(high, source[i + c]);
                    flat &= source[i + 3] >= 250;
                }
                flat &= high - low <= FlatRange;
            }
            if (!flat) continue;
            points++;
            for (int c = 0; c < 3; c++)
            {
                int value = source[offset + c], actual = pixels[offset + c], band = value / 32;
                minimum[c] = Math.Min(minimum[c], value); maximum[c] = Math.Max(maximum[c], value);
                errors[0, c, band].Add(Math.Abs(actual - value));
                errors[1, c, band].Add(Math.Abs(actual - (16 + value * 219.0 / 255)));
                errors[2, c, band].Add(Math.Abs(actual - Math.Clamp((value - 16) * 255.0 / 219, 0, 255)));
            }
        }
        double[,] scores = new double[3, 3];
        for (int m = 0; m < 3; m++)
        for (int c = 0; c < 3; c++)
        {
            var bands = new List<double>();
            for (int b = 0; b < 8; b++)
            {
                List<double> values = errors[m, c, b];
                if (values.Count == 0) continue;
                values.Sort();
                int retained = Math.Max(1, values.Count - (int)(values.Count * 0.2));
                bands.Add(values.Take(retained).Average());
            }
            scores[m, c] = bands.Count == 0 ? 255 : bands.Average();
        }
        double[] totals = Enumerable.Range(0, 3).Select(m =>
            (scores[m, 0] + scores[m, 1] + scores[m, 2]) / 3).ToArray();
        int[] order = Enumerable.Range(0, 3).OrderBy(m => totals[m]).ToArray();
        int best = order[0], second = order[1];
        int range = points == 0 ? 0 : Enumerable.Range(0, 3).Max(c => maximum[c] - minimum[c]);
        string status = points < MinimumPoints ? "too-few-flat-points"
            : range < MinimumDynamicRange ? "low-dynamic-range"
            : Enumerable.Range(0, 3).Any(c => scores[best, c] > MaximumError) ? "model-mismatch"
            : totals[best] > totals[second] * WinnerRatio || totals[second] - totals[best] < 1 ? "ambiguous"
            : "verified";
        VideoRangePresentation? model = status != "verified" ? null : best switch
        {
            0 => VideoRangePresentation.Correct,
            1 => VideoRangePresentation.StudioRetained,
            _ => VideoRangePresentation.ExpandedAsStudio,
        };
        return new(model, new(totals[0], totals[1], totals[2]),
            Enumerable.Range(0, 3).Select(c => new RangeModelErrors(scores[0, c], scores[1, c], scores[2, c])).ToArray(),
            points, range, 0, shown.PositionMs, status);
    }

    // Exact box coverage, including fractional boundary pixels; no point/bilinear resampling.
    internal static RangeVerificationFrame AreaDownscale(byte[] pixels, int width, int height, int stride,
        int targetWidth, int targetHeight, double positionMs, CancellationToken token = default)
    {
        byte[] result = new byte[checked(targetWidth * targetHeight * 4)];
        double sx = (double)width / targetWidth, sy = (double)height / targetHeight;
        for (int y = 0; y < targetHeight; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < targetWidth; x++)
            {
                double left = x * sx, right = (x + 1) * sx, top = y * sy, bottom = (y + 1) * sy;
                double b = 0, g = 0, r = 0;
                for (int yy = (int)top; yy < Math.Ceiling(bottom) && yy < height; yy++)
                for (int xx = (int)left; xx < Math.Ceiling(right) && xx < width; xx++)
                {
                    double weight = (Math.Min(right, xx + 1) - Math.Max(left, xx))
                        * (Math.Min(bottom, yy + 1) - Math.Max(top, yy));
                    int i = yy * stride + xx * 4;
                    b += pixels[i] * weight; g += pixels[i + 1] * weight; r += pixels[i + 2] * weight;
                }
                int o = (y * targetWidth + x) * 4;
                result[o] = (byte)Math.Clamp(Math.Round(b / (sx * sy)), 0, 255);
                result[o + 1] = (byte)Math.Clamp(Math.Round(g / (sx * sy)), 0, 255);
                result[o + 2] = (byte)Math.Clamp(Math.Round(r / (sx * sy)), 0, 255);
                result[o + 3] = 255;
            }
        }
        return new(targetWidth, targetHeight, result, positionMs);
    }
}
