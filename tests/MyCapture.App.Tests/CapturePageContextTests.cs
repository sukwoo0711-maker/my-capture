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

    [Theory]
    [InlineData(-450, -50, 500, 400, true)] // Fully covered on a negative-origin display.
    [InlineData(-400, 0, 400, 300, true)] // Equal bounds are also covered.
    [InlineData(0, 300, -400, -300, true)] // RectD edges normalize negative dimensions.
    [InlineData(-400, 0, 200, 300, false)] // Partly exposed.
    [InlineData(-400, 0, 399, 300, false)] // One physical pixel remains exposed.
    [InlineData(-400, 0, 399.999999, 300, false)] // Never round away a sliver.
    [InlineData(-400, 0, 400, 299.999999, false)]
    [InlineData(0, 0, 400, 300, false)] // Adjacent monitor/window.
    [InlineData(-500, 0, 101, 300, false)] // Tiny overlap is not containment.
    [InlineData(-400, 0, 0, 300, false)] // Empty geometry cannot establish occlusion.
    public async Task OnlyExactContainmentByAnEarlierWindowSkipsTheUrlProbe(
        double x, double y, double width, double height, bool covered)
    {
        var front = new WindowCandidate(new IntPtr(11), new RectD(x, y, width, height),
            Title: "", ProcessId: 99);
        WindowCandidate[] original = [front, Browser];
        var requests = new List<IntPtr[]>();
        var result = await CapturePageContext.AcquireAsync(original, Frame, candidates =>
        {
            requests.Add(candidates.Select(candidate => candidate.Handle).ToArray());
            IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> snapshot =
                candidates.Any(candidate => candidate.Handle == Browser.Handle)
                    ? new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = Page }
                    : new Dictionary<IntPtr, BrowserDocumentSnapshot>();
            return Task.FromResult(snapshot);
        }, () => original);

        Assert.Equal(covered ? new[] { front.Handle } : new[] { front.Handle, Browser.Handle }, requests[0]);
        Assert.Equal(covered ? 1 : 2, requests.Count);
        if (!covered) Assert.Equal(new[] { Browser.Handle }, requests[1]);
        Assert.Equal(original.Select(candidate => candidate.Handle), result.Windows.Select(candidate => candidate.Handle));
        Assert.Equal(front, result.Windows[0]);
        Assert.Equal(Browser with { SourcePageUrl = covered ? "" : Url }, result.Windows[1]);
        Assert.Empty(Browser.SourcePageUrl);
        if (covered)
        {
            var selection = new RectD(-300, 50, 100, 100);
            Assert.Equal(Browser.Title, WindowCandidateService.ResolveSourceTitle(result.Windows, selection));
            Assert.Empty(WindowCandidateService.ResolveSourcePageUrl(result.Windows, selection));
        }
    }

    [Fact]
    public async Task AContainingWindowBehindTheBrowserDoesNotPruneTheBrowser()
    {
        WindowCandidate behind = new(new IntPtr(11), new RectD(-450, -50, 500, 400), "Behind", ProcessId: 99);
        WindowCandidate[] original = [Browser, behind];
        var requests = new List<IntPtr[]>();
        var result = await CapturePageContext.AcquireAsync(original, Frame, candidates =>
        {
            requests.Add(candidates.Select(candidate => candidate.Handle).ToArray());
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = Page });
        }, () => original);

        Assert.Equal(new[] { Browser.Handle, behind.Handle }, requests[0]);
        Assert.Equal(new[] { Browser.Handle }, requests[1]);
        Assert.Equal(Url, result.Windows[0].SourcePageUrl);
        Assert.Equal(behind, result.Windows[1]);
    }

    [Fact]
    public async Task SeparatePartialOccludersAreNotCombinedToPruneAWindow()
    {
        WindowCandidate left = new(new IntPtr(11), new RectD(-400, 0, 200, 300));
        WindowCandidate right = new(new IntPtr(12), new RectD(-200, 0, 200, 300));
        WindowCandidate[] original = [left, right, Browser];
        var requests = new List<IntPtr[]>();
        var result = await CapturePageContext.AcquireAsync(original, Frame, candidates =>
        {
            requests.Add(candidates.Select(candidate => candidate.Handle).ToArray());
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = Page });
        }, () => original);

        Assert.Equal(original.Select(candidate => candidate.Handle), requests[0]);
        Assert.Equal(new[] { Browser.Handle }, requests[1]);
        Assert.Equal(Url, result.Windows[2].SourcePageUrl);
    }

    [Fact]
    public async Task AfterProbeReadsOnlySuccessfulBeforeCandidatesInOriginalOrder()
    {
        const string oldUrl = "https://example.com/old-capture";
        WindowCandidate failed = new(new IntPtr(11), new RectD(0, 0, 400, 300), "Unavailable", oldUrl, 43);
        WindowCandidate second = new(new IntPtr(12), new RectD(400, 0, 400, 300), "Second", oldUrl, 44);
        WindowCandidate[] original = [Browser with { SourcePageUrl = oldUrl }, failed, second];
        var secondPage = new BrowserDocumentSnapshot(44, second.Title, "document-2", "https://example.org/second");
        var requests = new List<IntPtr[]>();
        var result = await CapturePageContext.AcquireAsync(original, Frame, candidates =>
        {
            requests.Add(candidates.Select(candidate => candidate.Handle).ToArray());
            // The provider's dictionary order must not change the window probe order.
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot>
                {
                    [second.Handle] = secondPage,
                    [Browser.Handle] = Page,
                    [new IntPtr(999)] = Page,
                });
        }, () => original);

        Assert.Equal(2, requests.Count);
        Assert.Equal(original.Select(candidate => candidate.Handle), requests[0]);
        Assert.Equal(new[] { Browser.Handle, second.Handle }, requests[1]);
        Assert.Equal(Url, result.Windows[0].SourcePageUrl);
        Assert.Empty(result.Windows[1].SourcePageUrl);
        Assert.Equal(secondPage.Url, result.Windows[2].SourcePageUrl);
        Assert.All(original, candidate => Assert.Equal(oldUrl, candidate.SourcePageUrl));
    }

    [Theory]
    [InlineData("move", 1)]
    [InlineData("move", 2)]
    [InlineData("title", 2)]
    [InlineData("pid", 2)]
    [InlineData("remove", 2)]
    [InlineData("reorder", 2)]
    public async Task ExcludedWindowDriftStillInvalidatesAllUrls(string drift, int changedRefresh)
    {
        WindowCandidate covered = new(new IntPtr(11), new RectD(-300, 50, 100, 100), "Covered", ProcessId: 99);
        WindowCandidate[] original = [Browser, covered];
        WindowCandidate changed = drift switch
        {
            "title" => covered with { Title = "Changed" },
            "pid" => covered with { ProcessId = 100 },
            _ => covered with { ScreenBounds = new RectD(-299, 50, 100, 100) },
        };
        WindowCandidate[] mutated = drift switch
        {
            "remove" => [Browser],
            "reorder" => [covered, Browser],
            _ => [Browser, changed],
        };
        int reads = 0, refreshes = 0, acquisitions = 0;
        var result = await CapturePageContext.AcquireAsync(original, () => { acquisitions++; return Frame(); }, candidates =>
        {
            reads++;
            Assert.Equal(new[] { Browser.Handle }, candidates.Select(candidate => candidate.Handle));
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot> { [Browser.Handle] = Page });
        }, () => ++refreshes == changedRefresh ? mutated : original);

        Assert.Equal(1, acquisitions);
        Assert.Equal(1, reads);
        Assert.Equal(2, refreshes);
        Assert.Equal(original, result.Windows);
        Assert.All(result.Windows, candidate => Assert.Empty(candidate.SourcePageUrl));
    }

    [Fact]
    public async Task PrunedAndUnavailableCandidatesDoNotKeepUrlsFromAnEarlierCapture()
    {
        const string oldUrl = "https://example.com/old-capture";
        WindowCandidate front = new(new IntPtr(11), Browser.ScreenBounds, "", oldUrl, 99);
        WindowCandidate[] original = [front, Browser with { SourcePageUrl = oldUrl }];
        int reads = 0;
        var result = await CapturePageContext.AcquireAsync(original, Frame, candidates =>
        {
            reads++;
            Assert.Equal(new[] { front.Handle }, candidates.Select(candidate => candidate.Handle));
            return Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(
                new Dictionary<IntPtr, BrowserDocumentSnapshot>());
        }, () => original);

        Assert.Equal(1, reads);
        Assert.Equal(2, result.Windows.Count);
        Assert.All(result.Windows, candidate => Assert.Empty(candidate.SourcePageUrl));
        Assert.All(original, candidate => Assert.Equal(oldUrl, candidate.SourcePageUrl));
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
