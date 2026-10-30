using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Editing;
using MyCapture.App.Recording;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Imaging;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecoveredVideoFrameRound2Tests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task RecoveredVideoCanSaveAFrameAndProtectsItsDraftOnNavigation() => Run(
        nameof(RecoveredVideoCanSaveAFrameAndProtectsItsDraftOnNavigation), () =>
        {
            using var fixture = new Fixture();
            VideoEditorWindow editor = Wait(fixture.App.CreateRecoveredVideoEditorAsync(fixture.Draft));
            editor.Left = -30000; editor.Top = -30000; editor.ShowActivated = false;
            try
            {
                Assert.Same(fixture.Frames, editor.FrameCaptureService);
                Assert.Equal(fixture.Source.Id, editor.FrameSourceVideoId);
                Assert.Equal(7, editor.FrameSourceRevision);
                Assert.NotNull(editor.FrameImageCommitHandlerFactory);
                Assert.NotNull(editor.ShowFrameInLibrary);
                editor.Show();
                PumpUntil(() => editor.IsMediaReadyForTest);
                Assert.False(editor.HasMediaFailedForTest);
                var button = (Button)typeof(VideoEditorWindow).GetField("_saveCurrentFrameButton", Private)!.GetValue(editor)!;
                PumpUntil(() => button.IsEnabled);
                CaptureRecord saved = Assert.IsType<CaptureRecord>(Wait(editor.SaveCurrentFrameAsync()));
                Assert.Equal(fixture.Source.Id, saved.SourceVideoId);
                Assert.Equal(7, saved.SourceVideoContentRevision);
                Assert.Equal("source", saved.SourceAssetKind);
                Assert.Single(fixture.Saved);
                using var stream = File.OpenRead(Path.Combine(fixture.Root, saved.Id + ".png"));
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Assert.Equal(640, image.PixelWidth); Assert.Equal(360, image.PixelHeight);
                int prompts = 0;
                editor.UnsavedEditsPromptForTest = () => { prompts++; return EditorCloseChoice.Cancel; };
                editor.ShowFrameInLibrary!(saved.Id);
                PumpUntil(() => prompts == 1 && editor.IsEnabled);
                Assert.True(editor.IsVisible);
                Assert.True(editor.HasUnsavedEditsForTest);
            }
            finally
            {
                editor.Close();
                Wait(editor.FlushDraftAsync());
            }
            Assert.False(fixture.Library.IsBusy(fixture.Source.Id));
        });

    [Fact]
    public Task RecoveredVideoStillRejectsAChangedSourceBeforeOpeningAnEditSession() => Run(
        nameof(RecoveredVideoStillRejectsAChangedSourceBeforeOpeningAnEditSession), () =>
        {
            using var fixture = new Fixture();
            fixture.Draft.Metadata["sourceHash"] = "changed";
            Assert.Throws<IOException>(() => Wait(fixture.App.CreateRecoveredVideoEditorAsync(fixture.Draft)));
            Assert.False(fixture.Library.IsBusy(fixture.Source.Id));
            Assert.Empty(fixture.Saved);
        });

    private static Task Run(string name, Action body) => SettingsThemePreviewTests.RunIsolated(name, () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resource}.xaml") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { body(); }
        finally { application.Shutdown(); }
    }, typeof(RecoveredVideoFrameRound2Tests));

    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    private static void Wait(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> ready)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Assert.True(ready(), "The recovered editor did not settle.");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(@"C:\GitRepositories\my-capture-libvideo\work\library-video-impl", "recovery-r2-" + Guid.NewGuid().ToString("N"));
        internal App App { get; } = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        internal CaptureRecord Source { get; }
        internal VideoLibraryService Library { get; }
        internal VideoFrameCaptureService Frames { get; }
        internal EditDraft Draft { get; }
        internal List<CaptureRecord> Saved { get; } = [];
        private readonly ServiceProvider _services;

        internal Fixture()
        {
            AppPaths paths = AppPaths.CreateForRoot(Root); paths.EnsureCreated();
            var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
            Library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
            Source = new CaptureRecord { MediaKind = CaptureMediaKind.Video, Title = "복구 영상", Width = 640, Height = 360,
                DurationMs = 300, FrameRate = 10, FrameCount = 3, ContentRevision = 7 };
            Source.RelativeDirectory = CaptureQueue.BuildRelativeDirectory(Source.Id, Source.CreatedAt);
            Directory.CreateDirectory(queue.GetDirectory(Source));
            string sourcePath = queue.GetFilePath(Source, CaptureFileNames.VideoSource);
            using (var encoder = new MediaFoundationVideoEncoder(new VideoEncoderOptions(sourcePath, 640, 360, 10,
                VideoEncoderOptions.DeriveBitrate(640, 360, 10)), NullLogger.Instance))
            {
                foreach (int index in Enumerable.Range(0, 3))
                {
                    byte[] pixels = new byte[640 * 360 * 4];
                    for (int offset = 0; offset < pixels.Length; offset += 4)
                    { pixels[offset + (2 - index)] = 255; pixels[offset + 3] = 255; }
                    encoder.WriteFrame(new EncoderFrame(pixels, 640, 360, 640 * 4, index * 100.0));
                }
                encoder.Complete();
            }
            queue.Add(Source);
            Frames = new VideoFrameCaptureService(queue, (record, bitmap) =>
            {
                File.WriteAllBytes(Path.Combine(Root, record.Id + ".png"), ImageCodec.EncodePng(bitmap));
                Saved.Add(record); queue.Add(record); return Task.FromResult(record);
            }, isWriting: Library.IsWriting);
            _services = new ServiceCollection().AddSingleton(paths).AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).BuildServiceProvider();
            typeof(App).GetField("_services", Private)!.SetValue(App, _services);
            typeof(App).GetField("_queue", Private)!.SetValue(App, queue);
            typeof(App).GetField("_videoLibrary", Private)!.SetValue(App, Library);
            typeof(App).GetField("_videoFrameCapture", Private)!.SetValue(App, Frames);
            typeof(App).GetProperty("DraftStore", Private)!.SetValue(App, new EditDraftStore(Path.Combine(Root, "drafts"), _ => { }));
            Draft = new EditDraft(Guid.NewGuid(), 9, "video", JsonSerializer.Serialize(new VideoEditDocument()),
                new Dictionary<string, string>
                {
                    ["source"] = sourcePath, ["sourceHash"] = EditDraftStore.HashFile(sourcePath),
                    ["recording"] = JsonSerializer.Serialize(new RecordingResult(sourcePath, 300, 10, 3, 640, 360)),
                }, [], DateTimeOffset.UtcNow);
        }
        public void Dispose() => _services.Dispose(); // Retain isolated files; no cleanup or real app startup.
    }
}
