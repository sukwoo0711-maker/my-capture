using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Pin translation quality diagnosis: the bare model output beside what a pin shows, for sentence sets, short
/// paragraphs translated whole and split, and long inputs. Off unless MYCAPTURE_PIN_TRANSLATION_QUALITY_OUT names a
/// folder; everything it writes stays under that folder.
/// <para>
/// To run it, copy the installed model to &lt;folder&gt;\approot\translation-models\opus-mt-en-ko-1 and put the inputs
/// in &lt;folder&gt;: quality-input.txt, multi-input.txt, values-input.txt, dev-input.txt (one item per line, \n for a
/// line break, \t for a tab, "TABLE:" in front of a table pin). A missing sentence-set file skips that test. Then,
/// with no network needed, <c>set MYCAPTURE_PIN_TRANSLATION_QUALITY_OUT=C:\t\trq</c> and
/// <c>dotnet test tests\MyCapture.App.Tests -c Release --filter FullyQualifiedName~PinTranslationQualityDiagnosticTests</c>.
/// To compare another engine, change the translators made by ProductService and MarianTranslator.Load below.
/// </para>
/// </summary>
public sealed class PinTranslationQualityDiagnosticTests(ITestOutputHelper output) : KoreanCaptionTest
{
    // The folder comes from the environment, so every path under it goes through DiagnosticOutputPaths:
    // a local folder, single-name children, no links.
    private static string? Out
    {
        get
        {
            string? folder = Environment.GetEnvironmentVariable("MYCAPTURE_PIN_TRANSLATION_QUALITY_OUT");
            if (string.IsNullOrWhiteSpace(folder)) return null;
            return DiagnosticOutputPaths.Create(folder);
        }
    }

    private static string AppRoot => DiagnosticOutputPaths.Child(Out!, "approot");
    private static string ModelDir => DiagnosticOutputPaths.Child(
        DiagnosticOutputPaths.Child(AppRoot, "translation-models"), TranslationModelCatalog.FolderName);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(150);

    [Fact]
    public Task Quality() => Run(async () =>
    {
        if (Out is null) return;
        string[] items = Lines("quality-input.txt");
        var log = new ListLogger();
        using TextTranslationService service = ProductService(log);
        using MarianTranslator raw = MarianTranslator.Load(ModelDir, TranslationModelCatalog.ModelFiles, MarianTranslator.DefaultThreads);
        var recording = new Recording(service);
        PinManager manager = Manager(service, log);
        var rows = new List<string> { "id\tsource\tmodel\tproduct\tpinState\tproductReview\tunits" };
        try
        {
            for (int id = 0; id < items.Length; id++)
            {
                string source = items[id];
                string model = raw.Translate([source.ReplaceLineEndings(" ")], CancellationToken.None)[0];
                recording.Calls.Clear();
                PinTranslationResult? result = await new PinTranslationService(recording, PinTranslationDirection.EnglishToKorean)
                    .TranslateAsync(PinContent.FromText(Image(), source, false), CancellationToken.None);
                string units = string.Join(" || ", recording.Calls.SelectMany(call => call.Select(t =>
                    $"{t.Source} => {t.Text}{(t.NeedsReview ? $" (missing:{string.Join(",", t.Missing)} invented:{string.Join(",", t.Invented)})" : "")}")));
                PinWindow pin = manager.PinRenderedText(Image(), source, false);
                pin.InvokeTranslationAction();
                await Task.WhenAny(pin.TranslationWork, Task.Delay(Limit));
                string product = pin.IsShowingTranslation ? pin.DisplayedText ?? "" : "(" + pin.TranslationState + ")";
                rows.Add(string.Join('\t', id + 1, Tsv(source), Tsv(model), Tsv(product), pin.TranslationState, result?.NeedsReview, Tsv(units)));
                pin.Close();
            }
        }
        finally { manager.CloseAll(); }
        File.WriteAllLines(DiagnosticOutputPaths.Child(Out, "quality-after.tsv"), rows, Encoding.UTF8);
    });

    [Fact]
    public Task WholeVsSplit() => Run(async () =>
    {
        if (Out is null) return;
        string[] items = Lines("multi-input.txt");
        var log = new ListLogger();
        using MarianTranslator raw = MarianTranslator.Load(ModelDir, TranslationModelCatalog.ModelFiles, MarianTranslator.DefaultThreads);
        using TextTranslationService wholeService = ProductService(log);
        using TextTranslationService splitService = ProductService(log);
        using TextTranslationService productService = ProductService(log);
        var recording = new Recording(productService);
        var rows = new List<string> { "id\ttokens\testimate\tsentences\twhole\twholeReview\tsplit\tsplitReview\tproduct\tproductCalls" };
        for (int id = 0; id < items.Length; id++)
        {
            string source = items[id];
            string[] sentences = [.. TranslationSegmenter.Sentences(source)];
            TranslatedText whole = (await wholeService.TranslateAsync([source], null, CancellationToken.None))[0];
            IReadOnlyList<TranslatedText> split = await splitService.TranslateAsync(sentences, null, CancellationToken.None);
            recording.Calls.Clear();
            PinTranslationResult? result = await new PinTranslationService(recording, PinTranslationDirection.EnglishToKorean)
                .TranslateAsync(PinContent.FromText(Image(), source, false), CancellationToken.None);
            rows.Add(string.Join('\t', id + 1, raw.CountTokens(source), PinTranslationService.EstimateTokens(source), sentences.Length,
                Tsv(whole.Text), whole.NeedsReview, Tsv(string.Join(" ", split.Select(t => t.Text))), split.Any(t => t.NeedsReview),
                Tsv(result?.Content.OriginalText ?? "(null)"), recording.Calls.Count));
        }
        File.WriteAllLines(DiagnosticOutputPaths.Child(Out, "whole-vs-split.tsv"), rows, Encoding.UTF8);
    });

    [Fact]
    public Task LongSentenceInputs() => Run(async () =>
    {
        if (Out is null) return;
        var rows = new List<string> { Row.Header };
        foreach (int words in new[] { 300, 2000, 10000 })
        {
            var log = new ListLogger();
            using TextTranslationService service = ProductService(log);
            PinManager manager = Manager(service, log);
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    string text = LongParagraphs(words, 900 + words + round);
                    PinWindow pin = manager.PinRenderedText(Image(), text, false);
                    Row row = await TranslatePin(pin, "long-" + words, round == 0 ? "first" : "second", text);
                    rows.Add(row.ToString());
                    Save($"long-{words}-{round}.txt", text + "\n\n=====\n\n" + (pin.DisplayedText ?? ""));
                    pin.Close();
                }
            }
            finally { manager.CloseAll(); }
        }
        File.WriteAllLines(DiagnosticOutputPaths.Child(Out, "long-timing.tsv"), rows, Encoding.UTF8);
    });

    [Fact]
    public Task TextInputsFirstAndSecond() => Run(async () =>
    {
        if (Out is null) return;
        var rows = new List<string> { Row.Header };
        var inputs = new (string Name, Func<int, string> Make)[]
        {
            ("short", r => r == 0 ? "Save the file before you close the window." : "Check your settings after you sign in."),
            ("paragraph", r => Paragraphs(80, 100 + r)),
            ("words-2000", r => Paragraphs(2000, 700 + r)),
            ("short-paragraph", r => r == 0 ? "Thank you for your order. We will send you an email when it ships."
                : "Sorry, something went wrong. Please try again later."),
            // A short paragraph whose whole translation is rejected and translated again sentence by sentence.
            ("fallback", r => r == 0 ? "Hi Tom. Are you free for lunch tomorrow?"
                : "Are you sure? This action cannot be undone."),
        };
        foreach ((string name, Func<int, string> make) in inputs)
        {
            var log = new ListLogger();
            using TextTranslationService service = ProductService(log);
            PinManager manager = Manager(service, log);
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    string text = make(round);
                    PinWindow pin = manager.PinRenderedText(Image(), text, false);
                    Row row = await TranslatePin(pin, name, round == 0 ? "first" : "second", text);
                    rows.Add(row.ToString());
                    output.WriteLine(row.ToString());
                    Save($"text-{name}-{round}.txt", text + "\n\n=====\n\n" + (pin.DisplayedText ?? ""));
                    pin.Close();
                }
            }
            finally { manager.CloseAll(); }
        }
        File.WriteAllLines(DiagnosticOutputPaths.Child(Out, "text-timing.tsv"), rows, Encoding.UTF8);
    });

    [Fact]
    public Task ValueSentences() => SentenceSet("values-input.txt", "values-after.tsv");

    [Fact]
    public Task DeveloperSentences() => SentenceSet("dev-input.txt", "dev-after.tsv");

    /// <summary>
    /// Each item through the bare model (cell by cell for a table) and through a pin. keptSource is true when a unit
    /// was held back by the checks; reasons lists what each held-back unit missed or invented.
    /// </summary>
    private Task SentenceSet(string input, string result) => Run(async () =>
    {
        if (Out is null || !File.Exists(DiagnosticOutputPaths.Child(Out, input))) return;
        string[] items = Lines(input);
        var log = new ListLogger();
        using TextTranslationService service = ProductService(log);
        using MarianTranslator raw = MarianTranslator.Load(ModelDir, TranslationModelCatalog.ModelFiles, MarianTranslator.DefaultThreads);
        var recording = new Recording(service);
        PinManager manager = Manager(service, log);
        var rows = new List<string> { "id\ttable\tsource\tmodel\tproduct\tsameAsModel\tkeptSource\treasons" };
        try
        {
            for (int id = 0; id < items.Length; id++)
            {
                bool table = items[id].StartsWith("TABLE:", StringComparison.Ordinal);
                string source = table ? items[id]["TABLE:".Length..] : items[id];
                string model = string.Join("\n", source.Split('\n').Select(line => string.Join("\t", line.Split('\t')
                    .Select(cell => cell.Trim().Length == 0 ? cell : raw.Translate([cell], CancellationToken.None)[0]))));
                recording.Calls.Clear();
                await new PinTranslationService(recording, PinTranslationDirection.EnglishToKorean)
                    .TranslateAsync(PinContent.FromText(Image(), source, table), CancellationToken.None);
                TranslatedText[] held = [.. recording.Calls.SelectMany(call => call).Where(t => t.NeedsReview)];
                string reasons = string.Join(" | ", held.Select(t =>
                    $"{t.Source} => {t.Text} (missing:{string.Join(",", t.Missing)} invented:{string.Join(",", t.Invented)})"));
                PinWindow pin = manager.PinRenderedText(Image(), source, table);
                pin.InvokeTranslationAction();
                await Task.WhenAny(pin.TranslationWork, Task.Delay(Limit));
                string product = pin.IsShowingTranslation ? pin.DisplayedText ?? "" : "(" + pin.TranslationState + ")";
                rows.Add(string.Join('\t', id + 1, table, Tsv(source), Tsv(model), Tsv(product),
                    Normalize(product) == Normalize(model), held.Length > 0, Tsv(reasons)));
                pin.Close();
            }
        }
        finally { manager.CloseAll(); }
        File.WriteAllLines(DiagnosticOutputPaths.Child(Out, result), rows, Encoding.UTF8);
    });

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ---------------------------------------------------------------- inputs (copied from the 5.3.1 diagnosis)

    private static readonly string[] Openers = ["After the update", "When the meeting ended", "Because the network was slow",
        "Although the report was late", "Before the holiday", "During the last quarter", "If the server restarts",
        "While the team was testing the build", "Since the new office opened", "As the price of memory rose"];
    private static readonly string[] Names = ["Anna", "the manager", "our support team", "the customer", "a new engineer",
        "the city council", "most students", "the designer", "my neighbor", "the sales department"];
    private static readonly string[] Middles = ["decided to move the files to a shared folder", "asked everyone to check their email",
        "found three small problems in the checkout page", "wrote a short guide for new users", "planned a trip to the mountains",
        "replaced the old printer with a faster one", "compared the results of two experiments", "cleaned the kitchen after dinner",
        "booked a table at the Italian restaurant", "fixed the broken link on the home page"];
    private static readonly string[] Ends = ["so that nobody would lose their work", "and nobody complained about it",
        "but the problem appeared again on Friday", "which saved about twenty minutes every day", "even though it was raining all morning",
        "because the deadline was getting closer", "and then went home early", "to make the next release easier",
        "without telling the rest of the group", "and shared the photos with the family"];

    private static string LongParagraphs(int words, int seed)
    {
        var random = new Random(seed);
        var builder = new StringBuilder();
        int count = 0, sentences = 0;
        while (count < words)
        {
            string sentence = $"{Openers[random.Next(10)]}, {Names[random.Next(10)]} {Middles[random.Next(10)]} {Ends[random.Next(10)]}.";
            count += sentence.Split(' ').Length;
            builder.Append(sentence).Append(++sentences % 5 == 0 ? "\n\n" : " ");
        }
        return builder.ToString().Trim();
    }

    private static readonly string[] Subjects = ["The app", "This window", "Your team", "The new version", "Our service", "The report",
        "The editor", "The library", "Each user", "The system"];
    private static readonly string[] Verbs = ["saves", "shows", "sends", "checks", "opens", "updates", "keeps", "removes", "copies", "loads"];
    private static readonly string[] Objects = ["the file", "a short message", "the latest changes", "your settings", "the list of tasks",
        "every picture", "the weekly report", "a backup copy", "the main menu", "the results"];
    private static readonly string[] Tails = ["before you close it", "every morning", "when the network is slow", "after you sign in",
        "in the background", "without asking again", "for all members of the team", "at the end of the day", "on this computer",
        "when you press the button"];

    private static IEnumerable<string> Prose(int words, int seed)
    {
        var random = new Random(seed);
        int count = 0;
        while (count < words)
        {
            string sentence = $"{Subjects[random.Next(10)]} {Verbs[random.Next(10)]} {Objects[random.Next(10)]} {Tails[random.Next(10)]}.";
            count += sentence.Split(' ').Length;
            yield return sentence;
        }
    }

    private static string Paragraphs(int words, int seed)
    {
        var builder = new StringBuilder();
        int i = 0;
        foreach (string sentence in Prose(words, seed)) builder.Append(sentence).Append(++i % 6 == 0 ? "\n\n" : " ");
        return builder.ToString().Trim();
    }

    // ---------------------------------------------------------------- helpers

    private static string[] Lines(string name) => [.. File.ReadAllLines(DiagnosticOutputPaths.Child(Out!, name), Encoding.UTF8)
        .Where(line => line.Length > 0)
        .Select(line => line.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal))];

    private static string Tsv(string text) => text.Replace("\t", "\\t", StringComparison.Ordinal).ReplaceLineEndings("\\n");

    private sealed record Row(string Name, string Round, int Words, int Chars, string End, long Ms, bool TimedOut, string Feedback)
    {
        internal const string Header = "name\tround\twords\tchars\tend\tms\ttimedOut\tfeedback";
        public override string ToString() => string.Join('\t', Name, Round, Words, Chars, End, Ms, TimedOut, Feedback);
    }

    private static async Task<Row> TranslatePin(PinWindow pin, string name, string round, string text)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var watch = Stopwatch.StartNew();
        pin.InvokeTranslationAction();
        Task work = pin.TranslationWork;
        bool done = await Task.WhenAny(work, Task.Delay(Limit)) == work;
        long ms = watch.ElapsedMilliseconds;
        return new Row(name, round, text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, text.Length,
            pin.TranslationState.ToString(), ms, !done, pin.FeedbackForTest.Text.ReplaceLineEndings(" "));
    }

    private sealed class ListLogger : ILogger<TextTranslationService>
    {
        private readonly ConcurrentQueue<string> _lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }

    private sealed class Recording(ITextTranslator inner) : ITextTranslator
    {
        internal List<IReadOnlyList<TranslatedText>> Calls { get; } = [];
        public long ModelDownloadBytes => inner.ModelDownloadBytes;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => inner.IsModelInstalledAsync(cancellationToken);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            inner.DownloadModelAsync(progress, cancellationToken);
        public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            IReadOnlyList<TranslatedText> result = await inner.TranslateAsync(sources, progress, cancellationToken);
            Calls.Add(result);
            return result;
        }
    }

    private static TranslationModelStore ProductStore() =>
        new(AppPaths.CreateForRoot(AppRoot), new HttpClient(new Forbidden()), NullLogger.Instance);

    private sealed class Forbidden : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }

    private static TextTranslationService ProductService(ListLogger log) => new(ProductStore(), log);

    private static PinManager Manager(ITextTranslator translator, ILogger log)
    {
        var manager = new PinManager(() => new PinSettings(), log)
        {
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
        };
        manager.ConfigureTranslation(translator);
        return manager;
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(600, 240, 96, 96, PixelFormats.Bgra32, null, new byte[600 * 240 * 4], 600 * 4);
        image.Freeze();
        return image;
    }

    private static void Save(string name, string text) => File.WriteAllText(DiagnosticOutputPaths.Child(DiagnosticOutputPaths.Child(Out!, "out"), name), text, Encoding.UTF8);

    private static Task Run(Func<Task> action)
    {
        if (Out is not null) Directory.CreateDirectory(DiagnosticOutputPaths.Child(Out, "out"));
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            using IDisposable language = UiText.UseLanguage("ko-KR");
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
