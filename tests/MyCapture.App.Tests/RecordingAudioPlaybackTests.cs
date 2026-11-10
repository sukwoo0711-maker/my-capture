using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Display;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioPlaybackTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MonitorInfo Monitor = new("Synthetic", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, true);
    private static VideoSourceInfo Source(bool audio) => new(10000, false)
    {
        Audio = audio ? new AudioSourceInfo(1, new Guid("00001610-0000-0010-8000-00aa00389b71"), 48000, 2, 16) : null,
    };

    [Fact]
    public Task PlayerOpenSeekFrameSaveAndPauseAreSilent() => Run(nameof(PlayerOpenSeekFrameSaveAndPauseAreSilent), () =>
    {
        using var fixture = new Fixture();
        using var engine = new ControlledEngine();
        GalleryVideoPlayerWindow player = fixture.Player(engine);
        try
        {
            fixture.Open(player, audio: true);
            Silent(player.MediaForDiagnostic);
            player.RequestPlay();
            Audible(player.MediaForDiagnostic);
            Field<Slider>(player, "_seek").Value = 500;
            WaitUntil(() => engine.Pending is not null);
            Silent(player.MediaForDiagnostic);
            Assert.False(player.IsPlayingForDiagnostic);
            engine.Complete();
            Wait(player.FrameSeekTaskForTest);
            Audible(player.MediaForDiagnostic);

            fixture.OnDecode = () => Silent(player.MediaForDiagnostic);
            CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(player.SaveCurrentFrameAsync()));
            Assert.Equal(fixture.SourceRecord.Id, saved.SourceVideoId);
            Assert.Equal(1, fixture.Decodes);
            Audible(player.MediaForDiagnostic);
            Invoke(player, "SetPlayback", false, false);
            Silent(player.MediaForDiagnostic);
        }
        finally { player.Close(); }
        Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
        Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
    });

    [Fact]
    public Task PlayerThumbDragStaysSilentUntilReleaseAndFinalSeekLands() => Run(nameof(PlayerThumbDragStaysSilentUntilReleaseAndFinalSeekLands), () =>
    {
        using var fixture = new Fixture();
        using var engine = new ControlledEngine();
        GalleryVideoPlayerWindow player = fixture.Player(engine);
        try
        {
            fixture.Open(player, true);
            player.RequestPlay();
            Audible(player.MediaForDiagnostic);
            Slider seek = Field<Slider>(player, "_seek");
            seek.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            Silent(player.MediaForDiagnostic);
            foreach (double position in new[] { 250d, 500d })
            {
                seek.Value = position;
                WaitUntil(() => engine.Pending is not null);
                Silent(player.MediaForDiagnostic);
                engine.Complete();
                Wait(player.FrameSeekTaskForTest);
                Silent(player.MediaForDiagnostic);
                Assert.False(player.IsPlayingForDiagnostic);
            }
            seek.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            WaitUntil(() => engine.Pending is not null);
            Assert.Equal(500, engine.Pending!.Value.TargetPositionMs);
            Silent(player.MediaForDiagnostic);
            engine.Complete();
            Wait(player.FrameSeekTaskForTest);
            Assert.True(player.IsPlayingForDiagnostic);
            Audible(player.MediaForDiagnostic);
            // A pause requested during a later drag must survive an older seek completion.
            seek.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            seek.Value = 750;
            WaitUntil(() => engine.Pending is not null);
            Invoke(player, "SetPlayback", false, false);
            engine.Complete();
            Wait(player.FrameSeekTaskForTest);
            seek.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            WaitUntil(() => engine.Pending is not null);
            engine.Complete();
            Wait(player.FrameSeekTaskForTest);
            Silent(player.MediaForDiagnostic);
            Assert.False(player.IsPlayingForDiagnostic);
        }
        finally { player.Close(); }
    });

    [Fact]
    public Task EditorOpenSeekScrubStepAndFrameSaveAreSilent() => Run(nameof(EditorOpenSeekScrubStepAndFrameSaveAreSilent), () =>
    {
        using var fixture = new Fixture();
        using var engine = new ControlledEngine { Automatic = true };
        VideoEditorWindow editor = fixture.Editor(engine);
        try
        {
            fixture.Open(editor, audio: true);
            Silent(editor.MediaForTest);
            Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
            WaitUntil(() => Field<bool>(editor, "_isPlaying"));
            Audible(editor.MediaForTest);
            Assert.True(editor.HandleShortcut(Key.Right, ModifierKeys.None));
            Silent(editor.MediaForTest);
            Wait(editor.PreviewSeekCoordinatorForTest.WaitForIdleAsync());
            Assert.True(editor.HandleShortcut(Key.OemPeriod, ModifierKeys.None));
            Silent(editor.MediaForTest);
            Wait(editor.PreviewSeekCoordinatorForTest.WaitForIdleAsync());

            Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
            WaitUntil(() => Field<bool>(editor, "_isPlaying"));
            Invoke(editor, "OnTimelinePlayhead", null, 1200d);
            Silent(editor.MediaForTest);
            Invoke(editor, "OnTimelinePlayheadInteractionCompleted", null, 1200d);
            WaitUntil(() => Field<bool>(editor, "_isPlaying"));
            Audible(editor.MediaForTest);
            Wait(editor.PreviewSeekCoordinatorForTest.WaitForIdleAsync());
            Drain();
            fixture.OnDecode = () => Silent(editor.MediaForTest);
            CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(editor.SaveCurrentFrameAsync()));
            Assert.Equal(fixture.SourceRecord.Id, saved.SourceVideoId);
            Assert.Equal(1, fixture.Decodes);
            WaitUntil(() => Field<bool>(editor, "_isPlaying"));
            Audible(editor.MediaForTest);
            Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
            Silent(editor.MediaForTest);
        }
        finally { editor.Close(); }
        Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
        Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
    });

    [Fact]
    public Task CutTickMutesBeforeSeekAndWaitsForObservedLanding() => Run(nameof(CutTickMutesBeforeSeekAndWaitsForObservedLanding), () =>
    {
        using var fixture = new Fixture();
        using var engine = new ControlledEngine { Automatic = true };
        var document = VideoEditDocument.CreateFor(320, 180, 10000);
        document.RemovedRanges.Add(new(2000, 4000));
        VideoEditorWindow editor = fixture.Editor(engine, document);
        try
        {
            fixture.Open(editor, audio: true);
            double position = 1000;
            editor.PlaybackPositionForTest = () => position;
            Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
            WaitUntil(() => Field<bool>(editor, "_isPlaying"));
            Audible(editor.MediaForTest);
            Field<DispatcherTimer>(editor, "_playbackTimer").Stop();
            engine.Automatic = false;
            engine.Started = () => editor.Dispatcher.Invoke(() => Silent(editor.MediaForTest));
            position = 2050;
            Invoke(editor, "OnPlaybackTick", null, EventArgs.Empty);
            Silent(editor.MediaForTest);
            WaitUntil(() => engine.Pending is not null);
            Assert.Equal(4000, engine.Pending!.Value.TargetPositionMs);
            engine.Complete();
            WaitUntil(() => !Field<bool>(editor, "_cutSeekInFlight"));
            // Completing the best-effort seek does not prove that the native clock landed.
            Silent(editor.MediaForTest);
            Invoke(editor, "OnPlaybackTick", null, EventArgs.Empty);
            Silent(editor.MediaForTest);
            position = 4000;
            Invoke(editor, "OnPlaybackTick", null, EventArgs.Empty);
            Audible(editor.MediaForTest);
            position = 2050; // A late native clock regression must close output again.
            Invoke(editor, "OnPlaybackTick", null, EventArgs.Empty);
            Silent(editor.MediaForTest);
        }
        finally { editor.Close(); }
    });

    [Fact]
    public Task OnePersistedChoiceUpdatesBothWindowsAndSurvivesReload() => Run(nameof(OnePersistedChoiceUpdatesBothWindowsAndSurvivesReload), () =>
    {
        using var fixture = new Fixture();
        var store = new SettingsStore(fixture.Paths, NullLogger<SettingsStore>.Instance);
        AppSettings settings = store.Load();
        var preference = new PlaybackAudioPreference(() => settings.Recording.PlaybackMuted, muted =>
        {
            settings.Recording.PlaybackMuted = muted;
            store.Save(settings);
        });
        using var engine = new ControlledEngine { Automatic = true };
        GalleryVideoPlayerWindow player = fixture.Player(new ControlledEngine { Automatic = true }, preference);
        VideoEditorWindow editor = fixture.Editor(engine, preference: preference);
        try
        {
            fixture.Open(player, true);
            fixture.Open(editor, true);
            Assert.Equal(2, VideoPlaybackAudio.ActiveCount);
            Assert.Equal(1, MediaElementPreviewEngine.ActiveCount); // Editor native adapter; playback sinks are fake.
            player.RequestPlay();
            Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
            WaitUntil(() => Field<bool>(editor, "_isPlaying"));
            Audible(player.MediaForDiagnostic); Audible(editor.MediaForTest);
            Assert.False(player.HandleMuteShortcut(Key.M, ModifierKeys.Control, null));
            Assert.False(player.HandleMuteShortcut(Key.M, ModifierKeys.None, new TextBox()));
            Assert.False(player.HandleMuteShortcut(Key.M, ModifierKeys.None, null, isRepeat: true));
            Assert.True(player.HandleMuteShortcut(Key.M, ModifierKeys.None, null));
            Silent(player.MediaForDiagnostic); Silent(editor.MediaForTest);
            Assert.True(new SettingsStore(fixture.Paths, NullLogger<SettingsStore>.Instance).Load().Recording.PlaybackMuted);
            Assert.False(editor.HandleShortcut(Key.M, ModifierKeys.None, new PasswordBox()));
            _ = SetActiveWindow(new WindowInteropHelper(editor).Handle);
            editor.Focus();
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, 0, Key.M)
                { RoutedEvent = Keyboard.KeyDownEvent };
            editor.RaiseEvent(key);
            Assert.True(key.Handled);
            Audible(player.MediaForDiagnostic); Audible(editor.MediaForTest);
            editor.MuteButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Silent(player.MediaForDiagnostic); Silent(editor.MediaForTest);
        }
        finally { player.Close(); editor.Close(); }
        Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
        Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
        settings = new SettingsStore(fixture.Paths, NullLogger<SettingsStore>.Instance).Load();
        Assert.True(settings.Recording.PlaybackMuted);
        GalleryVideoPlayerWindow reopened = fixture.Player(new ControlledEngine { Automatic = true }, preference);
        try { fixture.Open(reopened, true); reopened.RequestPlay(); Silent(reopened.MediaForDiagnostic); }
        finally { reopened.Close(); }
        Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
        Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
    });

    [Fact]
    public Task NoAudioKeepsMuteCollapsedAndShortcutInactive() => Run(nameof(NoAudioKeepsMuteCollapsedAndShortcutInactive), () =>
    {
        using var fixture = new Fixture();
        using var engine = new ControlledEngine { Automatic = true };
        GalleryVideoPlayerWindow player = fixture.Player(new ControlledEngine { Automatic = true });
        VideoEditorWindow editor = fixture.Editor(engine);
        try
        {
            fixture.Open(player, false); fixture.Open(editor, false);
            Assert.Equal(Visibility.Collapsed, player.MuteButtonForTest.Visibility);
            Assert.Equal(Visibility.Collapsed, editor.MuteButtonForTest.Visibility);
            Assert.False(player.HandleMuteShortcut(Key.M, ModifierKeys.None, null));
            Assert.False(editor.HandleShortcut(Key.M, ModifierKeys.None));
            player.RequestPlay();
            Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
            WaitUntil(() => Field<bool>(editor, "_isPlaying"));
            Silent(player.MediaForDiagnostic); Silent(editor.MediaForTest);
            Assert.True(player.IsPlayingForDiagnostic);
            Assert.True(editor.PlayButtonShowsPauseForTest);
        }
        finally { player.Close(); editor.Close(); }
        Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
        Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
    });

    [Fact]
    public Task MediaEndedPlayIntentSurvivesASynchronousSeek() => Run(nameof(MediaEndedPlayIntentSurvivesASynchronousSeek), () =>
    {
        using var fixture = new Fixture();
        using var engine = new ControlledEngine { Automatic = true };
        GalleryVideoPlayerWindow player = fixture.Player(engine);
        try
        {
            fixture.Open(player, true);
            player.RequestPlay();
            Audible(player.MediaForDiagnostic);
            player.MediaForDiagnostic.RaiseEvent(new RoutedEventArgs(MediaElement.MediaEndedEvent));
            Silent(player.MediaForDiagnostic);
            Assert.False(player.IsPlayingForDiagnostic);
            player.RequestPlay();
            Wait(player.FrameSeekTaskForTest);
            Assert.True(player.IsPlayingForDiagnostic);
            Audible(player.MediaForDiagnostic);
        }
        finally { player.Close(); }
    });

    [Fact]
    public Task NativePreviewAdaptersExistOnlyWhileTheirWindowsAreOpen() => Run(nameof(NativePreviewAdaptersExistOnlyWhileTheirWindowsAreOpen), () =>
    {
        using var fixture = new Fixture();
        Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
        Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
        var player = new GalleryVideoPlayerWindow { PlaybackForTest = _ => { } };
        var editor = new VideoEditorWindow(new RecordingResult(fixture.PathToSource, 10000, 10, 100, 320, 180),
            fixture.Paths, NullLoggerFactory.Instance, initialMonitor: Monitor);
        try
        {
            Assert.Equal(2, VideoPlaybackAudio.ActiveCount);
            Assert.Equal(1, MediaElementPreviewEngine.ActiveCount);
            fixture.Open(player, true);
            Assert.Equal(2, MediaElementPreviewEngine.ActiveCount);
            player.Close();
            Assert.Equal(1, VideoPlaybackAudio.ActiveCount);
            Assert.Equal(1, MediaElementPreviewEngine.ActiveCount);
            Silent(player.MediaForDiagnostic);
            Assert.Null(player.MediaForDiagnostic.Source);
            editor.Close();
            Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
            Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
            Silent(editor.MediaForTest);
        }
        finally { player.Close(); editor.Close(); }
    });

    [Fact]
    public Task NativeAudioSeekReopensEndedMediaWhileMuted() => Run(nameof(NativeAudioSeekReopensEndedMediaWhileMuted), () =>
    {
        string root = OwnedTestDirectory.Create("mc-audio-playback-ended-");
        string path = Path.Combine(root, "synthetic-audio.mp4");
        using (var encoder = new MediaFoundationVideoEncoder(new(path, 64, 64, 10, 1_000_000)
            { AllowHardwareEncoder = false, Audio = new AudioEncoderOptions() }, NullLogger.Instance))
        {
            for (int i = 0; i < 25; i++)
            {
                encoder.WriteFrame(new(new byte[64 * 64 * 4], 64, 64, 64 * 4, i * 100));
                encoder.WriteAudio(new(i * 4800, new short[9600]));
            }
            encoder.Complete();
        }
        Assert.NotNull(MediaFoundationAudioReader.ReadSourceInfo(path));
        var media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            IsMuted = true, Volume = 0, ScrubbingEnabled = true };
        var window = new Window { Content = media, Width = 200, Height = 160, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        using var engine = new MediaElementPreviewEngine(media, 10) { DurationMs = 2500 };
        bool opened = false;
        media.MediaOpened += (_, _) => { Silent(media); opened = true; };
        try
        {
            window.Show();
            media.Source = new Uri(path); media.Play(); media.Pause();
            WaitUntil(() => opened && media.HasVideo);
            engine.TreatNextReadAsEndedForTest = true;
            PresentedPreviewFrame frame = Wait(engine.SeekAsync(new(1, 500, 5, PreviewSeekMode.Exact), CancellationToken.None).AsTask());
            Wait(engine.WhenSettledAsync());
            Silent(media);
            Assert.True(engine.ReopenCountForTest >= 1);
            Assert.True(MediaElementPreviewEngine.HasLanded(frame.PresentedPositionMs, 500));
            PresentedPreviewFrame next = Wait(engine.SeekAsync(new(2, 1000, 10, PreviewSeekMode.Exact), CancellationToken.None).AsTask());
            Silent(media);
            Assert.True(MediaElementPreviewEngine.HasLanded(next.PresentedPositionMs, 1000));
        }
        finally { media.Close(); media.Source = null; window.Close(); }
    });

    [Fact]
    public Task TransportEvidenceShowsBothLanguagesAndAllAudioStates() => Run(nameof(TransportEvidenceShowsBothLanguagesAndAllAudioStates), () =>
    {
        string? evidence = TestDiagnosticArtifacts.Create("MYCAPTURE_R4_PLAYBACK_EVIDENCE", "playback");
        foreach (string language in new[] { "ko-KR", "en-US" })
        foreach (string state in new[] { "sound", "muted", "no-audio" })
        {
            using var culture = UiText.UseLanguage(language);
            using var fixture = new Fixture();
            var preference = PlaybackAudioPreference.InMemory(state == "muted");
            using var engine = new ControlledEngine { Automatic = true };
            GalleryVideoPlayerWindow player = fixture.Player(new ControlledEngine { Automatic = true }, preference);
            VideoEditorWindow editor = fixture.Editor(engine, preference: preference);
            try
            {
                FluidMotion.SetWindowEntrance(player, false);
                FluidMotion.SetWindowEntrance(editor, false);
                fixture.Open(player, state != "no-audio"); fixture.Open(editor, state != "no-audio");
                player.Show(); player.UpdateLayout(); editor.UpdateLayout();
                Drain();
                _ = SetActiveWindow(new WindowInteropHelper(editor).Handle);
                foreach (var (window, button, name) in new (Window, Button, string)[]
                    { (player, player.MuteButtonForTest, "player"), (editor, editor.MuteButtonForTest, "editor") })
                {
                    Silent(window == player ? player.MediaForDiagnostic : editor.MediaForTest);
                    Assert.Equal(state == "no-audio" ? Visibility.Collapsed : Visibility.Visible, button.Visibility);
                    if (state != "no-audio")
                    {
                        Assert.Equal(UiText.Get(state == "muted" ? "PlaybackAudio_Unmute" : "PlaybackAudio_Mute"), AutomationProperties.GetName(button));
                        Assert.Equal("M", AutomationProperties.GetAcceleratorKey(button));
                        Assert.Equal(44, button.ActualHeight);
                    }
                    FrameworkElement transport = name == "editor"
                        ? Descendants(window).OfType<Grid>().Single(grid => grid.Name == "VideoTransport")
                        : (FrameworkElement)VisualTreeHelper.GetParent(player.MuteButtonForTest);
                    if (evidence is not null) Save(transport, evidence, $"{name}-{language}-{state}-100.png");
                }
            }
            finally { player.Close(); editor.Close(); }
        }
        Assert.Equal(0, VideoPlaybackAudio.ActiveCount);
        Assert.Equal(0, MediaElementPreviewEngine.ActiveCount);
    });

    private static void Save(FrameworkElement element, string directory, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        // Rendering only the transport omits its window's surface. Preserve that actual theme
        // colour so the evidence has the same readable background as the open product window.
        var surface = new DrawingVisual();
        using (DrawingContext drawing = surface.RenderOpen())
        {
            var bounds = new Rect(element.RenderSize);
            drawing.DrawRectangle(element.TryFindResource("Surface.Base") as Brush ?? Brushes.Black, null, bounds);
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(surface);
        TestDiagnosticArtifacts.SavePng(directory, name, bitmap);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Silent(MediaElement media) { Assert.True(media.IsMuted); Assert.Equal(0, media.Volume); }
    private static void Audible(MediaElement media) { Assert.False(media.IsMuted); Assert.Equal(1, media.Volume); }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Invoke(object owner, string name, params object?[] args) => owner.GetType().GetMethod(name, Private)!.Invoke(owner, args);
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    private static void WaitUntil(Func<bool> completed)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!completed() && DateTime.UtcNow < deadline) RangePatternClip.Pump(TimeSpan.FromMilliseconds(10));
        Assert.True(completed(), "The synthetic playback operation did not finish.");
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
    }, typeof(RecordingAudioPlaybackTests));

    private sealed class Fixture : IDisposable
    {
        internal AppPaths Paths { get; }
        internal CaptureRecord SourceRecord { get; }
        internal string PathToSource { get; }
        private readonly VideoFrameCaptureService _capture;
        internal Action? OnDecode;
        internal int Decodes;
        internal Fixture()
        {
            Paths = AppPaths.CreateForRoot(OwnedTestDirectory.Create("mc-audio-playback-")); Paths.EnsureCreated();
            var queue = new CaptureQueue(Paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            SourceRecord = new CaptureRecord { MediaKind = CaptureMediaKind.Video, Width = 320, Height = 180,
                DurationMs = 10000, FrameRate = 10, FrameCount = 100, Title = "Synthetic recording" };
            SourceRecord.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(SourceRecord.Id, SourceRecord.CreatedAt);
            Directory.CreateDirectory(queue.GetDirectory(SourceRecord));
            PathToSource = queue.GetFilePath(SourceRecord, CaptureFileNames.VideoSource);
            File.WriteAllBytes(PathToSource, [0, 1, 2, 3]);
            queue.Add(SourceRecord);
            _capture = new VideoFrameCaptureService(queue, (record, _) => Task.FromResult(record), (request, _) =>
            {
                OnDecode?.Invoke(); Decodes++;
                BitmapSource image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
                image.Freeze();
                return Task.FromResult(new VideoFrameCapturePixels(image, request.RequestedTimeMs));
            });
        }
        internal GalleryVideoPlayerWindow Player(IVideoPreviewEngine engine, PlaybackAudioPreference? preference = null)
        {
            var player = new GalleryVideoPlayerWindow(null, null, preference)
            {
                Left = -20000, Top = -20000, Topmost = false, ShowActivated = false, ShowInTaskbar = false,
                PlaybackForTest = _ => { }, FrameSeekEngineFactoryForTest = _ => engine,
            };
            player.ConfigureFrameCapture(_capture, SourceRecord.ContentRevision);
            return player;
        }
        internal void Open(GalleryVideoPlayerWindow player, bool audio)
        {
            player.SourceProbeForTest = _ => Task.FromResult(Source(audio));
            Assert.True(player.Open(SourceRecord.Id, SourceRecord.Title, PathToSource, false));
            // Prevent decoding of the dummy file. No sink or native audio source is opened.
            player.MediaForDiagnostic.Close(); player.MediaForDiagnostic.Source = null;
            player.MediaForDiagnostic.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
            // SaveCurrentFrame uses this URI as an identity; PlaybackForTest owns playback.
            player.MediaForDiagnostic.Source = new Uri(PathToSource);
            Assert.True(player.IsMediaReadyForDiagnostic);
        }
        internal VideoEditorWindow Editor(IVideoPreviewEngine engine, VideoEditDocument? document = null, PlaybackAudioPreference? preference = null) =>
            new(new RecordingResult(PathToSource, 10000, 10, 100, 320, 180), Paths, NullLoggerFactory.Instance,
                document, engine, () => { }, preference, Monitor)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                Width = 920, Height = 680, ShowActivated = false, ShowInTaskbar = false,
                FrameCaptureService = _capture, FrameSourceVideoId = SourceRecord.Id, FrameSourceRevision = SourceRecord.ContentRevision,
            };
        internal void Open(VideoEditorWindow editor, bool audio)
        {
            editor.SourceProbeForTest = _ => Task.FromResult(Source(audio));
            editor.OpenMediaForTest = media => media.RaiseEvent(new RoutedEventArgs(MediaElement.MediaOpenedEvent));
            editor.Show(); editor.UpdateLayout();
            Assert.True(editor.IsMediaReadyForTest);
            Assert.Null(editor.MediaForTest.Source);
        }
        public void Dispose() { } // Owned scratch is retained by this round's file-handling contract.
    }

    private sealed class ControlledEngine : IVideoPreviewEngine
    {
        internal bool Automatic;
        internal Action? Started;
        internal PreviewSeekRequest? Pending;
        private TaskCompletionSource<PresentedPreviewFrame>? _completion;
        public ValueTask<PresentedPreviewFrame> SeekAsync(PreviewSeekRequest request, CancellationToken cancellationToken)
        {
            Started?.Invoke();
            if (Automatic) return ValueTask.FromResult(new PresentedPreviewFrame(request.Generation, request.TargetPositionMs,
                request.TargetPositionMs, request.TargetFrameIndex, request.Mode));
            Pending = request;
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(_completion.Task.WaitAsync(cancellationToken));
        }
        internal void Complete()
        {
            PreviewSeekRequest request = Pending!.Value;
            Pending = null;
            _completion!.SetResult(new(request.Generation, request.TargetPositionMs, request.TargetPositionMs, request.TargetFrameIndex, request.Mode));
        }
        public void Dispose() => _completion?.TrySetCanceled();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr window);
}
