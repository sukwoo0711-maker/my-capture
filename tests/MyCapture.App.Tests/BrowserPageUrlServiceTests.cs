using System.Diagnostics;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class BrowserPageUrlServiceTests
{
    private static readonly RectD Bounds = new(-500, 100, 900, 700);
    private static readonly WindowCandidate[] Candidates =
        [new(new IntPtr(17), Bounds, "Observed page", ProcessId: 23)];

    [Theory]
    [InlineData("chrome", "Chrome_WidgetWin_1", true)]
    [InlineData("msedge", "Chrome_WidgetWin_1", true)]
    [InlineData("Brave", "Chrome_WidgetWin_1", true)]
    [InlineData("vivaldi", "Chrome_WidgetWin_1", true)]
    [InlineData("opera", "Chrome_WidgetWin_1", true)]
    [InlineData("firefox", "MozillaWindowClass", true)]
    [InlineData("Code", "Chrome_WidgetWin_1", false)]
    [InlineData("msedgewebview2", "Chrome_WidgetWin_1", false)]
    [InlineData("chrome", "MozillaWindowClass", false)]
    [InlineData("firefox", "Chrome_WidgetWin_1", false)]
    public void BrowserIdentity_RequiresMatchingProcessAndTopLevelClass(string process, string windowClass, bool expected) =>
        Assert.Equal(expected, BrowserPageUrlService.IsSupportedBrowser(process, windowClass));

    [Fact]
    public void DocumentValueWins_WithoutReadingUnsubmittedAddressBarOrNestedIframe()
    {
        var address = new Node(value: "https://typed-but-not-visited.example/");
        var iframe = Document("https://embedded.example/", "iframe");
        var document = Document("https://visited.example/path?query=one#section", "current", iframe);
        Node root = new(children: [address, document]);

        BrowserDocumentValue? result = Find(root);

        Assert.Equal("https://visited.example/path?query=one#section", result?.Url);
        Assert.Equal("current", result?.DocumentId);
        Assert.Equal(0, address.ValueReads);
        Assert.Equal(0, iframe.InfoReads);
        Assert.Equal(0, iframe.ValueReads);
    }

    [Fact]
    public void AddressBarAlone_CannotSupplyTheSourcePage()
    {
        var address = new Node(value: "https://typed.example/");
        Assert.Null(Find(new Node(children: [address])));
        Assert.Equal(0, address.ValueReads);
    }

    [Fact]
    public void LegacyDocumentValue_IsAcceptedWithoutInventingAScheme()
    {
        var document = new Node(isDocument: true, legacyValue: "https://visited.example/");
        Assert.Equal("https://visited.example/", Find(new Node(children: [document]))?.Url);
        Assert.Null(Find(new Node(children: [new Node(isDocument: true, legacyValue: "visited.example")])));
    }

    [Fact]
    public void ConflictingDocumentPatterns_AreAmbiguous()
    {
        var document = new Node(isDocument: true, value: "https://first.example/", legacyValue: "https://second.example/");
        Assert.Null(Find(new Node(children: [document])));
    }

    [Fact]
    public void HiddenTabsAreSkipped_ButMultipleVisibleDocumentsAreRejected()
    {
        var hiddenDocument = Document("https://hidden.example/", "hidden");
        var hiddenContainer = new Node(offscreen: true, children: [hiddenDocument]);
        var visible = Document("https://active.example/", "active");
        Assert.Equal("https://active.example/", Find(new Node(children: [hiddenContainer, visible]))?.Url);
        Assert.Equal(0, hiddenDocument.InfoReads);
        Assert.Null(Find(new Node(children: [visible, Document("https://other.example/", "other")])));
        Assert.Null(Find(new Node(children: [visible, Document("https://active.example/", "same-url-other-document")])));
    }

    [Fact]
    public void DocumentOutsideWindow_AndMissingDocumentIdentity_AreRejected()
    {
        var outside = new Node(isDocument: true, value: "https://visited.example/", bounds: new RectD(2000, 0, 500, 400));
        Assert.Null(Find(new Node(children: [outside])));
        Assert.Null(Find(new Node(children: [Document("https://visited.example/", string.Empty)])));
    }

    [Fact]
    public void DeepBrowserPlumbingCanExposeDocument_WhileEmptyAndOutsideDocumentsDoNotPoisonIt()
    {
        Node branch = Document("https://visited.example/", "actual-page");
        for (int depth = 0; depth < 12; depth++) branch = new Node(children: [branch]);
        var emptyFrame = Document("https://embedded-in-empty.example/", "empty-iframe");
        var outsideFrame = Document("https://embedded-outside.example/", "outside-iframe");
        var emptyDocument = new Node(isDocument: true, value: "https://placeholder.example/",
            bounds: RectD.Empty, children: [emptyFrame]);
        var outsideDocument = new Node(isDocument: true, value: "https://outside.example/",
            bounds: new RectD(3000, 100, 400, 300), children: [outsideFrame]);

        BrowserDocumentValue? found = Find(new Node(children: [emptyDocument, branch, outsideDocument]));

        Assert.Equal("https://visited.example/", found?.Url);
        Assert.Equal("actual-page", found?.DocumentId);
        Assert.Equal(0, emptyDocument.ValueReads);
        Assert.Equal(0, outsideDocument.ValueReads);
        Assert.Equal(0, emptyFrame.InfoReads);
        Assert.Equal(0, outsideFrame.InfoReads);
        Assert.Null(Find(new Node(children: [emptyDocument, outsideDocument])));
    }

    [Theory]
    [InlineData(50030, true, false, true)]
    [InlineData(50030, false, true, true)]
    [InlineData(50030, true, true, true)]
    [InlineData(50030, false, false, false)]
    [InlineData(50030, null, null, false)]
    [InlineData(50033, true, true, false)]
    [InlineData(50004, true, true, false)]
    public void OnlySemanticDocumentRolesCanSupplyUrls(int controlType, bool? controlElement, bool? contentElement,
        bool expected) => Assert.Equal(expected,
            BrowserPageUrlService.IsSemanticDocument(controlType, controlElement, contentElement));

    [Fact]
    public void RawLayoutDocumentWrapperIsTraversed_WithoutReadingItsValue()
    {
        var page = Document("https://visited.example/", "semantic-page");
        var wrapper = new Node(isDocument: true, semanticDocument: false,
            value: "https://layout-wrapper.example/", children: [page]);

        BrowserDocumentValue? found = Find(new Node(children: [wrapper]));

        Assert.Equal("https://visited.example/", found?.Url);
        Assert.Equal("semantic-page", found?.DocumentId);
        Assert.Equal(0, wrapper.ValueReads);
        Assert.Equal(1, page.ValueReads);
        Assert.Null(Find(new Node(children: [new Node(isDocument: true, semanticDocument: false,
            value: "https://layout-wrapper.example/")])));
    }

    [Fact]
    public void DefaultTraversalHandlesNormalBrowserChromeBeyond256Nodes()
    {
        Node[] browserControls = Enumerable.Range(0, 400).Select(_ => new Node()).ToArray();
        Node root = new(children: [.. browserControls, Document("https://visited.example/", "page")]);

        BrowserDocumentValue? found = Find(root);

        Assert.Equal("https://visited.example/", found?.Url);
        Assert.All(browserControls, control => Assert.Equal(0, control.ValueReads));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("data:text/html,page")]
    [InlineData("ms-settings:display")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("https://example.com/\nother")]
    [InlineData("https://")]
    public void UnsafeOrIncompleteDocumentUrlsAreNotReturned(string value) =>
        Assert.Null(Find(new Node(children: [Document(value, "document")])));

    [Fact]
    public void UnsupportedOuterDocument_DoesNotFallBackToAnIframe()
    {
        var iframe = Document("https://embedded.example/", "iframe");
        Assert.Null(Find(new Node(children: [Document("about:blank", "outer", iframe)])));
        Assert.Equal(0, iframe.InfoReads);
    }

    [Fact]
    public void TraversalLimitsRejectPartialResults_AndCancellationStopsTheWalk()
    {
        var descendants = Enumerable.Range(0, 40).Select(_ => new Node()).ToArray();
        Node root = new(children: [Document("https://visited.example/", "document"), .. descendants]);
        Assert.Null(BrowserPageUrlService.FindVisibleDocument(root, Bounds, maxNodes: 16));
        Assert.InRange(descendants.Sum(node => node.InfoReads), 1, 14);

        Node branch = Document("https://deep.example/", "deep");
        for (int index = 0; index < 12; index++) branch = new Node(children: [branch]);
        Assert.Null(BrowserPageUrlService.FindVisibleDocument(branch, Bounds, maxDepth: 8));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var untouched = new Node();
        Assert.Throws<OperationCanceledException>(() => BrowserPageUrlService.FindVisibleDocument(untouched, Bounds,
            cancellation.Token));
        Assert.Equal(0, untouched.InfoReads);
    }

    [Fact]
    public async Task SuccessfulProbeRunsOnBackgroundMtaThread_AndEmptyInputDoesNotProbe()
    {
        using var gate = new SemaphoreSlim(1, 1);
        int calls = 0;
        bool background = false;
        ApartmentState apartment = ApartmentState.Unknown;
        var service = new BrowserPageUrlService((_, _) =>
        {
            Interlocked.Increment(ref calls);
            background = Thread.CurrentThread.IsBackground;
            apartment = Thread.CurrentThread.GetApartmentState();
            return Result("https://visited.example/");
        }, TimeSpan.FromSeconds(1), gate);

        Assert.Empty(await service.ReadAsync([]));
        Assert.Equal(0, calls);
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> result = await service.ReadAsync(Candidates);
        Assert.Equal("https://visited.example/", Assert.Single(result).Value.Url);
        Assert.True(background);
        Assert.Equal(ApartmentState.MTA, apartment);
        Assert.Equal(1, calls);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task TimeoutRetainsSharedProbeSlot_AndLateResultNeverLeaksIntoNextRead()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        bool cancellationObserved = false;
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> Probe(IReadOnlyList<WindowCandidate> _, CancellationToken token)
        {
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test provider was not released.");
                cancellationObserved = token.IsCancellationRequested;
                return Result("https://stale.example/");
            }
            return Result("https://fresh.example/");
        }

        var firstService = new BrowserPageUrlService(Probe, TimeSpan.FromMilliseconds(40), gate);
        var secondService = new BrowserPageUrlService(Probe, TimeSpan.FromSeconds(1), gate);
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>? timedOut = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            Task<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>> pending = firstService.ReadAsync(Candidates);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            timedOut = await pending;
            Assert.Empty(timedOut);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(0, gate.CurrentCount);
            for (int index = 0; index < 20; index++) Assert.Empty(await secondService.ReadAsync(Candidates));
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
            Assert.True(SpinWait.SpinUntil(() => gate.CurrentCount == 1, TimeSpan.FromSeconds(3)));
        }

        Assert.True(cancellationObserved);
        Assert.Empty(timedOut!);
        IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> fresh = await secondService.ReadAsync(Candidates);
        Assert.Equal("https://fresh.example/", Assert.Single(fresh).Value.Url);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ProviderFailureReturnsNoMetadata_AndReleasesSlot()
    {
        using var gate = new SemaphoreSlim(1, 1);
        int calls = 0;
        var service = new BrowserPageUrlService((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Unavailable provider");
            return Result("https://visited.example/");
        }, TimeSpan.FromSeconds(1), gate);
        Assert.Empty(await service.ReadAsync(Candidates));
        Assert.Equal(1, gate.CurrentCount);
        Assert.Single(await service.ReadAsync(Candidates));
        Assert.Equal(2, calls);
    }

    private static IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot> Result(string url) =>
        new Dictionary<IntPtr, BrowserDocumentSnapshot>
        {
            [Candidates[0].Handle] = new(23, Candidates[0].Title, "document-runtime-id", url),
        };

    private static BrowserDocumentValue? Find(Node root) => BrowserPageUrlService.FindVisibleDocument(root, Bounds);
    private static Node Document(string url, string identity, params Node[] children) =>
        new(isDocument: true, value: url, identity: identity, children: children);

    private sealed class Node : IBrowserDocumentNode
    {
        private readonly BrowserDocumentNodeInfo _info;
        private readonly BrowserDocumentValues _values;
        private readonly string _identity;
        private readonly Node[] _children;
        private Node? _next;
        internal int InfoReads { get; private set; }
        internal int ValueReads { get; private set; }

        internal Node(bool isDocument = false, bool offscreen = false, string? value = null,
            string? legacyValue = null, string identity = "runtime-id", RectD? bounds = null,
            bool semanticDocument = true, params Node[] children)
        {
            _info = new(BrowserPageUrlService.IsSemanticDocument(isDocument ? 50030 : 50033,
                semanticDocument, semanticDocument), offscreen, bounds ?? Bounds);
            _values = new(value, legacyValue);
            _identity = identity;
            _children = children;
            for (int index = 0; index + 1 < children.Length; index++) children[index]._next = children[index + 1];
            if (children.Length > 0) children[^1]._next = null;
        }

        public BrowserDocumentNodeInfo ReadInfo() { InfoReads++; return _info; }
        public BrowserDocumentValues ReadDocumentValues() { ValueReads++; return _values; }
        public string ReadIdentity() => _identity;
        public IBrowserDocumentNode? FirstChild() => _children.FirstOrDefault();
        public IBrowserDocumentNode? NextSibling() => _next;
    }
}
