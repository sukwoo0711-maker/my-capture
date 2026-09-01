namespace MyCapture.Core.Queue;

/// <summary>
/// Which field a search term matched, so the UI can explain a hit and so tests can
/// assert that OCR text — not just the title — actually drives full-text search.
/// </summary>
[Flags]
public enum CaptureMatchField
{
    None = 0,
    Title = 1,
    WindowTitle = 2,
    OcrText = 4,
    MediaType = 8,
}

/// <summary>
/// One capture that matched a query, with the fields that matched.
/// </summary>
public sealed record CaptureSearchHit(CaptureRecord Record, CaptureMatchField Fields)
{
    /// <summary>True when the match came at least partly from recognised OCR text.</summary>
    public bool MatchedOcr => (Fields & CaptureMatchField.OcrText) != 0;
}

/// <summary>
/// How much of the queue is actually full-text searchable right now.
/// </summary>
/// <param name="Total">Total retained captures.</param>
/// <param name="Indexed">Captures whose current pixel generation has completed OCR.</param>
/// <param name="WithOcrText">Captures that carry recognised searchable text.</param>
public readonly record struct OcrCoverage(int Total, int Indexed, int WithOcrText)
{
    /// <summary>Captures whose current pixel generation has not been OCR-indexed yet.</summary>
    public int Missing => Math.Max(0, Total - Indexed);

    /// <summary>Fraction 0..1 of the queue for which OCR has completed.</summary>
    public double Fraction => Total <= 0 ? 1.0 : (double)Indexed / Total;

    public bool IsComplete => Missing == 0;
}

/// <summary>
/// Full-text search over the persistent capture queue: matches a free-text query against
/// each record's title, source-window title, and recognised OCR text.
/// </summary>
/// <remarks>
/// <para>
/// This is the logic behind the product's one genuine lock-in candidate — "find a past
/// capture by the text that was inside it". It is kept free of WPF and OS types so the
/// matching rules (multi-term AND, case/whitespace handling, field attribution) are
/// unit-tested directly, without a gallery window or the OCR engine.
/// </para>
/// <para>
/// Matching is multi-term AND: every whitespace-separated term must be found somewhere in
/// the record (any field). A term matches by case-insensitive substring, which is the right
/// default for mixed Korean/English UI text where stemming would do more harm than good.
/// A record only becomes findable by its picture's words once its OCR text has been indexed
/// (see the app's OCR indexing service); this class reports that coverage so the UI can tell
/// the user how much of their history is searchable.
/// </para>
/// </remarks>
public static class CaptureTextSearch
{
    private static readonly char[] TermSeparators = [' ', '\t', '\r', '\n'];

    /// <summary>
    /// Returns the records matching <paramref name="query"/>, newest first, each with the
    /// fields that matched. An empty query returns every record (newest first) with no field
    /// attribution.
    /// </summary>
    public static IReadOnlyList<CaptureSearchHit> Search(
        IEnumerable<CaptureRecord> records,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(records);

        string[] terms = SplitTerms(query);
        var hits = new List<CaptureSearchHit>();

        foreach (CaptureRecord record in records)
        {
            if (terms.Length == 0)
            {
                hits.Add(new CaptureSearchHit(record, CaptureMatchField.None));
                continue;
            }

            if (TryMatch(record, terms, out CaptureMatchField fields))
            {
                hits.Add(new CaptureSearchHit(record, fields));
            }
        }

        hits.Sort(static (a, b) => b.Record.CreatedAt.CompareTo(a.Record.CreatedAt));
        return hits;
    }

    /// <summary>
    /// True when every term in <paramref name="query"/> is found in the record. Convenience
    /// wrapper used by the gallery's per-record filter.
    /// </summary>
    public static bool IsMatch(CaptureRecord record, string? query)
    {
        ArgumentNullException.ThrowIfNull(record);
        string[] terms = SplitTerms(query);
        return terms.Length == 0 || TryMatch(record, terms, out _);
    }

    /// <summary>Reports how much of <paramref name="records"/> carries recognised OCR text.</summary>
    public static OcrCoverage MeasureCoverage(IEnumerable<CaptureRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        int total = 0;
        int indexed = 0;
        int withText = 0;
        foreach (CaptureRecord record in records)
        {
            if (!record.IsImage)
            {
                continue;
            }

            total++;
            if (record.HasCurrentOcrIndex)
            {
                indexed++;
            }

            if (record.HasOcrText)
            {
                withText++;
            }
        }

        return new OcrCoverage(total, indexed, withText);
    }

    private static string[] SplitTerms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split(TermSeparators, StringSplitOptions.RemoveEmptyEntries);

    private static bool TryMatch(CaptureRecord record, string[] terms, out CaptureMatchField fields)
    {
        fields = CaptureMatchField.None;

        string title = record.Title ?? string.Empty;
        string window = record.SourceWindowTitle ?? string.Empty;
        string ocr = record.OcrText ?? string.Empty;
        string media = record.IsVideo
            ? "동영상 비디오 video recording mp4"
            : "이미지 사진 스크린샷 image screenshot png";

        foreach (string term in terms)
        {
            CaptureMatchField termFields = CaptureMatchField.None;

            if (Contains(title, term))
            {
                termFields |= CaptureMatchField.Title;
            }

            if (Contains(window, term))
            {
                termFields |= CaptureMatchField.WindowTitle;
            }

            if (Contains(ocr, term))
            {
                termFields |= CaptureMatchField.OcrText;
            }

            if (Contains(media, term))
            {
                termFields |= CaptureMatchField.MediaType;
            }

            // AND semantics: a term found in no field fails the whole record.
            if (termFields == CaptureMatchField.None)
            {
                fields = CaptureMatchField.None;
                return false;
            }

            fields |= termFields;
        }

        return true;
    }

    private static bool Contains(string haystack, string term) =>
        haystack.Length > 0 && haystack.Contains(term, StringComparison.OrdinalIgnoreCase);
}
