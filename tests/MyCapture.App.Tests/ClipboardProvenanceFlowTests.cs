using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MyCapture.App.Editing;
using MyCapture.App.Pinning;
using MyCapture.Core.Capture;
using MyCapture.Core.Pin;
using MyCapture.Core.Primitives;
using MyCapture.Core.Settings;
using MyCapture.Platform.Display;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class ClipboardProvenanceFlowTests
{
    [Fact]
    public async Task ImageCopyCarriesOnlyOpaqueReferenceAndKeepsPngEncodingUnchanged()
    {
        BitmapSource image = Image();
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Private\report.xlsx", appName: "Excel");
        string? token = null;
        byte[]? png = null;
        Assert.True(await ClipboardImageService.CopyImageWithProvenanceAsync(image, null, source, data =>
        {
            token = Assert.IsType<string>(data.GetData(ClipboardProvenance.Format, autoConvert: false));
            using var bytes = new MemoryStream();
            Assert.IsAssignableFrom<Stream>(data.GetData("PNG", autoConvert: false)).CopyTo(bytes);
            png = bytes.ToArray();
            Assert.DoesNotContain(source.DocumentPath, token);
        }));
        Assert.Equal(ImageCodec.EncodePng(image, null), png);
        Assert.Equal(source, ClipboardProvenance.Resolve(token));
        Assert.Empty(PngSourceUrlMetadata.TryReadSourceUrl(png!));
    }

    [Fact]
    public async Task FailedCopyKeepsThePreviousClipboardSourceReference()
    {
        CaptureProvenance first = CaptureProvenance.Create(url: "https://example.com/first");
        string token = ClipboardProvenance.Remember(first);
        Assert.False(await ClipboardImageService.CopyImageWithProvenanceAsync(Image(), null,
            CaptureProvenance.Create(documentPath: @"C:\Private\other.xlsx"),
            _ => throw new COMException("clipboard busy", unchecked((int)0x800401D0))));
        Assert.Equal(first, ClipboardProvenance.Resolve(token));
    }

    [Fact]
    public async Task CaptureWithoutImmediateSourcePublishesTokenForTheLateSource()
    {
        var late = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? token = null;
        Assert.True(await ClipboardImageService.CopyImageWithProvenanceAsync(Image(), null, CaptureProvenance.None,
            data => token = Assert.IsType<string>(data.GetData(ClipboardProvenance.Format, autoConvert: false)), late.Task));
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Specs\report.xlsx", appName: "Excel");
        late.SetResult(source);
        await Task.Run(() => Assert.True(SpinWait.SpinUntil(() => ClipboardProvenance.Resolve(token) == source, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void PinReadDuringLookupReceivesItsOwnSourceOnDispatcherAndPersistsIt() => StaTestHost.Run(() =>
    {
        string root = TestRecycleBin.CreateTempSubdirectory("pending-pin-source-").FullName;
        var settings = new PinSettings { RestoreOpenPinsOnLaunch = true };
        var log = new SessionLogger();
        var store = new PinSessionStore(root, log);
        var manager = new PinManager(() => settings, saveService: null,
            (_, _, _, _) => Task.FromResult(true), _ => Task.FromResult(true), NullLogger.Instance)
        {
            ReadPlacementMonitorForTest = () => new MonitorInfo("fake", new RectD(0, 0, 1920, 1080),
                new RectD(0, 0, 1920, 1040), 96, true),
            ReadPinOrderForTest = handles => handles,
            ShowPinForTest = (_, _) => { },
            ActivatePinForTest = _ => { },
        };
        try
        {
            using var service = new PinSessionService(manager, store, () => settings,
                Dispatcher.CurrentDispatcher, log);
            Wait(service.RestoreAsync());
            var firstLookup = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
            PinContent firstContent = ReadPendingClipboard(firstLookup.Task);
            Assert.False(firstContent.Provenance.HasSource);
            Assert.Same(firstLookup.Task, firstContent.PendingProvenance);
            Assert.Equal(PasteResult.Pinned, manager.PasteFromClipboardAsync(
                () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(firstContent))).GetAwaiter().GetResult());
            PinWindow first = Assert.Single(manager.SnapshotOpenPinWindows());
            PinHoverBar bar = first.CreateHoverBarForTest();
            Assert.Equal(Visibility.Collapsed, bar.SourceButton.Visibility);
            Assert.Null(bar.SourceButton.ContextMenu);
            Wait(service.FlushDebounceForTest());
            Assert.True(store.Load().Count > 0, log.Messages);
            Assert.True(string.IsNullOrEmpty(Assert.Single(store.Load()).Pin.SourceUrl));

            // A later copy and lookup completes first, but belongs only to the later pin.
            var secondLookup = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
            PinContent secondContent = ReadPendingClipboard(secondLookup.Task);
            Assert.Equal(PasteResult.Pinned, manager.PasteFromClipboardAsync(
                () => Task.FromResult(ClipboardImageReader.PinReadAttempt.Success(secondContent))).GetAwaiter().GetResult());
            PinWindow second = Assert.Single(manager.SnapshotOpenPinWindows(), pin => !ReferenceEquals(pin, first));
            CaptureProvenance secondSource = CaptureProvenance.Create(url: "https://second.example.test/source");
            secondLookup.SetResult(secondSource);
            Wait(second.PendingProvenanceUpdateForTest);
            Assert.Same(secondSource, second.Provenance);
            Assert.False(first.Provenance.HasSource);

            int changes = 0;
            int dispatcherThread = Environment.CurrentManagedThreadId;
            manager.OpenPinsChanged += () =>
            {
                Assert.Equal(dispatcherThread, Environment.CurrentManagedThreadId);
                changes++;
            };
            CaptureProvenance source = CaptureProvenance.Create(
                url: "https://first.example.test/report?access_token=private#section", appName: "Browser");
            firstLookup.SetResult(source);
            Wait(first.PendingProvenanceUpdateForTest);

            Assert.Equal(1, changes);
            Assert.Same(source, first.Provenance);
            Assert.Same(secondSource, second.Provenance);
            Assert.Same(bar, first.HoverBarForTest);
            Assert.Equal(Visibility.Visible, bar.SourceButton.Visibility);
            Assert.Contains(source.DisplayText, Assert.IsType<string>(bar.SourceButton.ToolTip));
            Assert.Contains(source.DisplayText, AutomationProperties.GetName(bar.SourceButton));
            Assert.NotEmpty(Assert.IsType<ContextMenu>(bar.SourceButton.ContextMenu).Items);
            CaptureProvenance? opened = null;
            first.OpenSource = actual => { opened = actual; return Task.FromResult(string.Empty); };
            Wait(first.OpenSourceAsync());
            Assert.Same(source, opened);

            Wait(service.FlushDebounceForTest());
            PinSessionSnapshot saved = Assert.Single(store.Load(), item => item.Pin.SourceUrl == source.Url);
            Assert.Equal(source.AppName, saved.Pin.SourceAppName);
            Assert.Equal(secondSource.Url, Assert.Single(store.Load(), item => item.Pin.SourceUrl != source.Url).Pin.SourceUrl);
            service.Dispose();
            manager.CloseAllForExit();
            using var restore = new PinSessionService(manager, store, () => settings,
                Dispatcher.CurrentDispatcher, NullLogger.Instance);
            Wait(restore.RestoreAsync());
            Assert.Contains(manager.SnapshotOpenPins(), pin => pin.Content.Provenance == source);
            Assert.Contains(manager.SnapshotOpenPins(), pin => pin.Content.Provenance == secondSource);
        }
        finally
        {
            manager.CloseAllForExit();
            TestRecycleBin.DeleteDirectory(root, recursive: true);
        }
    });

    [Theory]
    [InlineData("empty")]
    [InlineData("failure")]
    [InlineData("cancelled")]
    public void UnresolvedLookupKeepsThePinsInitialSource(string outcome) => StaTestHost.Run(() =>
    {
        var pending = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureProvenance initial = CaptureProvenance.Create(appName: "Initial application");
        var pin = new PinWindow(PinContent.FromImage(Image(), provenance: initial)
            with { PendingProvenance = pending.Task }, new PinViewState(2, 2, 1, 1, 0.1), 0, 0);
        int changes = 0;
        pin.ContentChanged += () => changes++;
        try
        {
            if (outcome == "failure") pending.SetException(new InvalidOperationException("Lookup failed."));
            else if (outcome == "cancelled") pending.SetCanceled();
            else pending.SetResult(CaptureProvenance.None);
            Wait(pin.PendingProvenanceUpdateForTest);
            Assert.Same(initial, pin.Provenance);
            Assert.Equal(0, changes);
        }
        finally { pin.Close(); }
    });

    [Fact]
    public void ClosedPinIgnoresLookupButRestoredContentReceivesItsResolvedSource() => StaTestHost.Run(() =>
    {
        var pending = new TaskCompletionSource<CaptureProvenance>(TaskCreationOptions.RunContinuationsAsynchronously);
        PinContent content = ReadPendingClipboard(pending.Task);
        var closed = new PinWindow(content, new PinViewState(2, 2, 1, 1, 0.1), 0, 0);
        int changes = 0;
        closed.ContentChanged += () => changes++;
        closed.Close();
        CaptureProvenance source = CaptureProvenance.Create(documentPath: @"C:\Specs\restored.xlsx", appName: "Excel");
        pending.SetResult(source);
        Wait(closed.PendingProvenanceUpdateForTest);
        Assert.False(closed.Provenance.HasSource);
        Assert.Equal(0, changes);

        var restored = new PinWindow(content, new PinViewState(2, 2, 1, 1, 0.1), 0, 0);
        try
        {
            Wait(restored.PendingProvenanceUpdateForTest);
            Assert.Same(source, restored.Provenance);
            Assert.Null(restored.PinnedContent.PendingProvenance);
        }
        finally { restored.Close(); }
    });

    private static PinContent ReadPendingClipboard(Task<CaptureProvenance> pending)
    {
        BitmapSource image = Image();
        var data = new DataObject();
        data.SetData("PNG", ImageCodec.EncodePng(image), autoConvert: false);
        data.SetData(ClipboardProvenance.Format, ClipboardProvenance.Remember(CaptureProvenance.None, pending), autoConvert: false);
        ClipboardImageReader.CapturedAttempt captured = ClipboardImageReader.CaptureFrom(data, 17,
            () => throw new InvalidOperationException("Clipboard fallback is not expected."));
        return Assert.IsType<PinContent>(ClipboardImageReader.CompletePinAttemptAsync(captured,
            _ => image, (_, _) => throw new InvalidOperationException("Text rendering is not expected."),
            _ => throw new InvalidOperationException("Clipboard fallback is not expected.")).GetAwaiter().GetResult().Content);
    }

    private static void Wait(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            var timeout = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Send,
                (_, _) => frame.Continue = false, dispatcher);
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            Assert.True(task.IsCompleted, "The pending pin source task did not finish.");
        }
        task.GetAwaiter().GetResult();
    }

    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        image.Freeze();
        return image;
    }

    private sealed class SessionLogger : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();
        internal string Messages => string.Join(Environment.NewLine, _messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => _messages.Enqueue(formatter(state, exception));
    }
}
