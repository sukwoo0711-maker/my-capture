using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class FontCatalogTests
{
    [Theory]
    [InlineData("ko-KR", "맑은 고딕", "설치된 다른 글꼴…")]
    [InlineData("en-US", "Malgun Gothic", "Other installed fonts…")]
    public void RecommendationsHaveExactOrderAndOnlyInstalledOptionalFamilies(string language, string first, string more)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var absent = FontCatalog.BuildRecommended(culture, []);
        Assert.Equal(new[] { "Malgun Gothic", "Pretendard", "D2Coding", "Maple Mono", "JetBrains Mono", "Consolas", "Segoe UI", "" },
            absent.Select(font => font.Value));
        Assert.Equal(first, absent[0].Label);
        Assert.Equal(more, absent[^1].Label);
        Assert.True(absent[^1].IsMore);
        Assert.All(absent, font => Assert.Equal(font.Label, font.ToString()));
        FontChoice[] optional = [new("Cascadia Mono", "Cascadia Mono"), new("Fira Code", "Fira Code"),
            new("Cascadia Code", "Cascadia Code"), new("Source Code Pro", "Source Code Pro"),
            new("NanumGothicCoding", language == "ko-KR" ? "나눔고딕코딩" : "NanumGothicCoding"), new("Noto Sans KR", "Noto Sans KR")];
        var present = FontCatalog.BuildRecommended(culture, optional);
        Assert.Equal(new[] { "Malgun Gothic", "Pretendard", "D2Coding", "Maple Mono", "JetBrains Mono", "Cascadia Mono", "Consolas",
            "Segoe UI", "Fira Code", "Cascadia Code", "Source Code Pro", "NanumGothicCoding", "Noto Sans KR", "" }, present.Select(font => font.Value));
        Assert.Equal(optional[4].Label, present[11].Label);
        Assert.Equal("maple mono", FontCatalog.ChoicesFor("maple mono", present)[3].Value);
        Assert.Equal("Private Font", FontCatalog.ChoicesFor("Private Font", present)[0].Value);
        Assert.True(FontCatalog.ChoicesFor("Private Font", present)[^1].IsMore);
    }

    [Theory]
    [InlineData("Maple Mono", false)]
    [InlineData("Maple Mono", true)]
    [InlineData("JetBrains Mono", false)]
    [InlineData("JetBrains Mono", true)]
    [InlineData("D2Coding", false)]
    [InlineData("D2Coding", true)]
    [InlineData("Pretendard", false)]
    [InlineData("Pretendard", true)]
    public void BundledTextUsesBundledFileGlyphsAndKoreanFallsBackToD2Coding(string family, bool bold) => StaTestHost.Run(() =>
    {
        var text = new TextAnnotation { Text = "ABC 한글", FontFamily = family, FontSize = 32, Bold = bold, AutoSize = true };
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open()) context.DrawText(TextAnnotationLayout.Format(text, 1), new Point());
        GlyphRun[] runs = Glyphs(drawing).ToArray();
        Assert.NotEmpty(runs);
        Assert.All(runs, run =>
        {
            Assert.Equal(Uri.UriSchemeFile, run.GlyphTypeface.FontUri.Scheme);
            Assert.StartsWith(new Uri(FontCatalog.BundledFontRoot).AbsoluteUri, run.GlyphTypeface.FontUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(bold ? FontWeights.Bold : FontWeights.Normal, run.GlyphTypeface.Weight);
            Assert.DoesNotContain((ushort)0, run.GlyphIndices);
        });
        GlyphRun latin = runs.First(run => run.Characters.Contains('A'));
        GlyphRun korean = runs.First(run => run.Characters.Contains('한'));
        Assert.Contains(family, latin.GlyphTypeface.FamilyNames.Values);
        Assert.Contains(family is "Maple Mono" or "JetBrains Mono" ? "D2Coding" : family, korean.GlyphTypeface.FamilyNames.Values);
        Assert.Same(FontCatalog.Resolve(family), FontCatalog.Resolve(family.ToUpperInvariant()));
    });

    [Theory]
    [InlineData("Maple Mono")]
    [InlineData("JetBrains Mono")]
    [InlineData("D2Coding")]
    [InlineData("Pretendard")]
    public void BundledAnnotationsFlattenAndExportWithTheSameGlyphs(string family) => StaTestHost.Run(() =>
    {
        var document = AnnotationDocument.CreateFor(400, 120);
        var text = new TextAnnotation { Text = "ABC 한글", FontFamily = family, FontSize = 32, AutoSize = true,
            Rect = new RectD(10, 10, 360, 80), HasOutline = false, Foreground = ColorRgba.Black, Background = ColorRgba.Transparent };
        document.Add(text);
        var image = BitmapSource.Create(400, 120, 96, 96, PixelFormats.Pbgra32, null, new byte[400 * 120 * 4], 400 * 4);
        var renderer = new AnnotationRenderer(new AnnotationImageStore());
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) renderer.Render(context, document, 1, blurSource: image);
        Assert.Contains(Glyphs(drawing), run => run.GlyphTypeface.FamilyNames.Values.Contains(family));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawDrawing(drawing);
        var expected = new RenderTargetBitmap(400, 120, 96, 96, PixelFormats.Pbgra32);
        expected.Render(visual);
        BitmapSource flat = AnnotationFlattener.Flatten(image, document, renderer);
        Assert.Equal(Pixels(expected), Pixels(flat));
        Assert.Contains(Pixels(flat), value => value != 0);
        BitmapSource export = ImageExportRenderer.Render(flat, new ImageExportOptions { Margin = 4 }, Colors.Red);
        var interior = new CroppedBitmap(export, new Int32Rect(4, 4, 400, 120));
        Assert.Equal(Pixels(flat), Pixels(interior));
    });

    [Fact]
    public void ExpandedFontsKeepEveryInstalledAndBundledFamily() => StaTestHost.Run(() =>
    {
        foreach (string language in new[] { "ko-KR", "en-US" })
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(language);
            var all = FontCatalog.AvailableFonts(culture);
            Assert.All(FontCatalog.InstalledFonts(culture), font => Assert.Contains(font, all));
            foreach (string family in new[] { "Maple Mono", "JetBrains Mono", "D2Coding", "Pretendard" })
                Assert.Single(all, font => string.Equals(font.Value, family, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(all.Count, all.Select(font => font.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(all.OrderBy(font => font.Label, StringComparer.Create(culture, true)), all);
        }
    });

    [Fact]
    public void ThirdPartyFontLicensesAreCopiedBesideTheApplication()
    {
        foreach (string folder in new[] { "MapleMono", "JetBrainsMono", "D2Coding", "Pretendard" })
        {
            string license = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", folder, "OFL.txt"));
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", license, StringComparison.Ordinal);
            Assert.Contains("Copyright", license, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<GlyphRun> Glyphs(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (GlyphRun run in Glyphs(child)) yield return run;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }
}
