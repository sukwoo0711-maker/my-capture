using MyCapture.App.Capture;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The late read of a code editor's document. The tab reader is a UI Automation double: an empty list is a
/// tree the earlier reads have not woken yet.
/// </summary>
public sealed class EditorDocumentServiceTests
{
    private const string Code = @"C:\Users\Someone\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    private const string Alpha = @"C:\Work\Project\alpha.cs";
    private const string Beta = @"C:\Work\Project\beta.md";
    private static readonly DateTime Started = new(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc);
    private static readonly WindowCandidate Editor = new(new IntPtr(30), new RectD(0, 0, 100, 100),
        "alpha.cs - Project - Visual Studio Code", ProcessId: 9, ClassName: "Chrome_WidgetWin_1")
    { Source = CaptureSourceDetails.Create("Visual Studio Code", Code, null) };

    [Fact]
    public async Task ATreeThatFillsAfterTheFirstReadGivesTheSelectedTabsPathOnABackgroundSta()
    {
        int reads = 0;
        ApartmentState? apartment = null;
        var delays = new List<TimeSpan>();
        var service = Service(read: (_, _) =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            // The first attempt finds the tree still empty; the second sees the selected tab and its label.
            return ++reads == 1 ? [] : ["alpha.cs", string.Empty, Alpha];
        }, delays: delays);
        IReadOnlyDictionary<IntPtr, string> found = await service.ReadAsync([Editor]);
        Assert.Equal(Alpha, found[Editor.Handle]);
        Assert.Equal(2, reads);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.All(delays, delay => Assert.Equal(EditorDocumentService.Interval, delay));
    }

    [Fact]
    public async Task APathThatNeverAppearsGivesNothingAfterTheLastAttempt()
    {
        int reads = 0;
        var service = Service(read: (_, _) => { reads++; return []; });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Assert.Empty(await service.ReadAsync([Editor]));
        Assert.Equal(EditorDocumentService.MaxAttempts, reads);
        Assert.True(timer.Elapsed < EditorDocumentService.Budget + TimeSpan.FromSeconds(1));
        // Labels without a path, or with a file that does not exist, name nothing either.
        Assert.Empty(await Service(read: (_, _) => ["alpha.cs"]).ReadAsync([Editor]));
        Assert.Empty(await Service(read: (_, _) => [Alpha], fileExists: _ => false).ReadAsync([Editor]));
    }

    [Fact]
    public async Task SeveralSelectedTabsAreNarrowedByTheTitleAndDoubtfulOnesGiveNothing()
    {
        // Two editor groups side by side: each has a selected tab; the title names the focused one.
        Assert.Equal(Alpha, (await Service(read: (_, _) => ["beta.md", Beta, "alpha.cs", Alpha]).ReadAsync([Editor]))[Editor.Handle]);
        // Two files of the title's name in different folders.
        Assert.Empty(await Service(read: (_, _) => [Alpha, @"C:\Other\alpha.cs"]).ReadAsync([Editor]));
        // A selected tab of another file than the title names.
        Assert.Empty(await Service(read: (_, _) => [Beta]).ReadAsync([Editor]));
        // A changed title format without a file name: only one selected path qualifies.
        WindowCandidate untitled = Editor with { Title = "Project - Visual Studio Code" };
        Assert.Equal(Beta, (await Service(read: (_, _) => [Beta]).ReadAsync([untitled]))[untitled.Handle]);
        Assert.Empty(await Service(read: (_, _) => [Alpha, Beta]).ReadAsync([untitled]));
    }

    [Fact]
    public async Task AnotherProcessOrTitleInTheSameWindowGivesNothing()
    {
        int starts = 0;
        // The process ID was reused by a process that started later.
        Assert.Empty(await Service(read: (_, _) => [Alpha], processStarted: _ => Started.AddSeconds(starts++)).ReadAsync([Editor]));
        // The window no longer belongs to that process, or shows another tab.
        Assert.Empty(await Service(read: (_, _) => [Alpha], stillMatches: _ => false).ReadAsync([Editor]));
        int checks = 0;
        Assert.Empty(await Service(read: (_, _) => [Alpha], stillMatches: _ => ++checks < 2).ReadAsync([Editor]));
        // The process start cannot be read.
        Assert.Empty(await Service(read: (_, _) => [Alpha], processStarted: _ => null).ReadAsync([Editor]));
    }

    [Fact]
    public async Task OnlyEditorsWithoutADocumentAreRead()
    {
        int reads = 0;
        var service = Service(read: (_, _) => { reads++; return [Alpha]; });
        Assert.Empty(await service.ReadAsync([Editor with { Source = CaptureSourceDetails.Create("Notepad", @"C:\Windows\notepad.exe", null) }]));
        Assert.Empty(await service.ReadAsync([Editor with { Source = CaptureSourceDetails.Create("Code", Code, Beta) }]));
        Assert.Empty(await service.ReadAsync([Editor with { Source = null }]));
        Assert.Empty(await service.ReadAsync([Editor with { ProcessId = 0 }]));
        Assert.Empty(await service.ReadAsync([Editor with { Title = "Untitled-1 - Visual Studio Code" }]));
        Assert.Equal(0, reads);
        Assert.True(EditorDocumentService.IsTarget(Editor with
        {
            Source = CaptureSourceDetails.Create("Code - Insiders", @"C:\Apps\Code - Insiders.exe", null),
        }));
    }

    [Fact]
    public async Task AStalledAttemptEndsAtItsDeadline()
    {
        using var release = new ManualResetEventSlim();
        var service = Service(read: (_, _) => { release.Wait(TimeSpan.FromSeconds(5)); return [Alpha]; },
            probe: new BoundedNativeProbe(new SemaphoreSlim(1, 1), TimeSpan.FromMilliseconds(60), ApartmentState.STA, "fake editor"));
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(await service.ReadAsync([Editor]));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task TheConfirmedEditorGetsItsDocumentAfterThePixelsWithoutHoldingThem()
    {
        var late = new TaskCompletionSource<IReadOnlyDictionary<IntPtr, string>>();
        IReadOnlyList<WindowCandidate>? asked = null;
        WindowCandidate hotkey = Editor with { Source = null };
        var snapshot = new WindowSourceSnapshot(9, "Visual Studio Code", Code, string.Empty);
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[256], 32);
        bitmap.Freeze();
        var frame = new FrozenFrame(bitmap, new RectD(0, 0, 8, 8), null, 0);
        (FrozenFrame acquired, Task<IReadOnlyList<WindowCandidate>> windows) = await CapturePageContext.AcquireFrameAsync(
            [hotkey], () => frame,
            _ => Task.FromResult<IReadOnlyDictionary<IntPtr, BrowserDocumentSnapshot>>(new Dictionary<IntPtr, BrowserDocumentSnapshot>()),
            readSources: _ => Task.FromResult<IReadOnlyDictionary<IntPtr, WindowSourceSnapshot>>(
                new Dictionary<IntPtr, WindowSourceSnapshot> { [hotkey.Handle] = snapshot }),
            readEditorDocuments: candidates => { asked = candidates; return late.Task; });
        Assert.Same(frame, acquired);
        // The pixels are back; the confirmed windows wait only for the late editor read.
        await WaitUntilAsync(() => asked is not null);
        Assert.False(windows.IsCompleted);
        WindowCandidate target = Assert.Single(asked!);
        Assert.Equal(9u, target.ProcessId);
        Assert.Equal(Code, target.Source!.AppPath);
        late.SetResult(new Dictionary<IntPtr, string> { [hotkey.Handle] = Alpha });
        CaptureSourceDetails source = Assert.Single(await windows).Source!;
        Assert.Equal(Alpha, source.DocumentPath);
        Assert.Equal(CaptureDocumentOrigin.Editor, source.DocumentOrigin);
    }

    [Fact]
    public async Task OtherWindowsNeverStartTheLateReadAndAFailedReadKeepsTheConfirmedSource()
    {
        int reads = 0;
        var notepad = new WindowSourceSnapshot(9, "Notepad", @"C:\Windows\notepad.exe", string.Empty);
        WindowCandidate[] confirmed = [Editor with { Source = CaptureSourceDetails.Create("Notepad", notepad.AppPath, null) }];
        Assert.Same(confirmed, await CapturePageContext.AddEditorDocumentsAsync(confirmed,
            new Dictionary<IntPtr, WindowSourceSnapshot> { [Editor.Handle] = notepad },
            _ => { reads++; return Task.FromResult<IReadOnlyDictionary<IntPtr, string>>(new Dictionary<IntPtr, string>()); }));
        Assert.Equal(0, reads);
        var code = new WindowSourceSnapshot(9, "Visual Studio Code", Code, string.Empty);
        WindowCandidate[] editors = [Editor];
        WindowCandidate kept = Assert.Single(await CapturePageContext.AddEditorDocumentsAsync(editors,
            new Dictionary<IntPtr, WindowSourceSnapshot> { [Editor.Handle] = code }, _ => throw new InvalidOperationException()));
        Assert.Equal(Editor.Source, kept.Source);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Assert.True(condition());
    }

    private static EditorDocumentService Service(
        Func<WindowCandidate, CancellationToken, IReadOnlyList<string>>? read = null,
        Func<WindowCandidate, bool>? stillMatches = null,
        Func<uint, DateTime?>? processStarted = null,
        Func<string, bool>? fileExists = null,
        List<TimeSpan>? delays = null,
        BoundedNativeProbe? probe = null) =>
        new(read ?? ((_, _) => [Alpha]), stillMatches ?? (_ => true), processStarted ?? (_ => Started),
            fileExists ?? (path => path is Alpha or Beta or @"C:\Other\alpha.cs"),
            wait => { delays?.Add(wait); return Task.CompletedTask; },
            probe ?? new BoundedNativeProbe(new SemaphoreSlim(1, 1), TimeSpan.FromSeconds(1), ApartmentState.STA, "fake editor"));
}
