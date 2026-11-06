using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.Ocr;
using MyCapture.Platform.Imaging;
using Xunit;
using Xunit.Abstractions;
using Data = MyCapture.App.Tests.OcrCommercialCorpusData;
using WinRtSoftwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in #226 diagnostics for the installed Windows OCR engine. "fixture" records every stage
/// (source, preparation, bridge, raw recognition, table extraction) of the isolated table at
/// several scales and interpolations; "explore" and "render" vary pixel operations and text
/// rendering at one scale; "generate"/"generate-clutter" write the stratified and cluttered
/// corpora; "sweep" applies preparation configurations to a corpus and to the isolated table;
/// "estimate" compares the line-height estimators without recognition. Results and rerun
/// settings: docs/maintenance/ocr-226-windows-table-diagnosis.md.
/// </summary>
public sealed class Ocr226PreprocessingDiagnosticTests(ITestOutputHelper output)
{
    internal const string RunVariable = "MYCAPTURE_OCR226_RUN";
    internal const string OutputVariable = "MYCAPTURE_OCR226_OUTPUT";
    internal const string ConfigsVariable = "MYCAPTURE_OCR226_CONFIGS";
    internal const string SplitVariable = "MYCAPTURE_OCR226_SPLIT";
    internal const string CorpusVariable = "MYCAPTURE_OCR226_CORPUS";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Ocr226DiagnosticFact]
    [Trait("Category", "Ocr226Diagnostic")]
    public void Observe()
    {
        string mode = Required(RunVariable);
        string root = Data.NewOutput(Required(OutputVariable));
        StaTestHost.Run(() =>
        {
            if (mode == "fixture") Fixture(root);
            else if (mode == "sweep") Sweep(root);
            else if (mode == "explore") Explore(root);
            else if (mode == "render") RenderVariants(root);
            else if (mode == "generate") Ocr226StrataCorpus.Generate(root);
            else if (mode == "generate-clutter") Ocr226ClutterCorpus.Generate(root);
            else if (mode == "estimate") Estimates(root);
            else throw new InvalidDataException(RunVariable + " must be fixture, explore, render, generate, generate-clutter, estimate or sweep.");
        });
        output.WriteLine("Preserved #226 diagnostics: " + root);
    }

    /// <summary>Same table values and layout, varying only text rendering and bitmap DPI.</summary>
    private static void RenderVariants(string root)
    {
        var recognizer = new WindowsOcrRecognizer(NullLogger.Instance);
        (BitmapSource fixture, OcrResult truth) = Ocr410Fixture.Table();
        CorpusSample sample = FixtureSample(truth, fixture);
        var rows = new List<object>();
        var variants = new (string Name, TextFormattingMode? Formatting, TextRenderingMode? Rendering, double Dpi)[]
        {
            ("fixture", null, null, 96), ("ideal-gray", TextFormattingMode.Ideal, TextRenderingMode.Grayscale, 96),
            ("display-gray", TextFormattingMode.Display, TextRenderingMode.Grayscale, 96),
            ("display-cleartype", TextFormattingMode.Display, TextRenderingMode.ClearType, 96),
            ("display-aliased", TextFormattingMode.Display, TextRenderingMode.Aliased, 96),
            ("ideal-aliased", TextFormattingMode.Ideal, TextRenderingMode.Aliased, 96),
            ("fixture-dpi72", null, null, 72), ("fixture-dpi144", null, null, 144), ("fixture-dpi300", null, null, 300),
        };
        foreach (var variant in variants)
        {
            BitmapSource image = variant.Name.StartsWith("fixture", StringComparison.Ordinal) ? fixture
                : DrawTable(variant.Formatting!.Value, variant.Rendering!.Value);
            if (variant.Dpi != 96) image = WithDpi(image, variant.Dpi);
            Save(image, DiagnosticOutputPaths.Child(root, variant.Name + ".png"));
            foreach (double scale in new[] { 1.0, 2.0, 3.0, 4.0 })
            {
                BitmapSource prepared = Prepare(image, scale, "nn");
                RecognizedText? raw = recognizer.RecognizeAsync(prepared, "ko-KR", CancellationToken.None).GetAwaiter().GetResult();
                Assert.NotNull(raw);
                OcrResult result = Assemble([raw], scale, TimeSpan.Zero, image.PixelWidth, image.PixelHeight);
                OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
                string csv = OcrTableExport.Csv(extraction.Table);
                rows.Add(new { variant = variant.Name, scale, interpolation = "nn", op = "none", text = result.Text, csv, metric = Score(sample, result),
                    table = new { columns = extraction.Table.Columns.Count, rows = extraction.Table.Rows.Count, extraction.Table.HasHeader },
                    header = csv.Contains("항목", StringComparison.Ordinal), amount = csv.Contains("1,204,000", StringComparison.Ordinal) });
            }
        }
        Write(root, "explore.json", rows);
    }

    private static BitmapSource DrawTable(TextFormattingMode formatting, TextRenderingMode rendering)
    {
        string[][] values = [["항목", "수량", "금액"], ["키보드", "2", "128,400"], ["USB 케이블", "12", "9,900"], ["모니터", "1", "1,204,000"]];
        var visual = new DrawingVisual();
        TextOptions.SetTextFormattingMode(visual, formatting);
        TextOptions.SetTextRenderingMode(visual, rendering);
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 560, 300));
            for (int row = 0; row < values.Length; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    var text = new FormattedText(values[row][column], CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                        new Typeface("Malgun Gothic"), 22, Brushes.Black, null, formatting, 1);
                    double x = column switch { 0 => 32, 1 => 280 - text.Width, _ => 520 - text.Width };
                    drawing.DrawText(text, new Point(x, 35 + row * 60));
                }
                drawing.DrawLine(new Pen(Brushes.LightGray, 1), new Point(25, 82 + row * 60), new Point(535, 82 + row * 60));
            }
        }
        var image = new RenderTargetBitmap(560, 300, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return image;
    }

    private static BitmapSource WithDpi(BitmapSource bitmap, double dpi)
    {
        byte[] pixels = Pixels(bitmap, out int width, out int height);
        BitmapSource result = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    private static void Explore(string root)
    {
        var recognizer = new WindowsOcrRecognizer(NullLogger.Instance);
        Assert.True(recognizer.IsAvailable);
        (BitmapSource image, OcrResult truth) = Ocr410Fixture.Table();
        CorpusSample sample = FixtureSample(truth, image);
        string[] ops = (Environment.GetEnvironmentVariable(ConfigsVariable)
            ?? "none,opaque,gray,otsu,gamma2,min3,sharpen,pad40,invert,opaque-pad40").Split(',');
        var rows = new List<object>();
        foreach (double scale in new[] { 1.0, 1.5, 2.0, 3.0, 4.0 })
        foreach (string interpolation in new[] { "nn", "bicubic" })
        {
            if (scale == 1.0 && interpolation != "nn") continue;
            BitmapSource scaled = Prepare(image, scale, interpolation);
            foreach (string op in ops)
            {
                (BitmapSource prepared, int pad) = Apply(scaled, op);
                RecognizedText? raw = recognizer.RecognizeAsync(prepared, "ko-KR", CancellationToken.None).GetAwaiter().GetResult();
                Assert.NotNull(raw);
                if (pad > 0)
                    raw = raw with { Lines = raw.Lines.Select(line => line with { Words = line.Words.Select(word => word with
                        { Bounds = word.Bounds with { X = word.Bounds.X - pad, Y = word.Bounds.Y - pad } }).ToArray() }).ToArray() };
                OcrResult result = Assemble([raw], scale, TimeSpan.Zero, image.PixelWidth, image.PixelHeight);
                OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
                string csv = OcrTableExport.Csv(extraction.Table);
                rows.Add(new
                {
                    scale, interpolation, op, text = result.Text, csv, metric = Score(sample, result),
                    table = new { columns = extraction.Table.Columns.Count, rows = extraction.Table.Rows.Count, extraction.Table.HasHeader },
                    header = csv.Contains("항목", StringComparison.Ordinal), amount = csv.Contains("1,204,000", StringComparison.Ordinal),
                });
            }
        }
        Write(root, "explore.json", rows);
    }

    internal static (BitmapSource Bitmap, int Pad) Apply(BitmapSource bitmap, string operations)
    {
        BitmapSource current = bitmap;
        int pad = 0;
        foreach (string op in operations.Split('-'))
        {
            if (op == "none") continue;
            if (op.StartsWith("pad", StringComparison.Ordinal))
            {
                int amount = int.Parse(op[3..], CultureInfo.InvariantCulture);
                current = Pad(current, amount);
                pad += amount;
                continue;
            }
            if (op == "sharpen")
            {
                current = ImageCodec.SharpenForRecognition(Opaque(current));
                continue;
            }
            current = Gray(current, op);
        }
        return (current, pad);
    }

    private static byte[] Pixels(BitmapSource bitmap, out int width, out int height)
    {
        BitmapSource bgra = bitmap.Format == PixelFormats.Bgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        byte[] pixels = new byte[checked(width * height * 4)];
        bgra.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static BitmapSource Create(byte[] pixels, int width, int height)
    {
        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }

    /// <summary>Straight-alpha pixels composited over white.</summary>
    internal static BitmapSource Opaque(BitmapSource bitmap)
    {
        byte[] pixels = Pixels(bitmap, out int width, out int height);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)((pixels[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
            pixels[i + 3] = 255;
        }
        return Create(pixels, width, height);
    }

    private static BitmapSource Gray(BitmapSource bitmap, string op)
    {
        byte[] pixels = Pixels(Opaque(bitmap), out int width, out int height);
        byte[] gray = new byte[width * height];
        for (int i = 0; i < gray.Length; i++)
            gray[i] = (byte)Math.Round(0.114 * pixels[i * 4] + 0.587 * pixels[i * 4 + 1] + 0.299 * pixels[i * 4 + 2]);
        if (op == "opaque") return Create(pixels, width, height);
        switch (op)
        {
            case "gray": break;
            case "invert": for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(255 - gray[i]); break;
            case "gamma2": for (int i = 0; i < gray.Length; i++) gray[i] = (byte)Math.Round(255 * Math.Pow(gray[i] / 255.0, 2)); break;
            case "otsu":
            {
                int threshold = Otsu(gray);
                for (int i = 0; i < gray.Length; i++) gray[i] = gray[i] > threshold ? (byte)255 : (byte)0;
                break;
            }
            case "min3":
            {
                byte[] copy = (byte[])gray.Clone();
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    byte min = 255;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sx = Math.Clamp(x + dx, 0, width - 1), sy = Math.Clamp(y + dy, 0, height - 1);
                        min = Math.Min(min, copy[sy * width + sx]);
                    }
                    gray[y * width + x] = min;
                }
                break;
            }
            default: throw new InvalidDataException("Unknown pixel operation: " + op);
        }
        for (int i = 0; i < gray.Length; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray[i];
            pixels[i * 4 + 3] = 255;
        }
        return Create(pixels, width, height);
    }

    private static int Otsu(byte[] gray)
    {
        var histogram = new long[256];
        foreach (byte value in gray) histogram[value]++;
        long total = gray.Length, sumAll = 0;
        for (int i = 0; i < 256; i++) sumAll += i * histogram[i];
        long weightBackground = 0, sumBackground = 0;
        double best = -1;
        int threshold = 127;
        for (int i = 0; i < 256; i++)
        {
            weightBackground += histogram[i];
            if (weightBackground == 0) continue;
            long weightForeground = total - weightBackground;
            if (weightForeground == 0) break;
            sumBackground += i * histogram[i];
            double meanBackground = (double)sumBackground / weightBackground;
            double meanForeground = (double)(sumAll - sumBackground) / weightForeground;
            double between = (double)weightBackground * weightForeground * (meanBackground - meanForeground) * (meanBackground - meanForeground);
            if (between > best) { best = between; threshold = i; }
        }
        return threshold;
    }

    private static BitmapSource Pad(BitmapSource bitmap, int amount)
    {
        byte[] pixels = Pixels(Opaque(bitmap), out int width, out int height);
        int paddedWidth = width + amount * 2, paddedHeight = height + amount * 2;
        byte[] padded = new byte[checked(paddedWidth * paddedHeight * 4)];
        Array.Fill(padded, (byte)255);
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * width * 4, padded, ((y + amount) * paddedWidth + amount) * 4, width * 4);
        return Create(padded, paddedWidth, paddedHeight);
    }

    private static void Fixture(string root)
    {
        var recognizer = new WindowsOcrRecognizer(NullLogger.Instance);
        Assert.True(recognizer.IsAvailable);
        (BitmapSource image, OcrResult truth) = Ocr410Fixture.Table();
        Save(image, DiagnosticOutputPaths.Child(root, "source.png"));
        CorpusSample sample = FixtureSample(truth, image);
        var readings = new List<object>();
        double[] scales = [1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0];
        string[] modes = ["nn", "linear", "fant", "bicubic"];
        foreach (double scale in scales)
        foreach (string interpolation in modes)
        {
            if (scale == 1.0 && interpolation != "nn") continue;
            for (int repeat = 0; repeat < 3; repeat++)
            {
                long started = Stopwatch.GetTimestamp();
                BitmapSource prepared = Prepare(image, scale, interpolation);
                double prepareMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                string preparedHash = Data.PixelHash(prepared);
                BridgeReceipt bridge = Bridge(prepared);
                long recognizeStarted = Stopwatch.GetTimestamp();
                RecognizedText? raw = recognizer.RecognizeAsync(prepared, "ko-KR", CancellationToken.None).GetAwaiter().GetResult();
                double recognizeMs = Stopwatch.GetElapsedTime(recognizeStarted).TotalMilliseconds;
                Assert.NotNull(raw);
                OcrResult result = Assemble([raw], scale, TimeSpan.FromMilliseconds(prepareMs + recognizeMs), image.PixelWidth, image.PixelHeight);
                OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
                string csv = OcrTableExport.Csv(extraction.Table);
                string name = $"{scale.ToString("0.00", CultureInfo.InvariantCulture)}-{interpolation}";
                if (repeat == 0) Save(prepared, DiagnosticOutputPaths.Child(root, "prepared-" + name + ".png"));
                readings.Add(new
                {
                    name, scale, interpolation, repeat,
                    prepared = new { prepared.PixelWidth, prepared.PixelHeight, format = prepared.Format.ToString(), prepared.DpiX, pixelSha256 = preparedHash },
                    bridge, bridgeMatchesPrepared = bridge.PixelSha256 == preparedHash,
                    raw = new
                    {
                        raw.LanguageTag, raw.TextAngle,
                        lines = raw.Lines.Select(line => new { line.Text, words = line.Words.Select(word => new { word.Text, word.Bounds }).ToArray() }).ToArray(),
                    },
                    text = result.Text, csv,
                    table = new { columns = extraction.Table.Columns.Count, rows = extraction.Table.Rows.Count, extraction.Table.HasHeader },
                    checks = new
                    {
                        header = csv.Contains("항목", StringComparison.Ordinal),
                        amount128400 = csv.Contains("128,400", StringComparison.Ordinal),
                        amount9900 = csv.Contains("9,900", StringComparison.Ordinal),
                        amount1204000 = csv.Contains("1,204,000", StringComparison.Ordinal),
                    },
                    metric = Score(sample, result), prepareMs, recognizeMs,
                });
            }
        }

        var service = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        OcrResult product = service.RecognizeAsync(OcrRequest.FromBitmap(image, preferredLanguages: ["ko-KR"],
            searchRotatedOrientations: false)).GetAwaiter().GetResult();
        string productCsv = OcrTableExport.Csv(OcrExtractionAnalyzer.Analyze(product).Table);
        Write(root, "fixture.json", new
        {
            source = new { image.PixelWidth, image.PixelHeight, format = image.Format.ToString(), image.DpiX, pixelSha256 = Data.PixelHash(image),
                pngSha256 = Data.FileHash(DiagnosticOutputPaths.Child(root, "source.png")) },
            fonts = FixtureFonts(),
            engine = new { recognizer.SupportedLanguages, recognizer.MaxImageDimension, os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString() },
            adaptiveScale = OcrPlanner.ResolveAdaptiveScale(image.PixelWidth, image.PixelHeight, 1.0, recognizer.MaxImageDimension),
            product = new { product.Status, product.LanguageTag, product.Text, csv = productCsv, metric = Score(sample, product) },
            readings,
        });
    }

    private static void Sweep(string root)
    {
        string corpus = Data.ArtifactPath(Required(CorpusVariable));
        string split = Environment.GetEnvironmentVariable(SplitVariable) ?? "all";
        string[] configs = Required(ConfigsVariable).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        CorpusSample[] samples = LoadCorpus(corpus).Where(sample => split == "all" || sample.Split == split).ToArray();
        Assert.NotEmpty(samples);
        var recognizer = new WindowsOcrRecognizer(NullLogger.Instance);
        Assert.True(recognizer.IsAvailable);
        var rows = new List<SweepRow>();
        // Warm the engine once so the first configuration does not carry engine start-up.
        _ = recognizer.RecognizeAsync(samples[0].Load(corpus), null, CancellationToken.None).GetAwaiter().GetResult();
        foreach (CorpusSample sample in samples)
        {
            BitmapSource source = sample.Load(corpus);
            double? estimated = EstimateLineHeight(source);
            double truthLine = sample.Lines.Select(line => line.Bounds.Height).Order().ElementAt(sample.Lines.Length / 2);
            IReadOnlyList<string> languages = OcrPlanner.SelectLanguages(sample.Languages, recognizer.SupportedLanguages, 2);
            string?[] passes = languages.Count == 0 ? [null] : languages.Select(language => (string?)language).ToArray();
            foreach (string config in configs)
            {
                long started = Stopwatch.GetTimestamp();
                (double scale, string interpolation, string op) = Resolve(config, source, recognizer.MaxImageDimension);
                BitmapSource prepared = PostProcess(Prepare(source, scale, interpolation), op);
                var texts = new List<RecognizedText>();
                foreach (string? language in passes)
                {
                    RecognizedText? raw = recognizer.RecognizeAsync(prepared, language, CancellationToken.None).GetAwaiter().GetResult();
                    if (raw is not null) texts.Add(raw);
                }
                double wallMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                OcrResult result = Assemble(texts, scale, TimeSpan.FromMilliseconds(wallMs), source.PixelWidth, source.PixelHeight);
                rows.Add(new SweepRow(sample.Id, sample.Category, sample.Split, sample.FontPixels, sample.Width, sample.Height,
                    config, scale, wallMs, result.Text, Score(sample, result), estimated, truthLine));
            }
        }
        // The #226 table under every configuration, scored with the service test's assertions.
        (BitmapSource fixture, _) = Ocr410Fixture.Table();
        var fixtureRows = configs.Select(config =>
        {
            (double scale, string interpolation, string op) = Resolve(config, fixture, recognizer.MaxImageDimension);
            BitmapSource prepared = PostProcess(Prepare(fixture, scale, interpolation), op);
            RecognizedText? raw = recognizer.RecognizeAsync(prepared, "ko", CancellationToken.None).GetAwaiter().GetResult();
            OcrResult result = Assemble(raw is null ? [] : [raw], scale, TimeSpan.Zero, fixture.PixelWidth, fixture.PixelHeight);
            OcrExtraction extraction = OcrExtractionAnalyzer.Analyze(result);
            string csv = OcrTableExport.Csv(extraction.Table);
            bool service = extraction.Table.Columns.Count == 3 && extraction.Table.Rows.Count == 4 && extraction.Table.HasHeader
                && csv.Contains("128,400", StringComparison.Ordinal) && csv.Contains("1,204,000", StringComparison.Ordinal);
            return new
            {
                config, scale, estimatedLine = EstimateLineHeight(fixture), text = result.Text, csv,
                columns = extraction.Table.Columns.Count, rows = extraction.Table.Rows.Count, extraction.Table.HasHeader,
                header = csv.Contains("항목", StringComparison.Ordinal), service,
            };
        }).ToArray();
        Write(root, "fixture-policies.json", fixtureRows);
        Write(root, "rows.json", rows);
        Write(root, "summary.json", new
        {
            corpus, split, configs, count = samples.Length, engine = new { recognizer.SupportedLanguages, recognizer.MaxImageDimension },
            byConfig = configs.Select(config => Summarize(config, rows.Where(row => row.Config == config).ToArray())).ToArray(),
            byConfigCategory = configs.SelectMany(config => rows.Where(row => row.Config == config).GroupBy(row => row.Category)
                .Select(group => Summarize(config + "/" + group.Key, group.ToArray()))).ToArray(),
        });
    }

    internal static (double Scale, string Interpolation, string Op) Resolve(string config, BitmapSource source, int maxDimension)
    {
        // "<policy>-<interpolation>[+<op>]": policy is adaptive (product rule), s<factor> (fixed),
        // or t/T/r<target> (line-height aware, below).
        int plus = config.IndexOf('+');
        string op = plus < 0 ? "none" : config[(plus + 1)..];
        string head = plus < 0 ? config : config[..plus];
        int dash = head.LastIndexOf('-');
        string policy = head[..dash], interpolation = head[(dash + 1)..];
        double adaptive = OcrPlanner.ResolveAdaptiveScale(source.PixelWidth, source.PixelHeight, 1.0, maxDimension);
        double scale;
        if (policy == "adaptive") scale = adaptive;
        else if (policy.StartsWith('s'))
            scale = OcrPlanner.ResolveScale(source.PixelWidth, source.PixelHeight,
                double.Parse(policy[1..], CultureInfo.InvariantCulture), maxDimension);
        else if (policy.StartsWith('t') || policy.StartsWith('T') || policy.StartsWith('r'))
        {
            // t<target>: bring the estimated ink line height toward target pixels, never above
            // the adaptive rule; T<target>: the same but bounded only by the 4x maximum;
            // r<target>: t with thin vertical runs (grid lines, box sides) left out of the estimate.
            double target = double.Parse(policy[1..], CultureInfo.InvariantCulture);
            double? line = EstimateLineHeight(source, ignoreThinVerticalRuns: policy[0] == 'r');
            // An image above the dimension limit has an adaptive scale below one; ResolveScale
            // then applies the same downscale, so the requested factor never drops under one.
            double ceiling = Math.Max(1.0, policy[0] == 'T' ? OcrPlanner.MaxUpscale : adaptive);
            scale = line is { } value
                ? OcrPlanner.ResolveScale(source.PixelWidth, source.PixelHeight, Math.Clamp(target / value, 1.0, ceiling), maxDimension)
                : adaptive;
        }
        else throw new InvalidDataException("Unknown scale policy: " + policy);
        return (scale, interpolation, op);
    }

    /// <summary>Estimator comparison without recognition: both estimates against the truth line height.</summary>
    private static void Estimates(string root)
    {
        var rows = new List<object>();
        foreach (string name in Required(CorpusVariable).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string corpus = Data.ArtifactPath(name);
            foreach (CorpusSample sample in LoadCorpus(corpus))
            {
                BitmapSource source = sample.Load(corpus);
                double truthLine = sample.Lines.Select(line => line.Bounds.Height).Order().ElementAt(sample.Lines.Length / 2);
                long started = Stopwatch.GetTimestamp();
                double? simple = EstimateLineHeight(source);
                double simpleMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                double? robust = EstimateLineHeight(source, ignoreThinVerticalRuns: true);
                double robustMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                rows.Add(new { corpus = Path.GetFileName(corpus), sample.Id, sample.Category, sample.Split, sample.FontPixels, sample.Width, sample.Height,
                    truthLine, simple, robust, simpleMs, robustMs });
            }
        }
        Write(root, "estimates.json", rows);
    }

    /// <summary>
    /// Median ink-band height in source pixels: rows with text-polarity ink inside vertical
    /// tiles, runs of such rows are bands, rules/specks below four pixels are ignored.
    /// With <paramref name="ignoreThinVerticalRuns"/>, ink that forms a thin (at most three
    /// pixels wide) vertical run of 24 rows or more — a table grid line or a box side — is not
    /// counted, so it cannot join separate text lines into one band.
    /// </summary>
    internal static double? EstimateLineHeight(BitmapSource source, bool ignoreThinVerticalRuns = false)
    {
        byte[] pixels = Pixels(Opaque(source), out int width, out int height);
        byte[] gray = new byte[width * height];
        var histogram = new int[256];
        for (int i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)((pixels[i * 4] * 29 + pixels[i * 4 + 1] * 150 + pixels[i * 4 + 2] * 77) >> 8);
            histogram[gray[i]]++;
        }
        int background = 0, best = -1;
        for (int level = 0; level < 256; level++)
        {
            int windowed = 0;
            for (int k = Math.Max(0, level - 2); k <= Math.Min(255, level + 2); k++) windowed += histogram[k];
            if (windowed > best) { best = windowed; background = level; }
        }
        const int contrast = 48;
        bool[] inked = new bool[gray.Length];
        for (int i = 0; i < gray.Length; i++) inked[i] = Math.Abs(gray[i] - background) >= contrast;
        if (ignoreThinVerticalRuns)
        {
            // Horizontal ink width through each pixel, then thin vertical runs per column.
            int[] across = new int[gray.Length];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width;)
            {
                int start = y * width + x;
                if (!inked[start]) { x++; continue; }
                int end = x;
                while (end < width && inked[y * width + end]) end++;
                for (int k = x; k < end; k++) across[y * width + k] = end - x;
                x = end;
            }
            var clear = new List<int>();
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height;)
            {
                if (!inked[y * width + x] || across[y * width + x] > 3) { y++; continue; }
                int end = y;
                while (end < height && inked[end * width + x] && across[end * width + x] <= 3) end++;
                if (end - y >= 24)
                    for (int k = y; k < end; k++) clear.Add(k * width + x);
                y = end;
            }
            foreach (int index in clear) inked[index] = false;
        }
        int tile = Math.Min(width, 320);
        var bands = new List<(int Height, int Ink)>();
        for (int left = 0; left < width; left += tile)
        {
            int right = Math.Min(width, left + tile);
            int runStart = -1, runInk = 0;
            for (int y = 0; y <= height; y++)
            {
                int ink = 0;
                if (y < height)
                    for (int x = left; x < right; x++)
                        if (inked[y * width + x]) ink++;
                if (ink > 0)
                {
                    if (runStart < 0) { runStart = y; runInk = 0; }
                    runInk += ink;
                    continue;
                }
                if (runStart >= 0)
                {
                    int bandHeight = y - runStart;
                    double coverage = (double)runInk / (bandHeight * (right - left));
                    if (bandHeight >= 4 && coverage < 0.6) bands.Add((bandHeight, runInk));
                    runStart = -1;
                }
            }
        }
        if (bands.Count == 0) return null;
        // Ink-weighted median: a long text line outweighs a stray icon or speck.
        var ordered = bands.OrderBy(band => band.Height).ToArray();
        long total = ordered.Sum(band => (long)band.Ink), seen = 0;
        foreach (var band in ordered)
            if ((seen += band.Ink) * 2 >= total) return band.Height;
        return ordered[^1].Height;
    }

    /// <summary>Polarity-aware variants: "ink" operations act on the minority (text) polarity.</summary>
    internal static BitmapSource PostProcess(BitmapSource bitmap, string op)
    {
        if (op == "none") return bitmap;
        if (op == "opaque") return Opaque(bitmap);
        byte[] pixels = Pixels(Opaque(bitmap), out int width, out int height);
        byte[] gray = new byte[width * height];
        var histogram = new int[256];
        for (int i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)Math.Round(0.114 * pixels[i * 4] + 0.587 * pixels[i * 4 + 1] + 0.299 * pixels[i * 4 + 2]);
            histogram[gray[i]]++;
        }
        int median = 0;
        for (int sum = 0; median < 255 && (sum += histogram[median]) < gray.Length / 2; median++) { }
        // Ink is the side of the dominant background level where the clearly different pixels
        // lie on average, so dark text on a mid-grey background still counts as dark ink.
        int background = Array.IndexOf(histogram, histogram.Max());
        long farSum = 0, farCount = 0;
        for (int level = 0; level < histogram.Length; level++)
        {
            if (Math.Abs(level - background) <= 48) continue;
            farSum += (long)level * histogram[level];
            farCount += histogram[level];
        }
        bool darkInk = farCount == 0 ? median >= 128 : farSum < (long)background * farCount;
        switch (op)
        {
            case "gray": break;
            case "otsu":
            {
                int threshold = Otsu(gray);
                for (int i = 0; i < gray.Length; i++) gray[i] = gray[i] > threshold ? (byte)255 : (byte)0;
                break;
            }
            case "gamma2":
                for (int i = 0; i < gray.Length; i++)
                {
                    double value = gray[i] / 255.0;
                    gray[i] = (byte)Math.Round(255 * (darkInk ? value * value : 1 - (1 - value) * (1 - value)));
                }
                break;
            case "min3":
            {
                // Separable 3x3 extremum toward the ink polarity: a one-pixel stroke emboldening.
                byte[] row = new byte[gray.Length];
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    byte a = gray[i], b = gray[y * width + Math.Max(0, x - 1)], c = gray[y * width + Math.Min(width - 1, x + 1)];
                    row[i] = darkInk ? Math.Min(a, Math.Min(b, c)) : Math.Max(a, Math.Max(b, c));
                }
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    byte a = row[i], b = row[Math.Max(0, y - 1) * width + x], c = row[Math.Min(height - 1, y + 1) * width + x];
                    gray[i] = darkInk ? Math.Min(a, Math.Min(b, c)) : Math.Max(a, Math.Max(b, c));
                }
                break;
            }
            default: throw new InvalidDataException("Unknown post operation: " + op);
        }
        for (int i = 0; i < gray.Length; i++)
        {
            pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = gray[i];
            pixels[i * 4 + 3] = 255;
        }
        return Create(pixels, width, height);
    }

    internal static BitmapSource Prepare(BitmapSource source, double scale, string interpolation)
    {
        if (Math.Abs(scale - 1.0) < 1e-6) return source;
        if (scale < 1.0) return ImageCodec.Resize(source, scale);
        return interpolation switch
        {
            "nn" => ImageCodec.UpscaleForRecognition(source, scale),
            "linear" => Render(source, scale, BitmapScalingMode.Linear),
            "fant" => Render(source, scale, BitmapScalingMode.HighQuality),
            "bicubic" => Bicubic(source, scale),
            _ => throw new InvalidDataException("Unknown interpolation: " + interpolation),
        };
    }

    private static BitmapSource Render(BitmapSource bitmap, double factor, BitmapScalingMode mode)
    {
        int width = (int)Math.Round(bitmap.PixelWidth * factor);
        int height = (int)Math.Round(bitmap.PixelHeight * factor);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, mode);
            dc.DrawImage(bitmap, new Rect(0, 0, width, height));
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Separable Keys cubic (a = -0.5) on pixels composited over white, edge-clamped.</summary>
    internal static BitmapSource Bicubic(BitmapSource bitmap, double factor)
    {
        // Composite first, as every pixel operation here does: RenderTargetBitmap leaves alpha 254
        // on glyph edges, and dropping alpha after un-premultiplying would change those colours.
        byte[] source = Pixels(Opaque(bitmap), out int sw, out int sh);
        int dw = (int)Math.Round(sw * factor), dh = (int)Math.Round(sh * factor);
        (int[] xIndex, double[] xWeight) = Taps(sw, dw);
        (int[] yIndex, double[] yWeight) = Taps(sh, dh);
        var horizontal = new double[checked(sh * dw * 3)];
        for (int y = 0; y < sh; y++)
        for (int x = 0; x < dw; x++)
        for (int c = 0; c < 3; c++)
        {
            double sum = 0;
            for (int k = 0; k < 4; k++) sum += xWeight[x * 4 + k] * source[(y * sw + xIndex[x * 4 + k]) * 4 + c];
            horizontal[(y * dw + x) * 3 + c] = sum;
        }
        byte[] pixels = new byte[checked(dw * dh * 4)];
        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++) sum += yWeight[y * 4 + k] * horizontal[(yIndex[y * 4 + k] * dw + x) * 3 + c];
                pixels[(y * dw + x) * 4 + c] = (byte)Math.Clamp(Math.Round(sum), 0, 255);
            }
            pixels[(y * dw + x) * 4 + 3] = 255;
        }
        BitmapSource result = BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Bgra32, null, pixels, dw * 4);
        result.Freeze();
        return result;
    }

    private static (int[] Index, double[] Weight) Taps(int sourceLength, int targetLength)
    {
        var index = new int[targetLength * 4];
        var weight = new double[targetLength * 4];
        double ratio = (double)sourceLength / targetLength;
        for (int i = 0; i < targetLength; i++)
        {
            double center = (i + 0.5) * ratio - 0.5;
            int first = (int)Math.Floor(center) - 1;
            double total = 0;
            for (int k = 0; k < 4; k++)
            {
                double w = Cubic(center - (first + k));
                index[i * 4 + k] = Math.Clamp(first + k, 0, sourceLength - 1);
                weight[i * 4 + k] = w;
                total += w;
            }
            for (int k = 0; k < 4; k++) weight[i * 4 + k] /= total;
        }
        return (index, weight);
    }

    private static double Cubic(double x)
    {
        const double a = -0.5;
        x = Math.Abs(x);
        return x <= 1 ? ((a + 2) * x - (a + 3)) * x * x + 1 : x < 2 ? ((a * x - 5 * a) * x + 8 * a) * x - 4 * a : 0;
    }

    /// <summary>The service's line building, language merge and block text for an unrotated reading.</summary>
    internal static OcrResult Assemble(IReadOnlyList<RecognizedText> passes, double scale, TimeSpan elapsed, int sourceWidth, int sourceHeight)
    {
        var results = passes.Select(raw =>
        {
            IReadOnlyList<OcrLine> lines = OcrPlanner.MergeLines([], BuildLines(raw, scale, sourceWidth, sourceHeight));
            return (raw.LanguageTag, Lines: lines, Score: OcrPlanner.ScoreLines(lines));
        }).ToArray();
        if (results.Length == 0) return OcrResult.NoText(string.Empty, elapsed);
        var dominant = results.OrderByDescending(pass => pass.Score).First();
        IReadOnlyList<OcrLine> merged = dominant.Lines;
        foreach (var pass in results.Where(pass => !ReferenceEquals(pass.Lines, dominant.Lines)).OrderByDescending(pass => pass.Score))
            merged = OcrPlanner.MergeLines(merged, pass.Lines);
        string block = OcrPlanner.BuildBlockText(merged.Select(line => line.Text));
        return block.Length == 0 ? OcrResult.NoText(dominant.LanguageTag, elapsed)
            : OcrResult.Success(block, dominant.LanguageTag, merged, elapsed);
    }

    private static List<OcrLine> BuildLines(RecognizedText recognized, double scale, int sourceWidth, int sourceHeight)
    {
        var lines = new List<OcrLine>(recognized.Lines.Count);
        foreach (RecognizedLine line in recognized.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            var texts = new List<string>(line.Words.Count);
            OcrRect bounds = default;
            foreach (RecognizedWord word in line.Words)
            {
                string text = OcrPlanner.NormalizeWord(word.Text);
                if (text.Length == 0) continue;
                // Same mapping as the service: back to source pixels, clamped to the source image.
                OcrRect box = word.Bounds.Unscale(scale).MapFromClockwiseRotation(0, sourceWidth, sourceHeight);
                words.Add(new OcrWord(text, box));
                texts.Add(text);
                bounds = OcrRect.Union(bounds, box);
            }
            string lineText = string.IsNullOrWhiteSpace(line.Text) ? OcrPlanner.BuildLineText(texts) : OcrPlanner.NormalizeWord(line.Text);
            if (lineText.Length > 0) lines.Add(new OcrLine(lineText, bounds, words));
        }
        return lines;
    }

    private static BridgeReceipt Bridge(BitmapSource prepared)
    {
        using WinRtSoftwareBitmap? direct = WindowsOcrBitmapBridge.TryCopyOpaquePixels(prepared, CancellationToken.None);
        WinRtSoftwareBitmap software = direct ?? WindowsOcrBitmapBridge.CreateLegacyAsync(prepared, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(software.PixelWidth * software.PixelHeight * 4)));
            software.CopyToBuffer(buffer);
            byte[] bytes = buffer.ToArray(0, (int)buffer.Length);
            return new BridgeReceipt(direct is not null ? "direct" : "png", software.PixelWidth, software.PixelHeight,
                software.BitmapPixelFormat.ToString(), software.BitmapAlphaMode.ToString(), software.DpiX,
                Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally
        {
            if (direct is null) software.Dispose();
        }
    }

    private static object[] FixtureFonts()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
            foreach (string value in new[] { "항목", "수량", "금액", "키보드", "128,400", "USB 케이블", "9,900", "모니터", "1,204,000" })
                drawing.DrawText(new FormattedText(value, CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                    new Typeface("Malgun Gothic"), 22, Brushes.Black, 1), new Point(0, 0));
        return GlyphRuns(visual.Drawing).Select(run => run.GlyphTypeface.FontUri).Distinct()
            .Select(uri => (object)new
            {
                uri = uri.ToString(),
                sha256 = uri.IsFile ? Data.FileHash(uri.LocalPath) : "",
                version = new GlyphTypeface(uri).VersionStrings.TryGetValue(CultureInfo.GetCultureInfo("en-US"), out string? version) ? version : "",
            }).ToArray();
    }

    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (GlyphRun run in GlyphRuns(child)) yield return run;
    }

    internal static CorpusSample FixtureSample(OcrResult truth, BitmapSource image) => new("fixture-table", "ko", "fixture", ["ko-KR"], 22,
        image.PixelWidth, image.PixelHeight, string.Join('\n', truth.Lines.Select(line => line.Text)),
        truth.Lines.Select(line => new Data.TruthLine(line.Text, new Data.Box(line.Bounds.X, line.Bounds.Y, line.Bounds.Width, line.Bounds.Height),
            line.Words.Select(word => word.Text).ToArray())).ToArray(), "");

    internal sealed record CorpusSample(string Id, string Category, string Split, string[] Languages, double FontPixels,
        int Width, int Height, string Text, Data.TruthLine[] Lines, string Png)
    {
        internal BitmapSource Load(string corpus) => ImageCodec.TryLoad(DiagnosticOutputPaths.Child(corpus, Png))
            ?? throw new InvalidDataException("Cannot load " + Id);
    }

    internal static CorpusSample[] LoadCorpus(string corpus)
    {
        using var stream = File.OpenRead(DiagnosticOutputPaths.Child(corpus, "corpus.json"));
        Data.Manifest manifest = JsonSerializer.Deserialize<Data.Manifest>(stream, Data.Json) ?? throw new InvalidDataException("Missing corpus manifest.");
        return manifest.Samples.Select(sample => new CorpusSample(sample.Id, sample.Category, sample.Split, sample.Languages,
            sample.FontSize * sample.Scale, sample.Width, sample.Height, sample.Text, sample.Lines, sample.Png)).ToArray();
    }

    internal sealed record Metric(int TruthCharacters, int CharacterEdits, double Cer, int TruthLines, int MissingLineCount,
        int ExactUnmatchedLines, bool RawExact, int NumericTokens, int NumericMatched, int SeparatorTokens, int SeparatorMatched);

    internal static Metric Score(CorpusSample sample, OcrResult result)
    {
        var data = new Data.Sample(sample.Id, sample.Id, sample.Category, sample.Split, sample.Languages, 1, "light", sample.FontPixels,
            "", false, sample.Width, sample.Height, sample.Text, sample.Lines, [], "", "", "", "");
        Data.Metric metric = Data.Measure(data, result);
        Dictionary<string, int> truth = Tokens(sample.Text), observed = Tokens(result.Text);
        int matched = truth.Sum(pair => Math.Min(pair.Value, observed.GetValueOrDefault(pair.Key)));
        var separators = truth.Where(pair => pair.Key.IndexOfAny([',', '.', ':']) >= 0).ToArray();
        int separatorMatched = separators.Sum(pair => Math.Min(pair.Value, observed.GetValueOrDefault(pair.Key)));
        return new Metric(metric.TruthCharacters, metric.CharacterEdits, metric.Cer, metric.TruthLines, metric.MissingLineCount,
            metric.ExactUnmatchedLines, metric.RawExact, truth.Values.Sum(), matched, separators.Sum(pair => pair.Value), separatorMatched);
    }

    private static Dictionary<string, int> Tokens(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"\d+(?:[,.:]\d+)*", RegexOptions.CultureInvariant))
            counts[match.Value] = counts.GetValueOrDefault(match.Value) + 1;
        return counts;
    }

    internal sealed record SweepRow(string Id, string Category, string Split, double FontPixels, int Width, int Height,
        string Config, double Scale, double WallMs, string? Text, Metric Metric, double? EstimatedLine = null, double TruthLine = 0);

    private static object Summarize(string name, SweepRow[] rows) => new
    {
        name, count = rows.Length,
        microCer = (double)rows.Sum(row => row.Metric.CharacterEdits) / Math.Max(1, rows.Sum(row => row.Metric.TruthCharacters)),
        edits = rows.Sum(row => row.Metric.CharacterEdits), truthCharacters = rows.Sum(row => row.Metric.TruthCharacters),
        missingLines = rows.Sum(row => row.Metric.MissingLineCount), exactUnmatchedLines = rows.Sum(row => row.Metric.ExactUnmatchedLines),
        truthLines = rows.Sum(row => row.Metric.TruthLines), rawExact = rows.Count(row => row.Metric.RawExact),
        numericTokens = rows.Sum(row => row.Metric.NumericTokens), numericMatched = rows.Sum(row => row.Metric.NumericMatched),
        separatorTokens = rows.Sum(row => row.Metric.SeparatorTokens), separatorMatched = rows.Sum(row => row.Metric.SeparatorMatched),
        meanWallMs = rows.Average(row => row.WallMs), p95WallMs = Percentile(rows.Select(row => row.WallMs), .95),
        totalWallMs = rows.Sum(row => row.WallMs),
    };

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
    }

    internal sealed record BridgeReceipt(string Path, int PixelWidth, int PixelHeight, string Format, string AlphaMode, double DpiX, string PixelSha256);

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(stream);
    }

    private static void Write(string root, string name, object value)
    {
        using var stream = new FileStream(DiagnosticOutputPaths.Child(root, name), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(stream, value, Json);
    }

    private static string Required(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value
        : throw new InvalidOperationException("Missing required opt-in setting: " + variable);
}

internal sealed class Ocr226DiagnosticFactAttribute : FactAttribute
{
    public Ocr226DiagnosticFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Ocr226PreprocessingDiagnosticTests.RunVariable) is not ("fixture" or "sweep" or "explore" or "render" or "generate" or "generate-clutter" or "estimate"))
            Skip = "Opt-in #226 Windows OCR diagnostics: set " + Ocr226PreprocessingDiagnosticTests.RunVariable + ".";
    }
}
