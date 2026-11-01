using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;

namespace MyCapture.Core.Recording;

public enum RecordingWatermarkSource { Image, Text }

/// <summary>Recording placement is separate from the image editor's stamp preferences.</summary>
public sealed record RecordingWatermarkSettings
{
    public bool Enabled { get; init; }
    public RecordingWatermarkSource Source { get; init; } = RecordingWatermarkSource.Image;
    public string Text { get; init; } = string.Empty;
    public string TextColor { get; init; } = "#FFFFFF";
    public StampAnchor Anchor { get; init; } = StampAnchor.BottomRight;
    public double WidthFraction { get; init; } = 0.15;
    public double Margin { get; init; } = 16;
    public double Opacity { get; init; } = 0.7;

    public RecordingWatermarkSettings DeepClone() => this with { };

    public RecordingWatermarkSettings Normalize() => this with
    {
        Source = Enum.IsDefined(Source) ? Source : RecordingWatermarkSource.Image,
        Anchor = Enum.IsDefined(Anchor) ? Anchor : StampAnchor.BottomRight,
        Text = NormalizeText(Text),
        TextColor = IsValidColor(TextColor) ? TextColor.ToUpperInvariant() : "#FFFFFF",
        WidthFraction = Math.Clamp(Finite(WidthFraction, 0.15), 0.02, 1),
        Margin = Math.Clamp(Finite(Margin, 16), 0, StampLayout.MaxMargin),
        Opacity = Math.Clamp(Finite(Opacity, 0.7), 0.1, 1),
    };

    public static string NormalizeText(string? text)
    {
        string line = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 80 ? line : line[..80];
    }

    public static bool IsValidColor(string? value) => value is { Length: 7 } && value[0] == '#'
        && value.AsSpan(1).ToArray().All(Uri.IsHexDigit);

    public RectD Place(double width, double height, double sourceWidth, double sourceHeight)
    {
        RecordingWatermarkSettings settings = Normalize();
        if (settings.Source == RecordingWatermarkSource.Text && sourceWidth > 0)
        {
            double factor = Math.Max(1, width / sourceWidth);
            sourceWidth *= factor;
            sourceHeight *= factor;
        }
        return StampLayout.Place(settings.Anchor, settings.Margin, settings.Margin,
            settings.WidthFraction, width, height, sourceWidth, sourceHeight);
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}
