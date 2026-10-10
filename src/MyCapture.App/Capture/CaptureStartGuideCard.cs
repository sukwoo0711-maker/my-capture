using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using MyCapture.App.Themes;

namespace MyCapture.App.Capture;

/// <summary>
/// Short start guide shown in the middle of the selector's monitor for a user's first captures and
/// whenever the mode bar's help button is pressed. It never takes input: clicks and drags go through
/// to the selector, and the overlay view decides when it fades away.
/// </summary>
internal sealed class CaptureStartGuideCard : Border
{
    /// <summary>Widest line of guide text, in DIPs before display scaling.</summary>
    internal const double TextWidth = 400;

    private readonly TextBlock _title;
    private readonly TextBlock _body;
    private readonly TextBlock _bar;
    private readonly TextBlock _again;

    internal CaptureStartGuideCard()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(22, 18, 22, 18);
        SetResourceReference(BackgroundProperty, CaptureModeBar.SurfaceKey);
        SetResourceReference(BorderBrushProperty, CaptureModeBar.BorderKey);
        _title = Line(16, FontWeights.SemiBold, CaptureModeBar.TextKey, new Thickness(0, 0, 0, 8));
        _body = Line(14, FontWeights.Normal, CaptureModeBar.TextKey, new Thickness(0, 0, 0, 6));
        _bar = Line(14, FontWeights.Normal, CaptureModeBar.TextKey, new Thickness(0, 0, 0, 10));
        _again = Line(12.5, FontWeights.Normal, CaptureModeBar.MutedKey, new Thickness(0));
        var lines = new StackPanel { MaxWidth = TextWidth };
        lines.Children.Add(_title);
        lines.Children.Add(_body);
        lines.Children.Add(_bar);
        lines.Children.Add(_again);
        Child = lines;
        Configure(detectWindows: true, withBar: true);
    }

    internal string TitleText => AutomationProperties.GetName(_title);

    /// <summary>The guide sentences without the joiners that keep Korean words whole.</summary>
    internal string BodyText => string.Join(" ", new[] { _body, _bar, _again }
        .Where(line => line.Visibility == Visibility.Visible)
        .Select(line => AutomationProperties.GetName(line)));

    /// <summary>
    /// Says what a click and a drag do (only a drag without window detection) and, with the mode bar,
    /// where the other capture types are and how to see the guide again.
    /// </summary>
    internal void Configure(bool detectWindows, bool withBar)
    {
        KoreanWordWrap.SetText(_title, UiText.Get("CaptureGuide2_IntroTitle"));
        KoreanWordWrap.SetText(_body, UiText.Get(detectWindows ? "CaptureGuide2_IntroBody" : "CaptureGuide2_IntroBodyRegionOnly"));
        KoreanWordWrap.SetText(_bar, UiText.Get("CaptureGuide2_IntroBar"));
        KoreanWordWrap.SetText(_again, UiText.Get("CaptureGuide2_IntroAgain"));
        _bar.Visibility = _again.Visibility = withBar ? Visibility.Visible : Visibility.Collapsed;
        _body.Margin = new Thickness(0, 0, 0, withBar ? 6 : 0);
        AutomationProperties.SetName(this, AutomationProperties.GetName(_title) + ". " + BodyText);
    }

    /// <summary>Same brushes as the mode bar and the chips.</summary>
    internal void ApplyPalette(IReadOnlyDictionary<string, Brush> palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Resources[CaptureModeBar.SurfaceKey] = palette["CaptureGuide.ChipSurface"];
        Resources[CaptureModeBar.BorderKey] = palette["CaptureGuide.ChipBorder"];
        Resources[CaptureModeBar.TextKey] = palette["CaptureGuide.Text"];
        Resources[CaptureModeBar.MutedKey] = palette["CaptureGuide.MutedText"];
    }

    private static TextBlock Line(double size, FontWeight weight, string brushKey, Thickness margin)
    {
        var line = new TextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
        line.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return line;
    }
}
