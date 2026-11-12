using System.IO;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using AppObservation = MyCapture.Platform.Capture.WindowSourceService.AppObservation;

namespace MyCapture.App.Tests;

/// <summary>The Recent Items fallback for a document window whose title names only its file.</summary>
public sealed class RecentDocumentServiceTests
{
    private const string Recent = @"C:\Users\Someone\AppData\Roaming\Microsoft\Windows\Recent";
    private const string Notes = @"C:\Work\notes.txt";
    private static readonly DateTime Started = new(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc);
    private static readonly WindowCandidate Notepad = new(new IntPtr(20), new RectD(0, 0, 100, 100),
        "notes.txt - Notepad", ProcessId: 7, ClassName: "Notepad");
    private static Dictionary<IntPtr, AppObservation> Apps => new()
    { [Notepad.Handle] = new(7, new SourceAppIdentity("Notepad", @"C:\Windows\System32\notepad.exe")) };

    [Fact]
    public async Task OneFreshShortcutGivesTheFileReadOnABackgroundSta()
    {
        ApartmentState? apartment = null;
        var asked = new List<string>();
        var service = Service(findLinks: (folder, name) =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            asked.Add(folder + "|" + name);
            return [Path.Combine(folder, name + ".lnk")];
        }, readLink: _ => new RecentDocumentLink(Notes, Started.AddMinutes(3)));
        IReadOnlyDictionary<IntPtr, string> found = await service.ReadAsync([Notepad], Apps);
        Assert.Equal(Notes, found[Notepad.Handle]);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal([Recent + "|notes.txt"], asked);
    }

    [Fact]
    public async Task DoubtfulMatchesGiveNothing()
    {
        // Two files of that name.
        Assert.Empty(await Service(findLinks: (folder, name) => [Path.Combine(folder, "a.lnk"), Path.Combine(folder, "b.lnk")],
            readLink: link => new RecentDocumentLink(link.EndsWith("a.lnk") ? Notes : @"D:\Old\notes.txt", Started.AddMinutes(1)))
            .ReadAsync([Notepad], Apps));
        // Written before the process started.
        Assert.Empty(await Service(readLink: _ => new RecentDocumentLink(Notes, Started.AddMinutes(-1))).ReadAsync([Notepad], Apps));
        // The file is gone, or the process start cannot be read.
        Assert.Empty(await Service(fileExists: _ => false).ReadAsync([Notepad], Apps));
        Assert.Empty(await Service(processStarted: _ => null).ReadAsync([Notepad], Apps));
        // An unreadable shortcut.
        Assert.Empty(await Service(readLink: _ => throw new IOException("locked")).ReadAsync([Notepad], Apps));
    }

    [Fact]
    public async Task OnlyKnownDocumentAppsWithAFileNameTitleAreLookedUp()
    {
        int lookups = 0;
        var service = Service(findLinks: (_, _) => { lookups++; return []; });
        var browser = new Dictionary<IntPtr, AppObservation>
        { [Notepad.Handle] = new(7, new SourceAppIdentity("Shell", @"C:\Windows\System32\cmd.exe")) };
        Assert.Empty(await service.ReadAsync([Notepad], browser));
        Assert.Empty(await service.ReadAsync([Notepad with { Title = "제목 없음 - 메모장" }], Apps));
        Assert.Empty(await service.ReadAsync([Notepad with { Title = "Notes - Notepad" }], Apps));
        // A reused window handle of another process.
        Assert.Empty(await service.ReadAsync([Notepad with { ProcessId = 99 }], Apps));
        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task AStalledReadEndsAtTheDeadline()
    {
        using var release = new ManualResetEventSlim();
        var service = Service(findLinks: (_, _) => { release.Wait(TimeSpan.FromSeconds(5)); return []; },
            probe: new BoundedNativeProbe(new SemaphoreSlim(1, 1), TimeSpan.FromMilliseconds(60), ApartmentState.STA, "fake recent"));
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(await service.ReadAsync([Notepad], Apps));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task WindowSourcesUseRecentItemsOnlyWhenTheWindowGaveNoPathAndRecordHowEachWasFound()
    {
        var windowDocument = new DocumentPathService((candidate, _, _) => candidate.Handle == Excel.Handle ? @"C:\Work\Budget.xlsx" : "",
            new BoundedNativeProbe(new SemaphoreSlim(1, 1), TimeSpan.FromSeconds(1), ApartmentState.STA, "fake document"));
        int recentLookups = 0;
        var recent = Service(findLinks: (folder, name) => { recentLookups++; return [Path.Combine(folder, name + ".lnk")]; },
            readLink: link => new RecentDocumentLink(link.Contains("notes") ? Notes : @"C:\Work\Budget.xlsx", Started.AddMinutes(2)));
        var apps = Apps;
        apps[Excel.Handle] = new(42, new SourceAppIdentity("Excel", @"C:\Office\EXCEL.EXE"));
        apps[Store.Handle] = new(9, new SourceAppIdentity("Notepad", @"C:\Program Files\WindowsApps\N\Notepad.exe",
            "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"));
        var sources = new WindowSourceService(_ => Task.FromResult<IReadOnlyDictionary<IntPtr, AppObservation>>(apps),
            _ => throw new InvalidOperationException("Not an Explorer window"), windowDocument, recent);

        IReadOnlyDictionary<IntPtr, WindowSourceSnapshot> snapshots = await sources.ReadAsync([Notepad, Excel, Store]);
        Assert.Equal(Notes, snapshots[Notepad.Handle].DocumentPath);
        Assert.Equal(CaptureDocumentOrigin.Recent, snapshots[Notepad.Handle].DocumentOrigin);
        Assert.Equal(@"C:\Work\Budget.xlsx", snapshots[Excel.Handle].DocumentPath);
        Assert.Equal(CaptureDocumentOrigin.Window, snapshots[Excel.Handle].DocumentOrigin);
        // Excel's own window answered, so only Notepad's title was looked up.
        Assert.Equal(1, recentLookups);
        Assert.Equal("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", snapshots[Store.Handle].AppUserModelId);
        Assert.Empty(snapshots[Store.Handle].DocumentOrigin);

        // Both observations must agree on the document, its origin and the application ID.
        WindowSourceSnapshot notes = snapshots[Notepad.Handle];
        CaptureSourceDetails stable = WindowSourceService.StableDetails(notes, notes, 7);
        Assert.Equal(Notes, stable.DocumentPath);
        Assert.Equal(CaptureDocumentOrigin.Recent, stable.DocumentOrigin);
        Assert.Empty(WindowSourceService.StableDetails(notes, notes with { DocumentPath = @"C:\Other\notes.txt" }, 7).DocumentPath);
        WindowSourceSnapshot store = snapshots[Store.Handle];
        Assert.Equal(store.AppUserModelId, WindowSourceService.StableDetails(store, store, 9).AppUserModelId);
        Assert.Empty(WindowSourceService.StableDetails(store, store with { AppUserModelId = "" }, 9).AppUserModelId);
    }

    private static readonly WindowCandidate Excel = new(new IntPtr(10), new RectD(0, 0, 100, 100),
        "Budget.xlsx - Excel", ProcessId: 42, ClassName: "XLMAIN");
    private static readonly WindowCandidate Store = new(new IntPtr(30), new RectD(0, 0, 100, 100),
        "Untitled - Notepad", ProcessId: 9, ClassName: "Notepad");

    private static RecentDocumentService Service(Func<string, string, IEnumerable<string>>? findLinks = null,
        Func<string, RecentDocumentLink?>? readLink = null, Func<uint, DateTime?>? processStarted = null,
        Func<string, bool>? fileExists = null, BoundedNativeProbe? probe = null) =>
        new(() => Recent,
            findLinks ?? ((folder, name) => [Path.Combine(folder, name + ".lnk")]),
            readLink ?? (_ => new RecentDocumentLink(Notes, Started.AddMinutes(1))),
            processStarted ?? (_ => Started),
            fileExists ?? (_ => true),
            probe ?? new BoundedNativeProbe(new SemaphoreSlim(1, 1), TimeSpan.FromSeconds(1), ApartmentState.STA, "fake recent"));
}
