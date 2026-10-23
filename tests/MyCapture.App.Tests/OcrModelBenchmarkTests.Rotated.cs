using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class OcrModelBenchmarkTests
{
    /// <summary>
    /// Four synthetic document originals, each paired with exact clockwise quarter-turns.
    /// Ground truth keeps human reading order from the upright document at every angle.
    /// </summary>
    private static List<Sample> CreateRotatedCorpus(string output)
    {
        string images = Path.Combine(output, "images");
        Directory.CreateDirectory(images);
        string koreanFont = SelectFont("Malgun Gothic", "Pretendard", "Segoe UI");
        string englishFont = SelectFont("Segoe UI", "Arial");
        RotatedCorpusScene[] scenes =
        [
            new("korean-scan", "rotated-korean", "ko-KR", false, 600, 300,
                [new("작업 확인서", 28, 24, 22, true),
                 new("접수 번호 7318", 28, 82, 18),
                 new("검수 완료 2026-10-24", 28, 126, 18),
                 new("담당 부서 자료 관리", 28, 170, 18)]),
            new("korean-photo", "rotated-korean", "ko-KR", true, 560, 220,
                [new("수령 확인 4821", 45, 78, 24)]),
            new("english-scan", "rotated-english", "en-US", false, 640, 290,
                [new("INSPECTION RECORD", 32, 23, 22, true),
                 new("Batch number ZX-731", 32, 81, 18),
                 new("Approved on 24 October 2026", 32, 125, 18),
                 new("Storage room 4", 32, 169, 18)]),
            new("english-photo", "rotated-english", "en-US", true, 580, 220,
                [new("Parcel receipt Q7-582", 46, 78, 24)]),
        ];
        var samples = new List<Sample>();
        var layouts = new List<RotatedCorpusImageLayout>();

        foreach (RotatedCorpusScene scene in scenes)
        {
            string font = scene.Language == "ko-KR" ? koreanFont : englishFont;
            var uprightBounds = new List<Rect>();
            var visual = new DrawingVisual();
            TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            TextOptions.SetTextHintingMode(visual, TextHintingMode.Fixed);
            using (DrawingContext dc = visual.RenderOpen())
            {
                var paper = new Rect(0, 0, scene.Width, scene.Height);
                if (scene.PhotoStyle)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(203, 205, 207)), null, paper);
                    paper = new Rect(20, 18, scene.Width - 40, scene.Height - 40);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(175, 177, 179)), null,
                        new Rect(paper.X + 5, paper.Y + 6, paper.Width, paper.Height));
                    var illumination = new LinearGradientBrush(Color.FromRgb(255, 253, 246),
                        Color.FromRgb(229, 224, 211), new Point(0, 0), new Point(1, 1));
                    dc.DrawRectangle(illumination, null, paper);
                }
                else
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(252, 252, 250)), null, paper);
                }

                Brush foreground = new SolidColorBrush(Color.FromRgb(31, 33, 35));
                for (int index = 0; index < scene.Lines.Length; index++)
                {
                    RotatedCorpusLine line = scene.Lines[index];
                    var typeface = new Typeface(new FontFamily(font), FontStyles.Normal,
                        line.Bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
                    var formatted = new FormattedText(line.Text, CultureInfo.GetCultureInfo(scene.Language),
                        FlowDirection.LeftToRight, typeface, line.FontSize, foreground,
                        null, TextFormattingMode.Display, 1.0);
                    var origin = new Point(line.X, line.Y);
                    Rect bounds = formatted.BuildGeometry(origin).Bounds;
                    Assert.True(origin.X + formatted.WidthIncludingTrailingWhitespace <= paper.Right - 14,
                        $"Rotated corpus text must fit the paper: {scene.Name}, {line.Text}");
                    Assert.True(!bounds.IsEmpty && bounds.Left >= paper.Left + 4 && bounds.Top >= paper.Top + 4
                        && bounds.Right <= paper.Right - 4 && bounds.Bottom <= paper.Bottom - 4,
                        $"Rotated corpus glyphs must not clip: {scene.Name}, {line.Text}, {bounds}");
                    if (uprightBounds.Count > 0)
                        Assert.True(bounds.Top > uprightBounds[^1].Bottom,
                            $"Rotated corpus lines must not overlap: {scene.Name}");
                    dc.DrawText(formatted, origin);
                    uprightBounds.Add(bounds);
                }
            }

            var upright = new RenderTargetBitmap(scene.Width, scene.Height, 96, 96, PixelFormats.Pbgra32);
            upright.Render(visual);
            upright.Freeze();
            string uprightId = $"{samples.Count + 1:D3}-rotated-{scene.Name}-cw000";
            string[] truthLines = scene.Lines.Select(line => line.Text).ToArray();

            foreach (int clockwiseDegrees in new[] { 0, 90, 180, 270 })
            {
                BitmapSource bitmap = RotateCorpusPixels(upright, clockwiseDegrees);
                string id = $"{samples.Count + 1:D3}-rotated-{scene.Name}-cw{clockwiseDegrees:D3}";
                string imagePath = Path.Combine(images, id + ".png");
                ImageCodec.SavePng(bitmap, imagePath);
                using var stream = File.OpenRead(imagePath);
                var truth = new TruthImage
                {
                    Id = id, Category = scene.Category, SourceCategory = scene.Category,
                    Text = string.Join('\n', truthLines), Lines = truthLines,
                    Font = font, FontSize = scene.Lines[0].FontSize, Scale = 1, Theme = "light",
                    Width = bitmap.PixelWidth, Height = bitmap.PixelHeight,
                    Png = "images/" + id + ".png", PngSha256 = Convert.ToHexString(SHA256.HashData(stream)),
                };
                RotatedCorpusLineLayout[] lineLayouts = scene.Lines.Select((line, index) =>
                {
                    Rect bounds = RotateCorpusBounds(uprightBounds[index], scene.Width, scene.Height, clockwiseDegrees);
                    Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= bitmap.PixelWidth
                        && bounds.Bottom <= bitmap.PixelHeight, $"Rotated truth bounds must fit: {id}, {line.Text}");
                    return new RotatedCorpusLineLayout(index, line.Text, line.FontSize, line.Bold,
                        RotatedCorpusBounds.From(uprightBounds[index]), RotatedCorpusBounds.From(bounds));
                }).ToArray();
                samples.Add(new Sample(truth, bitmap));
                layouts.Add(new RotatedCorpusImageLayout(id, scene.Name, uprightId, scene.Language,
                    scene.PhotoStyle ? "photo-style-paper" : "flat-scan", clockwiseDegrees,
                    scene.Width, scene.Height, lineLayouts));
            }
        }

        Assert.Equal(16, samples.Count);
        Assert.Equal(16, samples.Select(sample => sample.Truth.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, layouts.Count(layout => layout.IsSingleLine));
        Assert.Equal(8, layouts.Count(layout => !layout.IsSingleLine));
        Assert.All(layouts.GroupBy(layout => layout.Scene), group =>
            Assert.Equal(new[] { 0, 90, 180, 270 }, group.Select(layout => layout.ClockwiseDegrees)));
        WriteJson(Path.Combine(output, "truth.json"), new
        {
            schemaVersion = 2,
            corpus = "rotated",
            rendering = "WPF Display formatting and grayscale antialiasing at 96 DPI; quarter-turn variants use exact BGRA pixel permutations with no interpolation",
            fonts = new { koreanFont, englishFont },
            coverage = new
            {
                originals = 4, koreanImages = 8, englishImages = 8,
                singleLineImages = 8, multilineImages = 8,
                clockwiseDegrees = new[] { 0, 90, 180, 270 },
                notes = new[]
                {
                    "Each four-image group shares one rendered original and identical canonical upright reading-order truth.",
                    "One original per language is a single line, isolating orientation without a line-order confound. The other originals each contain four lines.",
                    "Photo-style paper uses fixed illumination and shadow; flat scans use a uniform background. There is no perspective skew or JPEG degradation.",
                    "A rotation changes the whole document, including line positions. Per-line recognition and document reading-order errors can therefore be distinguished using layouts[].lines.",
                    "SourceCategory stays rotated-korean or rotated-english for all orientations; group uprightId and clockwiseDegrees identify matched controls.",
                },
            },
            images = samples.Select(sample => sample.Truth).ToArray(),
            layouts,
        });
        return samples;
    }

    private static BitmapSource RotateCorpusPixels(BitmapSource source, int clockwiseDegrees)
    {
        Assert.Contains(clockwiseDegrees, new[] { 0, 90, 180, 270 });
        Assert.Equal(PixelFormats.Pbgra32, source.Format);
        if (clockwiseDegrees == 0) return source;
        int sourceWidth = source.PixelWidth, sourceHeight = source.PixelHeight;
        int width = clockwiseDegrees is 90 or 270 ? sourceHeight : sourceWidth;
        int height = clockwiseDegrees is 90 or 270 ? sourceWidth : sourceHeight;
        int sourceStride = checked(sourceWidth * 4), stride = checked(width * 4);
        byte[] original = new byte[checked(sourceStride * sourceHeight)];
        byte[] rotated = new byte[checked(stride * height)];
        source.CopyPixels(original, sourceStride, 0);
        for (int y = 0; y < sourceHeight; y++)
        for (int x = 0; x < sourceWidth; x++)
        {
            (int targetX, int targetY) = clockwiseDegrees switch
            {
                90 => (sourceHeight - 1 - y, x),
                180 => (sourceWidth - 1 - x, sourceHeight - 1 - y),
                270 => (y, sourceWidth - 1 - x),
                _ => throw new InvalidOperationException("Unsupported corpus rotation."),
            };
            Buffer.BlockCopy(original, y * sourceStride + x * 4, rotated, targetY * stride + targetX * 4, 4);
        }
        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, rotated, stride);
        result.Freeze();
        return result;
    }

    private static Rect RotateCorpusBounds(Rect source, int width, int height, int clockwiseDegrees) => clockwiseDegrees switch
    {
        0 => source,
        90 => new Rect(height - source.Bottom, source.Left, source.Height, source.Width),
        180 => new Rect(width - source.Right, height - source.Bottom, source.Width, source.Height),
        270 => new Rect(source.Top, width - source.Right, source.Height, source.Width),
        _ => throw new InvalidOperationException("Unsupported corpus bounds rotation."),
    };

    private sealed record RotatedCorpusScene(string Name, string Category, string Language, bool PhotoStyle,
        int Width, int Height, RotatedCorpusLine[] Lines);
    private sealed record RotatedCorpusLine(string Text, double X, double Y, double FontSize, bool Bold = false);
    private sealed record RotatedCorpusImageLayout(string Id, string Scene, string UprightId, string Language,
        string Appearance, int ClockwiseDegrees, int UprightWidth, int UprightHeight, RotatedCorpusLineLayout[] Lines)
    {
        public int TruthLineCount => Lines.Length;
        public bool IsSingleLine => Lines.Length == 1;
    }
    private sealed record RotatedCorpusLineLayout(int ReadingOrderIndex, string Text, double FontSizePixels, bool Bold,
        RotatedCorpusBounds UprightBounds, RotatedCorpusBounds Bounds);
    private sealed record RotatedCorpusBounds(double X, double Y, double Width, double Height)
    {
        public static RotatedCorpusBounds From(Rect bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
