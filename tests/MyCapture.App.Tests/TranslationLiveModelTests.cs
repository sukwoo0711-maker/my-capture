using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Ocr;
using MyCapture.Core.Localization;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in checks against the real model: set MYCAPTURE_TRANSLATION_LIVE_MODEL to a folder that
/// holds the catalog files. Without it these tests return at once, so CI never needs the model.
/// </summary>
public sealed class TranslationLiveModelTests
{
    private const string ModelVariable = "MYCAPTURE_TRANSLATION_LIVE_MODEL";

    /// <summary>The probe measured 0.84 s for this screen on 4 threads; the budget leaves room for slower PCs.</summary>
    private static readonly TimeSpan FortyLineBudget = TimeSpan.FromSeconds(3);

    [Fact]
    public void ReproducesTheProbeTranslationsSentenceBySentence()
    {
        using LiveModel? live = LiveModel.CreateIfPresent();
        if (live is null) return;
        using MarianTranslator translator = MarianTranslator.Load(live.Store.ModelDirectory, TranslationModelCatalog.ModelFiles, 4);

        var mismatches = new List<string>();
        foreach (ProbeTranslation expected in ProbeTranslations())
        {
            string actual = Assert.Single(translator.Translate([expected.Text], CancellationToken.None));
            if (actual != expected.Translation) mismatches.Add($"{expected.Id}: {actual} | probe: {expected.Translation}");
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void FortyLineScreenTranslatesWithinTheBudget()
    {
        using LiveModel? live = LiveModel.CreateIfPresent();
        if (live is null) return;
        using MarianTranslator translator = MarianTranslator.Load(live.Store.ModelDirectory, TranslationModelCatalog.ModelFiles, 4);
        string[] screen = [.. ProbeTranslations().Take(40).Select(item => item.Text)];
        _ = translator.Translate(screen[..3], CancellationToken.None);

        var timings = new List<TimeSpan>();
        IReadOnlyList<string> translated = [];
        for (int round = 0; round < 3; round++)
        {
            var watch = Stopwatch.StartNew();
            translated = [.. screen.Chunk(TextTranslationService.BatchSize)
                .SelectMany(batch => translator.Translate(batch, CancellationToken.None))];
            timings.Add(watch.Elapsed);
        }

        Assert.Equal(40, translated.Count);
        Assert.All(translated, text => Assert.Contains(text, character => character is >= '가' and <= '힣'));
        TimeSpan median = timings.Order().ElementAt(1);
        Assert.True(median < FortyLineBudget, $"40 lines took {median.TotalMilliseconds:F0} ms (rounds {string.Join(", ",
            timings.Select(time => time.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))}).");
    }

    /// <summary>
    /// Renders the translation view for review when MYCAPTURE_TRANSLATION_EVIDENCE names an output
    /// folder: the consent with the download size, and a register table translated with the model.
    /// </summary>
    [Fact]
    public void RendersTheTranslationViewForReview()
    {
        string? evidence = Environment.GetEnvironmentVariable("MYCAPTURE_TRANSLATION_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidence)) return;
        using LiveModel? live = LiveModel.CreateIfPresent(copyFiles: false);
        if (live is null) return;
        string output = DiagnosticOutputPaths.Create(evidence);
        using var service = new TextTranslationService(live.Store, NullLogger<TextTranslationService>.Instance);
        Ocr410Fixture.RunSta(() =>
        {
            using var culture = UiText.UseLanguage("ko");
            var (image, result) = TranslationFixture.RegisterTableWithImage();
            // The same service before and after the model is in its folder.
            Render(service, image, result, DiagnosticOutputPaths.Child(output, "translation-consent-ko.png"));
            live.CopyFiles();
            string listed = Render(service, image, result, DiagnosticOutputPaths.Child(output, "translation-ko.png"));
            File.WriteAllText(DiagnosticOutputPaths.Child(output, "translation-ko.txt"), listed);
        });
    }

    /// <summary>Saves the window in the translation view and returns its numbered originals and translations.</summary>
    private static string Render(ITextTranslator translator, BitmapSource image, OcrResult result, string path)
    {
        var window = new OcrResultWindow { ShowInTaskbar = false, Width = 1280, Height = 900, Translator = translator };
        Ocr410Fixture.LoadTheme(window);
        try
        {
            window.ShowResult(result, "MyCapture OCR", image);
            window.SelectExtractionMode(OcrExtractionMode.Translation);
            OcrTranslationWindowTests.PumpUntil(() => window.TranslationWork.IsCompleted, 60_000);
            window.UpdateLayout();
            Ocr410Fixture.Settle();
            // The window fits itself to the monitor under the pointer; lay the content out at a
            // fixed size so the picture does not depend on where the pointer is.
            var size = new System.Windows.Size(1280, 900);
            var content = (System.Windows.FrameworkElement)window.Content;
            content.Measure(size);
            content.Arrange(new System.Windows.Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            Ocr410Fixture.Save(bitmap, path);
            return string.Join(Environment.NewLine + Environment.NewLine, window.TranslationPanel.Results.Select(item =>
                $"{item.Block.Number}. {item.Block.Text}{Environment.NewLine}{item.Translation.Text}"
                + string.Concat(OcrTranslationPanel.ReviewNotes(item.Translation).Select(note => Environment.NewLine + "! " + note))));
        }
        finally { window.CloseForExit(); }
    }

    internal static IReadOnlyList<ProbeTranslation> ProbeTranslations()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "opus-mt-en-ko-probe-translations.json");
        return JsonSerializer.Deserialize<List<ProbeTranslation>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    internal sealed record ProbeTranslation(string Id, string Text, string Translation);

    /// <summary>
    /// The supplied model files copied into a fresh owned profile, so the tests read and verify only
    /// their own copy. Source files are confined to the supplied folder like other diagnostic inputs.
    /// </summary>
    private sealed class LiveModel : IDisposable
    {
        private readonly string _source;
        private readonly string _root;
        private readonly HttpClient _http;

        private LiveModel(string source, string root, HttpClient http, TranslationModelStore store)
        {
            _source = source;
            _root = root;
            _http = http;
            Store = store;
        }

        internal TranslationModelStore Store { get; }

        internal static LiveModel? CreateIfPresent(bool copyFiles = true)
        {
            string? setting = Environment.GetEnvironmentVariable(ModelVariable);
            if (string.IsNullOrWhiteSpace(setting)) return null;
            string root = OwnedTestDirectory.Create("translate-live-");
            var http = new HttpClient(new RejectingHandler());
            var live = new LiveModel(Path.GetFullPath(setting), root, http,
                new TranslationModelStore(AppPaths.CreateForRoot(root), http, NullLogger.Instance));
            try
            {
                if (copyFiles) live.CopyFiles();
                return live;
            }
            catch
            {
                live.Dispose();
                throw;
            }
        }

        internal void CopyFiles()
        {
            Directory.CreateDirectory(Store.ModelDirectory);
            foreach (TranslationModelFile file in Store.Files)
                File.Copy(DiagnosticOutputPaths.Child(_source, file.FileName), Store.PathFor(file));
            Assert.True(Store.IsInstalled, $"{ModelVariable} must name a folder with every verified translation model file.");
        }

        public void Dispose()
        {
            _http.Dispose();
            OwnedTestDirectory.Delete(_root);
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
