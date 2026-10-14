using System.Runtime.InteropServices;
using MyCapture.Core.Capture;
using MyCapture.Core.Primitives;
using MyCapture.Platform.Capture;
using Xunit;
using AppObservation = MyCapture.Platform.Capture.WindowSourceService.AppObservation;

namespace MyCapture.App.Tests;

public sealed class DocumentPathServiceTests
{
    private static readonly WindowCandidate Excel = new(new IntPtr(10), new RectD(0, 0, 100, 100),
        "Budget.xlsx - Excel", ProcessId: 42, ClassName: "XLMAIN");
    private const string Document = @"C:\Work\Budget.xlsx";
    private static Dictionary<IntPtr, AppObservation> Apps => new()
    { [Excel.Handle] = new(42, new SourceAppIdentity("Excel", @"C:\Office\EXCEL.EXE")) };

    [Fact]
    public async Task ReadsOnBackgroundStaAndCombinesWithExistingAppMetadata()
    {
        ApartmentState? apartment = null;
        var documents = new DocumentPathService((candidate, app, _) =>
        {
            apartment = Thread.CurrentThread.GetApartmentState();
            Assert.Equal(Excel, candidate);
            return Document;
        }, Probe());
        var sources = new WindowSourceService(_ => Task.FromResult<IReadOnlyDictionary<IntPtr, AppObservation>>(Apps),
            _ => throw new InvalidOperationException("Not an Explorer window"), documents);
        WindowSourceSnapshot snapshot = (await sources.ReadAsync([Excel]))[Excel.Handle];
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.Equal(Document, snapshot.DocumentPath);
        Assert.Equal(Document, WindowSourceService.StableDetails(snapshot, snapshot, 42).DocumentPath);
        Assert.Empty(WindowSourceService.StableDetails(snapshot, snapshot with { DocumentPath = @"C:\Other.xlsx" }, 42).DocumentPath);
    }

    [Fact]
    public async Task FailuresInvalidPathsAndReusedWindowsDoNotSupplyDocuments()
    {
        var failing = new DocumentPathService((_, _, _) => throw new COMException(), Probe());
        Assert.Empty(await failing.ReadAsync([Excel], Apps));
        var invalid = new DocumentPathService((_, _, _) => "Book1", Probe());
        Assert.Empty(await invalid.ReadAsync([Excel], Apps));
        int calls = 0;
        var never = new DocumentPathService((_, _, _) => { calls++; return Document; }, Probe());
        Assert.Empty(await never.ReadAsync([Excel with { ProcessId = 999 }], Apps));
        var apps = Apps;
        apps[Excel.Handle] = new(42, new SourceAppIdentity("Browser", @"C:\browser.exe"));
        Assert.Empty(await never.ReadAsync([Excel], apps));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TimedOutProviderKeepsItsGateUntilItFinishes()
    {
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var gate = new SemaphoreSlim(1, 1);
        int calls = 0;
        var service = new DocumentPathService((_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TimeSpan.FromSeconds(5));
            finished.Set();
            return Document;
        }, new BoundedNativeProbe(gate, TimeSpan.FromMilliseconds(60), ApartmentState.STA, "fake document"));
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.Empty(await service.ReadAsync([Excel], Apps));
            Assert.Empty(await service.ReadAsync([Excel], Apps));
            Assert.Equal(1, calls);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
        }
        finally { release.Set(); Assert.True(finished.Wait(TimeSpan.FromSeconds(2))); }
    }

    private static BoundedNativeProbe Probe() => new(new SemaphoreSlim(1, 1), TimeSpan.FromSeconds(1),
        ApartmentState.STA, "fake document");
}
