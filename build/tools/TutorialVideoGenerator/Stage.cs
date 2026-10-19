using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Primitives;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// The logical 960×540 frame every scene draws into. One logical unit is one device-independent
/// pixel of the product UI, so product windows render 1:1 at the default 960×540 output, which is
/// pixel for pixel what the tutorial window shows at 150% scaling. The bottom strip holds the
/// caption and the key that the step presses.
/// </summary>
internal static class Stage
{
    internal const double Width = 960, Height = 540, CaptionHeight = 52, SceneHeight = Height - CaptionHeight;
    internal const double TitleBarHeight = 32;

    /// <summary>Output pixels per logical unit.</summary>
    internal static double Scale { get; set; } = 1;
    internal static int PixelWidth => (int)Math.Round(Width * Scale);
    internal static int PixelHeight => (int)Math.Round(Height * Scale);

    internal static Rect SceneBounds => new(0, 0, Width, SceneHeight);

    /// <summary>Renders one opaque output frame.</summary>
    internal static BitmapSource Render(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Width, Height));
            draw(dc);
        }

        var bitmap = new RenderTargetBitmap(PixelWidth, PixelHeight, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Renders a logical-size picture (for example a synthetic app) at output resolution.</summary>
    internal static BitmapSource Picture(double width, double height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * Scale)), Math.Max(1, (int)Math.Ceiling(height * Scale)),
            96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    internal static FontFamily UiFont => (FontFamily)Application.Current.Resources["Font.Ui"];

    internal static FontFamily MonoFont => (FontFamily)Application.Current.Resources["Font.Mono"];

    internal static FormattedText Format(string text, double size, Brush brush, bool mono = false, bool bold = false, double maxWidth = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(mono ? MonoFont : UiFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, Scale);
        if (maxWidth > 0) formatted.MaxTextWidth = maxWidth;
        return formatted;
    }

    internal static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool mono = false, bool bold = false) =>
        dc.DrawText(Format(text, size, brush, mono, bold), new Point(x, y));

    /// <summary>A calm wallpaper; product windows and synthetic apps sit on it.</summary>
    internal static void DrawDesktop(DrawingContext dc)
    {
        var wallpaper = new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x45), Color.FromRgb(0x0E, 0x14, 0x22), new Point(0, 0), new Point(1, 1));
        dc.DrawRectangle(wallpaper, null, SceneBounds);
        var glow = new RadialGradientBrush(Color.FromArgb(0x55, 0x4C, 0x7D, 0xD8), Color.FromArgb(0, 0x4C, 0x7D, 0xD8))
        {
            Center = new Point(0.78, 0.18), GradientOrigin = new Point(0.78, 0.18), RadiusX = 0.6, RadiusY = 0.8,
        };
        dc.DrawRectangle(glow, null, SceneBounds);
    }

    /// <summary>
    /// Draws a window frame the way Windows 11 does for MyCapture: the caption takes the window's
    /// own base colour (ModernWindowChrome), with the title and the caption buttons. A dialog
    /// that cannot be resized (<paramref name="closeOnly"/>) has only the close button.
    /// </summary>
    internal static Rect DrawWindowFrame(DrawingContext dc, Rect client, string title, bool shadow = true, bool caption = true,
        bool closeOnly = false)
    {
        Rect outer = caption ? new Rect(client.X, client.Y - TitleBarHeight, client.Width, client.Height + TitleBarHeight) : client;
        if (shadow)
        {
            for (int i = 1; i <= 6; i++)
            {
                var ring = new Rect(outer.X - i, outer.Y - i + 3, outer.Width + 2 * i, outer.Height + 2 * i);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(34 - i * 5), 0, 0, 0)), 1.2), ring, 8 + i, 8 + i);
            }
        }

        dc.PushClip(new RectangleGeometry(outer, 8, 8));
        dc.DrawRectangle(Brushes.Black, null, outer);
        dc.DrawRectangle(Res("Surface.Base"), null, outer);
        if (caption)
        {
            Text(dc, title, outer.X + 14, outer.Y + 8, 12, Res("Text.Primary"));
            var glyphs = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            string[] buttons = ["", "", ""];
            for (int i = closeOnly ? buttons.Length - 1 : 0; i < buttons.Length; i++)
            {
                var glyph = new FormattedText(buttons[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(glyphs, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, Res("Text.Primary"), Scale);
                double cell = outer.Right - (buttons.Length - i) * 46;
                dc.DrawText(glyph, new Point(cell + (46 - glyph.Width) / 2, outer.Y + (TitleBarHeight - glyph.Height) / 2));
            }
        }

        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x88, 0x99)), 1), outer, 8, 8);
        return outer;
    }

    /// <summary>Draws a client-area snapshot inside its frame.</summary>
    internal static void DrawWindow(DrawingContext dc, BitmapSource client, Point clientOrigin, string title, double scale = 1, bool caption = true,
        bool closeOnly = false)
    {
        var bounds = new Rect(clientOrigin, new Size(client.PixelWidth / Scale * scale, client.PixelHeight / Scale * scale));
        DrawWindowFrame(dc, bounds, title, caption: caption, closeOnly: closeOnly);
        dc.PushClip(new RectangleGeometry(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), caption ? 0 : 8, caption ? 0 : 8));
        dc.DrawImage(client, bounds);
        dc.Pop();
    }

    internal static void DrawCaption(DrawingContext dc, string caption, string key)
    {
        var strip = new Rect(0, SceneHeight, Width, CaptionHeight);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)), null, strip);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)), 1), new Point(0, SceneHeight + 0.5), new Point(Width, SceneHeight + 0.5));
        double right = Width - 20;
        if (key.Length > 0)
        {
            double x = right;
            string[] parts = key.Split('+');
            var caps = new List<(FormattedText Text, double Width)>();
            foreach (string part in parts)
            {
                FormattedText label = Format(part, 15, Brushes.White, bold: true);
                caps.Add((label, Math.Max(32, label.Width + 20)));
            }

            double total = caps.Sum(cap => cap.Width) + (caps.Count - 1) * 18;
            x = right - total;
            right = x - 16;
            for (int i = 0; i < caps.Count; i++)
            {
                var cap = new Rect(x, SceneHeight + 10, caps[i].Width, 32);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x35, 0x44)), new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x8C)), 1), cap, 6, 6);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x2A)), 2), new Point(cap.X + 4, cap.Bottom - 1), new Point(cap.Right - 4, cap.Bottom - 1));
                dc.DrawText(caps[i].Text, new Point(cap.X + (cap.Width - caps[i].Text.Width) / 2, cap.Y + (cap.Height - caps[i].Text.Height) / 2 - 1));
                x += caps[i].Width;
                if (i < caps.Count - 1)
                {
                    FormattedText plus = Format("+", 15, new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB5)));
                    dc.DrawText(plus, new Point(x + (18 - plus.Width) / 2, SceneHeight + 15));
                    x += 18;
                }
            }
        }

        FormattedText text = Format(caption, 19, Brushes.White, maxWidth: right - 22);
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(text, new Point(22, SceneHeight + (CaptionHeight - text.Height) / 2));
    }

    private static readonly Geometry ArrowGeometry = Geometry.Parse("M0,0 L0,17.5 L4.2,13.6 L7.2,20.4 L10.2,19.1 L7.3,12.4 L12.8,12.4 Z");
    private static readonly Geometry BeamGeometry = Geometry.Parse("M-3.5,-9 L-1,-9 L0,-8 L1,-9 L3.5,-9 M0,-8 L0,8 M-3.5,9 L-1,9 L0,8 L1,9 L3.5,9");
    private static readonly Geometry HandGeometry = Geometry.Parse(
        "M5,0 C6.2,0 7,0.9 7,2 L7,8 L7.4,8 L7.4,6.6 C7.4,5.6 8.2,4.9 9.1,4.9 C10,4.9 10.7,5.6 10.8,6.5 L10.8,8.2 L11.2,8.2 L11.2,7.4 C11.2,6.5 12,5.8 12.9,5.8 C13.8,5.8 14.5,6.5 14.5,7.4 L14.5,8.8 L14.9,8.8 C14.9,7.9 15.6,7.3 16.4,7.3 C17.3,7.3 18,8 18,8.9 L18,14.5 C18,18.5 15.2,21.5 11.4,21.5 L9.9,21.5 C7.6,21.5 5.8,20.4 4.5,18.6 L0.6,13.1 C0,12.3 0.2,11.2 1,10.7 C1.8,10.2 2.8,10.4 3.3,11.1 L3,10.7 L3,2 C3,0.9 3.8,0 5,0 Z");

    internal enum PointerKind { Arrow, Hand, Beam, Cross, Move, SizeNwse, SizeNesw, SizeNs, SizeWe }

    private static readonly Geometry DoubleArrow = Geometry.Parse("M-9,0 L-5,-4 L-5,-1.6 L5,-1.6 L5,-4 L9,0 L5,4 L5,1.6 L-5,1.6 L-5,4 Z");

    /// <summary>The Windows pointer at <paramref name="point"/> (its hot spot).</summary>
    internal static void DrawPointer(DrawingContext dc, PointD point, PointerKind kind = PointerKind.Arrow)
    {
        dc.PushTransform(new TranslateTransform(point.X, point.Y));
        switch (kind)
        {
            case PointerKind.Beam:
                dc.DrawGeometry(null, new Pen(Brushes.White, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, BeamGeometry);
                dc.DrawGeometry(null, new Pen(Brushes.Black, 1.4), BeamGeometry);
                break;
            case PointerKind.Cross:
                var cross = Geometry.Parse("M-10,0 L-3,0 M3,0 L10,0 M0,-10 L0,-3 M0,3 L0,10");
                dc.DrawGeometry(null, new Pen(Brushes.White, 3.2), cross);
                dc.DrawGeometry(null, new Pen(Brushes.Black, 1.4), cross);
                break;
            case PointerKind.Hand:
                dc.PushTransform(new TranslateTransform(-5, 0));
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.1), HandGeometry);
                dc.Pop();
                break;
            case PointerKind.Move:
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), DoubleArrow);
                dc.PushTransform(new RotateTransform(90));
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), DoubleArrow);
                dc.Pop();
                break;
            case PointerKind.SizeNwse or PointerKind.SizeNesw or PointerKind.SizeNs or PointerKind.SizeWe:
                double angle = kind switch { PointerKind.SizeNwse => 45, PointerKind.SizeNesw => -45, PointerKind.SizeNs => 90, _ => 0 };
                dc.PushTransform(new RotateTransform(angle));
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), DoubleArrow);
                dc.Pop();
                break;
            default:
                dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1.1) { LineJoin = PenLineJoin.Round }, ArrowGeometry);
                break;
        }

        dc.Pop();
    }

    /// <summary>Draws a product cursor bitmap (for example an editor tool cursor) at its hot spot.</summary>
    internal static void DrawCursorBitmap(DrawingContext dc, BitmapSource cursor, PointD hotSpotLogical, PointD point)
    {
        double size = cursor.PixelWidth / (cursor.DpiX / 96.0);
        dc.DrawImage(cursor, new Rect(point.X - hotSpotLogical.X, point.Y - hotSpotLogical.Y, size, cursor.PixelHeight / (cursor.DpiY / 96.0)));
    }

    /// <summary>The expanding ring that marks a click.</summary>
    internal static void DrawClick(DrawingContext dc, PointD point, double progress)
    {
        if (progress <= 0 || progress >= 1) return;
        dc.PushOpacity(Math.Sin(progress * Math.PI) * 0.9);
        double radius = 7 + progress * 15;
        dc.DrawEllipse(null, new Pen(Brushes.Black, 5.5), new Point(point.X, point.Y), radius, radius);
        dc.DrawEllipse(null, new Pen(Brushes.White, 2.5), new Point(point.X, point.Y), radius, radius);
        dc.Pop();
    }

    /// <summary>Lets queued layout, render and async continuations run.</summary>
    internal static void Pump(int milliseconds = 30)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    internal static double Ease(double progress) => progress * progress * (3 - 2 * progress);

    internal static PointD Lerp(PointD from, PointD to, double progress) =>
        new(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress);
}
