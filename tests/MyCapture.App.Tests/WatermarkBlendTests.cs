using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Diagnostics;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// A watermark blended into the page darkens a light background and lightens a dark one through
/// the renderer every save, copy and export uses, and never covers the text under it.
/// </summary>
public sealed class WatermarkBlendTests
{
    private const byte Dark = 0x1E;
    private const byte LightText = 0xD4;

    // A crimson logo, uniform, so its whole shape is full-strength ink.
    private static readonly Color Crimson = Color.FromRgb(200, 30, 60);

    [Fact]
    public void TextStaysExactAndTheEmptyBackgroundShowsTheWatermarkOnLightAndDarkHalves() => StaTestHost.Run(() =>
    {
        BitmapSource page = HalfLightHalfDark();
        BitmapSource after = Flatten(page, ImageBlend.IntoPage, 0.3);
        BitmapSource before = Flatten(page, ImageBlend.Normal, 0.3);

        // Light half: black text untouched, paper darkened, outside unchanged.
        Assert.Equal((0, 0, 0), Rgb(after, 60, 61));
        Assert.InRange(Rgb(after, 60, 80).R, 176, 181);
        Assert.Equal(Rgb(after, 60, 80).R, Rgb(after, 60, 80).B);
        Assert.Equal((255, 255, 255), Rgb(after, 60, 10));

        // Dark half: light text not darkened, dark paper lightened.
        Assert.True(Rgb(after, 340, 61).R >= LightText);
        Assert.InRange(Rgb(after, 340, 80).R, 95, 100);
        Assert.Equal((Dark, Dark, Dark), Rgb(after, 340, 10));

        // Lying on top, the same logo covered the black text and darkened the light text.
        Assert.True(Rgb(before, 60, 61).R >= 50);
        Assert.True(Rgb(before, 340, 61).G < LightText - 50);
    });

    [Fact]
    public void AWatermarkAcrossTheBoundaryChangesInkSmoothlyAndStaysVisibleOnBothSides() => StaTestHost.Run(() =>
    {
        BitmapSource after = Flatten(HalfLightHalfDark(), ImageBlend.IntoPage, 0.3);
        for (int x = 150; x <= 250; x += 2)
        {
            int value = Rgb(after, x, 80).R;
            if (x < 200)
            {
                // White paper right up to the edge: darkened, never lightened.
                Assert.InRange(value, 170, 215);
            }
            else if (x >= 212)
            {
                // Dark paper a few pixels past the edge: lightened, never darkened.
                Assert.InRange(value, Dark + 40, 110);
            }
            else
            {
                // The narrow band where the page average crosses from light to dark: the ink turns
                // from dark to light, so dark paper may darken slightly before it lightens.
                Assert.InRange(value, 15, 110);
            }
        }

        // Text right beside the edge keeps its colour on both sides.
        Assert.Equal((0, 0, 0), Rgb(after, 178, 61));
        Assert.True(Rgb(after, 222, 61).R >= LightText);
    });

    [Fact]
    public void ARotatedWatermarkStillKeepsTextAndInksBothHalves() => StaTestHost.Run(() =>
    {
        BitmapSource after = Flatten(HalfLightHalfDark(), ImageBlend.IntoPage, 0.3, rotation: 10);
        Assert.Equal((0, 0, 0), Rgb(after, 100, 101));
        Assert.True(Rgb(after, 100, 90).R < 230);
        Assert.True(Rgb(after, 300, 90).R > Dark + 30);
    });

    [Fact]
    public void ThePlainInsertedImageStillLiesOnTopUnchanged() => StaTestHost.Run(() =>
    {
        BitmapSource before = Flatten(HalfLightHalfDark(), ImageBlend.Normal, 1.0);
        Assert.Equal((Crimson.R, Crimson.G, Crimson.B), Rgb(before, 60, 61));
        Assert.Equal((Crimson.R, Crimson.G, Crimson.B), Rgb(before, 340, 80));
    });

    /// <summary>
    /// Writes the before and after pictures and the comparison of the ways considered to the folder
    /// in <c>MYCAPTURE_WMBLEND_EVIDENCE</c>. Does nothing without it.
    /// </summary>
    [Fact]
    public void RenderBlendEvidence()
    {
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_WMBLEND_EVIDENCE");
        if (string.IsNullOrWhiteSpace(requested)) return;
        string root = DiagnosticOutputPaths.Create(requested);
        StaTestHost.Run(() => Evidence.Render(root));
    }

    /// <summary>
    /// Reports what a drag over a page half light, half dark costs: one overlay build for a
    /// 1024x1024 watermark across the edge (median of 25, each at a new place), and the first
    /// brightness map of a 3840x2160 capture with the managed bytes it allocated. Times are written
    /// to the console (and to <c>MYCAPTURE_WMBLEND_EVIDENCE</c>\perf.txt), never asserted.
    /// </summary>
    [Fact]
    public void MeasureTheOverlayCostOfADragAcrossLightAndDark() => StaTestHost.Run(() =>
    {
        const int width = 3840, height = 2160;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = ((y * width) + x) * 4;
                byte v = x < width / 2 ? Dark : (byte)255;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = v;
                pixels[o + 3] = 255;
            }
        }

        BitmapSource page = Frozen(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
        pixels = [];
        BitmapSource logo = DiscLogo(1024);

        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        PageBrightness brightness = WatermarkOverlay.BrightnessOf(page);
        double pageMs = clock.Elapsed.TotalMilliseconds;
        long pageBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(0, WatermarkInk.DarkShare(brightness.At(100, 100)));

        // The first build also reduces the picture to ink once.
        clock.Restart();
        BitmapSource first = WatermarkOverlay.For(logo, new Rect(1408, 568, 1024, 1024), 0, page);
        double firstMs = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(1024, first.PixelWidth);

        var times = new List<double>();
        for (int i = 1; i <= 25; i++)
        {
            clock.Restart();
            BitmapSource overlay = WatermarkOverlay.For(logo, new Rect(1408 + i, 568, 1024, 1024), 0, page);
            times.Add(clock.Elapsed.TotalMilliseconds);
            Assert.Equal(1024, overlay.PixelWidth);
        }

        times.Sort();
        string report = string.Create(CultureInfo.InvariantCulture,
            $"page 3840x2160 brightness: {pageMs:F1} ms, managed allocations {pageBytes / 1024.0 / 1024.0:F2} MB\n" +
            $"overlay 1024x1024 mixed: first {firstMs:F1} ms, median of 25 {times[times.Count / 2]:F2} ms, min {times[0]:F2} ms, max {times[^1]:F2} ms\n");
        Console.Write(report);
        string? requested = Environment.GetEnvironmentVariable("MYCAPTURE_WMBLEND_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            File.WriteAllText(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Create(requested), "perf.txt"), report);
        }
    });

    /// <summary>A navy disc with a white ring inside, on transparency.</summary>
    private static BitmapSource DiscLogo(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        double c = (size - 1) / 2.0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double r = Math.Sqrt(((x - c) * (x - c)) + ((y - c) * (y - c))) / c;
                if (r > 1) continue;
                bool ring = r is > 0.5 and < 0.6;
                int o = ((y * size) + x) * 4;
                pixels[o] = ring ? (byte)255 : (byte)0x80;
                pixels[o + 1] = ring ? (byte)255 : (byte)0x20;
                pixels[o + 2] = ring ? (byte)255 : (byte)0x10;
                pixels[o + 3] = 255;
            }
        }

        return Frozen(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4));
    }

    // ---- Fixture -----------------------------------------------------------------------

    /// <summary>400x200: white paper with black lines on the left, a dark editor with light lines on the right.</summary>
    private static BitmapSource HalfLightHalfDark()
    {
        const int width = 400, height = 200;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool light = x < width / 2;
                bool line = (y is >= 60 and < 64 or >= 100 and < 104) && x % 200 is >= 20 and < 180;
                byte value = light ? (line ? (byte)0 : (byte)255) : (line ? LightText : Dark);
                int o = ((y * width) + x) * 4;
                pixels[o] = pixels[o + 1] = pixels[o + 2] = value;
                pixels[o + 3] = 255;
            }
        }

        return Frozen(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
    }

    private static BitmapSource Flatten(BitmapSource page, ImageBlend blend, double opacity, double rotation = 0)
    {
        BitmapSource logo = SolidLogo(360, 120, Crimson);
        var document = AnnotationDocument.CreateFor(page.PixelWidth, page.PixelHeight);
        document.Items.Add(new ImageAnnotation
        {
            AssetFileName = "asset-01.png",
            SourceWidth = logo.PixelWidth,
            SourceHeight = logo.PixelHeight,
            Rect = new RectD(20, 40, 360, 120),
            RotationDegrees = rotation,
            Opacity = opacity,
            Blend = blend,
        });
        var renderer = new AnnotationRenderer(AnnotationImageStore.FromDecoded(
            new Dictionary<string, BitmapSource> { ["asset-01.png"] = logo }));
        return AnnotationFlattener.Flatten(page, document, renderer);
    }

    private static BitmapSource SolidLogo(int width, int height, Color color)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int o = 0; o < pixels.Length; o += 4)
        {
            pixels[o] = color.B;
            pixels[o + 1] = color.G;
            pixels[o + 2] = color.R;
            pixels[o + 3] = 255;
        }

        return Frozen(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4));
    }

    private static (int R, int G, int B) Rgb(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // ---- Evidence ----------------------------------------------------------------------

    private static class Evidence
    {
        private const double StampDefault = MyCapture.Core.Settings.StampSettings.DefaultPageOpacityPercent / 100.0;
        private const int Width = 1280;
        private const int Height = 720;

        private sealed record Candidate(string Label, Func<Inputs, double, BitmapSource> Render);

        /// <summary>The page and the watermark layer laid out on it, both straight BGRA.</summary>
        private sealed record Inputs(BitmapSource Page, byte[] PagePixels, byte[] Layer, PageBrightness Brightness, BitmapSource Logo, RectD Rect);

        internal static void Render(string root)
        {
            var scenes = new (string Name, string Label, BitmapSource Page)[]
            {
                ("doc", "흰 문서 · 검은 글자", Document()),
                ("code", "어두운 코드 편집기", Code()),
                ("mixed", "밝은 곳과 어두운 곳이 섞인 화면", Mixed()),
            };
            BitmapSource colourLogo = ColourLogo();
            BitmapSource whiteLogo = WhiteLogo();

            var candidates = new Candidate[]
            {
                new("A 위에 겹침 100% (지금)", (input, _) => Product(input, ImageBlend.Normal, 1.0)),
                new("B 위에 겹침 30%", (input, _) => Product(input, ImageBlend.Normal, 0.3)),
                new("C 곱하기(색 유지)", (input, a) => Exact(input, a, (_, _) => 1)),
                new("D 스크린(색 유지)", (input, a) => Exact(input, a, (_, _) => 0)),
                new("E 픽셀 밝기로 섞기", (input, a) => Exact(input, a, (i, _) => WatermarkInk.DarkShare(Luma(input.PagePixels, i)))),
                new("F 영역 평균으로 고르기", (input, a) => Exact(input, a, RegionMean(input))),
                new("G 주변 평균으로 섞기(색 유지)", (input, a) => Exact(input, a, (_, at) => WatermarkInk.DarkShare(input.Brightness.At(at.X + 0.5, at.Y + 0.5)))),
                new("H 주변 평균·단색 잉크(채택)", (input, a) => Product(input, ImageBlend.IntoPage, a)),
            };

            var sheetRows = new List<(string Label, BitmapSource[] Cells)>();
            foreach ((string name, string label, BitmapSource page) in scenes)
            {
                Inputs input = Prepare(page, colourLogo, Center(colourLogo));
                BitmapSource before = Product(input, ImageBlend.Normal, 1.0);
                BitmapSource after = Product(input, ImageBlend.IntoPage, StampDefault);
                Save(before, root, $"{name}-before.png");
                Save(after, root, $"{name}-after.png");

                Inputs corner = Prepare(page, colourLogo, Corner(colourLogo, page));
                Save(Product(corner, ImageBlend.Normal, 1.0), root, $"{name}-corner-before.png");
                Save(Product(corner, ImageBlend.IntoPage, StampDefault), root, $"{name}-corner-after.png");

                Inputs white = Prepare(page, whiteLogo, Center(whiteLogo));
                Save(Product(white, ImageBlend.Normal, 1.0), root, $"{name}-whitelogo-before.png");
                Save(Product(white, ImageBlend.IntoPage, StampDefault), root, $"{name}-whitelogo-after.png");

                sheetRows.Add((label, [.. candidates.Select(candidate => candidate.Render(input, 0.3))]));
            }

            Save(Sheet("워터마크 합성 방식 비교 (C~H 세기 30%)", [.. candidates.Select(c => c.Label)], sheetRows), root, "compare-methods.png");

            int[] strengths = [15, 20, 30, 50];
            var strengthRows = scenes.Select(scene =>
            {
                Inputs input = Prepare(scene.Page, colourLogo, Center(colourLogo));
                return (scene.Label, strengths.Select(percent => Product(input, ImageBlend.IntoPage, percent / 100.0)).ToArray());
            }).ToList();
            Save(Sheet("바탕에 스밈 세기 비교", [.. strengths.Select(p => $"{p}%")], strengthRows), root, "compare-strength.png");

            var whiteRows = scenes.Select(scene =>
            {
                Inputs input = Prepare(scene.Page, whiteLogo, Center(whiteLogo));
                return (scene.Label, new[]
                {
                    Product(input, ImageBlend.Normal, 1.0),
                    Exact(input, 0.3, (_, at) => WatermarkInk.DarkShare(input.Brightness.At(at.X + 0.5, at.Y + 0.5))),
                    Product(input, ImageBlend.IntoPage, 0.3),
                });
            }).ToList();
            Save(Sheet("흰 글자 로고(투명 바탕)", ["위에 겹침 100%", "주변 평균·색 유지", "주변 평균·단색 잉크(채택)"], whiteRows), root, "compare-white-logo.png");
        }

        // ---- Candidates ----

        private static BitmapSource Product(Inputs input, ImageBlend blend, double opacity)
        {
            var document = AnnotationDocument.CreateFor(input.Page.PixelWidth, input.Page.PixelHeight);
            document.Items.Add(new ImageAnnotation
            {
                AssetFileName = "asset-01.png",
                SourceWidth = input.Logo.PixelWidth,
                SourceHeight = input.Logo.PixelHeight,
                Rect = input.Rect,
                Opacity = opacity,
                Blend = blend,
            });
            var renderer = new AnnotationRenderer(AnnotationImageStore.FromDecoded(
                new Dictionary<string, BitmapSource> { ["asset-01.png"] = input.Logo }));
            return AnnotationFlattener.Flatten(input.Page, document, renderer);
        }

        /// <summary>
        /// Colour-keeping multiply and screen, mixed per pixel by <paramref name="darkShare"/>,
        /// computed exactly against the page (what a renderer with blend modes would draw).
        /// </summary>
        private static BitmapSource Exact(Inputs input, double strength, Func<int, Point, double> darkShare)
        {
            byte[] page = input.PagePixels;
            byte[] layer = input.Layer;
            byte[] result = (byte[])page.Clone();
            int width = input.Page.PixelWidth;
            for (int i = 0; i < page.Length; i += 4)
            {
                double a = layer[i + 3] / 255.0 * strength;
                if (a <= 0) continue;
                int pixel = i / 4;
                double share = darkShare(i, new Point(pixel % width, pixel / width));
                for (int c = 0; c < 3; c++)
                {
                    double b = page[i + c] / 255.0;
                    double s = layer[i + c] / 255.0;
                    double multiply = b * (1 - (a * (1 - s)));
                    double screen = 1 - ((1 - b) * (1 - (a * s)));
                    result[i + c] = (byte)Math.Round(((share * multiply) + ((1 - share) * screen)) * 255);
                }
            }

            return Frozen(BitmapSource.Create(width, input.Page.PixelHeight, 96, 96, PixelFormats.Bgra32, null, result, width * 4));
        }

        private static Func<int, Point, double> RegionMean(Inputs input)
        {
            int width = input.Page.PixelWidth;
            double total = 0;
            int count = 0;
            for (int y = (int)input.Rect.Top; y < (int)input.Rect.Bottom; y++)
            {
                for (int x = (int)input.Rect.Left; x < (int)input.Rect.Right; x++)
                {
                    total += Luma(input.PagePixels, ((y * width) + x) * 4);
                    count++;
                }
            }

            double share = WatermarkInk.DarkShare(total / Math.Max(1, count)) >= 0.5 ? 1 : 0;
            return (_, _) => share;
        }

        private static double Luma(byte[] bgra, int offset) => WatermarkInk.Luma(bgra[offset + 2], bgra[offset + 1], bgra[offset]);

        private static Inputs Prepare(BitmapSource page, BitmapSource logo, RectD rect)
        {
            byte[] pagePixels = Pixels(page);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawImage(logo, new Rect(rect.X, rect.Y, rect.Width, rect.Height));
            }

            var target = new RenderTargetBitmap(page.PixelWidth, page.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            byte[] layer = Pixels(target);
            for (int i = 0; i < layer.Length; i += 4)
            {
                int a = layer[i + 3];
                if (a is 0 or 255) continue;
                for (int c = 0; c < 3; c++) layer[i + c] = (byte)Math.Min(255, layer[i + c] * 255 / a);
            }

            return new Inputs(page, pagePixels, layer, PageBrightness.From(pagePixels, page.PixelWidth, page.PixelHeight, page.PixelWidth * 4), logo, rect);
        }

        private static RectD Center(BitmapSource logo) =>
            StampLayout.Place(StampAnchor.Center, 0, 0, 0.5, Width, Height, logo.PixelWidth, logo.PixelHeight);

        private static RectD Corner(BitmapSource logo, BitmapSource page) =>
            StampLayout.Place(StampAnchor.BottomRight, 16, 16, 0.2, page.PixelWidth, page.PixelHeight, logo.PixelWidth, logo.PixelHeight);

        private static byte[] Pixels(BitmapSource source)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
            converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
            return pixels;
        }

        // ---- Scenes ----

        private static readonly string[] Prose =
        [
            "분기별 판매 현황 보고서",
            "1. 개요: 이번 분기 매출은 전년 같은 기간보다 12.4% 늘었습니다.",
            "2. 지역별로는 수도권이 41%, 영남권이 23%를 차지했습니다.",
            "3. 신제품 출시 이후 재구매율은 18%에서 27%로 올랐습니다.",
            "The quick brown fox jumps over the lazy dog. 0123456789",
            "표 1. 월별 매출(단위: 백만 원)   1월 412   2월 398   3월 455",
            "4. 다음 분기에는 물류 비용 절감과 온라인 채널 확대에 집중합니다.",
            "5. 세부 실행 계획은 부록 A와 부록 B에 정리했습니다.",
            "작성: 영업기획팀 · 검토: 재무팀 · 배포: 전 부서",
            "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod.",
            "6. 위험 요인: 환율 변동, 원자재 가격 상승, 경쟁사 가격 인하.",
            "7. 대응: 장기 계약 비중 확대, 대체 공급처 확보, 가격 정책 재검토.",
        ];

        private static readonly (string Text, Color Color)[][] CodeLines =
        [
            [("using", Rgb(0x56, 0x9C, 0xD6)), (" System.Windows.Media;", Rgb(0xD4, 0xD4, 0xD4))],
            [],
            [("internal static class", Rgb(0x56, 0x9C, 0xD6)), (" WatermarkOverlay", Rgb(0x4E, 0xC9, 0xB0))],
            [("{", Rgb(0xD4, 0xD4, 0xD4))],
            [("    // The overlay is an ordinary premultiplied picture.", Rgb(0x6A, 0x99, 0x55))],
            [("    private const double", Rgb(0x56, 0x9C, 0xD6)), (" ProbeSpacing = ", Rgb(0xD4, 0xD4, 0xD4)), ("8", Rgb(0xB5, 0xCE, 0xA8)), (";", Rgb(0xD4, 0xD4, 0xD4))],
            [],
            [("    internal static", Rgb(0x56, 0x9C, 0xD6)), (" BitmapSource ", Rgb(0x4E, 0xC9, 0xB0)), ("For", Rgb(0xDC, 0xDC, 0xAA)), ("(BitmapSource picture, Rect box)", Rgb(0xD4, 0xD4, 0xD4))],
            [("    {", Rgb(0xD4, 0xD4, 0xD4))],
            [("        string", Rgb(0x56, 0x9C, 0xD6)), (" name = ", Rgb(0xD4, 0xD4, 0xD4)), ("\"watermark.png\"", Rgb(0xCE, 0x91, 0x78)), (";", Rgb(0xD4, 0xD4, 0xD4))],
            [("        if", Rgb(0xC5, 0x86, 0xC0)), (" (box.IsEmpty) ", Rgb(0xD4, 0xD4, 0xD4)), ("return", Rgb(0xC5, 0x86, 0xC0)), (" ink.Dark;", Rgb(0xD4, 0xD4, 0xD4))],
            [("        for", Rgb(0xC5, 0x86, 0xC0)), (" (int r = 0; r < rows; r++) total += Sample(r);", Rgb(0xD4, 0xD4, 0xD4))],
            [("        return", Rgb(0xC5, 0x86, 0xC0)), (" Frozen(BitmapSource.Create(width, height));", Rgb(0xD4, 0xD4, 0xD4))],
            [("    }", Rgb(0xD4, 0xD4, 0xD4))],
            [("}", Rgb(0xD4, 0xD4, 0xD4))],
        ];

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static BitmapSource Document() => Draw(dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            for (int i = 0; i < Prose.Length; i++)
            {
                double size = i == 0 ? 34 : 22;
                dc.DrawText(Text(Prose[i], size, Colors.Black, bold: i == 0), new Point(64, 48 + (i * 52)));
            }
        });

        private static BitmapSource Code() => Draw(dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(Rgb(0x1E, 0x1E, 0x1E)), null, new Rect(0, 0, Width, Height));
            dc.DrawRectangle(new SolidColorBrush(Rgb(0x25, 0x25, 0x26)), null, new Rect(0, 0, 56, Height));
            for (int line = 0; line < CodeLines.Length; line++)
            {
                double y = 32 + (line * 42);
                dc.DrawText(Text((line + 1).ToString(CultureInfo.InvariantCulture), 20, Rgb(0x85, 0x85, 0x85), mono: true), new Point(16, y));
                double x = 80;
                foreach ((string text, Color color) in CodeLines[line])
                {
                    FormattedText run = Text(text, 22, color, mono: true);
                    dc.DrawText(run, new Point(x, y));
                    x += run.WidthIncludingTrailingWhitespace;
                }
            }
        });

        private static BitmapSource Mixed() => Draw(dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, Width, Height));
            // A dark sidebar and title bar beside a light document.
            dc.DrawRectangle(new SolidColorBrush(Rgb(0x20, 0x22, 0x27)), null, new Rect(0, 0, 440, Height));
            dc.DrawRectangle(new SolidColorBrush(Rgb(0x2B, 0x2D, 0x31)), null, new Rect(440, 0, Width - 440, 64));
            dc.DrawText(Text("문서 제목 - 편집 중", 20, Rgb(0xE6, 0xE6, 0xE6)), new Point(470, 20));
            string[] side = ["받은 편지함 (12)", "보낸 편지함", "임시 보관함", "보관함", "스팸", "휴지통", "팀 공유 폴더", "프로젝트 A", "프로젝트 B", "개인 메모"];
            for (int i = 0; i < side.Length; i++)
                dc.DrawText(Text(side[i], 22, Rgb(0xD0, 0xD3, 0xD8)), new Point(40, 96 + (i * 56)));
            for (int i = 0; i < Prose.Length - 2; i++)
                dc.DrawText(Text(Prose[i + 1], 20, Rgb(0x11, 0x11, 0x11)), new Point(470, 96 + (i * 56)));
        });

        private static BitmapSource ColourLogo() => Draw(640, 200, dc =>
        {
            dc.DrawEllipse(new SolidColorBrush(Rgb(0x25, 0x63, 0xEB)), null, new Point(100, 100), 90, 90);
            FormattedText m = Text("M", 120, Colors.White, bold: true);
            dc.DrawText(m, new Point(100 - (m.Width / 2), 100 - (m.Height / 2)));
            dc.DrawText(Text("mimiloaf", 104, Rgb(0x1F, 0x29, 0x37), bold: true), new Point(206, 28));
        });

        private static BitmapSource WhiteLogo() => Draw(640, 200, dc =>
        {
            dc.DrawText(Text("© mimiloaf", 96, Colors.White, bold: true), new Point(16, 40));
        });

        private static BitmapSource Draw(Action<DrawingContext> paint) => Draw(Width, Height, paint);

        private static BitmapSource Draw(int width, int height, Action<DrawingContext> paint)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen()) paint(dc);
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            // Straight BGRA, as a decoded capture or logo file is.
            return Frozen(new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0));
        }

        private static FormattedText Text(string text, double size, Color color, bool bold = false, bool mono = false)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var face = new Typeface(new FontFamily(mono ? "Consolas" : "Malgun Gothic"), FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            return new FormattedText(text, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight, face, size, brush, 1);
        }

        // ---- Sheets ----

        private static BitmapSource Sheet(string title, string[] columns, List<(string Label, BitmapSource[] Cells)> rows)
        {
            const double cellWidth = 448, cellHeight = 252, gap = 12, header = 64, columnHeader = 40, rowLabel = 30;
            double width = gap + (columns.Length * (cellWidth + gap));
            double height = header + columnHeader + (rows.Count * (rowLabel + cellHeight + gap));
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Rgb(0xF3, 0xF4, 0xF6)), null, new Rect(0, 0, width, height));
                dc.DrawText(Text(title, 26, Colors.Black, bold: true), new Point(gap, 18));
                for (int c = 0; c < columns.Length; c++)
                    dc.DrawText(Text(columns[c], 18, Colors.Black, bold: true), new Point(gap + (c * (cellWidth + gap)), header + 8));
                for (int r = 0; r < rows.Count; r++)
                {
                    double top = header + columnHeader + (r * (rowLabel + cellHeight + gap));
                    dc.DrawText(Text(rows[r].Label, 16, Rgb(0x37, 0x41, 0x51)), new Point(gap, top + 4));
                    for (int c = 0; c < rows[r].Cells.Length; c++)
                        dc.DrawImage(rows[r].Cells[c], new Rect(gap + (c * (cellWidth + gap)), top + rowLabel, cellWidth, cellHeight));
                }
            }

            var target = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }

        private static void Save(BitmapSource bitmap, string root, string name) =>
            ImageCodec.SavePng(bitmap, DiagnosticOutputPaths.Child(root, name));
    }
}
