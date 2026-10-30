using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoFrameCaptureTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task SmallPlayerSavesDecodedResolutionAndSourceMetadata() => Run(nameof(SmallPlayerSavesDecodedResolutionAndSourceMetadata), () =>
    {
        using var fixture = new Fixture(realClip: true, width: 3840, height: 2160);
        GalleryVideoPlayerWindow player = fixture.Player();
        try
        {
            player.Width = 360; player.Height = 300;
            var playback = new List<bool>();
            player.PlaybackForTest = playback.Add;
            Set(player, "_playing", true);
            player.MediaForDiagnostic.Position = TimeSpan.FromMilliseconds(149);
            CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(player.SaveCurrentFrameAsync()));
            BitmapSource png = fixture.Read(saved);
            Assert.Equal(3840, png.PixelWidth);
            Assert.Equal(2160, png.PixelHeight);
            Assert.Equal(new[] { false, true }, playback);
            Assert.Equal(UiText.Format("VideoFrame_Saved", saved.Title), Field<TextBlock>(player, "_status").Text);
            Assert.True(png.PixelWidth > player.Width);
            Assert.Equal(fixture.Source.Id, saved.SourceVideoId);
            Assert.Equal("source", saved.SourceAssetKind);
            Assert.Equal(7, saved.SourceVideoContentRevision);
            Assert.Equal("녹화 예시", saved.SourceVideoName);
            Assert.NotNull(saved.SourceFrameRequestedAt);
            using var reader = RangeAwareVideoFrameDecoder.Open(fixture.SourcePath);
            double requested = Assert.IsType<double>(saved.SourceAssetRequestedTimeMs);
            Assert.Equal(reader.ReadFrameAt(requested).TimestampMs, saved.SourceAssetTimeMs);
            CaptureRecord reloaded = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(fixture.Meta(saved)))!;
            Assert.Equal(saved.SourceVideoId, reloaded.SourceVideoId);
            Assert.Equal(saved.SourceAssetTimeMs, reloaded.SourceAssetTimeMs);
            Assert.DoesNotContain("OriginalSourceTimeMs", File.ReadAllText(fixture.Meta(saved)));
            Assert.Equal("녹화 예시_00-01-23.456.png", VideoFrameCaptureService.FileName("녹화 예시.mp4", 83456));
        }
        finally { player.Close(); }
    });

    [Fact]
    public Task EditorSavesUnsavedTextAndFrameLayerOnceAtSourceSize() => Run(nameof(EditorSavesUnsavedTextAndFrameLayerOnceAtSourceSize), () =>
    {
        using var fixture = new Fixture(realClip: true);
        VideoEditorWindow editor = fixture.Editor();
        try
        {
            VideoEditDocument document = Field<VideoEditDocument>(editor, "_editDocument");
            document.TextOverlays.Add(new TimedTextOverlay { StartMs = 0, EndMs = 1000, Text = "FRAME", Placement = VideoTextPlacement.Bottom });
            document.FrameEditLayers.Add(new FrameEditLayer
            {
                StartMs = 0, EndMs = 1000, Bounds = new VideoLayerBounds(0, 0, 0.25, 0.25),
                OverlayPngBase64 = Convert.ToBase64String(ImageCodec.EncodePng(Solid(16, 16, Colors.Lime))),
            });
            CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(editor.SaveCurrentFrameAsync()));
            BitmapSource png = fixture.Read(saved);
            Assert.Equal(1280, png.PixelWidth); Assert.Equal(720, png.PixelHeight);
            byte[] pixel = new byte[4];
            png.CopyPixels(new Int32Rect(30, 30, 1, 1), pixel, 4, 0);
            Assert.True(pixel[1] > 240 && pixel[0] < 15 && pixel[2] < 15);
            BitmapSource expected = VideoFrameRenderPipeline.CaptureSingleFrame(fixture.SourcePath, 0, document).Bitmap;
            Assert.Equal(Pixels(expected), Pixels(png));
            Assert.Equal("source", saved.SourceAssetKind);
            Assert.False(saved.HasAnnotations); // The independent PNG has its layers baked once.
            Assert.True(editor.HasUnsavedEditsForTest);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public Task RenderedAssetIsDecodedWithoutReapplyingEditorLayers() => Run(nameof(RenderedAssetIsDecodedWithoutReapplyingEditorLayers), () =>
    {
        using var fixture = new Fixture(realClip: true);
        string rendered = fixture.Queue.GetFilePath(fixture.Source, CaptureFileNames.VideoRendered);
        File.Copy(fixture.SourcePath, rendered);
        CaptureRecord saved = Wait(fixture.Service.SaveAsync(fixture.Source.Id, 7, rendered, 299));
        Assert.Equal("rendered", saved.SourceAssetKind);
        using var reader = RangeAwareVideoFrameDecoder.Open(rendered);
        Assert.Equal(reader.ReadFrameAt(299).TimestampMs, saved.SourceAssetTimeMs);
        Assert.Equal(Pixels(VideoFrameRenderPipeline.CaptureSingleFrame(rendered, 299).Bitmap), Pixels(fixture.Read(saved)));
        VideoFrameCapturePixels eof = VideoFrameRenderPipeline.CaptureSingleFrame(rendered, 300);
        Assert.Equal(reader.ReadFrameAt(300).TimestampMs, eof.DecodedTimeMs);
        Assert.True(eof.DecodedTimeMs < 300);
    });

    [Fact]
    public Task BothShortcutsIgnoreTypingRepeatAndUnavailableMedia() => Run(nameof(BothShortcutsIgnoreTypingRepeatAndUnavailableMedia), () =>
    {
        using var fixture = new Fixture();
        GalleryVideoPlayerWindow player = fixture.Player();
        VideoEditorWindow editor = fixture.Editor();
        try
        {
            Assert.Equal("SaveFrame", ShortcutCatalog.Match(ShortcutScope.Video, Key.S, ModifierKeys.Shift));
            foreach (IInputElement input in new IInputElement[] { new TextBox(), new RichTextBox(), new PasswordBox() })
            {
                Assert.False(player.HandleFrameShortcut(Key.S, ModifierKeys.Shift, input));
                Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.Shift, input));
            }
            Assert.False(player.HandleFrameShortcut(Key.S, ModifierKeys.Shift, null, isRepeat: true));
            Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.Shift, null, isRepeat: true));
            Set(editor, "_operationRunning", true);
            Assert.False(editor.HandleShortcut(Key.S, ModifierKeys.Shift, null));
            Set(editor, "_operationRunning", false);
            Set(player, "_mediaReady", false);
            Assert.False(player.HandleFrameShortcut(Key.S, ModifierKeys.Shift, null));
            Assert.Empty(fixture.Saved);
            foreach (Window window in new Window[] { player, editor })
            {
                Button save = Assert.Single(FirstRunWelcomeTests.Descendants(window).OfType<Button>(), button =>
                    button.Name is "PlayerSaveFrame" or "VideoSaveCurrentFrame");
                Assert.Equal(UiText.Get("VideoFrame_Save"), AutomationProperties.GetName(save));
                Assert.Equal("Shift+S", AutomationProperties.GetAcceleratorKey(save));
            }
        }
        finally { player.Close(); editor.Close(); }
    });

    [Fact]
    public Task FrameButtonsRevealAltCaptionsAndFitNarrowEnglishTransport() => Run(nameof(FrameButtonsRevealAltCaptionsAndFitNarrowEnglishTransport), () =>
    {
        using var motion = new ShortcutMotionTestScope();
        using var language = UiText.UseLanguage("en-US");
        using var fixture = new Fixture(realClip: true);
        foreach (int width in new[] { 720, 1180 })
        {
            VideoEditorWindow editor = fixture.Editor();
            editor.MinWidth = Math.Min(editor.MinWidth, width); editor.Width = width; editor.Height = 760;
            try
            {
                editor.Show(); editor.UpdateLayout();
                Button save = Field<Button>(editor, "_saveCurrentFrameButton");
                AssertCaption(editor, save);
                Grid transport = Assert.Single(FirstRunWelcomeTests.Descendants(editor).OfType<Grid>(), grid => grid.Name == "VideoTransport");
                Border time = Assert.Single(FirstRunWelcomeTests.Descendants(transport).OfType<Border>(), item => item.Name == "VideoPositionChip");
                ToggleButton details = Assert.Single(FirstRunWelcomeTests.Descendants(transport).OfType<ToggleButton>(), item => item.Name == "VideoDetailsToggle");
                double timeRight = time.TranslatePoint(new Point(time.ActualWidth, 0), transport).X;
                double saveLeft = save.TranslatePoint(default, transport).X;
                double saveRight = save.TranslatePoint(new Point(save.ActualWidth, 0), transport).X;
                double detailsLeft = details.TranslatePoint(default, transport).X;
                Assert.True(saveLeft >= timeRight - 0.5);
                Assert.True(detailsLeft >= saveRight - 0.5);
                Assert.True(details.TranslatePoint(new Point(details.ActualWidth, 0), transport).X <= transport.ActualWidth + 0.5);
                if (width == 720) Assert.Equal("Save frame", Field<TextBlock>(editor, "_saveCurrentFrameText").Text);
            }
            finally { editor.Close(); }
        }
        GalleryVideoPlayerWindow player = fixture.Player();
        player.Width = 360; player.Height = 420;
        try
        {
            player.Show(); player.UpdateLayout();
            Button save = Field<Button>(player, "_saveFrameButton");
            AssertCaption(player, save);
            Assert.Equal("Save frame", Field<TextBlock>(player, "_saveFrameText").Text);
            FrameworkElement content = (FrameworkElement)player.Content;
            Rect bounds = save.TransformToAncestor(content).TransformBounds(new Rect(save.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= content.ActualWidth + 0.5);
        }
        finally { player.Close(); }

        static void AssertCaption(Window window, Button save)
        {
            TextBlock caption = Assert.Single(FirstRunWelcomeTests.Descendants(save).OfType<TextBlock>(), text => text.Text == "Shift+S");
            ShortcutPresentation.SetAltRevealed(window, false); window.UpdateLayout();
            ShortcutMotionTestScope.AssertState(caption, false);
            ShortcutPresentation.SetAltRevealed(window, true); window.UpdateLayout();
            ShortcutMotionTestScope.AssertState(caption, true);
            Assert.Equal(12, caption.FontSize);
            Assert.True(caption.ActualWidth + 0.5 >= caption.DesiredSize.Width);
            Rect bounds = caption.TransformToAncestor(save).TransformBounds(new Rect(caption.RenderSize));
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= save.ActualWidth + 0.5);
        }
    });

    [Fact]
    public Task PendingNamesAreUniqueAndVideoWritingBlocksFrameCapture() => Run(nameof(PendingNamesAreUniqueAndVideoWritingBlocksFrameCapture), () =>
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource();
        var pending = new List<CaptureRecord>();
        bool videoWriting = false;
        var service = new VideoFrameCaptureService(fixture.Queue,
            async (record, bitmap) => { pending.Add(record); await release.Task; return await fixture.Write(record, bitmap); },
            (_, _) => Task.FromResult(new VideoFrameCapturePixels(Solid(32, 18, Colors.Blue), 10)),
            _ => videoWriting);
        Task<CaptureRecord> first = service.SaveAsync(fixture.Source.Id, 7, fixture.SourcePath, 15);
        Task<CaptureRecord> second = service.SaveAsync(fixture.Source.Id, 7, fixture.SourcePath, 15);
        Assert.Equal(2, pending.Select(record => record.Title).Distinct().Count());
        Assert.Empty(fixture.Saved);
        release.SetResult(); Wait(Task.WhenAll(first, second));
        videoWriting = true;
        Assert.False(service.CanCapture(fixture.Source.Id, 7));
        Assert.Throws<InvalidOperationException>(() => Wait(service.SaveAsync(fixture.Source.Id, 7, fixture.SourcePath, 15)));
        Assert.Equal(2, fixture.Saved.Count);
    });

    [Fact]
    public Task PendingEditorSeekDisablesFrameButtonUntilDecoderSettles() => Run(nameof(PendingEditorSeekDisablesFrameButtonUntilDecoderSettles), () =>
    {
        using var fixture = new Fixture();
        using var engine = new HeldPreviewEngine();
        VideoEditorWindow editor = fixture.Editor(previewEngine: engine);
        try
        {
            Button save = Field<Button>(editor, "_saveCurrentFrameButton");
            Assert.True(save.IsEnabled);
            editor.PreviewSeekCoordinatorForTest.RequestExact(100);
            Assert.False(save.IsEnabled);
            Wait(engine.Started.Task);
            Assert.False(save.IsEnabled);
            Assert.Null(Wait(editor.SaveCurrentFrameAsync()));
            engine.Release.SetResult();
            Wait(editor.PreviewSeekCoordinatorForTest.WaitForIdleAsync());
            editor.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.True(save.IsEnabled);
            Assert.Empty(fixture.Saved);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public Task FileLeaseRevisionAndCancellationPreventStalePublication() => Run(nameof(FileLeaseRevisionAndCancellationPreventStalePublication), () =>
    {
        using var fixture = new Fixture();
        var decoded = new TaskCompletionSource<VideoFrameCapturePixels>();
        var service = new VideoFrameCaptureService(fixture.Queue, fixture.Write, (_, _) => decoded.Task);
        Task<CaptureRecord> saving = service.SaveAsync(fixture.Source.Id, 7, fixture.SourcePath, 15);
        Assert.Throws<IOException>(() => { using var replacement = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); });
        fixture.Source.ContentRevision++;
        decoded.SetResult(new(Solid(32, 18, Colors.SteelBlue), 10));
        Assert.Throws<InvalidOperationException>(() => Wait(saving));
        Assert.Empty(fixture.Saved);
        using (var replacement = new FileStream(fixture.SourcePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) Assert.True(replacement.CanWrite);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => Wait(fixture.Service.SaveAsync(fixture.Source.Id, 8, fixture.SourcePath, 15, cancellationToken: canceled.Token)));
        Assert.Empty(fixture.Saved);
    });

    [Fact]
    public Task ConcurrentClickAndCloseDoNotPublishOrResumeAnotherVideo() => Run(nameof(ConcurrentClickAndCloseDoNotPublishOrResumeAnotherVideo), () =>
    {
        using var fixture = new Fixture();
        var decoded = new TaskCompletionSource<VideoFrameCapturePixels>();
        var service = new VideoFrameCaptureService(fixture.Queue, fixture.Write, (_, _) => decoded.Task);
        GalleryVideoPlayerWindow player = fixture.Player(service);
        var playback = new List<bool>();
        player.PlaybackForTest = playback.Add;
        Set(player, "_playing", true);
        Task<CaptureRecord?> first = player.SaveCurrentFrameAsync();
        Assert.Null(Wait(player.SaveCurrentFrameAsync()));
        player.Close();
        decoded.SetResult(new(Solid(32, 18, Colors.SteelBlue), 10));
        Assert.Null(Wait(first));
        Assert.Empty(fixture.Saved);
        Assert.Equal(new[] { false }, playback);
    });

    [Fact]
    public Task DuplicateFrameNamesCreateIndependentImagesAndKeepCapturedSourceName() => Run(nameof(DuplicateFrameNamesCreateIndependentImagesAndKeepCapturedSourceName), () =>
    {
        using var fixture = new Fixture();
        CaptureRecord first = Wait(fixture.Service.SaveAsync(fixture.Source.Id, 7, fixture.SourcePath, 15));
        CaptureRecord second = Wait(fixture.Service.SaveAsync(fixture.Source.Id, 7, fixture.SourcePath, 15));
        Assert.NotEqual(first.Id, second.Id);
        Assert.EndsWith(" (2).png", second.Title);
        fixture.Source.Title = "새 이름";
        Assert.Equal("녹화 예시", first.SourceVideoName);
        Assert.Equal(10, first.SourceAssetTimeMs);
        Assert.Equal(15, first.SourceAssetRequestedTimeMs);
        Assert.Equal("녹화 예시 · " + VideoTimecode.Format(10), first.SourceWindowTitle);
        Assert.Equal("meeting.2026_00-00-00.010.png", VideoFrameCaptureService.FileName("meeting.2026", 10));
        Assert.True(File.Exists(fixture.Png(first)) && File.Exists(fixture.Png(second)));
    });

    [Fact]
    public Task FailedWriterShowsFailureAndNeverOffersAResult() => Run(nameof(FailedWriterShowsFailureAndNeverOffersAResult), () =>
    {
        using var fixture = new Fixture();
        var service = new VideoFrameCaptureService(fixture.Queue, (_, _) => throw new IOException("fixture full disk"),
            (_, _) => Task.FromResult(new VideoFrameCapturePixels(Solid(32, 18, Colors.Blue), 0)));
        GalleryVideoPlayerWindow player = fixture.Player(service);
        VideoEditorWindow editor = fixture.Editor(service);
        try
        {
            Assert.Null(Wait(player.SaveCurrentFrameAsync()));
            Assert.Null(Wait(editor.SaveCurrentFrameAsync()));
            Assert.Equal(UiText.Get("VideoFrame_SaveFailed"), Field<TextBlock>(player, "_status").Text);
            Assert.Equal(UiText.Get("VideoFrame_SaveFailed"), Field<TextBlock>(editor, "_statusLabel").Text);
            Assert.Equal(Visibility.Collapsed, Field<Button>(player, "_showFrameButton").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<Button>(editor, "_showSavedFrameButton").Visibility);
        }
        finally { player.Close(); editor.Close(); }
    });

    [Fact]
    public Task ShowInLibraryCancelDoesNotNavigateOnALaterClose() => Run(nameof(ShowInLibraryCancelDoesNotNavigateOnALaterClose), () =>
    {
        using var fixture = new Fixture();
        VideoEditorWindow editor = fixture.Editor();
        int shown = 0;
        Field<VideoEditDocument>(editor, "_editDocument").TextOverlays.Add(new TimedTextOverlay { StartMs = 0, EndMs = 1000, Text = "unsaved" });
        editor.UnsavedEditsPromptForTest = () => EditorCloseChoice.Cancel;
        Wait(editor.RequestCloseForNavigation(() => shown++));
        Assert.Equal(0, shown);
        editor.Close();
        Assert.Equal(0, shown);
    });

    [Fact]
    public Task ShowInLibraryWaitsForSavedVideoPublicationAndClose() => Run(nameof(ShowInLibraryWaitsForSavedVideoPublicationAndClose), () =>
    {
        using var fixture = new Fixture(realClip: true);
        VideoEditorWindow editor = fixture.Editor();
        bool closed = false; int shown = 0;
        editor.Closed += (_, _) => closed = true;
        Field<VideoEditDocument>(editor, "_editDocument").TextOverlays.Add(new TimedTextOverlay { StartMs = 0, EndMs = 300, Text = "unsaved" });
        editor.UnsavedEditsPromptForTest = () => EditorCloseChoice.SaveAndClose;
        var publishing = new TaskCompletionSource();
        var published = new TaskCompletionSource();
        editor.RenderStagingPathFactory = () => Path.Combine(fixture.Root, "navigation-render.mp4");
        editor.VideoCommitHandler = async (_, _, _) => { publishing.SetResult(); await published.Task; };
        Task navigation = editor.RequestCloseForNavigation(() => { Assert.True(closed); shown++; });
        Wait(publishing.Task);
        Assert.False(closed); Assert.Equal(0, shown); Assert.False(navigation.IsCompleted);
        published.SetResult();
        Wait(navigation);
        Assert.True(closed); Assert.Equal(1, shown);
    });

    [Fact]
    public Task ShowInLibraryWaitsForActualDiscardClose() => Run(nameof(ShowInLibraryWaitsForActualDiscardClose), () =>
    {
        using var fixture = new Fixture();
        VideoEditorWindow editor = fixture.Editor();
        bool closed = false; int shown = 0;
        editor.Closed += (_, _) => closed = true;
        Field<VideoEditDocument>(editor, "_editDocument").TextOverlays.Add(new TimedTextOverlay { StartMs = 0, EndMs = 1000, Text = "unsaved" });
        editor.UnsavedEditsPromptForTest = () => EditorCloseChoice.DontSave;
        Wait(editor.RequestCloseForNavigation(() => { Assert.True(closed); shown++; }));
        Assert.Equal(1, shown);
    });

    private static Task Run(string name, Action verify) => SettingsThemePreviewTests.RunIsolated(name, () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        using var language = UiText.UseLanguage("ko-KR");
        try { verify(); }
        finally { application.Shutdown(); }
    }, typeof(VideoFrameCaptureTests));

    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(task.IsCompleted, "The frame operation did not complete.");
        task.GetAwaiter().GetResult();
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Private)!.SetValue(owner, value);
    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0); return bytes;
    }
    private static BitmapSource Solid(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = color.B; pixels[i + 1] = color.G; pixels[i + 2] = color.R; pixels[i + 3] = 255; }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(@"C:\GitRepositories\my-capture-libvideo\work\library-video-impl", "frame-tests-" + Guid.NewGuid().ToString("N"));
        internal AppPaths Paths { get; }
        internal CaptureQueue Queue { get; }
        internal CaptureRecord Source { get; }
        internal string SourcePath { get; }
        internal VideoFrameCaptureService Service { get; }
        internal List<CaptureRecord> Saved { get; } = [];
        internal Fixture(bool realClip = false, int width = 1280, int height = 720)
        {
            Paths = AppPaths.CreateForRoot(Root); Paths.EnsureCreated();
            Queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Source = new CaptureRecord { MediaKind = CaptureMediaKind.Video, Title = "녹화 예시", Width = width, Height = height,
                DurationMs = 300, FrameRate = 10, FrameCount = 3, ContentRevision = 7 };
            Source.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(Source.Id, Source.CreatedAt);
            Directory.CreateDirectory(Queue.GetDirectory(Source));
            SourcePath = Queue.GetFilePath(Source, CaptureFileNames.VideoSource);
            if (realClip)
            {
                var options = new VideoEncoderOptions(SourcePath, width, height, 10, VideoEncoderOptions.DeriveBitrate(width, height, 10));
                using var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance);
                byte[] frame = Pixels(Solid(width, height, Colors.Black));
                for (int i = 0; i < 3; i++) encoder.WriteFrame(new EncoderFrame(frame, width, height, width * 4, i * 100.0));
                encoder.Complete();
            }
            else File.WriteAllBytes(SourcePath, [0, 1, 2, 3]);
            Queue.Add(Source);
            Service = new VideoFrameCaptureService(Queue, Write,
                realClip ? null : (_, _) => Task.FromResult(new VideoFrameCapturePixels(Solid(32, 18, Colors.SteelBlue), 10)));
        }
        internal Task<CaptureRecord> Write(CaptureRecord record, BitmapSource bitmap)
        {
            File.WriteAllBytes(Png(record), ImageCodec.EncodePng(bitmap));
            File.WriteAllText(Meta(record), JsonSerializer.Serialize(record));
            Saved.Add(record); Queue.Add(record); return Task.FromResult(record);
        }
        internal string Png(CaptureRecord record) => Path.Combine(Root, record.Id + ".png");
        internal string Meta(CaptureRecord record) => Path.Combine(Root, record.Id + ".json");
        internal BitmapSource Read(CaptureRecord record)
        {
            using var stream = File.OpenRead(Png(record));
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image.Freeze(); return image;
        }
        internal GalleryVideoPlayerWindow Player(VideoFrameCaptureService? service = null)
        {
            var player = new GalleryVideoPlayerWindow { Left = -10000, Top = -10000, Topmost = false, ShowActivated = false };
            Set(player, "_videoId", Source.Id); Set(player, "_mediaReady", true);
            player.PlaybackForTest = _ => { };
            player.MediaForDiagnostic.Source = new Uri(SourcePath);
            player.ConfigureFrameCapture(service ?? Service, 7, _ => { });
            return player;
        }
        internal VideoEditorWindow Editor(VideoFrameCaptureService? service = null, IVideoPreviewEngine? previewEngine = null)
        {
            var editor = new VideoEditorWindow(new RecordingResult(SourcePath, 300, 10, 3, 1280, 720), Paths, NullLoggerFactory.Instance, previewEngine: previewEngine)
            {
                Left = -10000, Top = -10000, ShowActivated = false,
                FrameCaptureService = service ?? Service, FrameSourceVideoId = Source.Id, FrameSourceRevision = 7, ShowFrameInLibrary = _ => { },
            };
            typeof(VideoEditorWindow).GetMethod("CompleteOpen", Private)!.Invoke(editor, [300.0, true]);
            return editor;
        }
        public void Dispose() { } // Preserve isolated evidence; no real profile or cleanup API.
    }

    private sealed class HeldPreviewEngine : IVideoPreviewEngine
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PresentedPreviewFrame> SeekAsync(PreviewSeekRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(request.Generation, request.TargetPositionMs, request.TargetPositionMs, request.TargetFrameIndex, request.Mode);
        }
        public void Dispose() { }
    }
}
