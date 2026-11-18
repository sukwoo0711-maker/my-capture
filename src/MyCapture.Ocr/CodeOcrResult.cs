namespace MyCapture.Ocr;

internal sealed record DecodedImageCode(string Text, OcrRect Bounds, bool IsTwoDimensional);

/// <summary>Combines completed code readings with OCR without normalizing payload text.</summary>
internal static class CodeOcrResult
{
    internal static OcrResult Merge(OcrResult text, IReadOnlyList<DecodedImageCode> codes)
    {
        if (codes.Count == 0 || text.Status == OcrStatus.Cancelled) return text;
        var lines = RemoveNoise(text.Lines, codes).ToList();
        foreach (DecodedImageCode code in codes)
        {
            // Keep blank lines, tabs, leading/trailing spaces and their shared source box.
            foreach (string payload in code.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                lines.Add(new(payload, code.Bounds, [new(payload, code.Bounds)]) { IsCodePayload = true });
        }
        OcrLine[] ordered = lines.OrderBy(line => text.ReadingOrientation.ToReading(line.Bounds).Y)
            .ThenBy(line => text.ReadingOrientation.ToReading(line.Bounds).X).ToArray();
        OcrResult result = OcrResult.Success(string.Join("\n", ordered.Select(line => line.Text)),
            text.LanguageTag ?? string.Empty, ordered, text.Elapsed, text.ReadingOrientation, text.TextAngle)
            .WithNeuralLines(RemoveNoise(text.NeuralLines, codes));
        return text.Quality is { } quality ? result.WithQuality(quality) : result;
    }

    internal static OcrResult TextOnly(OcrResult result)
    {
        if (!result.Lines.Any(line => line.IsCodePayload)) return result;
        OcrLine[] lines = result.Lines.Where(line => !line.IsCodePayload).ToArray();
        return OcrResult.Success(string.Join("\n", lines.Select(line => line.Text)), result.LanguageTag ?? "",
            lines, result.Elapsed, result.ReadingOrientation, result.TextAngle).WithNeuralLines(result.NeuralLines);
    }

    private static OcrLine[] RemoveNoise(IReadOnlyList<OcrLine> source, IReadOnlyList<DecodedImageCode> codes)
    {
        var lines = new List<OcrLine>();
        foreach (OcrLine line in source)
        {
            if (line.IsCodePayload) { lines.Add(line); continue; }
            OcrWord[] kept = line.Words.Where(word => !codes.Any(code => code.IsTwoDimensional
                && MostlyInside(word.Bounds, code.Bounds))).ToArray();
            if (kept.Length == line.Words.Count)
            {
                if (line.Words.Count > 0 || !codes.Any(code => code.IsTwoDimensional && MostlyInside(line.Bounds, code.Bounds)))
                    lines.Add(line);
            }
            else if (kept.Length > 0)
                lines.Add(new(string.Join(" ", kept.Select(word => word.Text)),
                    kept.Select(word => word.Bounds).Aggregate(OcrRect.Union), kept));
        }
        return lines.ToArray();
    }

    private static bool MostlyInside(OcrRect word, OcrRect code) => word.Width > 0 && word.Height > 0
        && Math.Max(0, Math.Min(word.Right, code.Right) - Math.Max(word.X, code.X))
        * Math.Max(0, Math.Min(word.Bottom, code.Bottom) - Math.Max(word.Y, code.Y)) >= word.Width * word.Height * .6;
}
