using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Ocr;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class OcrResultLifetimeTests
{
    [Fact]
    public void CloseReleasesImageFactoryCallbackAndResultWhilePresenterRemainsAlive() => Ocr410Fixture.RunSta(() =>
    {
        using var presenter = CreatePresenter(new ImmediateOcr());
        OcrResultWindow window = WindowOf(presenter);
        WeakReference[] retained = ShowCompletedCapture(presenter);

        window.Close();
        AssertReleased(presenter, window);
        AssertCollected(retained);
        GC.KeepAlive(presenter);
    });

    [Fact]
    public void DisposeReleasesDisplayedCaptureEvenWhilePresenterRemainsReferenced() => Ocr410Fixture.RunSta(() =>
    {
        var presenter = CreatePresenter(new ImmediateOcr());
        OcrResultWindow window = WindowOf(presenter);
        WeakReference[] retained = ShowCompletedCapture(presenter);
        try
        {
            presenter.Dispose();
            AssertReleased(presenter, window);
            AssertCollected(retained);
            GC.KeepAlive(presenter);
        }
        finally { presenter.Dispose(); }
    });

    [Fact]
    public void DismissedWindowReopensAndRerunsWithOnlyTheNewCapture() => Ocr410Fixture.RunSta(() =>
    {
        var service = new ImmediateOcr();
        using var presenter = CreatePresenter(service);
        OcrResultWindow window = WindowOf(presenter);
        presenter.ShowRecognized(() => OcrRequest.FromBitmap(Image()), "first");
        PumpUntil(() => window.ImageOverlay.SourceImage is not null);
        window.Close();

        BitmapSource next = Image(80, 60);
        presenter.ShowRecognized(() => OcrRequest.FromBitmap(next), "second");
        PumpUntil(() => ReferenceEquals(next, window.ImageOverlay.SourceImage));
        Assert.True(window.IsVisible);
        Assert.Same(window, WindowOf(presenter));
        Assert.NotNull(window.Extraction);
        window.ImageOverlay.SetSelection(new OcrRect(1, 1, 40, 20));
        var rerun = Field<Button>(window, "_rerunButton");
        rerun.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => ReferenceEquals(next, window.ImageOverlay.SourceImage));
        Assert.Equal(3, service.Calls);
        Assert.NotNull(window.ImageOverlay.Selection);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CloseRejectsLateAndAlreadyQueuedRecognitionResults(bool completeBeforeClose) => Ocr410Fixture.RunSta(() =>
    {
        var service = new PendingOcr();
        var busy = new List<bool>();
        using var presenter = CreatePresenter(service, busy.Add);
        OcrResultWindow window = WindowOf(presenter);
        int writes = 0;
        presenter.ShowRecognized(() => OcrRequest.FromBitmap(Image()), "pending", _ => writes++);
        if (completeBeforeClose) service.Complete();
        window.Close();
        Assert.True(service.Token.IsCancellationRequested);
        if (!completeBeforeClose) service.Complete();
        Ocr410Fixture.Pump();

        AssertReleased(presenter, window);
        Assert.Equal(0, writes);
        Assert.False(busy[^1]);
    });

    [Fact]
    public void DismissalAbortsQueuedCachedImageAndDoesNotAffectTheNextPresentation() => Ocr410Fixture.RunSta(() =>
    {
        using var presenter = CreatePresenter(new ImmediateOcr());
        OcrResultWindow window = WindowOf(presenter);
        BitmapSource first = Image();
        presenter.ShowCached(Result(), "cached", () => OcrRequest.FromBitmap(first));
        // The source is frozen and decoded already; only its dispatcher publication is pending.
        window.Close();
        Ocr410Fixture.Pump();
        AssertReleased(presenter, window);

        BitmapSource next = Image(80, 60);
        presenter.ShowCached(Result(), "next", () => OcrRequest.FromBitmap(next));
        PumpUntil(() => ReferenceEquals(next, window.ImageOverlay.SourceImage));
        Assert.True(window.IsVisible);
        Assert.NotNull(window.Extraction);
        Ocr410Fixture.Pump();
        Assert.Same(next, window.ImageOverlay.SourceImage);
    });

    [Fact]
    public void FactoryThatDismissesThenThrowsCannotReopenAnErrorResult() => Ocr410Fixture.RunSta(() =>
    {
        using var presenter = CreatePresenter(new ImmediateOcr());
        OcrResultWindow window = WindowOf(presenter);
        presenter.ShowRecognized(() => OcrRequest.FromBitmap(Image()), "first");
        PumpUntil(() => window.ImageOverlay.SourceImage is not null);
        presenter.ShowRecognized(() =>
        {
            window.Close();
            throw new InvalidOperationException("Synthetic factory failure after dismissal.");
        }, "dismissed");
        Ocr410Fixture.Pump();
        AssertReleased(presenter, window);
    });

    private static OcrResultPresenter CreatePresenter(IOcrService service, Action<bool>? busy = null)
    {
        var presenter = new OcrResultPresenter(service, Dispatcher.CurrentDispatcher, busy ?? (_ => { }),
            NullLogger<OcrResultPresenter>.Instance);
        WindowOf(presenter).ShowInTaskbar = false;
        return presenter;
    }

    // Keep the allocating frame out of the collection assertion so JIT local lifetime cannot hide a root.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ShowCompletedCapture(OcrResultPresenter presenter)
    {
        var owner = new RequestOwner(Image());
        OcrResultWindow window = WindowOf(presenter);
        presenter.ShowRecognized(owner.CreateRequest, "large capture", owner.AcceptResult);
        PumpUntil(() => window.ImageOverlay.SourceImage is not null);
        Assert.Same(owner.Image, window.ImageOverlay.SourceImage);
        window.SelectExtractionMode(OcrExtractionMode.Table);
        window.UpdateLayout();
        Ocr410Fixture.Pump();
        return [new(owner), new(owner.Image), new(Field<OcrResult>(window, "_result")),
            new(window.Extraction!)];
    }

    private static void AssertReleased(OcrResultPresenter presenter, OcrResultWindow window)
    {
        Assert.False(window.IsVisible);
        Assert.Null(window.ImageOverlay.SourceImage);
        Assert.Empty(window.ImageOverlay.Regions);
        Assert.Null(window.ImageOverlay.Selection);
        Assert.Null(window.Extraction);
        Assert.Empty(window.ExtractionText);
        Assert.Empty(window.TableGrid.Columns);
        Assert.Empty(window.TableGrid.Items);
        Assert.Null(Field<object?>(window, "_result"));
        Assert.Null(Field<object?>(window, "_allExtraction"));
        Assert.Null(Field<object?>(window, "_deferredSelection"));
        Assert.Null(Field<object?>(window, "_translatedExtraction"));
        Assert.Null(Field<object?>(presenter, "_currentRequestFactory"));
        Assert.Null(Field<object?>(presenter, "_onFreshResult"));
        Assert.Equal(string.Empty, Field<string>(presenter, "_currentContext"));
    }

    private static void AssertCollected(IEnumerable<WeakReference> references)
    {
        WeakReference[] retained = references.ToArray();
        for (int attempt = 0; attempt < 4 && retained.Any(reference => reference.IsAlive); attempt++)
        {
            Ocr410Fixture.Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(retained, reference => Assert.False(reference.IsAlive, "A dismissed OCR payload is still rooted."));
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.Elapsed < TimeSpan.FromSeconds(5)) Ocr410Fixture.Pump();
        Assert.True(ready(), "The OCR presentation did not settle.");
    }

    private static OcrResultWindow WindowOf(OcrResultPresenter presenter) => Field<OcrResultWindow>(presenter, "_window");

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    private static BitmapSource Image(int width = 128, int height = 96)
    {
        BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[width * height * 4], width * 4);
        image.Freeze();
        return image;
    }

    private static OcrResult Result() => OcrResult.Success("sample", "en-US",
        [new OcrLine("sample", new OcrRect(2, 2, 30, 14), [new OcrWord("sample", new OcrRect(2, 2, 30, 14))])],
        TimeSpan.Zero);

    private sealed class RequestOwner(BitmapSource image)
    {
        internal BitmapSource Image { get; } = image;
        internal OcrRequest CreateRequest() => OcrRequest.FromBitmap(Image);
        internal void AcceptResult(OcrResult result) { }
    }

    private sealed class ImmediateOcr : IOcrService
    {
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        internal int Calls { get; private set; }
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result());
        }
    }

    private sealed class PendingOcr : IOcrService
    {
        private readonly TaskCompletionSource<OcrResult> _completion = new();
        public bool IsAvailable => true;
        public IReadOnlyList<string> SupportedLanguages => ["en-US"];
        internal CancellationToken Token { get; private set; }
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return _completion.Task;
        }
        internal void Complete() => _completion.SetResult(Result());
    }
}
