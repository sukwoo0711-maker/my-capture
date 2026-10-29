using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Read-only opt-in comparison of saved OCR and saved accepted paragraphs.</summary>
public sealed class PinTranslationOverlayR7DiagnosticTests(ITestOutputHelper output)
{
    internal const string EvidenceVariable = "MYCAPTURE_PIN_OVERLAY_R7_DIAGNOSTIC";
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    [PinOverlayR7DiagnosticFact]
    public void SmallSourceParagraphComparesSavedOcrAndTranslationPairs() => StaTestHost.Run(() =>
    {
        string root = Environment.GetEnvironmentVariable(EvidenceVariable)!;
        const string name = "var-small60-en-wiki";
        var paragraphs = new Dictionary<string, string>();
        foreach (string version in new[] { "c3", "c4" })
        {
            string directory = Path.Combine(root, version);
            string[][] decisions = File.ReadLines(Path.Combine(directory, "decisions.tsv")).Skip(1)
                .Select(line => line.Split('\t', 9)).Where(fields => fields[0] == name
                    && fields[1] == "en-ko" && fields[2] == "after").ToArray();
            int selected = Array.FindIndex(decisions, fields => fields[8].StartsWith("A screenshot (also known", StringComparison.Ordinal));
            Assert.True(selected >= 0);
            string[] document = File.ReadAllText(Path.Combine(directory, name + ".en-ko-text.txt"))
                .ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.None);
            Assert.Equal(decisions.Length, document.Length);
            paragraphs.Add(version, document[selected]);
            output.WriteLine($"translation={version}: {document[selected]}");
        }
        foreach (string version in new[] { "c3", "c4" })
        {
            string directory = Path.Combine(root, version);
            OcrResult recognized = PinTranslationOverlayCorpusTests.ReadRecognized(Path.Combine(directory, name + "-ocr.json"));
            using FileStream stream = File.OpenRead(Path.Combine(directory, name + "-source.png"));
            BitmapFrame source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source.Freeze();
            PinOverlayBlock[] blocks = PinTranslationOverlay.BuildBlocks(recognized, source).ToArray();
            PinOverlayBlock block = Assert.Single(blocks, item => item.Text.StartsWith("A screenshot (also known", StringComparison.Ordinal));
            foreach (var (translationVersion, translation) in paragraphs)
            {
                string reason = "unobserved";
                PinOverlayFit? chosen = null;
                PinTranslationOverlay.Compose(source, blocks.Select(item => new PinOverlayTranslation(item,
                    ReferenceEquals(item, block) ? translation : null)).ToArray(),
                    observeFit: (item, fit, _) => { if (ReferenceEquals(item, block)) chosen = fit; },
                    observeDecision: (item, decision) => { if (ReferenceEquals(item, block)) reason = decision; });
                output.WriteLine($"OCR={version}, translation={translationVersion}: {reason}, fit={chosen}");
                Diagnose(source, blocks, block, translation);
                Assert.NotEqual("unobserved", reason);
            }
        }
    });

    private void Diagnose(BitmapSource source, PinOverlayBlock[] blocks, PinOverlayBlock block, string translation)
    {
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        Type overlay = typeof(PinTranslationOverlay);
        double ink = (double)overlay.GetMethod("SourceInkHeight", Static)!.Invoke(null, [block, pixels, width, height, stride])!;
        double estimate = PinTranslationOverlay.EstimateFontSizes(blocks)[Array.IndexOf(blocks, block)];
        double cap = ink > 0 ? Math.Min(estimate, ink * 1.1) : estimate;
        double pitch = PinTranslationOverlay.SourceLinePitch(block.Lines), scale = pitch / cap;
        Color[] colors = block.Lines.Select(line => PinTranslationOverlay.EstimateBackground(pixels, width, height, stride, line.Bounds).Color).ToArray();
        Color background = (Color)overlay.GetMethod("MedianColor", Static)!.Invoke(null, [colors])!;
        OcrRect[] protectedRegions = blocks.SelectMany(item => item.ProtectedRegions).ToArray();
        OcrRect[] obstacles = blocks.Where(item => !ReferenceEquals(item, block))
            .SelectMany(item => item.Lines.Select(line => line.Bounds)).Where(region => !block.ProtectedRegions.Contains(region))
            .Concat(protectedRegions.Where(region => !block.ProtectedRegions.Contains(region))).ToArray();
        Type type = overlay.GetNestedType("BackgroundSurface", BindingFlags.NonPublic)!;
        object surface = Activator.CreateInstance(type, Instance, null,
            [pixels, width, height, stride, block, background, obstacles], CultureInfo.InvariantCulture)!;
        var canOccupy = type.GetMethod("CanOccupy", Instance)!.CreateDelegate<Func<OcrRect, bool>>(surface);
        var exclusions = (IReadOnlyList<OcrRect>)type.GetProperty("TextExclusions", Instance)!.GetValue(surface)!;
        TextAlignment alignment = PinTranslationOverlay.InferAlignment(block.Lines);
        var limits = (OcrRect)type.GetMethod("HorizontalLimits", Instance)!.Invoke(surface, [alignment])!;
        output.WriteLine($"  bounds={block.Bounds}; lines={block.Lines.Count}; estimate={estimate:R}; ink={ink:R}; cap={cap:R}; pitch={pitch:R}; scale={scale:R}; limits={limits}; sourceSurface={canOccupy(block.Bounds)}");
        output.WriteLine($"  exclusions={string.Join("; ", exclusions)}");
        FormattedText Format(string text, double size) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            ClipboardTextRenderer.TextTypeface, size, Brushes.Black, 1) { LineHeight = scale * size, Trimming = TextTrimming.None };
        Size Final(string text, double size)
        {
            FormattedText formatted = Format(text, size);
            return new(formatted.WidthIncludingTrailingWhitespace, Math.Max(0, formatted.Extent - 2 * Math.Min(1, size / 7)));
        }
        Size Placement(string text, double size) => new(Final(text, size).Width, Format(text, size).BuildGeometry(new Point()).Bounds.Height);
        Type metricsType = overlay.GetNestedType("CompositionMetrics", BindingFlags.NonPublic)!;
        object metrics = Activator.CreateInstance(metricsType, true)!;
        var measureWidth = metricsType.GetMethod("MeasureWidth", Instance)!.CreateDelegate<Func<string, double, double, double>>(metrics);
        foreach (string mode in new[] { "surface+exclusions", "exclusions-only", "surface-only", "plain" })
        {
            bool useSurface = mode.Contains("surface", StringComparison.Ordinal), useExclusions = mode.Contains("exclusions", StringComparison.Ordinal);
            PinOverlayFit? fit = PinTranslationOverlay.Fit(translation, block.Bounds, cap, width, height, PinTranslationOverlay.Measure,
                obstacles, block.Lines.Count, pitch, Final, measureWidth, Placement, alignment, limits,
                useSurface ? canOccupy : null, textExclusions: useExclusions ? exclusions : null,
                minimumFontSize: Math.Min(PinTranslationOverlay.MinimumFontSize, cap));
            output.WriteLine($"  {mode}: fit={fit}; offsets={string.Join(",", fit?.LineOffsets ?? [])}");
        }
    }
}

public sealed class PinOverlayR7DiagnosticFactAttribute : FactAttribute
{
    public PinOverlayR7DiagnosticFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayR7DiagnosticTests.EvidenceVariable)))
            Skip = "Set MYCAPTURE_PIN_OVERLAY_R7_DIAGNOSTIC to the retained evidence folder containing c3 and c4.";
    }
}
