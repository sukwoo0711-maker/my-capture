namespace MyCapture.Ocr;

/// <summary>
/// The typed outcome of a recognition request.
/// </summary>
/// <remarks>
/// Recognition can fail for reasons that are not exceptional and must not be treated as
/// errors: the OS OCR component may be unavailable (no packaged identity, no installed
/// language pack), or a perfectly valid image may simply contain no text. Callers branch on
/// this rather than on catching exceptions, which keeps every OCR failure non-fatal by
/// construction.
/// </remarks>
public enum OcrStatus
{
    /// <summary>Text was recognised.</summary>
    Success,

    /// <summary>Recognition ran but found no text.</summary>
    NoText,

    /// <summary>
    /// The OS OCR engine could not be created: no supported language, no package identity, or
    /// the WinRT API is not present on this platform.
    /// </summary>
    Unavailable,

    /// <summary>Recognition was attempted but failed (decode error, API exception).</summary>
    Failed,

    /// <summary>The request was cancelled.</summary>
    Cancelled,
}

/// <summary>
/// A single recognised word with its bounding box in original image pixels.
/// </summary>
public sealed record OcrWord(string Text, OcrRect Bounds);

/// <summary>
/// A recognised line: its text and bounding box (the union of its words) plus the words.
/// </summary>
public sealed record OcrLine(string Text, OcrRect Bounds, IReadOnlyList<OcrWord> Words);

/// <summary>
/// An axis-aligned rectangle in image-pixel coordinates.
/// </summary>
/// <remarks>
/// A plain value type rather than a WinRT or WPF rect so the OCR contract carries no UI-stack
/// type across the boundary, and so coordinate arithmetic (the unscale from the upscaled
/// recognition bitmap back to original pixels) is unit-testable without any imaging.
/// </remarks>
public readonly record struct OcrRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>
    /// Maps this rectangle from an upscaled recognition bitmap back to original image pixels
    /// by dividing every component by <paramref name="scale"/>.
    /// </summary>
    public OcrRect Unscale(double scale)
    {
        if (scale <= 0 || Math.Abs(scale - 1.0) < 1e-9)
        {
            return this;
        }

        return new OcrRect(X / scale, Y / scale, Width / scale, Height / scale);
    }

    /// <summary>
    /// Maps a rectangle reported against an image rotated clockwise back into the original
    /// image's coordinate system. The result is clamped to the original image bounds.
    /// </summary>
    public OcrRect MapFromClockwiseRotation(
        int clockwiseDegrees,
        int originalWidth,
        int originalHeight)
    {
        if (originalWidth <= 0 || originalHeight <= 0)
        {
            return this;
        }

        int normalizedDegrees = ((clockwiseDegrees % 360) + 360) % 360;
        OcrRect mapped = normalizedDegrees switch
        {
            0 => this,
            90 => new OcrRect(Y, originalHeight - Right, Height, Width),
            180 => new OcrRect(
                originalWidth - Right,
                originalHeight - Bottom,
                Width,
                Height),
            270 => new OcrRect(originalWidth - Bottom, X, Height, Width),
            _ => throw new ArgumentOutOfRangeException(
                nameof(clockwiseDegrees),
                clockwiseDegrees,
                "Only right-angle rotations are supported."),
        };

        double left = Math.Clamp(mapped.X, 0, originalWidth);
        double top = Math.Clamp(mapped.Y, 0, originalHeight);
        double right = Math.Clamp(mapped.Right, 0, originalWidth);
        double bottom = Math.Clamp(mapped.Bottom, 0, originalHeight);
        return new OcrRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>The smallest rectangle containing both operands; empty ignores the other.</summary>
    public static OcrRect Union(OcrRect a, OcrRect b)
    {
        if (a.Width <= 0 && a.Height <= 0)
        {
            return b;
        }

        if (b.Width <= 0 && b.Height <= 0)
        {
            return a;
        }

        double left = Math.Min(a.X, b.X);
        double top = Math.Min(a.Y, b.Y);
        double right = Math.Max(a.Right, b.Right);
        double bottom = Math.Max(a.Bottom, b.Bottom);
        return new OcrRect(left, top, right - left, bottom - top);
    }
}

/// <summary>
/// The full result of an OCR request.
/// </summary>
public sealed class OcrResult
{
    private OcrResult(
        OcrStatus status,
        string text,
        string? languageTag,
        IReadOnlyList<OcrLine> lines,
        TimeSpan elapsed,
        string? message)
    {
        Status = status;
        Text = text;
        LanguageTag = languageTag;
        Lines = lines;
        Elapsed = elapsed;
        Message = message;
    }

    public OcrStatus Status { get; }

    /// <summary>Recognised text, newline-joined by line. Empty unless <see cref="OcrStatus.Success"/>.</summary>
    public string Text { get; }

    /// <summary>BCP-47 tag actually used, when a recognizer ran.</summary>
    public string? LanguageTag { get; }

    /// <summary>Recognised lines with word boxes, in original image coordinates.</summary>
    public IReadOnlyList<OcrLine> Lines { get; }

    /// <summary>Wall-clock time spent, for the status line.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary>Optional diagnostic detail for the failed/unavailable states.</summary>
    public string? Message { get; }

    public bool HasText => Status == OcrStatus.Success && Text.Length > 0;

    public static OcrResult Success(
        string text,
        string languageTag,
        IReadOnlyList<OcrLine> lines,
        TimeSpan elapsed) =>
        new(OcrStatus.Success, text, languageTag, lines, elapsed, message: null);

    public static OcrResult NoText(string languageTag, TimeSpan elapsed) =>
        new(OcrStatus.NoText, string.Empty, languageTag, [], elapsed, message: null);

    public static OcrResult Unavailable(string? message = null) =>
        new(OcrStatus.Unavailable, string.Empty, languageTag: null, [], TimeSpan.Zero, message);

    public static OcrResult Failed(string message, TimeSpan elapsed = default) =>
        new(OcrStatus.Failed, string.Empty, languageTag: null, [], elapsed, message);

    public static OcrResult Cancelled() =>
        new(OcrStatus.Cancelled, string.Empty, languageTag: null, [], TimeSpan.Zero, message: null);
}
