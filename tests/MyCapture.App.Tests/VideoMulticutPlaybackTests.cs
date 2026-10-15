using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Core.Storage;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class VideoMulticutPlaybackTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static VideoCutTimeline Timeline() => VideoCutTimeline.Create(0, 10000,
        [new(2000, 4000), new(6000, 8000), new(9000, 10000)]);

    [Fact]
    public void RemovedRangeIssuesOneSeekWhileStaleDecoderPositionsWait()
    {
        var playback = new PreviewCutPlayback();
        VideoCutDecisionCheck(playback.Observe(Timeline(), 1999, TimeSpan.Zero), PreviewCutAction.Present, 1999);
        VideoCutDecisionCheck(playback.Observe(Timeline(), 2050, TimeSpan.Zero), PreviewCutAction.Seek, 4000);
        for (int tick = 1; tick < 30; tick++)
        {
            VideoCutDecisionCheck(playback.Observe(Timeline(), 2050, TimeSpan.FromMilliseconds(tick * 20)), PreviewCutAction.Wait, 4000);
        }
        VideoCutDecisionCheck(playback.Observe(Timeline(), 4000, TimeSpan.FromMilliseconds(600)), PreviewCutAction.Present, 4000);
        VideoCutDecisionCheck(playback.Observe(Timeline(), 6050, TimeSpan.FromMilliseconds(800)), PreviewCutAction.Seek, 8000);
    }

    [Fact]
    public void UnobservedLandingTimesOutInsteadOfSeekingForever()
    {
        var playback = new PreviewCutPlayback();
        playback.Observe(Timeline(), 2050, TimeSpan.Zero);
        Assert.Equal(PreviewCutAction.Wait, playback.Observe(Timeline(), 2050, TimeSpan.FromMilliseconds(999)).Action);
        Assert.Equal(PreviewCutAction.TimedOut, playback.Observe(Timeline(), 2050, PreviewCutPlayback.SettleTimeout).Action);
        Assert.Equal(PreviewCutAction.TimedOut, playback.Observe(Timeline(), 2050, TimeSpan.FromSeconds(2)).Action);
        playback.Reset();
        Assert.Equal(PreviewCutAction.Seek, playback.Observe(Timeline(), 2050, TimeSpan.FromSeconds(3)).Action);
    }

    [Fact]
    public void DecoderToleranceAndLateRegressionCannotReissueTheSameCut()
    {
        var playback = new PreviewCutPlayback();
        playback.Observe(Timeline(), 2000, TimeSpan.Zero);
        VideoCutDecisionCheck(playback.Observe(Timeline(), 3999.995, TimeSpan.FromMilliseconds(20)), PreviewCutAction.Present, 4000);
        Assert.Equal(PreviewCutAction.Wait, playback.Observe(Timeline(), 2000, TimeSpan.FromMilliseconds(40)).Action);
        Assert.Equal(PreviewCutAction.Present, playback.Observe(Timeline(), 4050, TimeSpan.FromMilliseconds(60)).Action);
    }

    [Fact]
    public void TrailingCutStopsAtTheLastKeptEnd()
    {
        var playback = new PreviewCutPlayback();
        VideoCutDecisionCheck(playback.Observe(Timeline(), 9100, TimeSpan.Zero), PreviewCutAction.Stop, 9000);
    }

    [Fact]
    public void StoppingSeeksAFrameInsideTheLastKeptSegment()
    {
        Assert.Equal(8900, PreviewCutPlayback.LastKeptFrame(Timeline(), 10), 6);
        VideoCutTimeline fractional = VideoCutTimeline.Create(0, 10000, [new(9005, 10000)]);
        Assert.Equal(9000, PreviewCutPlayback.LastKeptFrame(fractional, 10), 6);
        VideoCutTimeline shortLast = VideoCutTimeline.Create(0, 1000, [new(0, 899)]);
        Assert.InRange(PreviewCutPlayback.LastKeptFrame(shortLast, 1), 899, 999.999);
    }

    [Fact]
    public void LeadingCutAndNoCutTrimUseTheCorrectSourceStart()
    {
        var playback = new PreviewCutPlayback();
        VideoCutTimeline leading = VideoCutTimeline.Create(1000, 10000, [new(1000, 3000)]);
        VideoCutDecisionCheck(playback.Observe(leading, 1000, TimeSpan.Zero), PreviewCutAction.Seek, 3000);
        playback.Reset();
        VideoCutDecisionCheck(playback.Observe(VideoCutTimeline.Create(1000, 10000), 2000, TimeSpan.Zero), PreviewCutAction.Present, 2000);
    }

    [Fact]
    public void EditorCutSkipResumesOnlyAfterTheCoordinatorFinishesItsExactSeek() => RunWithEditor((editor, engine, playCalls) =>
    {
        Task skip = BeginCutSkip(editor);
        PendingSeek seek = Await(engine.NextSeekAsync());
        Assert.Equal(PreviewSeekMode.Exact, seek.Request.Mode);
        Assert.Equal(4000, seek.Request.TargetPositionMs);
        Assert.True(editor.PreviewSeekCoordinatorForTest.IsInFlightForTest);
        Assert.True(Field<bool>(editor, "_cutSeekInFlight"));
        Assert.Equal(0, playCalls());

        seek.Complete();
        Await(skip);
        Await(editor.PreviewSeekCoordinatorForTest.WaitForIdleForTestAsync());

        Assert.Equal(1, playCalls());
        Assert.True(Field<bool>(editor, "_isPlaying"));
        Assert.False(Field<bool>(editor, "_cutSeekInFlight"));
        Assert.Equal(4000, editor.PreviewSeekCoordinatorForTest.PresentedPositionMs);
        Assert.Equal(1, engine.CallCount);
    });

    [Fact]
    public void PauseDuringDelayedCutSkipPreventsItsLateCompletionFromRestartingPlayback() => RunWithEditor((editor, engine, playCalls) =>
    {
        Task skip = BeginCutSkip(editor);
        PendingSeek seek = Await(engine.NextSeekAsync());
        Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
        Assert.False(Field<bool>(editor, "_isPlaying"));
        Assert.False(editor.PlayButtonShowsPauseForTest);

        seek.Complete();
        Await(skip);
        Await(editor.PreviewSeekCoordinatorForTest.WaitForIdleForTestAsync());

        Assert.Equal(0, playCalls());
        Assert.False(Field<bool>(editor, "_isPlaying"));
        Assert.False(Field<bool>(editor, "_playRequested"));
        Assert.False(Field<bool>(editor, "_cutSeekInFlight"));
        Assert.False(Field<DispatcherTimer>(editor, "_playbackTimer").IsEnabled);
        Assert.Equal(1, engine.CallCount);
    });

    [Fact]
    public void ScrubReleaseDuringDelayedCutSkipSupersedesItsLandingWithoutAnEarlyResume() => RunWithEditor((editor, engine, playCalls) =>
    {
        Task skip = BeginCutSkip(editor);
        PendingSeek cutSeek = Await(engine.NextSeekAsync());
        editor.TimelineForTest.SetPlayhead(8500);
        Invoke(editor, "OnTimelinePlayhead", null, 8500d);
        Invoke(editor, "OnTimelinePlayheadInteractionCompleted", null, 8500d);
        Assert.True(Field<bool>(editor, "_playRequested"));
        Assert.True(editor.PreviewSeekCoordinatorForTest.HasPendingForTest);
        Assert.Equal(1, engine.CallCount);

        cutSeek.Complete();
        PendingSeek scrubSeek = Await(engine.NextSeekAsync());
        Await(skip);

        Assert.Equal(PreviewSeekMode.Exact, scrubSeek.Request.Mode);
        Assert.Equal(8500, scrubSeek.Request.TargetPositionMs);
        Assert.Equal(1, editor.PreviewSeekCoordinatorForTest.StaleResultCountForTest);
        Assert.Equal(0, playCalls());
        Assert.False(Field<bool>(editor, "_isPlaying"));
        Assert.True(Field<bool>(editor, "_playRequested"));
        Assert.False(Field<bool>(editor, "_cutSeekInFlight"));
        Assert.Equal(8500, editor.TimelineForTest.PlayheadMs);

        // An explicit pause also wins over the scrub's queued resume. This window is never
        // shown; normal cut-skip resumption is exercised separately above.
        Assert.True(editor.HandleShortcut(Key.Space, ModifierKeys.None));
        scrubSeek.Complete();
        Await(editor.PreviewSeekCoordinatorForTest.WaitForIdleForTestAsync());
        DrainDispatcher();
        Assert.Equal(8500, editor.PreviewSeekCoordinatorForTest.PresentedPositionMs);
        Assert.Equal(0, playCalls());
        Assert.False(Field<bool>(editor, "_playRequested"));
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(1, engine.MaxActive);
    });

    private static void RunWithEditor(Action<VideoEditorWindow, ControlledPreviewEngine, Func<int>> action) => StaTestHost.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var engine = new ControlledPreviewEngine();
        VideoEditorWindow? editor = null;
        try
        {
            // No application, visible window, or media file is needed for the production
            // editor/coordinator race; only the decoder boundary is controlled by the test.
            string root = Path.Combine(AppContext.BaseDirectory, "multicut-playback-in-memory");
            var recording = new RecordingResult(Path.Combine(root, "missing.mp4"), 10000, 30, 300, 640, 360);
            VideoEditDocument document = VideoEditDocument.CreateFor(640, 360, 10000);
            document.RemovedRanges.Add(new(2000, 4000));
            int playCalls = 0;
            editor = new VideoEditorWindow(recording, AppPaths.CreateForRoot(root), NullLoggerFactory.Instance,
                document, previewEngine: engine, playMedia: () => playCalls++);
            Invoke(editor, "CompleteOpen", 10000d, true);
            action(editor, engine, () => playCalls);
        }
        finally
        {
            editor?.Close();
            if (editor is not null) Await(editor.PreviewSeekCoordinatorForTest.DisposeAsync().AsTask());
            DrainDispatcher();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    });

    private static Task BeginCutSkip(VideoEditorWindow editor)
    {
        typeof(VideoEditorWindow).GetField("_isPlaying", Private)!.SetValue(editor, true);
        Invoke(editor, "UpdatePlayButton");
        return (Task)Invoke(editor, "SkipRemovedRange", 4000d)!;
    }

    private static void Await(Task task)
    {
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) => frame.Continue = false;
        _ = task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false)),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timeout.Stop();
        Assert.True(task.IsCompleted, "The controlled seek did not finish within ten seconds.");
        task.GetAwaiter().GetResult();
    }

    private static T Await<T>(Task<T> task)
    {
        Await((Task)task);
        return task.GetAwaiter().GetResult();
    }

    private static void DrainDispatcher() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, arguments);

    private sealed class PendingSeek(PreviewSeekRequest request)
    {
        internal PreviewSeekRequest Request { get; } = request;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete() => Completion.TrySetResult();
    }

    private sealed class ControlledPreviewEngine : IVideoPreviewEngine
    {
        private readonly ConcurrentQueue<PendingSeek> _seeks = new();
        private readonly SemaphoreSlim _available = new(0);
        private int _callCount;
        private int _active;
        private int _maxActive;
        internal int CallCount => Volatile.Read(ref _callCount);
        internal int MaxActive => Volatile.Read(ref _maxActive);

        public async ValueTask<PresentedPreviewFrame> SeekAsync(PreviewSeekRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            int active = Interlocked.Increment(ref _active);
            Interlocked.Exchange(ref _maxActive, Math.Max(active, MaxActive));
            var seek = new PendingSeek(request);
            _seeks.Enqueue(seek);
            _available.Release();
            try
            {
                await seek.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new PresentedPreviewFrame(request.Generation, request.TargetPositionMs, request.TargetPositionMs,
                    request.TargetFrameIndex, request.Mode);
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        internal async Task<PendingSeek> NextSeekAsync()
        {
            Assert.True(await _available.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false), "No decoder seek was issued.");
            Assert.True(_seeks.TryDequeue(out PendingSeek? seek));
            return seek!;
        }

        public void Dispose() => _available.Dispose();
    }

    private static void VideoCutDecisionCheck(PreviewCutDecision decision, PreviewCutAction action, double sourceMs)
    {
        Assert.Equal(action, decision.Action);
        Assert.Equal(sourceMs, decision.SourceTimeMs);
    }
}
