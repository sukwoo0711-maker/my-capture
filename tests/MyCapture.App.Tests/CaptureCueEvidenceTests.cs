using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Display;
using Xunit;
using static MyCapture.App.Tests.WindowDetectOverlayTests;

namespace MyCapture.App.Tests;

/// <summary>
/// Renders the screenshot selector (mode bar, status, start guide, window-detect cues and the text and
/// colour modes) on a synthetic desktop in all six palettes. Set MYCAPTURE_CAPTURE_CUE_EVIDENCE to a
/// directory to keep the PNGs for visual review.
/// </summary>
public sealed class CaptureCueEvidenceTests
{
    internal const int Width = 1280;
    internal const int Height = 720;
    private const uint Explorer = 7972;

    internal static readonly WindowCandidate Browser = new(new IntPtr(0x2002), new RectD(560, 200, 600, 400),
        "Docs - Browser", ProcessId: 5150, ClassName: "Chrome_WidgetWin_1");
    internal static readonly WindowCandidate Notes = new(new IntPtr(0x2001), new RectD(120, 90, 620, 420),
        "report.txt - Notepad", ProcessId: 4242, ClassName: "Notepad");
    internal static readonly WindowCandidate Taskbar = new(new IntPtr(0x1013A), new RectD(0, 672, Width, 48),
        ProcessId: Explorer, ClassName: "Shell_TrayWnd");
    internal static readonly WindowCandidate Shell = new(new IntPtr(0x1017E), new RectD(0, 0, Width, Height),
        "Program Manager", ProcessId: Explorer, ClassName: "Progman");
    internal static readonly IReadOnlyList<MonitorInfo> Monitors =
        [new(@"\\.\DISPLAY1", new RectD(0, 0, Width, Height), new RectD(0, 0, Width, 672), 96, IsPrimary: true)];

    /// <summary>Every state the screenshot selector shows, as the evidence set names them.</summary>
    internal static readonly string[] States =
        ["first-open", "window-hover", "drag", "desktop-hover", "text-mode", "color-mode"];

    [Fact]
    public void EverySelectorStateRendersInEveryPalette() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        FrozenFrame frame = Desktop();
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            foreach (string state in States)
            {
                CaptureOverlayView view = EvidenceView(frame, theme, state);
                try
                {
                    RenderTargetBitmap rendered = Render(view);
                    Assert.Equal(Width, rendered.PixelWidth);
                    Assert.Equal(Height, rendered.PixelHeight);
                    Assert.NotNull(view.ModeBarBoundsForTest);
                    Assert.Equal(state == "first-open", view.ShowStartGuide);
                    SaveEvidence(rendered, $"{AppThemeNames.ToSetting(theme)}-{state}.png");
                }
                finally { view.ReleaseResources(); }
            }
        }
    });

    /// <summary>
    /// The screenshot selector as the coordinator configures it (mode bar on), in one state. The
    /// start guide shows only in "first-open", where the pointer rests over the browser window.
    /// </summary>
    internal static CaptureOverlayView EvidenceView(FrozenFrame frame, AppTheme theme, string state)
    {
        PointD pointer = state switch
        {
            "first-open" => new PointD(900, 520),
            "desktop-hover" => new PointD(1000, 120),
            "drag" => new PointD(300, 250),
            "color-mode" => new PointD(700, 300),
            _ => new PointD(300, 300),
        };
        var view = new CaptureOverlayView(frame, showMagnifier: false)
        {
            ReadCursor = () => pointer,
            DetectWindows = true,
            ReadMonitors = () => Monitors,
            WindowCandidates = [Browser, Notes, Taskbar, Shell],
            CopyColorText = _ => Task.FromResult(true),
            TextCaptureAvailable = true,
            ShowModeBar = true,
            // The app runs the record switch and the ⋯ menu, so the coordinator always offers them.
            OffersOtherActions = true,
            ShowStartGuide = state == "first-open",
        };
        UsePalette(view, CaptureGuidePalette.Create(theme, customColor: null, systemHighContrast: false));
        view.InitializePointer();
        if (state == "text-mode") view.SelectMode(CaptureSelectorMode.Text);
        if (state == "color-mode") view.SelectMode(CaptureSelectorMode.Color);
        if (state == "drag")
        {
            Invoke(view, "OnMouseLeftButtonDown", Button(UIElement.MouseLeftButtonDownEvent));
            pointer = new PointD(520, 430);
            Invoke(view, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseMoveEvent });
        }
        return view;
    }

    /// <summary>Gives one view a palette without touching the process-wide theme other tests read.</summary>
    internal static void UsePalette(CaptureOverlayView view, IReadOnlyDictionary<string, Brush> palette)
    {
        typeof(CaptureOverlayView).GetField("_guidePalette", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, palette);
        typeof(CaptureOverlayView).GetMethod("ApplyGuideBrushes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, null);
    }

    internal static RenderTargetBitmap Render(CaptureOverlayView view, double scale = 1)
    {
        VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
        if (Math.Abs(view.GuidePixelSizeX - (1 / scale)) > 0.000001)
            typeof(CaptureOverlayView).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, [new DpiScale(1 / view.GuidePixelSizeX, 1 / view.GuidePixelSizeY), new DpiScale(scale, scale)]);
        view.InvalidateVisual();
        view.Measure(new Size(Width / scale, Height / scale));
        view.Arrange(new Rect(0, 0, Width / scale, Height / scale));
        view.UpdateLayout();
        var image = new RenderTargetBitmap(Width, Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(view);
        return image;
    }

    internal static void SaveEvidence(BitmapSource image, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MYCAPTURE_CAPTURE_CUE_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    /// <summary>A 1280x720 desktop: wallpaper, a notes window, a browser in front of it and a taskbar.</summary>
    internal static FrozenFrame Desktop()
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var wallpaper = new LinearGradientBrush(Color.FromRgb(0x2F, 0x55, 0x7A), Color.FromRgb(0x7A, 0x9C, 0xB8), 35);
            dc.DrawRectangle(wallpaper, null, new Rect(0, 0, Width, Height));
            DrawWindow(dc, Notes.ScreenBounds, Color.FromRgb(0xF7, 0xF8, 0xFA), Color.FromRgb(0xE4, 0xE8, 0xEE),
                Color.FromRgb(0x2B, 0x31, 0x3B), "report.txt - Notepad");
            DrawWindow(dc, Browser.ScreenBounds, Color.FromRgb(0x1E, 0x24, 0x31), Color.FromRgb(0x2C, 0x34, 0x45),
                Color.FromRgb(0xE6, 0xEA, 0xF2), "Docs - Browser");
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x26)), null, ToRect(Taskbar.ScreenBounds));
            for (int i = 0; i < 6; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x66)), null,
                    new Rect(520 + i * 40, 680, 30, 30), 6, 6);
        }

        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return new FrozenFrame(converted, new RectD(0, 0, Width, Height), null, 0);

        static void DrawWindow(DrawingContext dc, RectD bounds, Color body, Color title, Color text, string caption)
        {
            Rect rect = ToRect(bounds);
            dc.DrawRectangle(new SolidColorBrush(body), new Pen(new SolidColorBrush(title), 1), rect);
            dc.DrawRectangle(new SolidColorBrush(title), null, new Rect(rect.X, rect.Y, rect.Width, 32));
            var typeface = new Typeface("Segoe UI");
            var brush = new SolidColorBrush(text);
            dc.DrawText(new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 13, brush, 1),
                new Point(rect.X + 12, rect.Y + 8));
            for (int line = 0; line < 9; line++)
            {
                double width = (line % 3) switch { 0 => 0.82, 1 => 0.64, _ => 0.73 } * (rect.Width - 48);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x55, text.R, text.G, text.B)), null,
                    new Rect(rect.X + 24, rect.Y + 60 + line * 34, width, 10), 4, 4);
            }
        }
    }

    private static Rect ToRect(RectD rect) => new(rect.Left, rect.Top, rect.Width, rect.Height);
}
