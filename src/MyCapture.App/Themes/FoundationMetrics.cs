using System.Windows;

namespace MyCapture.App.Themes;

/// <summary>Shared 6.0 dimensions for code-built controls and Tokens.xaml.</summary>
public static class FoundationMetrics
{
    public const double SpaceXs = 4;
    public const double SpaceSm = 8;
    public const double SpaceMd = 12;
    public const double SpaceLg = 16;
    public const double SpaceXl = 24;
    public const double SpaceXxl = 32;

    public const double ControlHeight = 36;
    public const double CompactControlHeight = 32;
    public const double ControlRadius = 6;
    public const double FloatingRadius = 8;
    public const double ContentRadius = 12;

    public static CornerRadius ControlCornerRadius => new(ControlRadius);
    public static CornerRadius FloatingCornerRadius => new(FloatingRadius);
    public static CornerRadius ContentCornerRadius => new(ContentRadius);
}
