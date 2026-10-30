using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Localization;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Imaging;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class PlayerFrameRound2Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task DispatcherIdleAloneCannotConfirmASeekedFrame() => Run(nameof(DispatcherIdleAloneCannotConfirmASeekedFrame), () =>
    {
        using var fixture = new Fixture();
        fixture.OpenThroughPreparedEvent();
        Assert.True(fixture.SaveButton.IsEnabled);
        fixture.Seek.Value = 500;
        Assert.False(fixture.SaveButton.IsEnabled);
        fixture.PumpIdle();
        // This simulated surface has no opened decoder or presentation acknowledgement.
        // Dispatcher idleness alone cannot establish that the new frame reached the surface.
        Assert.False(fixture.Player.MediaForDiagnostic.HasVideo);
        Assert.False(fixture.SaveButton.IsEnabled);
    });

    [Fact]
    public Task SlowSeekKeepsSaveAndShortcutDisabledUntilPresentation() => Run(nameof(SlowSeekKeepsSaveAndShortcutDisabledUntilPresentation), () =>
    {
        var engine = new HeldEngine();
        using var fixture = new Fixture(_ => engine);
        fixture.OpenThroughPreparedEvent();
        fixture.Seek.Value = 500;
        WaitUntil(() => engine.Requests.Count == 1);
        fixture.PumpIdle();
        Assert.False(fixture.SaveButton.IsEnabled);
        Assert.False(fixture.Player.HandleFrameShortcut(Key.S, ModifierKeys.Shift, null));
        Assert.Null(Wait(fixture.Player.SaveCurrentFrameAsync()));
        engine.Complete(0);
        Wait(fixture.Player.FrameSeekTaskForTest);
        Assert.True(fixture.SaveButton.IsEnabled);
        Assert.Empty(fixture.Saved);
    });

    [Fact]
    public Task WrongLandingStaysLockedUntilPlaybackIsExplicitlyRestarted() => Run(nameof(WrongLandingStaysLockedUntilPlaybackIsExplicitlyRestarted), () =>
    {
        var engine = new HeldEngine();
        using var fixture = new Fixture(_ => engine);
        fixture.OpenThroughPreparedEvent();
        fixture.Seek.Value = 500;
        WaitUntil(() => engine.Requests.Count == 1);
        engine.Complete(0, presentedMs: 800); // A stable clock at the wrong frame is still a failed seek.
        Wait(fixture.Player.FrameSeekTaskForTest);
        Assert.False(fixture.SaveButton.IsEnabled);
        Assert.False(fixture.Player.HandleFrameShortcut(Key.S, ModifierKeys.Shift, null));
        Assert.Null(Wait(fixture.Player.SaveCurrentFrameAsync()));
        Assert.Equal(UiText.Get("VideoMulticut_PreviewSeekFailed"), Field<TextBlock>(fixture.Player, "_status").Text);
        Assert.Empty(fixture.Saved);

        // Explicit playback resumes the media path and must refresh the disabled command now,
        // without requiring an unrelated timer tick, activation, pointer entry or second key.
        Button play = Field<Button>(fixture.Player, "_playButton");
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(fixture.Player.IsPlayingForDiagnostic);
        Assert.True(fixture.SaveButton.IsEnabled);
        Assert.True(fixture.Playback[^1]);
    });

    [Fact]
    public Task LatestSeekWinsAndPlayIntentCanBeCanceledWhileWaiting() => Run(nameof(LatestSeekWinsAndPlayIntentCanBeCanceledWhileWaiting), () =>
    {
        var engine = new HeldEngine();
        using var fixture = new Fixture(_ => engine, realClip: true);
        fixture.OpenThroughPreparedEvent();
        fixture.Seek.Value = 100;
        WaitUntil(() => engine.Requests.Count == 1);
        fixture.Seek.Value = 500;
        Task latest = fixture.Player.FrameSeekTaskForTest;
        Assert.True(engine.Tokens[0].IsCancellationRequested);
        engine.Complete(0); // A stale native callback must not clear the latest request's lock.
        WaitUntil(() => engine.Requests.Count == 2);
        Assert.False(fixture.SaveButton.IsEnabled);
        Button play = Field<Button>(fixture.Player, "_playButton");
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        engine.Complete(1);
        Wait(latest);
        Assert.True(fixture.SaveButton.IsEnabled);
        Assert.False(fixture.Player.IsPlayingForDiagnostic);
        fixture.Player.MediaForDiagnostic.Source = new Uri(fixture.SourcePath);
        CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(fixture.Player.SaveCurrentFrameAsync()));
        Assert.Equal(500, saved.SourceAssetRequestedTimeMs);
        Assert.Equal(500, saved.SourceAssetTimeMs);
        AssertColor(fixture.Read(saved), Colors.Blue);
    });

    [Fact]
    public Task CloseAndSourceReplacementRejectLateSeekResults() => Run(nameof(CloseAndSourceReplacementRejectLateSeekResults), () =>
    {
        var oldEngine = new HeldEngine();
        var nextEngine = new HeldEngine();
        int opens = 0;
        using var fixture = new Fixture(_ => opens++ == 0 ? oldEngine : nextEngine);
        fixture.OpenThroughPreparedEvent();
        fixture.Seek.Value = 100;
        WaitUntil(() => oldEngine.Requests.Count == 1);
        Task oldSeek = fixture.Player.FrameSeekTaskForTest;
        Guid nextId = Guid.NewGuid();
        Assert.True(fixture.Player.Open(nextId, "다른 영상", fixture.SourcePath, autoPlay: false));
        fixture.Player.MediaForDiagnostic.Close(); fixture.Player.MediaForDiagnostic.Source = null;
        Assert.True(oldEngine.Tokens[0].IsCancellationRequested);
        oldEngine.Complete(0); Wait(oldSeek);
        Assert.Equal(nextId, fixture.Player.VideoId);
        Assert.False(fixture.Player.IsMediaReadyForDiagnostic);
        Assert.False(fixture.SaveButton.IsEnabled);
        // A fresh source also owns a fresh seek engine; closing invalidates its completion.
        fixture.Player.MediaForDiagnostic.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
        fixture.Seek.Value = 500;
        WaitUntil(() => nextEngine.Requests.Count == 1);
        Task closingSeek = fixture.Player.FrameSeekTaskForTest;
        fixture.Player.Close();
        Assert.True(nextEngine.Tokens[0].IsCancellationRequested);
        nextEngine.Complete(0); Wait(closingSeek);
        Assert.False(fixture.SaveButton.IsEnabled);
        Assert.Empty(fixture.Saved);
    });

    [Fact]
    public Task NativePreparedPlayerSavesKnownColorsAfterSeekAndEof() => Run(nameof(NativePreparedPlayerSavesKnownColorsAfterSeekAndEof), () =>
    {
        using var fixture = new Fixture(realClip: true, nativePlayback: true);
        bool ended = false;
        fixture.Player.MediaForDiagnostic.MediaEnded += (_, _) => ended = true;
        Assert.True(fixture.Player.Open(fixture.Source.Id, fixture.Source.Title, fixture.SourcePath, autoPlay: false));
        Assert.False(fixture.Player.IsMediaReadyForDiagnostic);
        fixture.Player.Show();
        WaitUntil(() => fixture.Player.IsMediaReadyForDiagnostic, 15);
        Assert.True(fixture.Player.MediaForDiagnostic.HasVideo);
        foreach ((double target, Color expected) in new[] { (100d, Colors.Lime), (500d, Colors.Blue), (900d, Colors.Red) })
            CheckNativeFrame(fixture, target, expected);
        fixture.Player.RequestPlay();
        WaitUntil(() => ended, 5);
        CheckNativeFrame(fixture, 500, Colors.Blue);
        Assert.Equal(4, fixture.Saved.Count);
    });

    private static void CheckNativeFrame(Fixture fixture, double target, Color expected)
    {
        fixture.Seek.Value = target;
        Assert.False(fixture.SaveButton.IsEnabled);
        Wait(fixture.Player.FrameSeekTaskForTest);
        Assert.True(fixture.SaveButton.IsEnabled);
        fixture.Player.Dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
        MediaElement media = fixture.Player.MediaForDiagnostic;
        var displayed = new RenderTargetBitmap((int)Math.Ceiling(media.ActualWidth), (int)Math.Ceiling(media.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        displayed.Render(media);
        AssertColor(displayed, expected);
        CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(fixture.Player.SaveCurrentFrameAsync()));
        Assert.Equal(target, saved.SourceAssetRequestedTimeMs);
        Assert.Equal(target, saved.SourceAssetTimeMs);
        AssertColor(fixture.Read(saved), expected);
    }

    private static void AssertColor(BitmapSource bitmap, Color expected)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Assert.InRange(Math.Abs(pixel[0] - expected.B), 0, 10);
        Assert.InRange(Math.Abs(pixel[1] - expected.G), 0, 10);
        Assert.InRange(Math.Abs(pixel[2] - expected.R), 0, 10);
        Assert.Equal(255, pixel[3]);
    }

    private static void WaitUntil(Func<bool> done, int seconds = 10)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(done(), "The synthetic player did not reach its expected state.");
    }
    private static void Wait(Task task) { WaitUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }

    private static Task Run(string name, Action verify) => SettingsThemePreviewTests.RunIsolated(name, () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { verify(); }
        finally { application.Shutdown(); }
    }, typeof(PlayerFrameRound2Tests));

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(@"C:\GitRepositories\my-capture-libvideo\work\library-video-impl", "player-r2-" + Guid.NewGuid().ToString("N"));
        internal GalleryVideoPlayerWindow Player { get; }
        internal CaptureRecord Source { get; }
        internal string SourcePath { get; }
        internal TaskCompletionSource<VideoSourceInfo> Probe { get; } = new();
        internal List<bool> Playback { get; } = [];
        internal List<CaptureRecord> Saved { get; } = [];
        internal Button SaveButton => Field<Button>(Player, "_saveFrameButton");
        internal Slider Seek => Field<Slider>(Player, "_seek");

        internal Fixture(Func<MediaElement, IVideoPreviewEngine>? engineFactory = null, bool realClip = false, bool nativePlayback = false)
        {
            AppPaths paths = AppPaths.CreateForRoot(Root); paths.EnsureCreated();
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Source = new CaptureRecord { MediaKind = CaptureMediaKind.Video, Title = "탐색 검사", Width = 320, Height = 180,
                DurationMs = 2000, FrameRate = 10, FrameCount = 20, ContentRevision = 2 };
            Source.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(Source.Id, Source.CreatedAt);
            Directory.CreateDirectory(queue.GetDirectory(Source));
            SourcePath = queue.GetFilePath(Source, CaptureFileNames.VideoSource);
            if (realClip)
            {
                var options = new VideoEncoderOptions(SourcePath, Source.Width, Source.Height, 10,
                    VideoEncoderOptions.DeriveBitrate(Source.Width, Source.Height, 10));
                using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
                for (int index = 0; index < 20; index++)
                {
                    Color color = new[] { Colors.Red, Colors.Lime, Colors.Blue }[index % 3];
                    var pixels = new byte[Source.Width * Source.Height * 4];
                    for (int offset = 0; offset < pixels.Length; offset += 4)
                    { pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255; }
                    encoder.WriteFrame(new EncoderFrame(pixels, Source.Width, Source.Height, Source.Width * 4, index * 100));
                }
                encoder.Complete();
            }
            else File.WriteAllBytes(SourcePath, [0, 1, 2, 3]);
            queue.Add(Source);
            var service = new VideoFrameCaptureService(queue, (record, bitmap) =>
            {
                File.WriteAllBytes(Path.Combine(Root, record.Id + ".png"), ImageCodec.EncodePng(bitmap));
                Saved.Add(record); queue.Add(record); return Task.FromResult(record);
            });
            Player = new GalleryVideoPlayerWindow { Left = -10000, Top = -10000, Topmost = false, ShowActivated = false, ShowInTaskbar = false,
                FrameSeekEngineFactoryForTest = engineFactory };
            if (!nativePlayback)
            {
                Player.SourceProbeForTest = _ => Probe.Task;
                Player.PlaybackForTest = Playback.Add;
            }
            Player.ConfigureFrameCapture(service, Source.ContentRevision);
        }

        internal void OpenThroughPreparedEvent()
        {
            Assert.True(Player.Open(Source.Id, Source.Title, SourcePath, autoPlay: false));
            // Same isolated routed-event seam as GalleryVideoPlayerReadyTests. It deliberately
            // does not claim native WPF readiness or display a window.
            Player.MediaForDiagnostic.Close();
            Player.MediaForDiagnostic.Source = null;
            Assert.False(Player.IsMediaReadyForDiagnostic);
            Assert.False(SaveButton.IsEnabled);
            Player.MediaForDiagnostic.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent, Player.MediaForDiagnostic));
            Assert.False(Player.IsMediaReadyForDiagnostic);
            Probe.SetResult(new VideoSourceInfo(2000, StudioRange: false));
            PumpIdle();
            Assert.True(Player.IsMediaReadyForDiagnostic);
            Assert.Equal(new[] { false }, Playback);
        }

        internal void PumpIdle() => Player.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        internal BitmapSource Read(CaptureRecord record)
        {
            using var stream = File.OpenRead(Path.Combine(Root, record.Id + ".png"));
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bitmap.Freeze(); return bitmap;
        }
        public void Dispose() => Player.Close(); // Retain synthetic fixtures; no deletion or recycling.
    }

    private sealed class HeldEngine : IVideoPreviewEngine
    {
        internal List<PreviewSeekRequest> Requests { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        private readonly List<TaskCompletionSource<PresentedPreviewFrame>> _completions = [];
        public ValueTask<PresentedPreviewFrame> SeekAsync(PreviewSeekRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request); Tokens.Add(cancellationToken);
            var completion = new TaskCompletionSource<PresentedPreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completions.Add(completion);
            return new(completion.Task); // Deliberately permits late completion after cancellation.
        }
        internal void Complete(int index, double? presentedMs = null)
        {
            PreviewSeekRequest request = Requests[index];
            _completions[index].SetResult(new(request.Generation, request.TargetPositionMs, presentedMs ?? request.TargetPositionMs, 0, request.Mode));
        }
        public void Dispose() { }
    }
}
