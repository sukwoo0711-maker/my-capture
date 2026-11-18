namespace MyCapture.Core.Pin;

public enum PinTextStyle { PlainText, Grid, Code, Markdown }

public sealed record PinCodeRun(int Start, int Length, string Color);

/// <summary>The bounded, immutable text and colours parsed from a code clipboard payload.</summary>
public sealed record PinCodeDocument(
    string DisplayText,
    IReadOnlyList<PinCodeRun> Runs,
    string Background,
    string Foreground)
{
    public const int MaxDisplayTextLength = 262_144;
    public const int MaxRunCount = 4096;
    public const int MaxOriginalTextLength = 16_384;

    /// <summary>Smallest source font size, in DIP (CSS px), a code pin follows.</summary>
    public const double MinFontSizeDip = 8;

    /// <summary>Largest source font size, in DIP (CSS px), a code pin follows.</summary>
    public const double MaxFontSizeDip = 40;

    /// <summary>Smallest source line height, in DIP, a code pin follows.</summary>
    public const double MinLineHeightDip = 8;

    /// <summary>Largest source line height, in DIP, a code pin follows.</summary>
    public const double MaxLineHeightDip = 80;

    /// <summary>Longest font-family list a code pin keeps from its source.</summary>
    public const int MaxFontFamilyLength = 256;

    private IReadOnlyList<PinCodeRun> _runs = CopyRuns(Runs);

    /// <summary>
    /// The source editor's font size in DIP, or <see langword="null"/> for the pin's default.
    /// Optional session metadata: a manifest written before this field restores the default.
    /// </summary>
    public double? FontSizeDip { get; init; }

    /// <summary>The source editor's line height in DIP, or <see langword="null"/> for the pin's default.</summary>
    public double? LineHeightDip { get; init; }

    /// <summary>
    /// Installed font families to try before the pin's default code font, as a WPF fallback
    /// list ("Cascadia Code, Consolas"), or <see langword="null"/> for the default alone.
    /// </summary>
    public string? FontFamily { get; init; }

    public IReadOnlyList<PinCodeRun> Runs
    {
        get => _runs;
        init => _runs = CopyRuns(value);
    }

    public bool IsValid()
    {
        if (string.IsNullOrEmpty(DisplayText) || DisplayText.Length > MaxDisplayTextLength
            || Runs is null || Runs.Count > MaxRunCount || !IsColor(Background) || !IsColor(Foreground)
            || FontSizeDip is { } size && !(size >= MinFontSizeDip && size <= MaxFontSizeDip)
            || LineHeightDip is { } line && !(line >= MinLineHeightDip && line <= MaxLineHeightDip)
            || FontFamily is not null && !IsFontFamilyList(FontFamily))
            return false;
        int end = 0;
        foreach (PinCodeRun run in Runs)
        {
            if (run is null || run.Start < end || run.Length <= 0
                || run.Start > DisplayText.Length || run.Length > DisplayText.Length - run.Start
                || !IsColor(run.Color)) return false;
            end = run.Start + run.Length;
        }
        return true;
    }

    public bool MatchesOriginalText(string? originalText)
    {
        if (originalText is null || originalText.Length > MaxOriginalTextLength || DisplayText is null)
            return false;
        int display = 0, original = 0;
        while (true)
        {
            while (display < DisplayText.Length && char.IsWhiteSpace(DisplayText[display])) display++;
            while (original < originalText.Length && char.IsWhiteSpace(originalText[original])) original++;
            if (display == DisplayText.Length || original == originalText.Length)
                return display == DisplayText.Length && original == originalText.Length;
            if (DisplayText[display++] != originalText[original++]) return false;
        }
    }

    private static IReadOnlyList<PinCodeRun> CopyRuns(IReadOnlyList<PinCodeRun>? runs) =>
        runs is null ? null! : Array.AsReadOnly(runs.ToArray());

    /// <summary>
    /// A comma-separated list of plain family names: letters, digits, spaces, hyphens and
    /// underscores only, so a value can never name a font file, URI or resource.
    /// </summary>
    public static bool IsFontFamilyList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxFontFamilyLength) return false;
        foreach (string family in value.Split(','))
        {
            string name = family.Trim();
            if (name.Length == 0) return false;
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c is not (' ' or '-' or '_')) return false;
        }
        return true;
    }

    private static bool IsColor(string? color) => color is { Length: 7 } && color[0] == '#'
        && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
