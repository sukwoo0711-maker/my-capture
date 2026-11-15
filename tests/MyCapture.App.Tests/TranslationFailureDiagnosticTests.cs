using System.Collections.Concurrent;
using System.Diagnostics;
using MyCapture.App.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Localization;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// Opt-in end-to-end diagnosis of pin translation with the product's own store, HTTP client, loader and recognizers.
/// Runs only with MYCAPTURE_TRANSLATION_DIAGNOSIS naming an output folder; the first run downloads the published
/// model into that folder's own profile. Without the variable every test returns at once.
/// </summary>
public sealed class TranslationFailureDiagnosticTests(ITestOutputHelper output) : KoreanCaptionTest
{
    private const string OutputVariable = "MYCAPTURE_TRANSLATION_DIAGNOSIS";
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

    private static string? Out => Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } value ? value : null;

    [Fact]
    public Task EveryPinKindFromAFreshProfile() => Run(async () =>
    {
        if (Out is null) return;
        string profile = Environment.GetEnvironmentVariable("MYCAPTURE_TRANSLATION_DIAGNOSIS_PROFILE") is { Length: > 0 } named
            ? named : "profile";
        bool package = string.Equals(Environment.GetEnvironmentVariable("MYCAPTURE_TRANSLATION_DIAGNOSIS_PACKAGE"), "1", StringComparison.Ordinal);
        if (!Path.IsPathFullyQualified(Out)) throw new IOException("Diagnosis requires an absolute output directory.");
        string outRoot = DiagnosticOutputPaths.Create(Out);
        string root = DiagnosticOutputPaths.Create(DiagnosticOutputPaths.Child(outRoot, profile));
        // The Store build keeps models in the package's LocalCache and its library in LocalState.
        AppPaths paths = package
            ? AppPaths.CreateForPackage(DiagnosticOutputPaths.Child(root, "LocalState"), DiagnosticOutputPaths.Child(root, "LocalCache"), DiagnosticOutputPaths.Child(root, "Pictures"))
            : AppPaths.CreateForRoot(root);
        var sink = new LogSink();
        var lines = new List<string>
        {
            $"profile {paths.TranslationModelsRoot} package={package} culture={UiText.Culture.Name} cpus={Environment.ProcessorCount} "
            + $"os={Environment.OSVersion.VersionString} x64={Environment.Is64BitProcess}",
        };
        var store = new TranslationModelStore(paths, new SinkLog<TranslationModelStore>(sink));
        using var service = new TextTranslationService(store, new SinkLog<TextTranslationService>(sink));
        // Visible pins run the product's reveal, progress and consent paths that a hidden pin skips.
        bool visible = string.Equals(Environment.GetEnvironmentVariable("MYCAPTURE_TRANSLATION_DIAGNOSIS_VISIBLE"), "1", StringComparison.Ordinal);
        lines.Add($"visible={visible}");
        var manager = new PinManager(() => new PinSettings(), new SinkLog<PinManager>(sink));
        if (!visible)
        {
            manager.ShowPinForTest = static (_, _) => { };
            manager.ActivatePinForTest = static _ => { };
        }
        manager.ConfigureTranslation(direction => direction == PinTranslationDirection.EnglishToKorean ? service : null);
        using var neural = new NeuralOcrEngine(new OcrModelStore(paths, NullLogger<OcrModelStore>.Instance), new SinkLog<NeuralOcrEngine>(sink));
        var windows = new WindowsOcrService(new SinkLog<WindowsOcrService>(sink));
        lines.Add("windows ocr languages: " + string.Join(",", windows.SupportedLanguages));
        IOcrService ocr = new CodeOcrService(new CaptureOcrService(windows, neural, new SinkLog<CaptureOcrService>(sink)));
        var settings = new OcrSettings();
        lines.Add("ocr preferred: " + string.Join(",", settings.PreferredLanguages) + " quality " + settings.Quality);
        manager.ConfigureImageTranslation((image, windowsReading, token) =>
            PinTranslationEngines.RecognizeResultAsync(ocr, image, settings, token, windowsReading));
        lines.Add($"installed before: {await store.IsInstalledAsync(CancellationToken.None)} ({store.InstalledBytes} of {store.TotalBytes} bytes)");
        try
        {
            foreach ((string name, Func<Task<PinWindow>> open) in Pins(manager))
            {
                PinWindow pin = await open();
                lines.AddRange(await TranslatePin(pin, name, sink));
                output.WriteLine(lines[^1]);
                pin.Close();
                await Task.WhenAny(pin.TranslationWork ?? Task.CompletedTask, Task.Delay(TimeSpan.FromSeconds(30)));
            }
        }
        finally { manager.CloseAll(); }
        lines.Add($"installed after: {await store.IsInstalledAsync(CancellationToken.None)}");
        File.WriteAllLines(DiagnosticOutputPaths.Child(outRoot, $"pins-{profile}.tsv"), lines, Encoding.UTF8);
    });

    private static IEnumerable<(string, Func<Task<PinWindow>>)> Pins(PinManager manager)
    {
        const string sentence = "Save the document before closing the window.";
        yield return ("text-sentence", () => Task.FromResult(manager.PinRenderedText(Blank(), sentence, false)));
        yield return ("text-paragraph", () => Task.FromResult(manager.PinRenderedText(Blank(),
            "The update adds a new export option. Open the settings page and choose where the files are saved. "
            + "If the folder is missing, the app creates it the next time you capture the screen.", false)));
        yield return ("text-table", () => Task.FromResult(manager.PinRenderedText(Blank(),
            "Name\tStatus\tNote\nBuild\tReady\tWaiting for the review\nTests\tFailed\tThe network was slow", true)));
        yield return ("markdown", () => Paste(manager, PinContent.FromMarkdown(Blank(),
            "# Getting started\n\nInstall the package and restart the editor.\n\n- Open the command palette.\n- Run the build task.\n\n"
            + "```csharp\nvar x = 1; // keep this value\n```")));
        yield return ("code", () => Task.FromResult(manager.PinRenderedText(Blank(),
            "// Returns the number of open windows before the app closes.\nint CountWindows() => windows.Count;", false)));
        yield return ("picture-article", () => Task.FromResult(manager.PinImage(Article(1000, 420, 16, dark: false))));
        yield return ("picture-dark-ui", () => Task.FromResult(manager.PinImage(Article(900, 300, 14, dark: true))));
        string guides = Path.Combine(AppContext.BaseDirectory, "Assets", "SettingsGuides");
        foreach (string file in new[] { "G01.editor.en.after.png", "G02.windows.en.after.png" })
        {
            string path = Path.Combine(guides, file);
            if (!File.Exists(path)) continue;
            yield return ("picture-" + file, () =>
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return Task.FromResult(manager.PinImage(image));
            });
        }
        // The same sentence again: a second translation in the same process.
        yield return ("text-sentence-again", () => Task.FromResult(manager.PinRenderedText(Blank(), "Check your settings after you sign in.", false)));
    }

    private static async Task<PinWindow> Paste(PinManager manager, PinContent content)
    {
        Assert.Equal(PasteResult.Pinned, await manager.PasteFromClipboardAsync(() => Task.FromResult(
            ClipboardImageReader.PinReadAttempt.Success(content))));
        return manager.SnapshotOpenPinWindows().Last();
    }

    private static async Task<List<string>> TranslatePin(PinWindow pin, string name, LogSink sink)
    {
        int logStart = sink.Count;
        var errors = new ConcurrentQueue<string>();
        void FirstChance(object? sender, FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is OperationCanceledException || errors.Count > 20) return;
            string site = "?";
            foreach (StackFrame frame in new StackTrace(1, false).GetFrames())
            {
                string? type = frame.GetMethod()?.DeclaringType?.FullName;
                if (type is null || !type.StartsWith("MyCapture.", StringComparison.Ordinal) || type.Contains("Tests", StringComparison.Ordinal)) continue;
                site = type[(type.LastIndexOf('.') + 1)..] + "." + frame.GetMethod()!.Name;
                break;
            }
            errors.Enqueue($"{e.Exception.GetType().Name}@{site}: {Short(e.Exception.Message, 160)}");
        }
        int prompts = 0;
        pin.ConfirmTranslationModelDownload = _ => { prompts++; return true; };
        AppDomain.CurrentDomain.FirstChanceException += FirstChance;
        var watch = Stopwatch.StartNew();
        string before = pin.TranslationState.ToString();
        pin.InvokeTranslationAction();
        Task work = pin.TranslationWork ?? Task.CompletedTask;
        bool done = await Task.WhenAny(work, Task.Delay(Limit)) == work;
        AppDomain.CurrentDomain.FirstChanceException -= FirstChance;
        string shown = pin.IsShowingTranslation ? Short(pin.DisplayedText ?? "", 120) : "(original)";
        var result = new List<string>
        {
            string.Join('\t', name, before, pin.TranslationState, done ? "done" : "TIMEOUT", watch.ElapsedMilliseconds + " ms",
                "prompts=" + prompts, "feedback=" + pin.FeedbackForTest.Text.ReplaceLineEndings(" "), "shown=" + shown.ReplaceLineEndings(" ")),
        };
        if (work.IsFaulted) result.Add("  work faulted: " + work.Exception!.GetBaseException().GetType().Name);
        result.AddRange(errors.Distinct().Select(error => "  first-chance " + error));
        result.AddRange(sink.Since(logStart).Where(line => !line.Contains("Pinned ", StringComparison.Ordinal)).Select(line => "  log " + line));
        return result;
    }

    private static string Short(string text, int length) => text.Length > length ? text[..length] : text;

    private static BitmapSource Blank()
    {
        var image = BitmapSource.Create(600, 240, 96, 96, PixelFormats.Bgra32, null, new byte[600 * 240 * 4], 600 * 4);
        image.Freeze();
        return image;
    }

    private static readonly string[] Sentences =
    [
        "Your changes are saved automatically when you close the window.",
        "Select a folder to keep the captured pictures and recordings.",
        "The network connection was lost while the file was uploading.",
        "Press the button again to stop the recording at any time.",
        "Settings that you change here apply to every new capture.",
        "Restart the application to finish installing the update.",
        "Some files could not be copied because the disk is full.",
        "Open the help page to learn more about keyboard shortcuts.",
    ];

    private static BitmapSource Article(int width, int height, double size, bool dark)
    {
        var visual = new DrawingVisual();
        var typeface = new Typeface("Segoe UI");
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(dark ? new SolidColorBrush(Color.FromRgb(32, 32, 32)) : Brushes.White, null, new Rect(0, 0, width, height));
            double y = 24;
            int index = 0;
            while (y + size * 1.8 < height - 16)
            {
                drawing.DrawText(new FormattedText(Sentences[index++ % Sentences.Length], CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, size, dark ? Brushes.Gainsboro : Brushes.Black, 1), new Point(32, y));
                y += size * (index % 3 == 0 ? 3 : 1.8);
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Task Run(Func<Task> action)
    {
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

    private sealed class LogSink
    {
        private readonly ConcurrentQueue<string> _lines = new();
        internal int Count => _lines.Count;
        internal IEnumerable<string> Since(int start) => _lines.Skip(start);
        internal void Add(string line) => _lines.Enqueue(line);
    }

    private sealed class SinkLog<T>(LogSink sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            sink.Add($"{typeof(T).Name} {logLevel}: {formatter(state, exception)}"
                + (exception is null ? "" : $" [{exception.GetType().Name} 0x{exception.HResult:X8}: {Short(exception.Message, 200)}"
                    + (exception.InnerException is { } inner ? $" <- {inner.GetType().Name}: {Short(inner.Message, 160)}" : "") + "]"));
    }
}
