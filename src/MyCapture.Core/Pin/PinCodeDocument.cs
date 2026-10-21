namespace MyCapture.Core.Pin;

public enum PinTextStyle { PlainText, Grid, Code }

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
    private IReadOnlyList<PinCodeRun> _runs = CopyRuns(Runs);

    public IReadOnlyList<PinCodeRun> Runs
    {
        get => _runs;
        init => _runs = CopyRuns(value);
    }

    public bool IsValid()
    {
        if (string.IsNullOrEmpty(DisplayText) || DisplayText.Length > MaxDisplayTextLength
            || Runs is null || Runs.Count > MaxRunCount || !IsColor(Background) || !IsColor(Foreground))
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

    private static bool IsColor(string? color) => color is { Length: 7 } && color[0] == '#'
        && color.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}
