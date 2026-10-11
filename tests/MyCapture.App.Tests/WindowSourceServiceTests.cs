using System.IO;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using AppObservation = MyCapture.Platform.Capture.WindowSourceService.AppObservation;

namespace MyCapture.App.Tests;

public sealed class WindowSourceServiceTests
{
    private const string EdgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string ExplorerPath = @"C:\Windows\explorer.exe";
    private const string Folder = @"C:\Users\test\Documents\계약";
    private static readonly WindowCandidate Browser = new(new IntPtr(10), new RectD(0, 0, 400, 300), "Docs - Edge",
        ProcessId: 42, ClassName: "Chrome_WidgetWin_1");
    private static readonly WindowCandidate Explorer = new(new IntPtr(20), new RectD(0, 0, 400, 300), "계약",
        ProcessId: 7, ClassName: "CabinetWClass");

    [Theory]
    [InlineData("Microsoft Edge", "Microsoft Edge")]
    [InlineData("  Google\tChrome ", "Google Chrome")]
    [InlineData(null, "msedge")]
    [InlineData("   ", "msedge")]
    public void FriendlyNamePrefersFileDescriptionThenFileName(string? description, string expected) =>
        Assert.Equal(expected, SourceAppResolver.FriendlyName(EdgePath, description));

    [Fact]
    public void ResolverCachesTheNamePerExecutableAndRejectsUnusableProcesses()
    {
        int descriptions = 0;
        var resolver = new SourceAppResolver(
            processId => processId switch
            {
                42 or 43 => EdgePath,
                50 => "msedge.exe",
                51 => throw new UnauthorizedAccessException("protected process"),
                _ => null,
            },
            _ => { descriptions++; return "Microsoft Edge"; });

        Assert.Equal(new SourceAppIdentity("Microsoft Edge", EdgePath), resolver.Resolve(42));
        Assert.Equal(new SourceAppIdentity("Microsoft Edge", EdgePath), resolver.Resolve(43));
        Assert.Equal(1, descriptions);
        Assert.Null(resolver.Resolve(0));
        Assert.Null(resolver.Resolve(50)); // A bare file name is not a location.
        Assert.Null(resolver.Resolve(51));
        Assert.Null(resolver.Resolve(99));

        var failingDescription = new SourceAppResolver(_ => ExplorerPath, _ => throw new IOException("locked"));
        Assert.Equal(new SourceAppIdentity("explorer", ExplorerPath), failingDescription.Resolve(7));
    }

    [Fact]
    public void StoreAppFramesResolveToTheirContentProcess()
    {
        Assert.Equal(42u, SourceAppResolver.ResolveContentProcess("Chrome_WidgetWin_1", 42, () => throw new InvalidOperationException()));
        Assert.Equal(900u, SourceAppResolver.ResolveContentProcess("ApplicationFrameWindow", 12, () => [12, 0, 900, 901]));
        // A suspended app has no content window; the frame host is never named instead.
        Assert.Equal(0u, SourceAppResolver.ResolveContentProcess("ApplicationFrameWindow", 12, () => [12, 12]));
    }

    [Theory]
    [InlineData("CabinetWClass", true)]
    [InlineData("cabinetwclass", true)]
    [InlineData("ExploreWClass", true)]
    [InlineData("Progman", false)]
    [InlineData("WorkerW", false)]
    [InlineData("Shell_TrayWnd", false)]
    [InlineData("", false)]
    public void OnlyFileExplorerFramesAreAskedForAFolder(string className, bool expected) =>
        Assert.Equal(expected, ExplorerFolderService.IsExplorerWindowClass(className));

    [Fact]
    public void ExplorerFolderFollowsTheVisibleTabOfTheCapturedWindow()
    {
        IntPtr window = new(20), otherWindow = new(30), tab1 = new(201), tab2 = new(202), otherTab = new(301);
        ExplorerTabSnapshot[] tabs =
        [
            new(window, tab1, @"C:\Users\test\Downloads"),
            new(window, tab2, Folder),
            new(otherWindow, otherTab, @"D:\Elsewhere"),
        ];

        Assert.Equal(Folder, ExplorerFolderService.ResolveFolder(window, tab2, tabs));
        Assert.Equal(@"C:\Users\test\Downloads", ExplorerFolderService.ResolveFolder(window, tab1, tabs));
        Assert.Equal(@"D:\Elsewhere", ExplorerFolderService.ResolveFolder(otherWindow, IntPtr.Zero, tabs));
        // Two tabs and no visible tab, or a visible tab the shell did not report: ambiguous.
        Assert.Empty(ExplorerFolderService.ResolveFolder(window, IntPtr.Zero, tabs));
        Assert.Empty(ExplorerFolderService.ResolveFolder(window, new IntPtr(299), tabs));
        Assert.Empty(ExplorerFolderService.ResolveFolder(new IntPtr(40), IntPtr.Zero, tabs));
        Assert.Empty(ExplorerFolderService.ResolveFolder(IntPtr.Zero, IntPtr.Zero, tabs));
    }

    [Fact]
    public void ExplorerFolderRejectsVirtualLocationsAndDuplicateViews()
    {
        IntPtr window = new(20), tab = new(201);
        Assert.Empty(ExplorerFolderService.ResolveFolder(window, tab, [new(window, tab, "")]));
        Assert.Empty(ExplorerFolderService.ResolveFolder(window, tab, [new(window, tab, "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]));
        Assert.Empty(ExplorerFolderService.ResolveFolder(window, tab, [new(window, tab, Folder), new(window, tab, @"C:\Other")]));
        // An untabbed shell reports the frame itself as the view's window.
        Assert.Equal(Folder, ExplorerFolderService.ResolveFolder(window, new IntPtr(555), [new(window, window, Folder)]));
        Assert.Equal(Folder, ExplorerFolderService.ResolveFolder(window, IntPtr.Zero, [new(window, window, Folder + @"\")]));
    }

    [Fact]
    public async Task ExplorerReadIsBoundedAndNeverThrows()
    {
        IntPtr window = new(20), tab = new(201);
        var ready = new ExplorerFolderService(_ => [new(window, tab, Folder)], _ => tab, Probe(TimeSpan.FromSeconds(1)));
        Assert.Equal(Folder, Assert.Contains(window, await ready.ReadAsync([window, IntPtr.Zero, window])));
        Assert.Empty(await ready.ReadAsync([]));

        var failing = new ExplorerFolderService(_ => throw new System.Runtime.InteropServices.COMException("RPC server unavailable"),
            _ => tab, Probe(TimeSpan.FromSeconds(1)));
        Assert.Empty(await failing.ReadAsync([window]));

        using var release = new ManualResetEventSlim();
        var gate = new SemaphoreSlim(1, 1);
        var hung = new ExplorerFolderService(_ => { release.Wait(TimeSpan.FromSeconds(5)); return [new(window, tab, Folder)]; },
            _ => tab, new BoundedNativeProbe(gate, TimeSpan.FromMilliseconds(60), ApartmentState.STA, "test explorer"));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(await hung.ReadAsync([window]));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        // While the abandoned read still owns the shell, a new read returns at once instead of queueing.
        Assert.Empty(await hung.ReadAsync([window]));
        release.Set();
    }

    [Fact]
    public async Task ExplorerReadRunsOnAnStaThread()
    {
        ApartmentState? apartment = null;
        var service = new ExplorerFolderService(_ =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            return [];
        }, _ => IntPtr.Zero, Probe(TimeSpan.FromSeconds(1)));
        _ = await service.ReadAsync([new IntPtr(20)]);
        Assert.Equal(ApartmentState.STA, apartment);
    }

    [Fact]
    public async Task SourcesCombineApplicationAndOnlyAskExplorerAboutExplorerWindows()
    {
        IReadOnlyList<IntPtr>? folderRequest = null;
        var service = new WindowSourceService(
            candidates => Task.FromResult<IReadOnlyDictionary<IntPtr, AppObservation>>(new Dictionary<IntPtr, AppObservation>
            {
                [Browser.Handle] = new(42, new SourceAppIdentity("Microsoft Edge", EdgePath)),
                [Explorer.Handle] = new(7, new SourceAppIdentity("Windows 탐색기", ExplorerPath)),
            }),
            windows =>
            {
                folderRequest = windows;
                return Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string>
                {
                    [Explorer.Handle] = Folder,
                    [Browser.Handle] = @"C:\never-asked",
                });
            });

        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> sources = await service.ReadAsync([Browser, Explorer]);

        Assert.Equal(new[] { Explorer.Handle }, folderRequest!.ToArray());
        Assert.Equal(new WindowSourceSnapshot(42, "Microsoft Edge", EdgePath, ""), sources[Browser.Handle]);
        Assert.Equal(new WindowSourceSnapshot(7, "Windows 탐색기", ExplorerPath, Folder), sources[Explorer.Handle]);
    }

    [Fact]
    public async Task OneFailingPartLeavesTheOtherAndReusedWindowsAreDropped()
    {
        var folderFails = new WindowSourceService(
            _ => Task.FromResult<IReadOnlyDictionary<IntPtr, AppObservation>>(new Dictionary<IntPtr, AppObservation>
            {
                [Explorer.Handle] = new(7, new SourceAppIdentity("Windows 탐색기", ExplorerPath)),
            }),
            _ => throw new TimeoutException());
        Assert.Equal(new WindowSourceSnapshot(7, "Windows 탐색기", ExplorerPath, ""),
            (await folderFails.ReadAsync([Explorer]))[Explorer.Handle]);

        var appFails = new WindowSourceService(_ => throw new InvalidOperationException(),
            _ => Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string> { [Explorer.Handle] = Folder }));
        // Without the process identity a folder could belong to a reused handle; nothing is kept.
        Assert.Empty(await appFails.ReadAsync([Explorer]));

        var reused = new WindowSourceService(
            _ => Task.FromResult<IReadOnlyDictionary<IntPtr, AppObservation>>(new Dictionary<IntPtr, AppObservation>
            {
                [Explorer.Handle] = new(99, new SourceAppIdentity("Other", @"C:\Other\other.exe")),
            }),
            _ => Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string> { [Explorer.Handle] = Folder }));
        Assert.Empty(await reused.ReadAsync([Explorer]));
        Assert.Empty(await reused.ReadAsync([]));
    }

    [Fact]
    public void OnlyWhatBothObservationsAgreeOnIsKept()
    {
        var before = new WindowSourceSnapshot(7, "Windows 탐색기", ExplorerPath, Folder);
        Assert.Equal(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, Folder),
            WindowSourceService.StableDetails(before, before with { }, 7));
        // Navigating during the grab drops only the folder.
        Assert.Equal(new CaptureSourceDetails("Windows 탐색기", ExplorerPath, ""),
            WindowSourceService.StableDetails(before, before with { DocumentPath = @"C:\Users\test\Downloads" }, 7));
        Assert.True(WindowSourceService.StableDetails(before, before with { ProcessId = 8 }, 7).IsEmpty);
        Assert.True(WindowSourceService.StableDetails(before, before, 8).IsEmpty);
        Assert.True(WindowSourceService.StableDetails(null, before, 7).IsEmpty);
        Assert.True(WindowSourceService.StableDetails(before, null, 7).IsEmpty);
        Assert.Equal(new CaptureSourceDetails("", "", Folder),
            WindowSourceService.StableDetails(before, before with { AppName = "Other" }));
    }

    private static BoundedNativeProbe Probe(TimeSpan budget) =>
        new(new SemaphoreSlim(1, 1), budget, ApartmentState.STA, "test explorer");
}
