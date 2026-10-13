using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Core.Primitives;

namespace MyCapture.TutorialGenerator.Scenes;

/// <summary>
/// A Windows 11 taskbar along the bottom of the scene, with the notification area holding the
/// product's own tray icon (Assets/tray-idle.ico, the file the app loads for its tray icon).
/// </summary>
internal static class Taskbar
{
    internal const double Height = 40;

    internal static double Top => Stage.SceneHeight - Height;

    /// <summary>The bottom of the work area: windows and cards stay above the taskbar.</summary>
    internal static double WorkBottom => Top;

    /// <summary>Centre of the MyCapture icon in the notification area.</summary>
    internal static PointD TrayIcon => new(Stage.Width - 132, Top + Height / 2);

    private static BitmapSource? _icon;

    private static BitmapSource Icon => _icon ??= LoadIcon();

    private static BitmapSource LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray-idle.ico");
        Uri source = File.Exists(path) ? new Uri(path) : new Uri("pack://application:,,,/MyCapture;component/Assets/app.ico");
        BitmapDecoder decoder = BitmapDecoder.Create(source, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        // The frame closest to the 24 pixels a 16-DIP tray icon uses at 150%.
        BitmapFrame frame = decoder.Frames.OrderBy(candidate => Math.Abs(candidate.PixelWidth - 24 * Stage.Scale)).First();
        frame.Freeze();
        return frame;
    }

    internal static void Draw(DrawingContext dc, bool korean, DateTimeOffset now, bool trayPressed = false)
    {
        var bar = new Rect(0, Top, Stage.Width, Height);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x20, 0x2A)), null, bar);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), null, new Rect(0, Top, Stage.Width, 1));

        // Start, search and two pinned apps in the centre.
        double x = Stage.Width / 2 - 70;
        DrawStart(dc, new Point(x, Top + Height / 2));
        Color[] apps = [Color.FromRgb(0x2F, 0x6F, 0xEB), Color.FromRgb(0xF2, 0xC9, 0x4C), Color.FromRgb(0x16, 0xA0, 0x6E)];
        for (int i = 0; i < apps.Length; i++)
            dc.DrawRoundedRectangle(new SolidColorBrush(apps[i]), null, new Rect(x + 34 + i * 38, Top + 11, 18, 18), 4, 4);

        // Notification area: overflow chevron, MyCapture, network, volume, then the clock.
        PointD tray = TrayIcon;
        if (trayPressed)
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), null, new Rect(tray.X - 14, Top + 5, 28, 30), 4, 4);
        Glyph(dc, "", tray.X - 30, 10);
        dc.DrawImage(Icon, new Rect(tray.X - 8, tray.Y - 8, 16, 16));
        Glyph(dc, "", tray.X + 28, 14);
        Glyph(dc, "", tray.X + 52, 14);
        CultureInfo culture = korean ? CultureInfo.GetCultureInfo("ko-KR") : CultureInfo.GetCultureInfo("en-US");
        string time = now.ToString(korean ? "tt h:mm" : "h:mm tt", culture);
        string date = now.ToString(korean ? "yyyy-MM-dd" : "M/d/yyyy", culture);
        FormattedText clock = Stage.Format(time, 11, Brushes.White);
        FormattedText day = Stage.Format(date, 11, Brushes.White);
        double right = Stage.Width - 12;
        dc.DrawText(clock, new Point(right - clock.Width, Top + 5));
        dc.DrawText(day, new Point(right - day.Width, Top + 21));
    }

    private static void DrawStart(DrawingContext dc, Point center)
    {
        var blue = new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        for (int row = 0; row < 2; row++)
        for (int column = 0; column < 2; column++)
            dc.DrawRectangle(blue, null, new Rect(center.X - 8 + column * 8.5, center.Y - 8 + row * 8.5, 7.5, 7.5));
    }

    private static void Glyph(DrawingContext dc, string glyph, double centerX, double size)
    {
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Brushes.White, Stage.Scale);
        dc.DrawText(text, new Point(centerX - text.Width / 2, Top + (Height - text.Height) / 2));
    }
}
