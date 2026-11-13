using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in measurement of how the picture-translation reading reads English: known text is drawn with WPF in
/// several fonts, sizes, polarities and scales, read the way the overlay reads it (Fast, the user's languages,
/// Latin recovery) with and without the offline PP-OCR model, and once by PP-OCR alone. The pictures, the truth
/// and every reading are written for scoring; nothing is asserted beyond the run completing.
/// </summary>
public sealed class OcrEnglishReadingDiagnosticTests(ITestOutputHelper output)
{
    internal const string OutputVariable = "MYCAPTURE_OCR_ENGLISH_READING";
    internal const string NeuralReferenceVariable = "MYCAPTURE_OCR_ENGLISH_READING_NEURAL";
    internal const string ScalesVariable = "MYCAPTURE_OCR_ENGLISH_READING_SCALES";
    private const int PageWidth = 1280, PageHeight = 860, Margin = 24;

    [OcrEnglishReadingFact]
    public void ReadSyntheticEnglishPages() => StaTestHost.Run(() =>
    {
        string root = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
        string directory = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "synthetic"));
        bool neuralReference = Environment.GetEnvironmentVariable(NeuralReferenceVariable) == "1";
        // Optional whole-picture Windows readings at other scale factors, for example "2,3".
        double[] scales = (Environment.GetEnvironmentVariable(ScalesVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        using var http = new HttpClient(new RejectNetwork());
        using NeuralOcrEngine neural = Neural(http);
        var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        output.WriteLine("Windows OCR languages: " + string.Join(", ", windows.SupportedLanguages));
        var absent = new CaptureOcrService(windows, neural.RecognizeAsync, () => false, () => false,
            NullLogger<CaptureOcrService>.Instance, readNeuralRows: neural.ReadRowsAsync);
        var present = new CaptureOcrService(windows, neural.RecognizeAsync, () => neural.CanRun, () => neural.IsReady,
            NullLogger<CaptureOcrService>.Instance, readNeuralRows: neural.ReadRowsAsync);
        OcrSettings reading = PinTranslationEngines.OverlayReading(new OcrSettings());
        var timing = new StringBuilder("page\tabsentMs\tpresentMs\tneuralMs\n");
        string[] items = [.. EnglishReadingTruth.Sentences, .. EnglishReadingTruth.Interface, EnglishReadingTruth.Dense];
        // A 4K article: the dense paragraph in long lines across a 3840 x 2160 picture, at 100% only.
        var conditions = new List<(string Font, string Label, int Size, bool Dark, Func<IReadOnlyList<(BitmapSource Page, string[] Truth)>> Pages, double[] Scales)>();
        foreach (int size in new[] { 14, 18 })
            conditions.Add(("Segoe UI", "Wide4KSegoeUI", size, false,
                () => Render(Enumerable.Repeat(EnglishReadingTruth.Dense, 12).ToArray(), "Segoe UI", size, false, 3840, 2160), [1.0]));
        foreach (string font in new[] { "Segoe UI", "Arial", "Times New Roman", "Consolas" })
        foreach (int size in new[] { 12, 14, 16, 20 })
        foreach (bool dark in new[] { false, true })
            conditions.Add((font, font.Replace(" ", string.Empty, StringComparison.Ordinal), size, dark, () => Render(items, font, size, dark), [1.0, 0.6, 1.5]));
        foreach ((_, string label, int size, bool dark, Func<IReadOnlyList<(BitmapSource Page, string[] Truth)>> draw, double[] pageScales) in conditions)
        {
            IReadOnlyList<(BitmapSource Page, string[] Truth)> pages = draw();
            foreach (double scale in pageScales)
            {
                for (int index = 0; index < pages.Count; index++)
                {
                    string name = string.Create(CultureInfo.InvariantCulture,
                        $"{label}-{size}-{(dark ? "dark" : "light")}-{scale * 100:0}-p{index + 1}");
                    BitmapSource page = Scale(pages[index].Page, scale);
                    Save(directory, name + "-source.png", page);
                    File.WriteAllText(DiagnosticOutputPaths.Child(directory, name + "-truth.json"),
                        JsonSerializer.Serialize(pages[index].Truth), Encoding.UTF8);
                    var watch = Stopwatch.StartNew();
                    OcrResult first = absent.RecognizeAsync(OcrRequestFactory.FromBitmap(page, reading).WithLatinRecovery())
                        .GetAwaiter().GetResult();
                    long absentMs = watch.ElapsedMilliseconds;
                    watch.Restart();
                    OcrResult second = present.RecognizeAsync(OcrRequestFactory.FromBitmap(page, reading).WithLatinRecovery())
                        .GetAwaiter().GetResult();
                    long presentMs = watch.ElapsedMilliseconds;
                    Write(directory, name + "-absent.json", first);
                    Write(directory, name + "-present.json", second);
                    foreach (double factor in scales)
                    {
                        OcrResult upscaled = windows.RecognizeAsync(OcrRequest.FromBitmap(page, factor, ["ko-KR", "en-US"], false))
                            .GetAwaiter().GetResult();
                        Write(directory, name + string.Create(CultureInfo.InvariantCulture, $"-win{factor:0}.json"), upscaled);
                    }
                    long neuralMs = -1;
                    if (neuralReference)
                    {
                        watch.Restart();
                        OcrResult whole = neural.RecognizeAsync(OcrRequest.FromBitmap(page, preferredLanguages: ["ko-KR", "en-US"],
                            searchRotatedOrientations: false, useNeuralModel: true), CancellationToken.None).GetAwaiter().GetResult();
                        neuralMs = watch.ElapsedMilliseconds;
                        Write(directory, name + "-neural.json", whole);
                    }
                    timing.Append(CultureInfo.InvariantCulture, $"{name}\t{absentMs}\t{presentMs}\t{neuralMs}\n");
                }
            }
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "timing.tsv"), timing.ToString(), Encoding.UTF8);
    });

    /// <summary>Reads every corpus picture the overlay way, with and without the offline model, and records the time.</summary>
    [OcrEnglishReadingCorpusFact]
    public void ReadCorpusPictures() => StaTestHost.Run(() =>
    {
        string input = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)!);
        string root = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(OutputVariable)!);
        string directory = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(root, "corpus"));
        using var http = new HttpClient(new RejectNetwork());
        using NeuralOcrEngine neural = Neural(http);
        var windows = new WindowsOcrService(NullLogger<WindowsOcrService>.Instance);
        var absent = new CaptureOcrService(windows, neural.RecognizeAsync, () => false, () => false,
            NullLogger<CaptureOcrService>.Instance, readNeuralRows: neural.ReadRowsAsync);
        var present = new CaptureOcrService(windows, neural.RecognizeAsync, () => neural.CanRun, () => neural.IsReady,
            NullLogger<CaptureOcrService>.Instance, readNeuralRows: neural.ReadRowsAsync);
        OcrSettings reading = PinTranslationEngines.OverlayReading(new OcrSettings());
        var timing = new StringBuilder("name\tround\tabsentMs\tpresentMs\n");
        string? replay = Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.ComparisonVariable);
        var pictures = new List<(string Name, BitmapSource Image)>();
        foreach (string entry in Directory.EnumerateFiles(input).Order(StringComparer.OrdinalIgnoreCase))
            pictures.AddRange(PinTranslationOverlayCorpusTests.LoadPicturesAsync(
                DiagnosticOutputPaths.Child(input, Path.GetFileName(entry)), replay).GetAwaiter().GetResult());
        // Three rounds; the first warms both engines, the report uses the median.
        for (int round = 0; round < 3; round++)
        foreach ((string name, BitmapSource image) in pictures)
        {
            var watch = Stopwatch.StartNew();
            OcrResult first = absent.RecognizeAsync(OcrRequestFactory.FromBitmap(image, reading).WithLatinRecovery())
                .GetAwaiter().GetResult();
            long absentMs = watch.ElapsedMilliseconds;
            watch.Restart();
            OcrResult second = present.RecognizeAsync(OcrRequestFactory.FromBitmap(image, reading).WithLatinRecovery())
                .GetAwaiter().GetResult();
            long presentMs = watch.ElapsedMilliseconds;
            timing.Append(CultureInfo.InvariantCulture, $"{name}\t{round}\t{absentMs}\t{presentMs}\n");
            if (round > 0) continue;
            Save(directory, name + "-source.png", image);
            Write(directory, name + "-absent.json", first);
            Write(directory, name + "-present.json", second);
        }
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, "timing.tsv"), timing.ToString(), Encoding.UTF8);
    });

    private static NeuralOcrEngine Neural(HttpClient http)
    {
        string models = DiagnosticOutputPaths.Create(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable)!);
        var neural = new NeuralOcrEngine(new OcrModelStore(AppPaths.CreateForRoot(models), http,
            NullLogger<OcrModelStore>.Instance), NullLogger<NeuralOcrEngine>.Instance);
        Assert.True(neural.CanRun, "The supplied offline OCR model folder must be complete; no download is permitted.");
        return neural;
    }

    internal static IReadOnlyList<(BitmapSource Page, string[] Truth)> Render(IReadOnlyList<string> items, string font, int size, bool dark,
        int pageWidth = PageWidth, int pageHeight = PageHeight)
    {
        Brush ink = dark ? new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)) : new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        Brush paper = dark ? new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)) : Brushes.White;
        var typeface = new Typeface(new FontFamily(font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var pages = new List<(BitmapSource, string[])>();
        var texts = new List<FormattedText>();
        var truth = new List<string>();
        double y = Margin;
        foreach (string item in items)
        {
            var text = new FormattedText(item, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight, typeface, size, ink,
                null, TextFormattingMode.Display, 1.0) { MaxTextWidth = pageWidth - Margin * 2 };
            if (texts.Count > 0 && y + text.Height > pageHeight - Margin) Flush();
            texts.Add(text);
            truth.Add(item);
            y += text.Height + size * .7;
        }
        if (texts.Count > 0) Flush();
        return pages;

        void Flush()
        {
            var visual = new DrawingVisual();
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawRectangle(paper, null, new Rect(0, 0, pageWidth, pageHeight));
                double top = Margin;
                foreach (FormattedText text in texts)
                {
                    context.DrawText(text, new Point(Margin, top));
                    top += text.Height + size * .7;
                }
            }
            var bitmap = new RenderTargetBitmap(pageWidth, pageHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            pages.Add((bitmap, truth.ToArray()));
            texts.Clear();
            truth.Clear();
            y = Margin;
        }
    }

    internal static BitmapSource Scale(BitmapSource source, double scale)
    {
        if (scale == 1) return source;
        int width = (int)Math.Round(source.PixelWidth * scale), height = (int)Math.Round(source.PixelHeight * scale);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (DrawingContext context = visual.RenderOpen())
            context.DrawImage(source, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Write(string directory, string name, OcrResult result) =>
        File.WriteAllText(DiagnosticOutputPaths.Child(directory, name),
            JsonSerializer.Serialize(new { result.Status, result.Text, result.Lines, ElapsedMs = result.Elapsed.TotalMilliseconds },
                new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8);

    private static void Save(string directory, string name, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(stream);
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}

internal static class EnglishReadingTruth
{
    internal static readonly string[] Sentences =
    [
        "Good morning. How are you today?",
        "I will call you after lunch.",
        "Please close the door when you leave.",
        "The store opens at nine and closes at six.",
        "My brother lives in a small town near the sea.",
        "It is raining, so take an umbrella.",
        "We had dinner with our friends last night.",
        "Can you help me carry these boxes?",
        "The coffee is too hot to drink.",
        "She reads a book before going to bed.",
        "Save",
        "Cancel",
        "Open file",
        "Settings",
        "Delete all items",
        "Sign in with your email address",
        "Your changes have been saved.",
        "Are you sure you want to delete this file?",
        "No internet connection. Check your network and try again.",
        "Click Next to continue.",
        "Press Ctrl+S to save the document.",
        "The file could not be found.",
        "This field is required.",
        "Update available. Restart the app to install it.",
        "Enter your password.",
        "Show more",
        "The meeting is on March 3, 2025 at 2:30 PM.",
        "The package weighs 2.5 kg and costs $19.99.",
        "Download speed is 120 Mbps.",
        "Version 5.3.1 was released on November 12.",
        "The room is 4 meters wide and 6 meters long.",
        "About 75% of users opened the app twice a day.",
        "The battery lasts up to 10 hours.",
        "Call 010-1234-5678 before 6 p.m.",
        "The temperature will drop to -5°C tonight.",
        "Order #4821 will arrive in 3 to 5 days.",
        "The function returns null if the list is empty.",
        "Restart the server after you change the configuration file.",
        "This method is deprecated and will be removed in the next version.",
        "The API returns a JSON object with two fields.",
        "Use a background thread for long-running tasks.",
        "The build failed because a dependency is missing.",
        "Memory usage grows when the cache is never cleared.",
        "Run npm install to download the packages.",
        "Each request must include a valid access token.",
        "The test passes on Windows but fails on Linux.",
        "Our team moved to a new office last month. The new building is close to the station. Everyone is happy with the change.",
        "The library is open every day. You can borrow up to five books at a time.",
        "I tried to fix the printer this morning. It still does not work. I will ask the technician tomorrow.",
        "Thank you for your order. We will send you an email when it ships.",
        "The weather was nice, so we walked to the park. The children played on the grass for two hours.",
        "First, open the settings page. Then choose a language. Finally, click Save.",
        "This app captures your screen. You can pin the image and keep it on top of other windows.",
        "Learn more about privacy",
        "Copy to clipboard",
        "Recent files",
        "Drag a file here or click to browse.",
        "Sorry, something went wrong. Please try again later.",
        "Dr. Smith will see you at 10 a.m. on Monday.",
        "The U.S. team won the game, e.g. by two points.",
    ];

    internal static readonly string[] Interface =
    [
        "File  Edit  View  Window  Help",
        "Open in new window",
        "Sign out of your account",
        "Recently opened folders",
        "Check for updates",
        "Turn on notifications",
        "Show hidden files and folders",
        "Paste as plain text",
        "Zoom in  Zoom out  Reset zoom",
        "Keyboard shortcuts",
        "Privacy and security",
        "Clipboard history",
        "Snipping Tool",
        "Microsoft Copilot",
        "Bluetooth and devices",
        "Network and internet",
        "Personalization",
        "Accessibility options",
        "Image size and position",
        "Also known as screen capture, it shows what is on the screen.",
        "Most of the box shows two windows of the same app.",
        "Press the Windows logo key + Shift + S to open the tool.",
        "Select an area or choose a window to capture.",
        "Saved to your Pictures folder.",
    ];

    internal const string Dense =
        "The library opens a short message when a capture is saved, so you can copy it, pin it, or open the folder where it was " +
        "stored. Most of the options are also available from the tray icon, and the keyboard shortcuts work even when the app " +
        "window is closed. If a picture shows text, you can select the words with the mouse, copy them as plain text, or ask " +
        "the app to translate them into your own language. Large screenshots of web pages, source code, spreadsheets and " +
        "settings windows are read the same way, and the result keeps the position of every line so that the translation " +
        "can be shown over the original picture.";
}

public sealed class OcrEnglishReadingFactAttribute : FactAttribute
{
    public OcrEnglishReadingFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OcrEnglishReadingDiagnosticTests.OutputVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable)))
            Skip = "Set MYCAPTURE_OCR_ENGLISH_READING and MYCAPTURE_PIN_OVERLAY_MODELS to measure English reading.";
    }
}

public sealed class OcrEnglishReadingCorpusFactAttribute : FactAttribute
{
    public OcrEnglishReadingCorpusFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OcrEnglishReadingDiagnosticTests.OutputVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayEvidenceTests.ModelsVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PinTranslationOverlayCorpusTests.CorpusVariable)))
            Skip = "Set MYCAPTURE_OCR_ENGLISH_READING, MYCAPTURE_PIN_OVERLAY_MODELS and MYCAPTURE_PIN_OVERLAY_CORPUS to read the corpus.";
    }
}
