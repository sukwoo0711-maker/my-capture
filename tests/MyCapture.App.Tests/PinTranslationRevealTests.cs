using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Diagnostics;
using MyCapture.App.Pinning;
using MyCapture.App.Themes;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Ocr;
using MyCapture.Ocr.Translation;
using MyCapture.Platform.Display;
using Xunit;
using Xunit.Abstractions;

namespace MyCapture.App.Tests;

/// <summary>
/// The live change of a pin into its translation ("E"): a sweep at the press, untranslated blocks dimming, blocks changing
/// in reading order as their sentences come back, one outline glow, and nothing left behind. A test clock drives every frame.
/// </summary>
public sealed class PinTranslationRevealTests(ITestOutputHelper output) : KoreanCaptionTest
{
    internal const string EvidenceVariable = "MYCAPTURE_TRANSLATION_REVEAL_EVIDENCE";

    private static readonly Dictionary<string, string> Phrases = new(StringComparer.Ordinal)
    {
        ["Document library"] = "문서 라이브러리",
        ["File Edit View"] = "파일 편집 보기",
        ["Save document"] = "문서 저장",
        ["Open folder"] = "폴더 열기",
        ["Copy picture"] = "그림 복사",
        ["Translate selection"] = "선택 영역 번역",
        ["Show original"] = "원본 보기",
        ["Close window"] = "창 닫기",
        ["Working with documents and pictures"] = "문서와 그림 관리",
        ["Create a new document."] = "새 문서를 만듭니다.",
        ["Save your work regularly."] = "작성한 내용을 저장합니다.",
        ["Review the work with your team."] = "동료와 작업을 검토합니다.",
        ["Check the latest changes."] = "변경 사항을 확인합니다.",
        ["Add pictures to your library."] = "그림을 라이브러리에 추가합니다.",
        ["Select the area you need."] = "필요한 부분을 선택합니다.",
        ["Keep the original picture safe."] = "원본을 안전하게 보관합니다.",
        ["Open it again later."] = "나중에 다시 열 수 있습니다.",
    };

    private static string Translate(string source) =>
        Phrases.TryGetValue(PinTranslationOverlay.NormalizeWhitespace(source), out string? korean) ? korean : "번역된 문장입니다.";

    [Theory]
    [InlineData("light-ui")]
    [InlineData("dark-ui")]
    public Task APictureChangesBlockByBlockOnTheTimelineAndLeavesNothingBehind(string scene) => RunAsync(async () =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw(scene, korean: false, lines);
        var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new BatchTranslator(Translate, batches: 2, batchOf: (unit, count) => unit < (count + 1) / 2 ? 0 : 1);
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation(async (_, _) =>
        {
            await reading.Task;
            return PinTranslationOverlayTests.Recognition([.. lines]);
        });
        string? evidence = EvidenceDirectory();
        try
        {
            PinWindow pin = manager.PinImage(picture);
            BitmapSource original = Render(pin);
            pin.InvokeTranslationAction();
            PinTranslationReveal reveal = Assert.IsType<PinTranslationReveal>(pin.TranslationRevealForTest);
            Assert.True(reveal.IsPicture);
            Assert.NotNull(pin.TranslationRevealLayerForTest);
            Save(evidence, scene, 0, pin);

            clock = 250;
            BitmapSource sweep = Advance(pin);
            Save(evidence, scene, 250, sweep);
            (int column, double gain) = Brightest(original, sweep);
            double expected = (-0.28 + (1.28 * 0.5) + 0.14) * picture.PixelWidth;
            output.WriteLine($"{scene} 250 ms: brightest column {column} (+{gain:F1}), band centre expected near {expected:F0}");
            Assert.InRange(column, expected - (0.14 * picture.PixelWidth), expected + (0.14 * picture.PixelWidth));
            Assert.True(gain > 2, "The sweep brightens the picture.");

            clock = 260;
            reading.SetResult(true);
            await Until(() => reveal.BlockCount > 0, "the blocks to translate");
            Assert.Equal(PinTranslationState.Translating, pin.TranslationState);

            clock = 500;
            Save(evidence, scene, 500, Advance(pin));
            clock = 700;
            translator.Release(0);
            await Until(() => reveal.PlacedBlockCount > 0, "the first batch's blocks");
            // The first blocks change while the rest of the translation still runs.
            Assert.Equal(PinTranslationState.Translating, pin.TranslationState);
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal(700, reveal.BlockStartsAfter!.Value.First);
            int previewed = reveal.PlacedBlockCount;

            clock = 800;
            Save(evidence, scene, 800, Advance(pin));
            clock = 1000;
            translator.Release(1);
            await pin.TranslationWork;
            // The translation is applied at once; only the drawing over it still changes.
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(PinTranslationState.Translated, pin.TranslationState);
            Assert.True(reveal.FinalApplied);
            Assert.IsType<DrawingImage>(pin.ImageSourceForTest);
            output.WriteLine($"{scene}: {reveal.BlockCount} blocks, {previewed} placed before the result, starts {reveal.BlockStartsAfter}");

            clock = 1100;
            Save(evidence, scene, 1100, Advance(pin));
            Assert.True(pin.IsTranslationRevealActive);
            clock = 1500;
            Save(evidence, scene, 1500, Advance(pin));
            double finished = reveal.FinishedAfter ?? throw new InvalidOperationException("The change has not finished.");
            output.WriteLine($"{scene}: last block ended at {finished:F0} ms");
            clock = finished + 125;
            Save(evidence, scene, "end-glow", Advance(pin));
            Assert.InRange(pin.TranslationGlowAmountForTest, 0.99, 1);
            Assert.NotEqual(pin.FrameColorForTest, PinFrameColor.Resolve(pin.FrameAccentBrush, pin.FrameNeutralBrush, false, false));
            clock = finished + 250;
            BitmapSource done = Advance(pin);
            Save(evidence, scene, "end", done);
            Assert.False(pin.IsTranslationRevealActive);
            Assert.Null(pin.TranslationRevealLayerForTest);
            Assert.Equal(0, pin.TranslationGlowAmountForTest);
            Assert.Equal(3, ((Grid)pin.Content).Children.Count);
            // What is left is the translation exactly as the pin shows it without the change.
            Assert.Equal(0, Difference(done, Render(pin)));
        }
        finally { manager.CloseAll(); }
    });

    public static TheoryData<string> Scenes => new() { "light-ui", "dark-ui", "table", "columns", "noisy-photo", "dense-settings" };

    /// <summary>
    /// Showing the change does not change the translation: with the pin's receiver and without it the translator gets the
    /// same calls with the same sentences, and the result's picture, text and review flag are identical to the byte.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenes))]
    public Task TheChangeLeavesTheCallsAndTheResultExactlyAsWithoutIt(string scene) => RunAsync(async () =>
    {
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw(scene, korean: false, lines);
        OcrResult recognition = PinTranslationOverlayTests.Recognition([.. lines]);
        async Task<(PinTranslationResult Result, BatchTranslator Translator, RecordingSink? Sink)> Translate(bool reveal)
        {
            var translator = new BatchTranslator(PinTranslationRevealTests.Translate, batches: 3, batchOf: (unit, _) => unit * 7 % 3);
            RecordingSink? sink = reveal ? new RecordingSink() : null;
            var progress = new Progress<TranslationProgress>(_ => { });
            Task<PinTranslationResult?> work = new PinTranslationService(translator)
            {
                Progress = progress,
                Reveal = sink,
            }.TranslateRecognizedAsync(recognition, PinContent.FromImage(picture), CancellationToken.None);
            // Each batch ends only after the preview of the one before has placed what it could.
            for (int batch = 0; batch < 3; batch++)
            {
                translator.Release(batch);
                if (sink is not null) await Settled(() => sink.Ready);
            }
            PinTranslationResult result = Assert.IsType<PinTranslationResult>(await work);
            return (result, translator, sink);
        }
        var plain = await Translate(reveal: false);
        var shown = await Translate(reveal: true);
        Assert.Equal(0, plain.Translator.BlockSinks);
        Assert.Equal(1, shown.Translator.BlockSinks);
        Assert.Equal(plain.Translator.Requests.Count, shown.Translator.Requests.Count);
        for (int call = 0; call < plain.Translator.Requests.Count; call++)
            Assert.Equal(plain.Translator.Requests[call], shown.Translator.Requests[call]);
        Assert.Equal(plain.Result.NeedsReview, shown.Result.NeedsReview);
        Assert.Equal(plain.Result.Document?.OriginalText, shown.Result.Document?.OriginalText);
        Assert.Equal(Pixels(plain.Result.Content.Image), Pixels(shown.Result.Content.Image));
        Assert.Null(plain.Result.BlockLayers);
        Assert.NotNull(shown.Result.BlockLayers);
        Assert.True(shown.Sink!.Known > 0, "The receiver learns the blocks.");
        output.WriteLine($"{scene}: {plain.Translator.Requests.Count} calls, {shown.Sink.Known} blocks known, "
            + $"{shown.Sink.Ready} placed early, {shown.Result.BlockLayers!.Count} placed in the end; picture and text identical");
    });

    internal sealed class RecordingSink : IPinTranslationRevealSink
    {
        private int _known, _ready;
        internal int Known => Volatile.Read(ref _known);
        internal int Ready => Volatile.Read(ref _ready);
        public void BlocksKnown(int pixelWidth, int pixelHeight, IReadOnlyList<PinRevealBlock> blocks) => Interlocked.Add(ref _known, blocks.Count);
        public void BlocksReady(IReadOnlyList<PinOverlayBlockLayer> layers) => Interlocked.Add(ref _ready, layers.Count);
    }

    [Theory]
    [InlineData(1.0, 0.05)]
    // At 1.5 the early blocks are drawn as vectors at the zoom and the result as the pin's resampled picture: the same
    // places, a little sharper (mean 1.06 before and 1.59 after the even-size change of the overlay).
    [InlineData(1.5, 2.0)]
    public Task BlocksPlacedBeforeTheResultLookLikeTheResultAlsoOnAZoomedPin(double zoom, double tolerance) => RunAsync(async () =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw("columns", korean: false, lines);
        // All sentences come back in the first call, the last block first; the second call returns nothing new.
        var translator = new BatchTranslator(Translate, batches: 2, batchOf: (_, _) => 0);
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition([.. lines])));
        try
        {
            PinWindow pin = manager.PinImage(picture);
            typeof(PinWindow).GetMethod("ApplyZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pin, [zoom, pin.Left, pin.Top, false]);
            pin.ForceZoomSettleForTest();
            pin.InvokeTranslationAction();
            PinTranslationReveal reveal = pin.TranslationRevealForTest!;
            await Until(() => reveal.BlockCount > 0, "the blocks to translate");
            translator.Release(0);
            // Every sentence is in, so the preview places every block it can; wait until it has stopped placing.
            await Until(() => reveal.PlacedBlockCount > 0, "blocks placed early");
            int placedEarly = await Settled(() => reveal.PlacedBlockCount);
            int blockCount = reveal.BlockCount;
            clock = 5000;
            BitmapSource early = Advance(pin);
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal(pin.Width, pin.TranslationRevealLayerForTest!.RenderSize.Width, 3);
            Assert.Equal(pin.Height, pin.TranslationRevealLayerForTest!.RenderSize.Height, 3);
            translator.Release(1);
            await pin.TranslationWork;
            // The final placement places the same blocks the preview did, each exactly where the preview put it.
            Assert.Equal(placedEarly, reveal.PlacedBlockCount);
            Assert.Equal(0, reveal.SwappedBlockCount);
            clock = 10000;
            Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);
            BitmapSource final = Render(pin);
            // Every block was placed early exactly as in the end, so swapping the drawing for the result shows no change.
            Save(EvidenceDirectory(), "zoom-" + zoom.ToString(CultureInfo.InvariantCulture), "early", early);
            Save(EvidenceDirectory(), "zoom-" + zoom.ToString(CultureInfo.InvariantCulture), "final", final);
            double difference = Difference(early, final);
            output.WriteLine($"zoom {zoom}: {blockCount} blocks, {placedEarly} placed early; mean channel difference early vs final {difference:F4}");
            Assert.True(difference < tolerance, $"The early blocks differ from the result ({difference:F4}).");
        }
        finally { manager.CloseAll(); }
    });

    /// <summary>
    /// The change asks for frames only while something moves: after the sweep and the dim settle it stands still until the
    /// next event (blocks known, blocks placed, the result), and asks again for that event only.
    /// </summary>
    [Fact]
    public Task TheChangeAsksForFramesOnlyWhileSomethingMoves() => RunAsync(async () =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw("light-ui", korean: false, lines);
        var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new BatchTranslator(Translate, batches: 2, batchOf: (unit, count) => unit < (count + 1) / 2 ? 0 : 1);
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation(async (_, _) =>
        {
            await reading.Task;
            return PinTranslationOverlayTests.Recognition([.. lines]);
        });
        try
        {
            PinWindow pin = manager.PinImage(picture);
            pin.InvokeTranslationAction();
            PinTranslationReveal reveal = pin.TranslationRevealForTest!;
            Assert.True(pin.TranslationRevealWantsFramesForTest, "The sweep starts at the press.");
            clock = 499;
            Advance(pin);
            Assert.True(pin.TranslationRevealWantsFramesForTest);
            clock = 500;
            BitmapSource still = Advance(pin);
            // The sweep is over and nothing is known yet (a long OCR or a model download): no more frames.
            Assert.False(pin.TranslationRevealWantsFramesForTest);
            Assert.False(pin.TranslationRevealClockAttachedForTest);
            Assert.True(pin.IsTranslationRevealActive);
            clock = 20000;
            Assert.Equal(0, Difference(still, Render(pin)));

            reading.SetResult(true);
            await Until(() => reveal.BlockCount > 0, "the blocks to translate");
            Assert.True(pin.TranslationRevealWantsFramesForTest, "The blocks dim from the moment they are known.");
            clock = 20119;
            Advance(pin);
            Assert.True(pin.TranslationRevealWantsFramesForTest);
            clock = 20120;
            Advance(pin);
            Assert.False(pin.TranslationRevealWantsFramesForTest);

            translator.Release(0);
            await Until(() => reveal.PlacedBlockCount > 0, "the first batch's blocks");
            Assert.True(pin.TranslationRevealWantsFramesForTest, "Placed blocks start changing.");
            clock = 30000;
            Advance(pin);
            Assert.False(pin.TranslationRevealWantsFramesForTest);

            translator.Release(1);
            await pin.TranslationWork;
            Assert.True(pin.TranslationRevealWantsFramesForTest, "The result's blocks change and the outline glows.");
            clock = 40000;
            Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);
            Assert.False(pin.TranslationRevealWantsFramesForTest);
            Assert.Null(pin.TranslationRevealLayerForTest);
            Assert.False(pin.TranslationRevealClockAttachedForTest);
        }
        finally { manager.CloseAll(); }
    });

    /// <summary>The real frame clock detaches while the change stands still and attaches again for the next event.</summary>
    [Fact]
    public Task TheFrameClockDetachesWhileStillAndAttachesForTheNextEvent() => RunAsync(async () =>
    {
        using var _ = new Motion(null);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw("light-ui", korean: false, lines);
        var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var translator = new BatchTranslator(Translate, batches: 1, batchOf: (_, _) => 0);
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation(async (_, _) =>
        {
            await reading.Task;
            return PinTranslationOverlayTests.Recognition([.. lines]);
        });
        try
        {
            PinWindow pin = manager.PinImage(picture);
            pin.InvokeTranslationAction();
            Assert.True(pin.TranslationRevealClockAttachedForTest);
            // The sweep takes half a second; then nothing moves until the picture's text is read.
            await Until(() => !pin.TranslationRevealClockAttachedForTest, "the frame clock to stop after the sweep");
            Assert.True(pin.IsTranslationRevealActive);
            Assert.NotNull(pin.TranslationRevealLayerForTest);

            reading.SetResult(true);
            await Until(() => pin.TranslationRevealClockAttachedForTest, "the frame clock to run for the known blocks");
            await Until(() => !pin.TranslationRevealClockAttachedForTest, "the frame clock to stop after the dim");
            Assert.True(pin.IsTranslationRevealActive);

            translator.Release(0);
            await pin.TranslationWork;
            await Until(() => !pin.IsTranslationRevealActive, "the change to end");
            Assert.False(pin.TranslationRevealClockAttachedForTest);
            Assert.Null(pin.TranslationRevealLayerForTest);
            Assert.Equal(0, pin.TranslationGlowAmountForTest);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task WithoutMotionTheTranslationAppearsAtOnceWithoutALayer() => RunAsync(async () =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock, animations: false);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw("light-ui", korean: false, lines);
        var translator = new BatchTranslator(Translate, batches: 1, batchOf: (_, _) => 0);
        translator.Release(0);
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition([.. lines])));
        try
        {
            PinWindow pin = manager.PinImage(picture);
            pin.InvokeTranslationAction();
            Assert.False(pin.IsTranslationRevealActive);
            await pin.TranslationWork;
            Assert.True(pin.IsShowingTranslation);
            Assert.False(pin.IsTranslationRevealActive);
            Assert.Null(pin.TranslationRevealLayerForTest);
            Assert.Equal(0, translator.BlockSinks);
            pin.InvokeTranslationAction();
            Assert.False(pin.IsShowingTranslation);
            Assert.False(pin.IsTranslationRevealActive);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task AFailureAfterSomeBlocksChangedFadesThemBackIn83Milliseconds() => RunAsync(async () =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw("light-ui", korean: false, lines);
        var translator = new BatchTranslator(Translate, batches: 2, batchOf: (unit, count) => unit < (count + 1) / 2 ? 0 : 1) { FailAt = 1 };
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition([.. lines])));
        try
        {
            PinWindow pin = manager.PinImage(picture);
            BitmapSource original = Render(pin);
            pin.InvokeTranslationAction();
            PinTranslationReveal reveal = pin.TranslationRevealForTest!;
            await Until(() => reveal.BlockCount > 0, "the blocks to translate");
            clock = 100;
            translator.Release(0);
            await Until(() => reveal.PlacedBlockCount > 0, "the first batch's blocks");
            clock = 400;
            Advance(pin);
            translator.Release(1);
            await pin.TranslationWork;
            Assert.Equal(PinTranslationState.Failed, pin.TranslationState);
            Assert.True(reveal.Aborting);
            clock = 440;
            Advance(pin);
            Assert.True(pin.IsTranslationRevealActive);
            clock = 483;
            BitmapSource back = Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);
            Assert.Null(pin.TranslationRevealLayerForTest);
            Assert.False(pin.IsShowingTranslation);
            Assert.Equal(0, Difference(original, back));
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ClosingDuringTheChangeLeavesNoLayerClockOrGlow() => RunAsync(async () =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw("light-ui", korean: false, lines);
        var translator = new BatchTranslator(Translate, batches: 2, batchOf: (unit, count) => unit < (count + 1) / 2 ? 0 : 1);
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition([.. lines])));
        try
        {
            PinWindow pin = manager.PinImage(picture);
            pin.InvokeTranslationAction();
            PinTranslationReveal reveal = pin.TranslationRevealForTest!;
            await Until(() => reveal.BlockCount > 0, "the blocks to translate");
            translator.Release(0);
            await Until(() => reveal.PlacedBlockCount > 0, "the first batch's blocks");
            clock = 120;
            Advance(pin);
            manager.CloseAll();
            Assert.True(pin.IsClosed);
            Assert.False(pin.IsTranslationRevealActive);
            Assert.Null(pin.TranslationRevealLayerForTest);
            Assert.True(reveal.Ended);
            Assert.Equal(0, pin.TranslationGlowAmountForTest);
            translator.Release(1);
            await pin.TranslationWork;
            Assert.False(pin.IsTranslationRevealActive);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task SwitchingViewsFadesBackIn83AndAgainIn167Milliseconds() => RunAsync(async () =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        var lines = new List<OcrLine>();
        BitmapSource picture = PinTranslationOverlayEvidenceTests.Draw("light-ui", korean: false, lines);
        var translator = new BatchTranslator(Translate, batches: 1, batchOf: (_, _) => 0);
        translator.Release(0);
        PinManager manager = Manager(translator);
        manager.ConfigureImageTranslation((_, _) => Task.FromResult(PinTranslationOverlayTests.Recognition([.. lines])));
        try
        {
            PinWindow pin = manager.PinImage(picture);
            pin.InvokeTranslationAction();
            await pin.TranslationWork;
            clock = 5000;
            BitmapSource translated = Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);

            pin.InvokeTranslationAction();
            Assert.False(pin.IsShowingTranslation);
            PinTranslationReveal back = Assert.IsType<PinTranslationReveal>(pin.TranslationRevealForTest);
            Assert.False(back.Glows);
            BitmapSource backFirst = Advance(pin);
            Save(EvidenceDirectory(), "switch", "translated", translated);
            Save(EvidenceDirectory(), "switch", "back-first", backFirst);
            double fromTranslation = ContentDifference(translated, backFirst);
            Assert.True(fromTranslation < OneLevel, $"The fade starts from the translation ({fromTranslation:F4}).");
            clock = 5082;
            Advance(pin);
            Assert.True(pin.IsTranslationRevealActive);
            clock = 5083;
            BitmapSource source = Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);

            pin.InvokeTranslationAction();
            Assert.True(pin.IsShowingTranslation);
            double fromSource = ContentDifference(source, Advance(pin));
            Assert.True(fromSource < OneLevel, $"The fade starts from the source ({fromSource:F4}).");
            clock = 5166;
            Advance(pin);
            Assert.True(pin.IsTranslationRevealActive);
            clock = 5250;
            Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);
            Assert.Equal(0, pin.TranslationGlowAmountForTest);
            Assert.Equal(1, translator.Calls);
        }
        finally { manager.CloseAll(); }
    });

    public static TheoryData<string> Documents => new() { "text", "table", "markdown" };

    [Theory]
    [MemberData(nameof(Documents))]
    public Task ADocumentPinDimsWhileTranslatingThenFadesAndGlowsOnce(string kind) => RunAsync(() =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        PinContent source = kind switch
        {
            "table" => PinContent.FromText(ClipboardTextRenderer.Render("Name\tStatus\r\nDocument\tReady", true),
                "Name\tStatus\r\nDocument\tReady", true),
            "markdown" => PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(Markdown), Markdown),
            _ => PinContent.FromText(ClipboardTextRenderer.Render(Sentence, false), Sentence, false),
        };
        PinContent translated = kind switch
        {
            "table" => PinContent.FromText(ClipboardTextRenderer.Render("이름\t상태\r\n문서\t준비됨", true), "이름\t상태\r\n문서\t준비됨", true),
            "markdown" => PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(MarkdownKorean), MarkdownKorean),
            _ => PinContent.FromText(ClipboardTextRenderer.Render(SentenceKorean, false), SentenceKorean, false),
        };
        var pin = new PinWindow(source, new PinViewState(source.Image.Width, source.Image.Height, 1, 1, 0.1), 200, 150);
        try
        {
            var frame = (pin.Width, pin.Height, pin.State.Zoom);
            BitmapSource before = Render(pin);
            pin.BeginTranslationReveal();
            pin.SetTranslationState(PinTranslationState.Translating);
            PinTranslationReveal reveal = Assert.IsType<PinTranslationReveal>(pin.TranslationRevealForTest);
            Assert.False(reveal.IsPicture);
            clock = 600;
            BitmapSource dimmed = Advance(pin);
            Assert.True(Difference(before, dimmed) > 0.5, "The content dims while it translates.");

            clock = 700;
            pin.ApplyTranslation(translated, false);
            Assert.True(pin.IsShowingTranslation);
            Assert.Equal(frame, (pin.Width, pin.Height, pin.State.Zoom));
            BitmapSource first = Advance(pin);
            Save(EvidenceDirectory(), "document-" + kind, "dimmed", dimmed);
            Save(EvidenceDirectory(), "document-" + kind, "first", first);
            double fromDimmed = ContentDifference(dimmed, first);
            Assert.True(fromDimmed < OneLevel, $"The change starts from the dimmed source ({fromDimmed:F4}).");
            clock = 866;
            Advance(pin);
            Assert.Equal(0, pin.TranslationGlowAmountForTest);
            clock = 867 + 125;
            Advance(pin);
            Assert.InRange(pin.TranslationGlowAmountForTest, 0.99, 1);
            clock = 867 + 250;
            BitmapSource after = Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);
            Assert.Equal(0, Difference(after, Render(pin)));
            Assert.Equal(frame, (pin.Width, pin.Height, pin.State.Zoom));
        }
        finally { pin.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ANewStateBeforeTheResultStopsTheDocumentChange() => RunAsync(() =>
    {
        double clock = 0;
        using var _ = new Motion(() => clock);
        PinContent source = PinContent.FromText(ClipboardTextRenderer.Render(Sentence, false), Sentence, false);
        var pin = new PinWindow(source, new PinViewState(source.Image.Width, source.Image.Height, 1, 1, 0.1), 200, 150);
        try
        {
            pin.BeginTranslationReveal();
            pin.SetTranslationState(PinTranslationState.Checking);
            Assert.True(pin.IsTranslationRevealActive);
            pin.SetTranslationState(PinTranslationState.ModelNeeded);
            Assert.True(pin.TranslationRevealForTest!.Aborting);
            clock = 83;
            Advance(pin);
            Assert.False(pin.IsTranslationRevealActive);
        }
        finally { pin.Close(); }
        return Task.CompletedTask;
    });

    private const string Sentence = "Save the document before closing the window.";
    private const string SentenceKorean = "창을 닫기 전에 문서를 저장합니다.";
    private const string Markdown = "# Release notes\n\nThe update improves startup time.";
    private const string MarkdownKorean = "# 릴리스 노트\n\n이번 업데이트는 시작 시간을 줄입니다.";

    internal static PinManager Manager(ITextTranslator translator)
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        {
            ShowPinForTest = static (_, _) => { },
            ActivatePinForTest = static _ => { },
            ReadPlacementMonitorForTest = static () => new MonitorInfo("fake",
                new RectD(0, 0, 3840, 2160), new RectD(0, 0, 3840, 2100), 96, true),
        };
        manager.ConfigureTranslation(translator);
        return manager;
    }

    internal static async Task Until(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Waited too long for " + what + ".");
            await Task.Delay(5);
        }
    }

    /// <summary>A value once it has stayed the same for half a second.</summary>
    private static async Task<int> Settled(Func<int> value)
    {
        int last = value();
        DateTime stable = DateTime.UtcNow, deadline = stable.AddSeconds(20);
        while (DateTime.UtcNow - stable < TimeSpan.FromMilliseconds(500))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Waited too long for the value to settle.");
            await Task.Delay(10);
            int now = value();
            if (now != last) (last, stable) = (now, DateTime.UtcNow);
        }
        return last;
    }

    private static BitmapSource Advance(PinWindow pin)
    {
        pin.AdvanceTranslationRevealForTest();
        return Render(pin);
    }

    /// <summary>The pin's content as drawn, without its bottom message (which changes with the state, not the change).</summary>
    internal static BitmapSource Render(PinWindow pin)
    {
        FrameworkElement feedback = pin.FeedbackSurfaceForTest;
        Visibility shown = feedback.Visibility;
        feedback.Visibility = Visibility.Collapsed;
        try { return RenderAll(pin); }
        finally { feedback.Visibility = shown; }
    }

    private static BitmapSource RenderAll(PinWindow pin)
    {
        var root = (Grid)pin.Content;
        root.Measure(new Size(pin.Width, pin.Height));
        root.Arrange(new Rect(0, 0, pin.Width, pin.Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(pin.Width), (int)Math.Ceiling(pin.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>The mean absolute difference per colour channel, 0..255.</summary>
    private static double Difference(BitmapSource a, BitmapSource b)
    {
        Assert.Equal((a.PixelWidth, a.PixelHeight), (b.PixelWidth, b.PixelHeight));
        byte[] first = Pixels(a), second = Pixels(b);
        long sum = 0;
        for (int i = 0; i < first.Length; i++) sum += Math.Abs(first[i] - second[i]);
        return sum / (double)first.Length;
    }

    /// <summary>
    /// The mean difference inside the outline: the outline's own colour follows the pin's state and activity (its eased
    /// emphasis), which the change does not draw.
    /// </summary>
    // Drawing the old view over the new one rounds a few glyph edges by one level (alpha 254 against 255); a view that
    // did not fade differs by more than a whole level on average.
    private const double OneLevel = 0.05;

    private static double ContentDifference(BitmapSource a, BitmapSource b)
    {
        const int inset = 3;
        var area = new Int32Rect(inset, inset, a.PixelWidth - (2 * inset), a.PixelHeight - (2 * inset));
        return Difference(new CroppedBitmap(a, area), new CroppedBitmap(b, area));
    }

    /// <summary>The column the change brightened most against the original, and by how much on average.</summary>
    private static (int Column, double Gain) Brightest(BitmapSource original, BitmapSource changed)
    {
        byte[] first = Pixels(original), second = Pixels(changed);
        int width = original.PixelWidth, height = original.PixelHeight;
        int best = -1;
        double gain = double.MinValue;
        for (int x = 0; x < width; x++)
        {
            long sum = 0;
            for (int y = 0; y < height; y++)
            {
                int offset = ((y * width) + x) * 4;
                sum += second[offset + 1] - first[offset + 1];
            }
            double mean = sum / (double)height;
            if (mean > gain) (best, gain) = (x, mean);
        }
        return (best, gain);
    }

    private static string? EvidenceDirectory() =>
        Environment.GetEnvironmentVariable(EvidenceVariable) is { Length: > 0 } root ? DiagnosticOutputPaths.Create(root) : null;

    private static void Save(string? directory, string scene, object time, PinWindow pin) => Save(directory, scene, time, Render(pin));

    private static void Save(string? directory, string scene, object time, BitmapSource bitmap)
    {
        if (directory is null) return;
        string name = time is int ms ? $"{scene}-{ms:D4}ms.png" : $"{scene}-{time}.png";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(DiagnosticOutputPaths.Child(directory, name));
        encoder.Save(file);
    }

    internal static Task RunAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
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

    /// <summary>Motion on (or off) and the test clock for this thread's pins, restored afterwards.</summary>
    internal sealed class Motion : IDisposable
    {
        private readonly bool? _animations = FluidMotion.AnimationsEnabledOverrideForTest;
        private readonly Func<double>? _clock = PinWindow.TranslationRevealClockForTest;

        internal Motion(Func<double>? clock, bool animations = true)
        {
            FluidMotion.AnimationsEnabledOverrideForTest = animations;
            PinWindow.TranslationRevealClockForTest = clock;
        }

        public void Dispose()
        {
            FluidMotion.AnimationsEnabledOverrideForTest = _animations;
            PinWindow.TranslationRevealClockForTest = _clock;
        }
    }

    /// <summary>
    /// Translates by a fixed table and ends its sentences in batches the test releases, handing each batch's results to a
    /// receiver that takes them, as the real translator does when a model call ends.
    /// </summary>
    internal sealed class BatchTranslator(Func<string, string> map, int batches, Func<int, int, int> batchOf) : ITextTranslator
    {
        private readonly TaskCompletionSource<bool>[] _gates = [.. Enumerable.Range(0, batches)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))];

        internal int? FailAt { get; init; }
        internal int Calls { get; private set; }
        /// <summary>The sentences of every call, in order.</summary>
        internal List<string[]> Requests { get; } = [];
        internal int BlockSinks { get; private set; }
        internal TimeSpan BatchDelay { get; init; }

        internal void Release(int batch) => _gates[batch].TrySetResult(true);
        internal void ReleaseAll() { foreach (TaskCompletionSource<bool> gate in _gates) gate.TrySetResult(true); }

        public long ModelDownloadBytes => 1;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<TranslationDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Never downloads.");

        public async Task<IReadOnlyList<TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<TranslationProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            lock (Requests) Requests.Add([.. sources]);
            var sink = progress as ITranslatedBlockSink;
            if (sink is not null) BlockSinks++;
            TranslatedText Result(int index) => new(sources[index], map(sources[index]), [], []);
            for (int batch = 0; batch < batches; batch++)
            {
                await _gates[batch].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (BatchDelay > TimeSpan.Zero) await Task.Delay(BatchDelay, cancellationToken).ConfigureAwait(false);
                if (FailAt == batch) throw new InvalidOperationException("The model failed.");
                int done = batch;
                var finished = Enumerable.Range(0, sources.Count).Where(index => batchOf(index, sources.Count) == done)
                    .Select(index => (index, Result(index))).ToList();
                progress?.Report(new TranslationProgress(Enumerable.Range(0, sources.Count).Count(index => batchOf(index, sources.Count) <= done),
                    sources.Count));
                if (finished.Count > 0) sink?.Translated(finished);
            }
            return [.. Enumerable.Range(0, sources.Count).Select(Result)];
        }
    }
}
