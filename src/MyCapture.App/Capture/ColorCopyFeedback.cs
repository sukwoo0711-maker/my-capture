using System.Windows.Media;
using MyCapture.Core.Capture;
using MyCapture.Core.Settings;

namespace MyCapture.App.Capture;

/// <summary>The copied value and sampled colour, independent of the selector's lifetime.</summary>
internal sealed record ColorCopyFeedback(string Value, byte Red, byte Green, byte Blue)
{
    internal Color ChipColor => Color.FromRgb(Red, Green, Blue);
    internal string HexValue => ColorSampleFormatter.Format(Red, Green, Blue, ColorFormat.Hex);
    internal string Message => UiText.Format("Tools_ColorCopied", Value);
}
