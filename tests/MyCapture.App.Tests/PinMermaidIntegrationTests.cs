using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Pinning;
using MyCapture.Core.Settings;
using Xunit;

namespace MyCapture.App.Tests;

// The renderer, clipboard and window display are injected: no personal data or browser launch.
public sealed class PinMermaidIntegrationTests
{
    private const string Source = "# 검토\r\n\r\n```mermaid\r\nflowchart LR\r\nA[복사] --> B[핀]\r\n```\r\n";

    [Fact]
    public Task PinAppearsBeforeDiagramAndKeepsExactSourceAfterCompletion() => Run(async () =>
    {
        var renderer = new FakeRenderer();
        PinManager manager = Manager(renderer);
        try
        {
            PinWindow pin = await Open(manager, Source);
            BitmapSource placeholder = pin.PinnedContent.Image;
            Assert.Null(pin.PinnedContent.MermaidResults);
            Assert.False(pin.MermaidWork.IsCompleted);
            await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            renderer.Completion.SetResult(new MermaidRenderResult(Image(1), null));
            await pin.MermaidWork;
            Assert.NotSame(placeholder, pin.PinnedContent.Image);
            Assert.NotNull(pin.PinnedContent.MermaidResults![0].Image);
            Assert.Equal(Source, pin.PinnedContent.OriginalText);
            Assert.Equal(pin.PinnedContent.LayoutHeightDip, pin.State.ImageHeightDip);
            string? copied = null;
            pin.OriginalTextCopyRequested += (_, source) => copied = source;
            pin.SimulateCtrlDoubleClickForTest();
            Assert.Equal(System.Text.Encoding.UTF8.GetBytes(Source), System.Text.Encoding.UTF8.GetBytes(copied!));
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task MarkdownWithoutDiagramNeverInvokesBrowserRenderer() => Run(async () =>
    {
        var renderer = new FakeRenderer();
        PinManager manager = Manager(renderer);
        try
        {
            PinWindow pin = await Open(manager, "# 제목\n\n**내용**을 확인합니다.");
            await pin.MermaidWork;
            Assert.Equal(0, renderer.Calls);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task ClosingPinCancelsWorkAndRejectsLateResult() => Run(async () =>
    {
        var renderer = new FakeRenderer();
        PinManager manager = Manager(renderer);
        PinWindow pin = await Open(manager, Source);
        await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        BitmapSource placeholder = pin.PinnedContent.Image;
        pin.Close();
        Assert.True(renderer.Token.IsCancellationRequested);
        renderer.Completion.SetResult(new MermaidRenderResult(Image(1), null));
        await pin.MermaidWork;
        Assert.Same(placeholder, pin.PinnedContent.Image);
        manager.CloseAll();
    });

    [Fact]
    public Task FailureKeepsPinAndStoresRecoverableCodeFallback() => Run(async () =>
    {
        var renderer = new FakeRenderer();
        PinManager manager = Manager(renderer);
        try
        {
            PinWindow pin = await Open(manager, Source);
            await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            renderer.Completion.SetException(new InvalidOperationException("Synthetic failure"));
            await pin.MermaidWork;
            Assert.False(pin.IsClosed);
            Assert.Null(pin.PinnedContent.MermaidResults![0].Image);
            Assert.NotEmpty(pin.PinnedContent.MermaidResults[0].Error!);
            Assert.Equal(Source, pin.PinnedContent.OriginalText);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task DiagramCompletionDoesNotReplaceRawViewAndProvenanceOrSource() => Run(async () =>
    {
        var renderer = new FakeRenderer();
        PinManager manager = Manager(renderer);
        try
        {
            PinWindow pin = await Open(manager, Source);
            pin.SetMarkdownSourceDisplay(true);
            await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            renderer.Completion.SetResult(new MermaidRenderResult(Image(1), null));
            await pin.MermaidWork;
            Assert.True(pin.IsShowingMarkdownSource);
            Assert.Equal(Source, pin.OriginalText);
            pin.SetMarkdownSourceDisplay(false);
            Assert.NotNull(pin.PinnedContent.MermaidResults![0].Image);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task TranslatedDocumentRendersItsOwnDiagramsAndTheOriginalKeepsItsOwn() => Run(async () =>
    {
        var renderer = new ScaleRenderer();
        PinManager manager = Manager(renderer);
        try
        {
            PinWindow pin = await Open(manager, Source);
            await pin.MermaidWork;
            Assert.Single(renderer.Sources);
            const string translatedSource = "# 검토\r\n\r\n```mermaid\r\nflowchart LR\r\nA[Copy] --> B[Pin]\r\n```\r\n";
            PinContent pendingTranslation = PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(translatedSource), translatedSource);
            pin.ApplyTranslation(pendingTranslation, false);
            Assert.True(pin.IsShowingTranslation);
            // A translated diagram has different labels, so the manager renders it again from the translated text.
            await pin.MermaidWork;
            Assert.Equal(2, renderer.Sources.Count);
            Assert.Contains("A[Copy]", renderer.Sources[1], StringComparison.Ordinal);
            var displayed = Assert.Single(Assert.IsType<System.Windows.Controls.Grid>(pin.Content).Children.OfType<System.Windows.Controls.Image>());
            Assert.NotSame(pendingTranslation.Image, displayed.Source);
            pin.SetTranslationDisplay(false);
            Assert.NotNull(pin.PinnedContent.MermaidResults![0].Image);
            Assert.Equal(Source, pin.OriginalText);
            // Switching back does not render again: both documents keep their finished bitmaps.
            pin.SetTranslationDisplay(true);
            await pin.MermaidWork;
            Assert.Equal(2, renderer.Sources.Count);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task MonitorDpiAndSettledZoomRequestFreshDiagramPixels() => Run(async () =>
    {
        var renderer = new ScaleRenderer();
        PinManager manager = Manager(renderer);
        try
        {
            PinWindow pin = await Open(manager, Source);
            await pin.MermaidWork;
            double logicalWidth = pin.PinnedContent.LayoutWidthDip;
            pin.SimulateDpiChangeForTest(2);
            await pin.MermaidWork;
            Assert.Equal(2, renderer.Scales[^1]);
            Assert.Equal(192, pin.PinnedContent.MermaidResults![0].Image!.DpiX);
            pin.HandleMouseWheel(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent }, ModifierKeys.None);
            pin.ForceZoomSettleForTest();
            await pin.MermaidWork;
            Assert.Equal(2 * pin.State.Zoom, renderer.Scales[^1], 6);
            Assert.Equal(logicalWidth, pin.PinnedContent.LayoutWidthDip, 3);
            Assert.Equal(Source, pin.PinnedContent.OriginalText);
        }
        finally { manager.CloseAll(); }
    });

    [Fact]
    public Task PureDiagramTranslatesOnlyItsLabels() => Run(async () =>
    {
        const string source = "flowchart LR\nA[Save the document before closing the window.] --> B[End]";
        PinContent content = PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source);
        Assert.True(PinTranslationService.CanTranslate(content));
        var service = new PinTranslationService(new LabelTranslator());
        PinTranslationResult result = Assert.IsType<PinTranslationResult>(await service.TranslateAsync(content, CancellationToken.None));
        Assert.Equal("flowchart LR\nA[문서를 저장합니다.] --> B[문서를 저장합니다.]", result.Content.OriginalText);
        Assert.True(result.Content.IsMarkdown);
        Assert.False(result.NeedsReview);
    });

    private sealed class LabelTranslator : MyCapture.Ocr.Translation.ITextTranslator
    {
        public long ModelDownloadBytes => 1;
        public Task<bool> IsModelInstalledAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public Task DownloadModelAsync(IProgress<MyCapture.Ocr.Translation.TranslationDownloadProgress>? progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MyCapture.Ocr.Translation.TranslatedText>> TranslateAsync(IReadOnlyList<string> sources,
            IProgress<MyCapture.Ocr.Translation.TranslationProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MyCapture.Ocr.Translation.TranslatedText>>(
                [.. sources.Select(static source => new MyCapture.Ocr.Translation.TranslatedText(source, "문서를 저장합니다.", [], []))]);
    }

    private static PinManager Manager(IMermaidRenderer renderer)
    {
        var manager = new PinManager(() => new PinSettings(), NullLogger.Instance)
        { ShowPinForTest = static (_, _) => { }, ActivatePinForTest = static _ => { } };
        manager.ConfigureMermaid(renderer);
        return manager;
    }

    private static async Task<PinWindow> Open(PinManager manager, string source)
    {
        PinContent content = PinContent.FromMarkdown(ClipboardMarkdownRenderer.Render(source), source);
        await manager.PasteFromClipboardAsync(() => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(content)));
        return Assert.Single(manager.SnapshotOpenPinWindows());
    }

    private static BitmapSource Image(double scale)
    {
        int width = (int)Math.Ceiling(400 * scale), height = (int)Math.Ceiling(200 * scale);
        var bitmap = BitmapSource.Create(width, height, 96 * scale, 96 * scale, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FakeRenderer : IMermaidRenderer
    {
        internal int Calls;
        internal CancellationToken Token;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<MermaidRenderResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<MermaidRenderResult> RenderAsync(string source, double scale, CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            Started.TrySetResult();
            return Completion.Task;
        }
    }

    private sealed class ScaleRenderer : IMermaidRenderer
    {
        internal readonly List<double> Scales = [];
        internal readonly List<string> Sources = [];
        public Task<MermaidRenderResult> RenderAsync(string source, double scale, CancellationToken cancellationToken)
        {
            Scales.Add(scale);
            Sources.Add(source);
            return Task.FromResult(new MermaidRenderResult(Image(scale), null));
        }
    }

    private static Task Run(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }
}
