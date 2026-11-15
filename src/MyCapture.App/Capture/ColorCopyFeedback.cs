using System.Windows.Media;
using MyCapture.Core.Capture;
using MyCapture.Core.Settings;

namespace MyCapture.App.Capture;

/// <summary>The copied value and sampled colour, independent of the selector's lifetime.</summary>
/// <remarks>The clipboard receives <see cref="Value"/> only; the colour's name is shown and announced.</remarks>
internal sealed record ColorCopyFeedback(string Value, byte Red, byte Green, byte Blue)
{
    internal Color ChipColor => Color.FromRgb(Red, Green, Blue);
    internal string HexValue => ColorSampleFormatter.Format(Red, Green, Blue, ColorFormat.Hex);
    internal string Message => UiText.Format("Tools_ColorCopied", Value);

    /// <summary>The nearest CSS colour name, in Korean with the CSS name for a Korean interface.</summary>
    internal string NameText => ColorNames.Display(ColorNames.Nearest(Red, Green, Blue),
        UiText.Culture.TwoLetterISOLanguageName == "ko");

    /// <summary>What a screen reader hears: the copied value and the name.</summary>
    internal string Announcement => Message + " " + NameText;
}
