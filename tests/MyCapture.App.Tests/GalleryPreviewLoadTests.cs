using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Ocr;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Ocr;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// #221: the library preview decodes its PNG off the UI thread. These tests hold what that move
/// needs: the file's pixels and size, no late image after the selection, the request, the capture
/// or the window changed, one full-size decode at a time, and no image or file kept afterwards.
/// The library's copy decodes off the UI thread too, and a late copy never replaces a newer one.
/// Neither waits behind the Send-priority calls of a UI Automation client.
/// </summary>
public sealed class GalleryPreviewLoadTests
{
    [Fact]
    public Task PreviewDecodesOffTheUiThread_AndOpensOnlyTheLatestCurrentImage() =>
        SettingsThemePreviewTests.RunIsolated(nameof(PreviewDecodesOffTheUiThread_AndOpensOnlyTheLatestCurrentImage), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                using var fixture = new Fixture();
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var decoder = new GatedDecoder(window.DecodePreviewForTest);
                window.DecodePreviewForTest = decoder.Decode;
                var previewButton = (Button)window.FindName("PreviewSelectedButton");
                int uiThread = Environment.CurrentManagedThreadId;

                // The click returns while the pool decodes, and the preview shows the file's pixels.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 1, "first decode");
                Pump(window);
                Assert.Empty(window.OwnedWindows);
                Assert.True(window.IsLoadingPreviewForTest);
                decoder.Release();
                PumpUntil(window, () => window.OwnedWindows.Count == 1 && !window.IsLoadingPreviewForTest, "first preview");
                Assert.NotEqual(uiThread, decoder.Finished[0].Thread);
                AssertShowsFile(window, fixture, fixture.Wide);
                // The image is loaded into memory: the file is not held open while the preview shows it.
                using (new FileStream(fixture.RenderedPath(fixture.Wide), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                ClosePreview(window);
                Assert.True(Collected(decoder.Finished[0].Image), "A closed preview kept its image.");

                // A selection change while the image decodes drops it.
                Request(window, fixture.Model, previewButton, fixture.Tall);
                PumpUntil(window, () => decoder.Started.Count == 2, "second decode");
                fixture.Model.Select(fixture.Small.Id);
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "dropped preview");
                Pump(window);
                Assert.Empty(window.OwnedWindows);
                Assert.True(Collected(decoder.Finished[1].Image), "A dropped preview image stayed referenced.");

                // Newer requests replace the waiting one, so only one full-size decode runs at a time and a
                // replaced request is never decoded; the image of the superseded request is dropped.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 3, "third decode");
                Request(window, fixture.Model, previewButton, fixture.Tall);
                Request(window, fixture.Model, previewButton, fixture.Small);
                Pump(window);
                Assert.Equal(3, decoder.Started.Count);
                decoder.Release();
                PumpUntil(window, () => decoder.Started.Count == 4, "latest decode");
                Assert.Equal(fixture.RenderedPath(fixture.Small), decoder.Started[3]);
                Assert.Empty(window.OwnedWindows);
                decoder.Release();
                PumpUntil(window, () => window.OwnedWindows.Count == 1 && !window.IsLoadingPreviewForTest, "latest preview");
                AssertShowsFile(window, fixture, fixture.Small);
                Assert.True(Collected(decoder.Finished[2].Image), "A superseded preview image stayed referenced.");
                ClosePreview(window);

                // A capture whose content changed, and a hidden library, drop the late image too.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 5, "changed capture decode");
                fixture.Wide.ContentRevision++;
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "changed capture");
                Pump(window);
                Assert.Empty(window.OwnedWindows);

                Request(window, fixture.Model, previewButton, fixture.Tall);
                PumpUntil(window, () => decoder.Started.Count == 6, "hidden library decode");
                window.Hide();
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "hidden library");
                Pump(window);
                Assert.Empty(Application.Current.Windows.OfType<GalleryImagePreviewWindow>());
                window.Show();
                window.UpdateLayout();

                // A capture whose files cannot be read reports it, as before, and opens nothing.
                Request(window, fixture.Model, previewButton, fixture.Missing);
                PumpUntil(window, () => decoder.Started.Count == 7, "missing decode");
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "missing preview");
                Pump(window);
                Assert.Empty(window.OwnedWindows);
                Assert.Null(decoder.Finished[6].Image.Target);
                Assert.Equal(KoreanWordWrap.Strip(UiText.Get("Gallery.FloatFailed")), window.ActionStatusText);

                Assert.Equal(1, decoder.MaxRunning);
                Assert.Equal(7, decoder.Finished.Count);
            }
            finally { application.Shutdown(); }
        }, typeof(GalleryPreviewLoadTests));

    [Fact]
    public Task PendingPreviewIsCancelledByVideoOrSelectionChange_ButNotByTheSameSelection() =>
        SettingsThemePreviewTests.RunIsolated(nameof(PendingPreviewIsCancelledByVideoOrSelectionChange_ButNotByTheSameSelection), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                using var fixture = new Fixture();
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var decoder = new GatedDecoder(window.DecodePreviewForTest);
                window.DecodePreviewForTest = decoder.Decode;
                var previewButton = (Button)window.FindName("PreviewSelectedButton");
                GalleryItemViewModel image = fixture.Model.FindTile(fixture.Wide.Id)!;
                GalleryItemViewModel video = fixture.Model.FindTile(fixture.Video.Id)!;
                var player = new FakeVideoPlayer();
                window.VideoPlayerFactory = () => player;

                // A video menu request supersedes the image without changing the multi-selection.
                fixture.Model.Select(image.Id);
                fixture.Model.Select(video.Id, control: true);
                Guid[] selected = [.. fixture.Model.Selection.SelectedIds];
                PreviewFromMenu(window, image);
                PumpUntil(window, () => decoder.Started.Count == 1, "menu image decode");
                PreviewFromMenu(window, video);
                Assert.Equal(selected, fixture.Model.Selection.SelectedIds);
                Assert.Equal(video.Id, player.VideoId);
                Assert.Equal(1, player.OpenCount);
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "video superseded image");
                Pump(window);
                Assert.Empty(Application.Current.Windows.OfType<GalleryImagePreviewWindow>());
                Assert.True(Collected(decoder.Finished[0].Image), "A video-superseded image stayed referenced.");
                player.Close();

                // Returning to the original selection cannot revive a cancelled request.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 2, "selection round-trip decode");
                fixture.Model.Select(fixture.Tall.Id);
                fixture.Model.Select(fixture.Wide.Id);
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "selection round-trip cancellation");
                Pump(window);
                Assert.Empty(Application.Current.Windows.OfType<GalleryImagePreviewWindow>());

                // Opening a player through another entry point also supersedes the image.
                player = new FakeVideoPlayer();
                fixture.Model.Select(image.Id);
                fixture.Model.Select(video.Id, control: true);
                PreviewFromMenu(window, image);
                PumpUntil(window, () => decoder.Started.Count == 3, "direct player image decode");
                window.SelectVideoForReview(video);
                Assert.Equal(selected, fixture.Model.Selection.SelectedIds);
                Assert.Equal(video.Id, player.VideoId);
                Assert.Equal(1, player.OpenCount);
                decoder.Release();
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "direct player superseded image");
                Pump(window);
                Assert.Empty(Application.Current.Windows.OfType<GalleryImagePreviewWindow>());

                // Publishing the same selection keeps the request, including with an existing player.
                Request(window, fixture.Model, previewButton, fixture.Wide);
                PumpUntil(window, () => decoder.Started.Count == 4, "same selection decode");
                fixture.Model.Select(fixture.Wide.Id);
                decoder.Release();
                PumpUntil(window, () => window.OwnedWindows.Count == 1 && !window.IsLoadingPreviewForTest, "same selection preview");
                AssertShowsFile(window, fixture, fixture.Wide);
                ClosePreview(window);
                Assert.Equal(1, decoder.MaxRunning);
                Assert.Equal(4, decoder.Finished.Count);
            }
            finally { application.Shutdown(); }
        }, typeof(GalleryPreviewLoadTests));

    [Fact]
    public Task CopyDecodesOffTheUiThread_AndALateCopyNeverReplacesANewerOne() =>
        SettingsThemePreviewTests.RunIsolated(nameof(CopyDecodesOffTheUiThread_AndALateCopyNeverReplacesANewerOne), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                using var fixture = new Fixture();
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var decoder = new GatedCopyDecoder(window.DecodeCopyForTest);
                window.DecodeCopyForTest = decoder.Decode;
                // The copies go to this list, never to the real clipboard.
                var copied = new List<BitmapSource>();
                window.WriteCopyForTest = data => { lock (copied) copied.Add((BitmapSource)data.GetData(DataFormats.Bitmap)); };
                BitmapSource[] Copied() { lock (copied) return [.. copied]; }
                int uiThread = Environment.CurrentManagedThreadId;
                string wide = fixture.RenderedPath(fixture.Wide), tall = fixture.RenderedPath(fixture.Tall);
                string small = fixture.RenderedPath(fixture.Small), missing = fixture.RenderedPath(fixture.Missing);
                string cannotCopy = KoreanWordWrap.Strip(UiText.Get("Text_3BF4FEA88C51"));

                // The copy returns to the dispatcher while the pool decodes, and the clipboard gets the file's pixels.
                Task<bool> first = Copy(window, fixture, fixture.Wide);
                PumpUntil(window, () => decoder.Count(wide) == 1, "first copy decode");
                Pump(window);
                Assert.False(first.IsCompleted);
                decoder.Release(wide);
                PumpUntil(window, () => first.IsCompleted, "first copy");
                Assert.True(first.Result);
                Assert.NotEqual(uiThread, decoder.Started[0].Thread);
                AssertCopiedFile(Assert.Single(Copied()), fixture.Wide);

                // A copy whose image arrives after a newer copy was asked for leaves the newer one on the clipboard.
                Task<bool> late = Copy(window, fixture, fixture.Tall);
                Task<bool> newer = Copy(window, fixture, fixture.Small);
                PumpUntil(window, () => decoder.Count(tall) == 1 && decoder.Count(small) == 1, "late and newer copy decodes");
                decoder.Release(small);
                PumpUntil(window, () => newer.IsCompleted, "newer copy");
                Assert.True(newer.Result);
                decoder.Release(tall);
                PumpUntil(window, () => late.IsCompleted, "late copy");
                Assert.False(late.Result);
                Pump(window);
                Assert.Equal(2, Copied().Length);
                AssertCopiedFile(Copied()[1], fixture.Small);

                // A late copy that cannot read its file reports nothing over the newer copy.
                window.ExpireActionStatus();
                Task<bool> lateMissing = Copy(window, fixture, fixture.Missing);
                Task<bool> newest = Copy(window, fixture, fixture.Tall);
                PumpUntil(window, () => decoder.Count(missing) == 1 && decoder.Count(tall) == 2, "late missing and newest decodes");
                decoder.Release(tall);
                PumpUntil(window, () => newest.IsCompleted, "newest copy");
                Assert.True(newest.Result);
                decoder.Release(missing);
                PumpUntil(window, () => lateMissing.IsCompleted, "late missing copy");
                Assert.False(lateMissing.Result);
                Pump(window);
                Assert.Equal(string.Empty, window.ActionStatusText);
                Assert.Equal(3, Copied().Length);
                AssertCopiedFile(Copied()[2], fixture.Tall);

                // A file that cannot be read reports it, as before, and copies nothing.
                Task<bool> unreadable = Copy(window, fixture, fixture.Missing);
                PumpUntil(window, () => decoder.Count(missing) == 2, "missing copy decode");
                decoder.Release(missing);
                PumpUntil(window, () => unreadable.IsCompleted, "missing copy");
                Assert.False(unreadable.Result);
                Assert.Equal(cannotCopy, window.ActionStatusText);
                Assert.Equal(3, Copied().Length);

                // The image is frozen on the pool before it leaves it, whatever the decoder returned.
                bool? decodedFrozen = null;
                window.DecodeCopyForTest = _ =>
                {
                    BitmapSource image = BitmapSource.Create(fixture.Small.Width, fixture.Small.Height, 96, 96, PixelFormats.Bgra32,
                        null, Fixture.Pattern(fixture.Small.Width, fixture.Small.Height), fixture.Small.Width * 4);
                    decodedFrozen = image.IsFrozen;
                    return image;
                };
                Task<bool> unfrozen = Copy(window, fixture, fixture.Small);
                PumpUntil(window, () => unfrozen.IsCompleted, "unfrozen copy");
                Assert.False(decodedFrozen);
                Assert.True(unfrozen.Result);
                AssertCopiedFile(Copied()[3], fixture.Small);

                // A decoder that throws reports the file and keeps the exception out of the dispatcher.
                window.ExpireActionStatus();
                window.DecodeCopyForTest = _ => throw new IOException("The test's decoder failed.");
                Task<bool> failing = Copy(window, fixture, fixture.Wide);
                PumpUntil(window, () => failing.IsCompleted, "failing copy");
                Assert.False(failing.Result);
                Assert.Equal(cannotCopy, window.ActionStatusText);
                Assert.Equal(4, Copied().Length);
            }
            finally { application.Shutdown(); }
        }, typeof(GalleryPreviewLoadTests));

    [Fact]
    public Task PreviewAndCopyFinish_WhileSendPriorityCallsKeepComing() =>
        SettingsThemePreviewTests.RunIsolated(nameof(PreviewAndCopyFinish_WhileSendPriorityCallsKeepComing), () =>
        {
            using var language = UiText.UseLanguage("ko-KR");
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            application.Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
            application.Resources["PinLabel"] = new PinLabelConverter();
            application.Resources["IsHeaderRow"] = new IsHeaderRowConverter();
            try
            {
                using var fixture = new Fixture();
                GalleryWindow window = fixture.Window;
                window.Show();
                window.UpdateLayout();
                var previewDecoder = new GatedDecoder(window.DecodePreviewForTest);
                window.DecodePreviewForTest = previewDecoder.Decode;
                var copyDecoder = new GatedCopyDecoder(window.DecodeCopyForTest);
                window.DecodeCopyForTest = copyDecoder.Decode;
                var copied = new List<BitmapSource>();
                window.WriteCopyForTest = data => { lock (copied) copied.Add((BitmapSource)data.GetData(DataFormats.Bitmap)); };
                var previewButton = (Button)window.FindName("PreviewSelectedButton");

                // A UI Automation client reaches WPF through Send-priority calls (ElementUtil.Invoke), and while it
                // reads the library one is nearly always waiting. Work at a lower priority then waits until the
                // client stops: the opening waited 175 ms behind 367 such calls, and seconds in a burst. Here a
                // Send call that posts the next one keeps one waiting from before the request until it finished.
                (bool Finished, int Calls, long Ms) WhileSendCallsKeepComing(Action request, Action step, Func<bool> finished)
                {
                    var frame = new DispatcherFrame();
                    var stream = Stopwatch.StartNew();
                    int calls = 0;
                    bool done = false;
                    void Call()
                    {
                        calls++;
                        step();
                        done = finished();
                        if (done || stream.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false;
                        else window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Call));
                    }
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Call));
                    request();
                    Dispatcher.PushFrame(frame);
                    return (done, calls, stream.ElapsedMilliseconds);
                }

                // The preview starts its decode and opens the image while the calls keep coming.
                bool previewReleased = false;
                var preview = WhileSendCallsKeepComing(
                    () => Request(window, fixture.Model, previewButton, fixture.Wide),
                    () =>
                    {
                        if (previewReleased || previewDecoder.Started.Count != 1) return;
                        previewDecoder.Release();
                        previewReleased = true;
                    },
                    () => window.OwnedWindows.Count == 1);
                Assert.True(preview.Finished,
                    $"The preview did not open in {preview.Ms} ms while {preview.Calls:N0} Send calls kept coming (decode started: {previewReleased}).");
                PumpUntil(window, () => !window.IsLoadingPreviewForTest, "preview after the calls");
                AssertShowsFile(window, fixture, fixture.Wide);
                ClosePreview(window);

                // The copy starts its decode and puts the image on the clipboard while the calls keep coming.
                string tall = fixture.RenderedPath(fixture.Tall);
                Task<bool>? copying = null;
                bool copyReleased = false;
                var copy = WhileSendCallsKeepComing(
                    () => copying = Copy(window, fixture, fixture.Tall),
                    () =>
                    {
                        if (copyReleased || copyDecoder.Count(tall) != 1) return;
                        copyDecoder.Release(tall);
                        copyReleased = true;
                    },
                    () => copying!.IsCompleted);
                Assert.True(copy.Finished,
                    $"The copy did not finish in {copy.Ms} ms while {copy.Calls:N0} Send calls kept coming (decode started: {copyReleased}).");
                Assert.True(copying!.Result);
                lock (copied) AssertCopiedFile(Assert.Single(copied), fixture.Tall);
            }
            finally { application.Shutdown(); }
        }, typeof(GalleryPreviewLoadTests));

    private static Task<bool> Copy(GalleryWindow window, Fixture fixture, CaptureRecord record)
    {
        GalleryItemViewModel tile = fixture.Model.FindTile(record.Id)!;
        MethodInfo copy = typeof(GalleryWindow).GetMethod("CopyRenderedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var timer = Stopwatch.StartNew();
        // As a click or a key handler asks for it: on the window's thread, under the Normal-priority context
        // the dispatcher gives its input processing.
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher, DispatcherPriority.Normal));
        Task<bool> copying;
        try { copying = (Task<bool>)copy.Invoke(window, [tile])!; }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        // The copy only starts its decode; a gated decode would otherwise hang it here.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"The copy took {timer.ElapsedMilliseconds} ms to return.");
        return copying;
    }

    private static void AssertCopiedFile(BitmapSource copied, CaptureRecord record)
    {
        Assert.True(copied.IsFrozen);
        Assert.Equal(record.Width, copied.PixelWidth);
        Assert.Equal(record.Height, copied.PixelHeight);
        var pixels = new byte[record.Width * record.Height * 4];
        new FormatConvertedBitmap(copied, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, record.Width * 4, 0);
        Assert.Equal(Fixture.Pattern(record.Width, record.Height), pixels);
    }

    private static void PreviewFromMenu(GalleryWindow window, GalleryItemViewModel tile)
    {
        var menuItem = new MenuItem { CommandParameter = tile };
        typeof(GalleryWindow).GetMethod("OnPreviewMenuClick", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [menuItem, new RoutedEventArgs(MenuItem.ClickEvent)]);
    }

    private static void Request(GalleryWindow window, GalleryViewModel model, Button previewButton, CaptureRecord record)
    {
        model.Select(record.Id);
        var timer = Stopwatch.StartNew();
        previewButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        // The click only queues the request; a gated decode would otherwise hang it here.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"The preview click took {timer.ElapsedMilliseconds} ms.");
        Assert.True(window.IsLoadingPreviewForTest);
    }

    // Separate frames so no local of the test body keeps a shown image alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertShowsFile(GalleryWindow window, Fixture fixture, CaptureRecord record)
    {
        Window preview = Assert.Single(window.OwnedWindows.Cast<Window>());
        Assert.IsType<GalleryImagePreviewWindow>(preview);
        var shown = Assert.IsAssignableFrom<BitmapSource>(Assert.IsType<Image>(
            Assert.Single(Assert.IsType<Canvas>(preview.Content).Children.Cast<UIElement>())).Source);
        Assert.True(shown.IsFrozen);
        Assert.Equal(record.Width, shown.PixelWidth);
        Assert.Equal(record.Height, shown.PixelHeight);
        var pixels = new byte[record.Width * record.Height * 4];
        new FormatConvertedBitmap(shown, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, record.Width * 4, 0);
        Assert.Equal(Fixture.Pattern(record.Width, record.Height), pixels);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClosePreview(GalleryWindow window)
    {
        Assert.Single(window.OwnedWindows.Cast<Window>()).Close();
        Pump(window);
        Assert.Empty(window.OwnedWindows);
    }

    private static bool Collected(WeakReference reference)
    {
        for (int attempt = 0; attempt < 5 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return !reference.IsAlive;
    }

    private static void Pump(Window window)
    {
        for (int pass = 0; pass < 5; pass++)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
    }

    private static void PumpUntil(Window window, Func<bool> done, string what)
    {
        var timeout = Stopwatch.StartNew();
        while (!done())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what + " did not happen.");
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(5);
        }
    }

    /// <summary>Wraps the window's own decoder; every decode waits until the test releases it.</summary>
    private sealed class GatedDecoder(Func<string, string, BitmapSource?> decode)
    {
        private readonly SemaphoreSlim _gate = new(0);
        private readonly object _sync = new();
        private readonly List<string> _started = [];
        private readonly List<(int Thread, WeakReference Image)> _finished = [];
        private int _running;
        private int _maxRunning;

        internal IReadOnlyList<string> Started { get { lock (_sync) return [.. _started]; } }
        internal IReadOnlyList<(int Thread, WeakReference Image)> Finished { get { lock (_sync) return [.. _finished]; } }
        internal int MaxRunning => Volatile.Read(ref _maxRunning);

        internal void Release() => _gate.Release();

        internal BitmapSource? Decode(string renderedPath, string originalPath)
        {
            int running = Interlocked.Increment(ref _running);
            lock (_sync)
            {
                _started.Add(renderedPath);
                _maxRunning = Math.Max(_maxRunning, running);
            }
            try
            {
                if (!_gate.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test never released the decode.");
                BitmapSource? image = decode(renderedPath, originalPath);
                lock (_sync) _finished.Add((Environment.CurrentManagedThreadId, new WeakReference(image)));
                return image;
            }
            finally { Interlocked.Decrement(ref _running); }
        }
    }

    /// <summary>Wraps the window's copy decoder; a decode waits until the test releases its file.</summary>
    private sealed class GatedCopyDecoder(Func<string, BitmapSource?> decode)
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, SemaphoreSlim> _gates = [];
        private readonly List<(string Path, int Thread)> _started = [];

        internal IReadOnlyList<(string Path, int Thread)> Started { get { lock (_sync) return [.. _started]; } }

        internal int Count(string path) { lock (_sync) return _started.Count(start => start.Path == path); }

        internal void Release(string path) => Gate(path).Release();

        internal BitmapSource? Decode(string path)
        {
            lock (_sync) _started.Add((path, Environment.CurrentManagedThreadId));
            if (!Gate(path).Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test never released the decode.");
            return decode(path);
        }

        private SemaphoreSlim Gate(string path)
        {
            lock (_sync)
            {
                if (!_gates.TryGetValue(path, out SemaphoreSlim? gate)) _gates.Add(path, gate = new SemaphoreSlim(0));
                return gate;
            }
        }
    }

    private sealed class FakeVideoPlayer : IGalleryVideoPlayer
    {
        public Guid VideoId { get; private set; }
        public event EventHandler? Closed;
        internal int OpenCount { get; private set; }

        public bool Open(Guid id, string title, string path, bool autoPlay)
        {
            VideoId = id;
            OpenCount++;
            return true;
        }

        public void RequestPlay() { }
        public void ShowPlayer(bool activate) { }
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OcrResultPresenter _presenter;
        private readonly CaptureQueue _queue;
        internal GalleryWindow Window { get; }
        internal GalleryViewModel Model { get; }
        internal CaptureRecord Wide { get; }
        internal CaptureRecord Tall { get; }
        internal CaptureRecord Small { get; }
        internal CaptureRecord Missing { get; }
        internal CaptureRecord Video { get; }

        internal Fixture()
        {
            string root = OwnedTestDirectory.Create("preview-load-");
            var paths = AppPaths.CreateForRoot(root);
            paths.EnsureCreated();
            var settings = new AppSettings();
            _queue = new CaptureQueue(paths, settings.Queue, NullLogger<CaptureQueue>.Instance);
            Wide = Add(300, 200);
            Tall = Add(90, 240);
            Small = Add(64, 48);
            Missing = Add(40, 30, writeFile: false);
            Video = new CaptureRecord
            {
                MediaKind = CaptureMediaKind.Video, CreatedAt = DateTimeOffset.Now.AddDays(-1),
                RelativeDirectory = Guid.NewGuid().ToString("N"),
            };
            _queue.Add(Video);
            var controller = new GalleryController(_queue, NullLogger<GalleryController>.Instance);
            Model = new GalleryViewModel(controller, record => _queue.GetFilePath(record, CaptureFileNames.Rendered), 96);
            var persistence = new CapturePersistenceService(_queue, paths, () => settings.Queue,
                NullLogger<CapturePersistenceService>.Instance);
            var commit = new CaptureCommitService(persistence, () => settings, () => paths,
                NullLogger<CaptureCommitService>.Instance, (_, _) => Task.FromResult(true));
            var ocr = new NoOcr();
            _presenter = new OcrResultPresenter(ocr, Dispatcher.CurrentDispatcher, _ => { }, NullLogger.Instance);
            var indexing = new OcrIndexingService(controller, ocr, _queue.GetDirectory, () => settings.Ocr,
                NullLogger<OcrIndexingService>.Instance, Dispatcher.CurrentDispatcher);
            Window = new GalleryWindow(Model, controller,
                new GalleryReeditLoader(_queue, NullLogger<GalleryReeditLoader>.Instance), commit, _queue,
                new VideoLibraryService(_queue, paths, NullLogger<VideoLibraryService>.Instance), paths,
                NullLoggerFactory.Instance, _presenter, () => settings.Ocr, indexing, new NoPrivacy(), NullLogger.Instance)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000,
            };

            CaptureRecord Add(int width, int height, bool writeFile = true)
            {
                var record = new CaptureRecord
                {
                    RelativeDirectory = Guid.NewGuid().ToString("N"), Width = width, Height = height,
                };
                _queue.Add(record);
                Directory.CreateDirectory(_queue.GetDirectory(record));
                if (writeFile)
                {
                    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                        Pattern(width, height), width * 4);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(_queue.GetFilePath(record, CaptureFileNames.Rendered));
                    encoder.Save(stream);
                }
                return record;
            }
        }

        internal string RenderedPath(CaptureRecord record) => _queue.GetFilePath(record, CaptureFileNames.Rendered);

        internal static byte[] Pattern(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * width + x) * 4;
                    pixels[offset] = (byte)x;
                    pixels[offset + 1] = (byte)y;
                    pixels[offset + 2] = (byte)(x * 7 + y * 3);
                    pixels[offset + 3] = 255;
                }
            }
            return pixels;
        }

        public void Dispose()
        {
            Window.CloseForExit();
            _presenter.Dispose();
        }
    }

    private sealed class NoOcr : IOcrService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<string> SupportedLanguages => [];
        public Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The preview must not run OCR.");
    }

    private sealed class NoPrivacy : IPrivacyRedactionService
    {
        public bool IsAvailable => false;
        public Task<PrivacyRedactionResult> FindAsync(BitmapSource bitmap, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The preview must not run privacy detection.");
    }
}
