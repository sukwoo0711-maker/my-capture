using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>Opt-in saved-OCR replay; exact cached translation units avoid repeated model execution.</summary>
public sealed class PinTranslationOverlayCorpusReplayTests(ITestOutputHelper output)
{
    internal const string ReplayVariable = "MYCAPTURE_PIN_OVERLAY_REPLAY";

    [PinOverlayCoverageReplayFact]
    public Task AllRecordedDirectionsMeetCoverageGate() => new PinTranslationOverlayCorpusTests(output)
        .RunCorpusAsync(Environment.GetEnvironmentVariable(ReplayVariable)!);

    [PinOverlayReplayFact]
    public async Task DenseParagraphsAndSubtitleRenderFromRecordedCorpus()
    {
        string directory = Environment.GetEnvironmentVariable(ReplayVariable)!;
        string corpus = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!;
        IReadOnlyList<(string Name, BitmapSource Image)> pdf = await PinTranslationOverlayCorpusTests.LoadPicturesAsync(
            Path.Combine(corpus, "pdf-en-arxiv-attention.pdf"), directory);
        StaTestHost.Run(() =>
        {
            var failures = new List<string>();
            foreach ((string name, double targetX, double targetY) in new[]
            {
                ("pdf-en-arxiv-attention-p1", 400d, 1150d),
                ("web-en-wiki-screenshot", 264d, 595d),
                ("var-small60-en-wiki", 158.333, 355d),
                ("ui-en-subtitle", 190d, 428.667),
            })
            {
                OcrResult recognized = PinTranslationOverlayCorpusTests.ReadRecognized(Path.Combine(directory, name + "-ocr.json"));
                BitmapSource source;
                if (name.StartsWith("pdf-", StringComparison.Ordinal)) source = pdf.Single(page => page.Name == name).Image;
                else
                {
                    var loaded = new BitmapImage();
                    loaded.BeginInit(); loaded.CacheOption = BitmapCacheOption.OnLoad;
                    string savedSource = Path.Combine(directory, name + "-source.png");
                    loaded.UriSource = new Uri(File.Exists(savedSource) ? savedSource : Path.Combine(corpus, name + ".png"));
                    loaded.EndInit(); loaded.Freeze();
                    source = loaded;
                }
                PinOverlayBlock[] blocks = PinTranslationOverlay.BuildBlocks(recognized, source).ToArray();
                int selected = Enumerable.Range(0, blocks.Length).MinBy(index =>
                    Math.Abs(blocks[index].Bounds.X - targetX) + Math.Abs(blocks[index].Bounds.Y - targetY));
                PinOverlayBlock block = blocks[selected];
                string document = File.ReadAllText(Path.Combine(directory, name + ".en-ko-text.txt"));
                string[] paragraphs = document.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.None);
                string[][] decisions = File.ReadLines(Path.Combine(directory, "decisions.tsv")).Skip(1)
                    .Select(line => line.Split('\t')).Where(fields => fields[0] == name && fields[1] == "en-ko" && fields[2] == "after").ToArray();
                Assert.Equal(decisions.Length, paragraphs.Length);
                int savedOrdinal = Enumerable.Range(0, decisions.Length).MinBy(index =>
                    Math.Abs(double.Parse(decisions[index][4], CultureInfo.InvariantCulture) - targetX)
                        + Math.Abs(double.Parse(decisions[index][5], CultureInfo.InvariantCulture) - targetY));
                string translated = paragraphs[savedOrdinal];
                PinOverlayTranslation[] items = blocks.Select(item => new PinOverlayTranslation(item,
                    ReferenceEquals(item, block) ? translated : null)).ToArray();
                string reason = "unobserved";
                PinOverlayFit? chosen = null;
                PinOverlayComposition result = PinTranslationOverlay.Compose(source, items,
                    observeFit: (item, fit, _) => { if (ReferenceEquals(item, block)) chosen = fit; },
                    observeDecision: (item, value) => { if (ReferenceEquals(item, block)) reason = value; });
                output.WriteLine($"{name}: {reason}; bounds={block.Bounds}; lines={block.Lines.Count}; fitted={chosen?.Bounds}; em={chosen?.FontSize:F3}");
                Diagnose(source, blocks, block, translated);
                if (result.RenderedBlocks != 1) failures.Add(name + ": " + reason);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        });
    }

    private void Diagnose(BitmapSource source, PinOverlayBlock[] blocks, PinOverlayBlock block, string translated)
    {
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[stride * height]; bitmap.CopyPixels(pixels, stride, 0);
        Color[] colors = block.Lines.Select(line => PinTranslationOverlay.EstimateBackground(pixels, width, height, stride, line.Bounds).Color).ToArray();
        static byte Median(IEnumerable<byte> values)
        {
            byte[] ordered = values.Order().ToArray();
            return (byte)((ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2);
        }
        Color background = Color.FromRgb(Median(colors.Select(color => color.R)), Median(colors.Select(color => color.G)), Median(colors.Select(color => color.B)));
        Type type = typeof(PinTranslationOverlay).GetNestedType("BackgroundSurface", BindingFlags.NonPublic)!;
        object surface = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            [pixels, width, height, stride, block, background], CultureInfo.InvariantCulture)!;
        var canOccupy = type.GetMethod("CanOccupy", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<OcrRect, bool>>(surface);
        var matches = type.GetMethod("Matches", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Func<int, bool>>(surface);
        var ink = (OcrRect[])type.GetField("sourceInk", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        bool gradient = (bool)type.GetField("gradient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
        TextAlignment alignment = PinTranslationOverlay.InferAlignment(block.Lines);
        OcrRect limits = (OcrRect)type.GetMethod("HorizontalLimits", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, [alignment])!;
        double em = PinTranslationOverlay.EstimateFontSizes(blocks)[Array.IndexOf(blocks, block)];
        double pitch = PinTranslationOverlay.SourceLinePitch(block.Lines);
        Size Measure(string text, double size, double unused)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                ClipboardTextRenderer.TextTypeface, size, Brushes.Black, 1) { LineHeight = pitch * size / em };
            return new(formatted.WidthIncludingTrailingWhitespace, formatted.BuildGeometry(new Point()).Bounds.Height);
        }
        OcrRect[] obstacles = blocks.Where(item => !ReferenceEquals(item, block)).SelectMany(item => item.Lines.Select(line => line.Bounds))
            .Concat(blocks.SelectMany(item => item.ProtectedRegions)).ToArray();
        PinOverlayFit? natural = PinTranslationOverlay.Fit(translated, block.Bounds, em, width, height, Measure,
            obstacles, block.Lines.Count, pitch, alignment: alignment);
        output.WriteLine($"  em={em:F3}, pitch={pitch:F3}, alignment={alignment}, bg={background}, gradient={gradient}, limits={limits}, unconstrained={natural?.Bounds}");
        OcrRect candidate = natural?.Bounds ?? block.Bounds;
        output.WriteLine($"  source surface={canOccupy(block.Bounds)}, fitted surface={canOccupy(candidate)}");
        if (!canOccupy(block.Bounds)) candidate = block.Bounds;
        output.WriteLine($"  recovered components={((OcrRect[])type.GetField("extraInk", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!).Length}");
        foreach (OcrRect obstacle in obstacles.Where(other => other.X < candidate.Right + 2 && other.Right > candidate.X - 2
            && other.Y < candidate.Bottom + 2 && other.Bottom > candidate.Y - 2).Take(3))
            output.WriteLine($"  obstacle={obstacle}");
        for (int y = (int)candidate.Y; y < Math.Ceiling(candidate.Bottom); y++)
            for (int x = (int)candidate.X; x < Math.Ceiling(candidate.Right); x++)
            {
                if (ink.Any(owned => x >= owned.X && x < owned.Right && y >= owned.Y && y < owned.Bottom)) continue;
                int index = y * stride + x * 4;
                if (matches(index)) continue;
                output.WriteLine($"  first pixel outside source ink: ({x},{y}) RGB({pixels[index + 2]},{pixels[index + 1]},{pixels[index]})");
                return;
            }
    }
}

public sealed class PinOverlayCoverageReplayFactAttribute : FactAttribute
{
    public PinOverlayCoverageReplayFactAttribute()
    {
        string[] variables = [PinTranslationOverlayCorpusReplayTests.ReplayVariable, PinTranslationOverlayCorpusTests.CorpusVariable,
            PinOverlayCorpusCoverage.ManifestVariable, PinTranslationOverlayCorpusBaseline.AssemblyVariable,
            PinTranslationOverlayEvidenceTests.OutputVariable, PinTranslationOverlayEvidenceTests.ModelsVariable];
        if (variables.Any(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))))
            Skip = "Set the replay, corpus, c1 coverage manifest, baseline assembly, evidence and local model folders.";
    }
}

public sealed class PinOverlayReplayFactAttribute : FactAttribute
{
    public PinOverlayReplayFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusReplayTests.ReplayVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)))
            Skip = "Set MYCAPTURE_PIN_OVERLAY_REPLAY and MYCAPTURE_PIN_OVERLAY_CORPUS to replay retained layout evidence.";
    }
}
