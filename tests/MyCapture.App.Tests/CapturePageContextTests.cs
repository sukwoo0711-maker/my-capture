using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.App.Gallery;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class CapturePageContextTests
{
    private const string Url = "https://example.com/pull/313?view=split#review";
    private static readonly WindowCandidate Browser = new(new IntPtr(10), new RectD(-400, 0, 400, 300), "Review — Browser", ProcessId: 42);
    private static BrowserDocumentSnapshot Page => new(42, Browser.Title, "document-1", Url);

    [Theory]
    [InlineData("stable", true)]
    [InlineData("url", false)]
    [InlineData("document", false)]
    [InlineData("process", false)]
    [InlineData("title", false)]
    [InlineData("missing", false)]
    public async Task OnlyStableDocumentAroundThePixelsIsFrozen(string change, bool expected)
    {
        int reads = 0;
        bool acquired = false;
        BrowserDocumentSnapshot after = change switch
        {
            "url" => Page with { Url = "https://example.com/other" },
            "document" => Page with { DocumentId = "document-2" },
            "process" => Page with { ProcessId = 43 },
            "title" => Page with { WindowTitle = "Other tab" },
            _ => Page
        };
        var result = await CapturePageContext.AcquireAsync([Browser], () =>
        {
            Assert.Equal(1, reads);
            acquired = true;
            return Frame();
        }, _ =>
        {
            reads++;
            if (reads == 2) Assert.True(acquired);
            IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> snapshot = reads == 2 && change == "missing"
                ? new Dictionary<IntPtr, BrowserDocumentSnapshot>()
                : new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = reads == 1 ? Page : after };
            return Task.FromResult(snapshot);
        });
        Assert.Equal(2, reads);
        Assert.Equal(expected ? Url : "", Assert.Single(result.Windows).SourcePageUrl);
        Assert.Empty(Browser.SourcePageUrl);
    }

    [Fact]
    public async Task OptionalMetadataFailureDoesNotFailOrRepeatPixelAcquisition()
    {
        int acquisitions = 0;
        var result = await CapturePageContext.AcquireAsync([Browser], () => { acquisitions++; return Frame(); },
            _ => throw new InvalidOperationException("unavailable accessibility provider"));
        Assert.Equal(1, acquisitions);
        Assert.Empty(Assert.Single(result.Windows).SourcePageUrl);
    }

    [Fact]
    public async Task NativeWindowSnapshotsImmediatelyBracketPixelsAfterTheDocumentProbe()
    {
        var events = new List<string>();
        var result = await CapturePageContext.AcquireAsync([Browser], () =>
        {
            events.Add("pixels");
            return Frame();
        }, _ =>
        {
            events.Add("document");
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = Page });
        }, () =>
        {
            events.Add("windows");
            return [Browser];
        });
        Assert.Equal(new[] { "document", "windows", "pixels", "windows", "document" }, events);
        Assert.Equal(Url, Assert.Single(result.Windows).SourcePageUrl);
    }

    [Theory]
    [InlineData("move-before-probe-completes")]
    [InlineData("move-during-frame")]
    [InlineData("raised-occluder")]
    [InlineData("new-window")]
    [InlineData("removed-window")]
    [InlineData("window-title")]
    [InlineData("process-reuse")]
    [InlineData("window-handle")]
    public async Task WindowLayoutOrIdentityDrift_OmitsUrlEvenWhenDocumentSnapshotRemainsEqual(string drift)
    {
        WindowCandidate other = new(new IntPtr(11), new RectD(-300, 50, 200, 200), "Other", ProcessId: 99);
        WindowCandidate[] original = [Browser, other];
        WindowCandidate moved = Browser with { ScreenBounds = new RectD(0, 0, 400, 300) };
        WindowCandidate[] changed = drift switch
        {
            "raised-occluder" => [other, Browser],
            "new-window" => [new(new IntPtr(12), Browser.ScreenBounds, "New", ProcessId: 100), Browser, other],
            "removed-window" => [Browser],
            "window-title" => [Browser with { Title = "Other title" }, other],
            "process-reuse" => [Browser with { ProcessId = 43 }, other],
            "window-handle" => [Browser with { Handle = new IntPtr(13) }, other],
            _ => [moved, other],
        };
        int acquisitions = 0, reads = 0, refreshes = 0;
        FrozenFrame expectedFrame = Frame();
        var result = await CapturePageContext.AcquireAsync(original, () =>
        {
            acquisitions++;
            return expectedFrame;
        }, _ =>
        {
            reads++;
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = Page });
        }, () =>
        {
            refreshes++;
            return drift == "move-before-probe-completes" || acquisitions > 0 ? changed : original;
        });
        Assert.Equal(1, acquisitions);
        Assert.Equal(2, refreshes);
        Assert.Equal(1, reads); // No reason to probe a later document once geometry is untrustworthy.
        Assert.Same(expectedFrame, result.Frame);
        Assert.All(result.Windows, candidate => Assert.Empty(candidate.SourcePageUrl));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CandidateRefreshFailure_OmitsMetadataButPreservesTheSingleCapture(int failingRead)
    {
        int refreshes = 0, acquisitions = 0;
        var result = await CapturePageContext.AcquireAsync([Browser], () =>
        {
            acquisitions++;
            return Frame();
        }, _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
            new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = Page }), () =>
        {
            if (++refreshes == failingRead) throw new InvalidOperationException("Window enumeration unavailable");
            return [Browser];
        });
        Assert.Equal(1, acquisitions);
        Assert.Equal(2, refreshes);
        Assert.Empty(Assert.Single(result.Windows).SourcePageUrl);
    }

    [Fact]
    public void UrlSelectionUsesFrozenFrontmostWindowAndNeverFallsThroughAnOccluder()
    {
        WindowCandidate page = Browser with { SourcePageUrl = Url };
        var selected = new RectD(-300, 50, 100, 100);
        Assert.Equal(Url, WindowCandidateService.ResolveSourcePageUrl([page], selected));
        var occluder = new WindowCandidate(new IntPtr(11), new RectD(-350, 0, 200, 300));
        Assert.Equal(Browser.Title, WindowCandidateService.ResolveSourceTitle([occluder, page], selected));
        Assert.Empty(WindowCandidateService.ResolveSourcePageUrl([occluder, page], selected));
        Assert.Empty(WindowCandidateService.ResolveSourcePageUrl([page], new RectD(0, 0, 100, 100)));
        WindowCandidate second = new(new IntPtr(12), new RectD(0, 0, 400, 300), "Second", "https://example.org/second", 43);
        Assert.Equal(second.SourcePageUrl, WindowCandidateService.ResolveSourcePageUrl([page, second], new RectD(20, 20, 100, 100)));
    }

    [Theory]
    [InlineData(Url, true)]
    [InlineData("", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void LibraryActionsOnlyExposeValidatedUrl(string? value, bool expected)
    {
        var tile = new GalleryItemViewModel(new CaptureRecord { SourcePageUrl = value! }, _ => "missing.png", 64);
        Assert.Equal(expected, tile.HasSourcePageUrl);
        Assert.Equal(expected ? Url : "", tile.SourcePageUrl);
    }

    private static FrozenFrame Frame()
    {
        BitmapSource bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[256], 32);
        bitmap.Freeze();
        return new FrozenFrame(bitmap, new RectD(-400, 0, 8, 8), null, 0);
    }
}
