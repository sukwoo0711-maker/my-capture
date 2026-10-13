using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyCapture.TutorialGenerator;

/// <summary>
/// Synthetic content the tutorials capture, edit and paste: a checklist page, a note app and a
/// terminal. Only these stand-ins for other people's apps are drawn by hand; every MyCapture
/// surface in a clip is the product's own UI.
/// </summary>
internal static class Fixtures
{
    internal const string SampleEmail = "demo@example.test";
    internal const string TerminalCommand = "npm run build";
    internal const string TerminalResult = "Build completed";

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x24, 0x2E));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x73));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xDD, 0xE2, 0xEA));
    private static readonly Brush Paper = new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFE));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xEB));

    /// <summary>The captured page the image-editor clips edit (pixels, 1 pixel = 1 DIP).</summary>
    internal static BitmapSource Checklist(bool korean, int width = 600, int height = 300)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Paper, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF8)), null, new Rect(0, 0, width, 38));
            dc.DrawRectangle(Line, null, new Rect(0, 38, width, 1));
            dc.DrawEllipse(Accent, null, new Point(24, 19), 8, 8);
            Text(dc, korean ? "릴리스 준비" : "Release prep", 40, 10, 14, Ink, true);
            Text(dc, korean ? "프로젝트 체크리스트" : "Project checklist", 28, 56, 22, Ink, true);
            string[] items = korean ? ["요구 사항 확인", "변경 사항 검토", "배포 일정 공유"] : ["Review requirements", "Check the changes", "Share the release date"];
            for (int i = 0; i < items.Length; i++)
            {
                double y = 104 + i * 36;
                var box = new Rect(30, y + 2, 16, 16);
                dc.DrawRoundedRectangle(i == 0 ? Accent : Paper, new Pen(i == 0 ? Accent : Muted, 1.4), box, 3, 3);
                if (i == 0) dc.DrawGeometry(null, new Pen(Brushes.White, 2), Geometry.Parse("M33,112 L37,116 L43,108"));
                Text(dc, items[i], 58, y, 17, Ink);
            }

            Text(dc, korean ? "담당자" : "Owner", 30, 216, 14, Muted);
            Text(dc, SampleEmail, 92, 214, 17, Ink);
            dc.DrawRectangle(Line, null, new Rect(28, 248, width - 260, 1));
            Text(dc, korean ? "마감 10월 17일" : "Due October 17", 30, 260, 14, Muted);

            var card = new Rect(width - 200, 64, 172, 196);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xEA, 0xF1, 0xFF)), null, card, 10, 10);
            Text(dc, korean ? "진행률" : "Progress", card.X + 16, card.Y + 14, 14, Muted);
            Text(dc, "68%", card.X + 16, card.Y + 34, 30, Ink, true);
            double[] bars = [0.45, 0.62, 0.38, 0.8, 0.68];
            for (int i = 0; i < bars.Length; i++)
            {
                double barHeight = 90 * bars[i];
                dc.DrawRoundedRectangle(i == bars.Length - 1 ? Accent : new SolidColorBrush(Color.FromRgb(0x9D, 0xB9, 0xF2)), null,
                    new Rect(card.X + 18 + i * 29, card.Bottom - 18 - barHeight, 18, barHeight), 3, 3);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Image-pixel rectangles of notable parts of <see cref="Checklist"/>.</summary>
    internal static class ChecklistParts
    {
        internal static readonly Rect Heading = new(24, 50, 250, 36);
        internal static readonly Rect SecondItem = new(24, 136, 210, 30);
        internal static readonly Rect ItemsBlock = new(22, 98, 300, 112);
        internal static readonly Rect Email = new(88, 210, 190, 30);
        internal static readonly Rect Card = new(400, 64, 172, 196);
    }

    internal static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool bold = false, bool mono = false)
    {
        var typeface = new Typeface(mono ? Stage.MonoFont : Stage.UiFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, 1), new Point(x, y));
    }
}
