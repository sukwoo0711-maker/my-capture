namespace MyCapture.Core.Settings;

/// <summary>
/// When the capture selector opens with its short start guide: the first <see cref="ShowCount"/>
/// selectors of an install, counted in <see cref="CaptureSettings.StartGuideShownCount"/>.
/// </summary>
/// <remarks>
/// A first-time user needs to learn that a click takes a window and a drag takes a region; by the
/// third capture the mode bar alone carries that. The count saturates at <see cref="ShowCount"/>
/// and a hand-edited or older value is repaired instead of trusted, so it never grows unbounded.
/// </remarks>
public static class CaptureStartGuidePolicy
{
    public const int ShowCount = 3;

    /// <summary>The stored count clamped to 0..<see cref="ShowCount"/>.</summary>
    public static int Normalize(int shownCount) => Math.Clamp(shownCount, 0, ShowCount);

    public static bool ShouldShow(int shownCount) => Normalize(shownCount) < ShowCount;

    /// <summary>
    /// Counts one selector that shows the guide. Returns false, leaving the count saturated, once
    /// the guide has been shown <see cref="ShowCount"/> times.
    /// </summary>
    public static bool TryClaim(CaptureSettings capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        int count = Normalize(capture.StartGuideShownCount);
        if (count >= ShowCount)
        {
            capture.StartGuideShownCount = ShowCount;
            return false;
        }

        capture.StartGuideShownCount = count + 1;
        return true;
    }
}
