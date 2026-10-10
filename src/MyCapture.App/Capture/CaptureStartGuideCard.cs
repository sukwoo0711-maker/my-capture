using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// Short start guide shown in the middle of the selector's monitor for a user's first captures and
/// whenever the mode bar's help button is pressed: one sentence saying what a click and a drag do.
/// The bar's buttons explain themselves with names and tooltips, so the card does not repeat them.
/// It never takes input: clicks and drags go through to the selector, and the overlay view decides
/// when it fades away.
/// </summary>
internal sealed class CaptureStartGuideCard : Border
{
    /// <summary>Widest line of guide text, in DIPs before display scaling.</summary>
    internal const double TextWidth = 400;

    private readonly TextBlock _sentence;

    internal CaptureStartGuideCard()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(22, 16, 22, 16);
        SetResourceReference(BackgroundProperty, CaptureModeBar.SurfaceKey);
        SetResourceReference(BorderBrushProperty, CaptureModeBar.BorderKey);
        _sentence = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = TextWidth };
        _sentence.SetResourceReference(TextBlock.ForegroundProperty, CaptureModeBar.TextKey);
        Child = _sentence;
        Configure(detectWindows: true);
    }

    /// <summary>The guide sentence without the joiners that keep Korean words whole.</summary>
    internal string BodyText => AutomationProperties.GetName(_sentence);

    /// <summary>What a click and a drag do (only a drag without window detection).</summary>
    internal void Configure(bool detectWindows)
    {
        KoreanWordWrap.SetText(_sentence, UiText.Get(detectWindows ? "Onboard_GuideSentence" : "Onboard_GuideSentenceRegionOnly"));
        AutomationProperties.SetName(this, BodyText);
    }

    /// <summary>Same brushes as the mode bar.</summary>
    internal void ApplyPalette(IReadOnlyDictionary<string, Brush> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Resources[CaptureModeBar.SurfaceKey] = palette["CaptureGuide.ChipSurface"];
        Resources[CaptureModeBar.BorderKey] = palette["CaptureGuide.ChipBorder"];
        Resources[CaptureModeBar.TextKey] = palette["CaptureGuide.Text"];
        Resources[CaptureModeBar.MutedKey] = palette["CaptureGuide.MutedText"];
    }
}
