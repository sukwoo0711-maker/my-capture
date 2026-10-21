using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The recording bar: icon-and-name buttons for start, pause or resume and stop, the frame size and
/// rate in a tooltip, the time stamp in a ⋯ menu instead of a check box, and cancel only before the
/// recording starts.
/// </summary>
public sealed class RecordingControlBarTests
{
    [Fact]
    public void BarButtonsFollowTheRecordingStateAndCancelLeavesOnceRecordingStarts() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        using var firstFrame = new ManualResetEventSlim();
        var encoder = new FrameSink(firstFrame);
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), includeCursor: false),
            _ => encoder, NullLogger.Instance);
        var window = new RecordingControlWindow(
            new RectD(100, 100, 16, 16),
            new RecordingSettings { UseStartDelay = false },
            () => recorder,
            () => "unused.mp4",
            NullLogger<RecordingControlWindow>.Instance) { ShowActivated = false };
        RecordingResult? finished = null;
        Exception? failure = null;
        window.RecordingFinished += (_, result) => finished = result;
        window.Failed += (_, args) => failure = args.Exception;
        try
        {
            Button primary = window.PrimaryButtonForTest;
            Button pause = window.PauseButtonForTest;
            Button cancel = window.CancelButtonForTest;
            Button more = window.MoreButtonForTest;

            // Before recording: ● Start recording, the size and rate only in the tooltip, cancel offered.
            Assert.Equal("녹화 시작", RecordingControlWindow.ButtonLabel(primary));
            Assert.IsType<StackPanel>(primary.Content);
            Assert.Equal("녹화 시작", AutomationProperties.GetName(primary));
            Assert.Equal("녹화 영역 16×16 · 30fps", primary.ToolTip);
            Assert.Equal(Visibility.Visible, cancel.Visibility);
            Assert.Equal(Visibility.Collapsed, pause.Visibility);
            Assert.Equal("녹화 옵션", AutomationProperties.GetName(more));
            TextBlock status = Field<TextBlock>(window, "_statusText");
            Assert.Equal(string.Empty, status.Text);
            Assert.DoesNotContain(Descendants(window), node => node is CheckBox);

            // The ⋯ menu holds the time stamp as one checked entry.
            ContextMenu menu = window.CreateMoreMenu();
            MenuItem time = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
            Assert.Equal("시각 표시", time.Header);
            Assert.True(time.IsCheckable);
            Assert.False(time.IsChecked);

            window.Show();
            window.UpdateLayout();
            primary.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(firstFrame.Wait(TimeSpan.FromSeconds(3)), "recording did not start");
            Assert.True(window.IsRecording);

            // While recording: ■ Stop and ❚❚ Pause; cancel would only have stopped, so it is gone.
            Assert.IsType<MyCapture.App.Themes.SymbolIcon>(SurfaceShortcutHintTests.PrimaryContent(primary));
            Assert.Equal(string.Empty, RecordingControlWindow.ButtonLabel(primary));
            Assert.Equal("녹화 중지", AutomationProperties.GetName(primary));
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
            Assert.Equal(Visibility.Visible, pause.Visibility);
            Assert.IsType<MyCapture.App.Themes.SymbolIcon>(SurfaceShortcutHintTests.PrimaryContent(pause));
            Assert.Equal("녹화 일시 정지", AutomationProperties.GetName(pause));

            ContextMenu during = window.CreateMoreMenu();
            ((MenuItem)during.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(window.ShowTime);
            Assert.NotNull(Field<RecordingWallClockWindow?>(window, "_wallClock"));
            Assert.NotNull(((MenuItem)window.CreateMoreMenu().Items[0]).Icon);

            pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpUntil(() => window.IsPaused && !window.IsPauseTransitionPending);
            Assert.IsType<MyCapture.App.Themes.SymbolIcon>(SurfaceShortcutHintTests.PrimaryContent(pause));
            Assert.Equal("녹화 재개", AutomationProperties.GetName(pause));
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);

            window.RequestStop();
            PumpUntil(() => finished is not null || failure is not null);
            Assert.Null(failure);
            Assert.Equal(Visibility.Collapsed, cancel.Visibility);
            Assert.False(more.IsEnabled);
            Assert.False(((MenuItem)window.CreateMoreMenu().Items[0]).IsEnabled);
        }
        finally
        {
            if (window.IsRecording)
            {
                window.RequestStop();
                PumpUntil(() => finished is not null || failure is not null);
            }

            window.CompleteAndClose();
        }
    });

    [Fact]
    public void CancelBeforeRecordingEndsTheSessionWithoutAClip() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        var window = new RecordingControlWindow(
            new RectD(100, 100, 640, 360),
            new RecordingSettings { UseStartDelay = true, StartDelaySeconds = 3 },
            () => throw new InvalidOperationException("Recorder must not start in this test."),
            () => "unused.mp4",
            NullLogger<RecordingControlWindow>.Instance);
        int cancelled = 0;
        window.Cancelled += (_, _) => cancelled++;
        Assert.Equal("Record after delay", RecordingControlWindow.ButtonLabel(window.PrimaryButtonForTest));
        Assert.Equal("Recording area 640×360 · 30fps", window.PrimaryButtonForTest.ToolTip);
        Assert.Equal("Show time", ((MenuItem)window.CreateMoreMenu().Items[0]).Header);
        window.CancelButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(1, cancelled);
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject descendant in Descendants(node)) yield return descendant;
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;

    private static void PumpUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(predicate(), "The recording bar did not reach the expected state.");
    }

    private sealed class FrameSink(ManualResetEventSlim received) : IVideoEncoder
    {
        public int Width => 16;
        public int Height => 16;
        public void WriteFrame(in EncoderFrame frame) => received.Set();
        public void Complete() { }
        public void Dispose() { }
    }
}
