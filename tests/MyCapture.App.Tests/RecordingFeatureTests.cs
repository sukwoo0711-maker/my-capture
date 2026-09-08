using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Recording feature wiring that must hold regardless of the machine: the Ctrl+Shift+X
/// default, the settings graph, the derived bitrate, the encoder contract exercised
/// through a fake, and the recorder's start/stop guards.
/// </summary>
public sealed class RecordingFeatureTests : KoreanCaptionTest
{
    [Fact]
    public void RecordRegionHotkey_DefaultsToCtrlShiftX()
    {
        var settings = new HotkeySettings();

        Assert.True(settings.RecordRegion.IsAssigned);
        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            settings.RecordRegion.Modifiers);
        Assert.Equal(Hotkey.VkX, settings.RecordRegion.VirtualKey);
        Assert.Equal("Ctrl+Shift+X", settings.RecordRegion.ToString());
    }

    [Fact]
    public void OpenLibraryHotkey_DefaultsToCtrlShiftZ()
    {
        var settings = new HotkeySettings();

        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            settings.OpenLibrary.Modifiers);
        Assert.Equal(Hotkey.VkZ, settings.OpenLibrary.VirtualKey);
        Assert.Equal("Ctrl+Shift+Z", settings.OpenLibrary.ToString());
        Assert.Contains(GlobalHotkeyCommand.OpenLibrary, Enum.GetValues<GlobalHotkeyCommand>());
    }

    [Fact]
    public void PrimaryWorkflowHotkeys_ShareCtrlShiftModifierFamily()
    {
        var settings = new HotkeySettings();
        const HotkeyModifiers primaryModifiers =
            HotkeyModifiers.Control | HotkeyModifiers.Shift;

        Assert.Equal(primaryModifiers, settings.Capture.Modifiers);
        Assert.Equal(primaryModifiers, settings.RecordRegion.Modifiers);
        Assert.Equal(primaryModifiers, settings.OpenLibrary.Modifiers);
        Assert.Equal([Hotkey.VkC, Hotkey.VkX, Hotkey.VkZ],
            new[]
            {
                settings.Capture.VirtualKey,
                settings.RecordRegion.VirtualKey,
                settings.OpenLibrary.VirtualKey,
            });
    }

    [Fact]
    public void RecordRegion_IsDistinctFromCaptureRegion()
    {
        var settings = new HotkeySettings();

        // Recording must never collide with still capture out of the box.
        Assert.NotEqual(settings.Capture.VirtualKey, settings.RecordRegion.VirtualKey);
    }

    [Fact]
    public void GlobalHotkeyCommand_IncludesRecordRegion()
    {
        Assert.Contains(GlobalHotkeyCommand.RecordRegion, Enum.GetValues<GlobalHotkeyCommand>());
    }

    [Fact]
    public void AppSettings_ExposesRecordingSection()
    {
        var settings = new AppSettings();

        Assert.NotNull(settings.Recording);
        Assert.Equal(RecordingFrameRate.Fps30, settings.Recording.FrameRate);
        Assert.Equal(30, settings.Recording.TargetFps);
        Assert.False(settings.Recording.UseStartDelay);
        Assert.True(settings.Recording.IncludeCursor);
    }

    [Fact]
    public void RecordingFrameRate_OffersSixtyFpsForMotionHeavyCapture()
    {
        var settings = new RecordingSettings { FrameRate = RecordingFrameRate.Fps60 };

        Assert.Equal(60, settings.TargetFps);
    }

    [Fact]
    public void RecordingResult_ReportsAdaptiveFrameDropMetrics()
    {
        var result = new RecordingResult("capture.mp4", 2_000, 30, 45, 1280, 720);

        Assert.Equal(60, result.ExpectedFrames);
        Assert.Equal(15, result.DroppedFrames);
        Assert.Equal(22.5, result.EffectiveFps, 3);
        Assert.Equal(0.25, result.DropRate, 3);
    }

    [Fact]
    public void RecordingResult_DropMetricsNeverReportNegativeValues()
    {
        // A timestamp-zero frame can make a very short recording contain more frames
        // than a duration-only estimate. It is not a drop and must never look like one.
        var result = new RecordingResult("capture.mp4", 1, 30, 1, 16, 16);

        Assert.Equal(1, result.ExpectedFrames);
        Assert.Equal(0, result.DroppedFrames);
        Assert.Equal(0d, result.DropRate);
    }

    [Theory]
    [InlineData(1920, 1080, 30)]
    [InlineData(320, 240, 15)]
    [InlineData(3840, 2160, 60)]
    public void DeriveBitrate_StaysInsideClampBand(int w, int h, int fps)
    {
        int bitrate = VideoEncoderOptions.DeriveBitrate(w, h, fps);

        Assert.InRange(bitrate, 1_000_000, 24_000_000);
    }

    [Fact]
    public void DeriveBitrate_LargerRegionGetsMoreBits()
    {
        int small = VideoEncoderOptions.DeriveBitrate(640, 360, 15);
        int large = VideoEncoderOptions.DeriveBitrate(1920, 1080, 15);

        Assert.True(large >= small);
    }

    [Fact]
    public void Recorder_StopWithoutStart_Throws()
    {
        var grabber = new RegionFrameGrabber(
            new MyCapture.Platform.Capture.ScreenCaptureEngine(NullLogger<MyCapture.Platform.Capture.ScreenCaptureEngine>.Instance),
            includeCursor: false);
        var recorder = new RegionRecorder(
            grabber,
            _ => new RecordingSpyEncoder(),
            NullLogger.Instance);

        Assert.Throws<InvalidOperationException>(() => recorder.Stop());
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void Recorder_DisposeWithoutStart_IsSafe()
    {
        var grabber = new RegionFrameGrabber(
            new MyCapture.Platform.Capture.ScreenCaptureEngine(NullLogger<MyCapture.Platform.Capture.ScreenCaptureEngine>.Instance),
            includeCursor: false);
        var recorder = new RegionRecorder(grabber, _ => new RecordingSpyEncoder(), NullLogger.Instance);

        recorder.Dispose(); // must not throw
    }

    [Fact]
    public void SpyEncoder_HonoursWriteThenCompleteContract()
    {
        var encoder = new RecordingSpyEncoder();
        var options = new VideoEncoderOptions("out.mp4", 4, 4, 15, 1_000_000);
        _ = options;

        encoder.WriteFrame(new EncoderFrame(new byte[4 * 4 * 4], 4, 4, 16, 0));
        encoder.WriteFrame(new EncoderFrame(new byte[4 * 4 * 4], 4, 4, 16, 66.6));
        encoder.Complete();

        Assert.Equal(2, encoder.Timestamps.Count);
        Assert.True(encoder.Completed);
    }

    [Fact]
    public void FrameImageCommitSession_DisposeReleasesRetentionLeaseExactlyOnce()
    {
        int releases = 0;
        var session = new FrameImageCommitSession(
            _ => Task.FromResult(true),
            () => Interlocked.Increment(ref releases));

        session.Dispose();
        session.Dispose();

        Assert.Equal(1, releases);
    }

    [Fact]
    public void RecordingRegionFrame_ExposesAutomationPeerAndKeyboardAlternative()
    {
        StaTestHost.Run(() =>
        {
            var window = new RecordingControlWindow(
                new RectD(100, 100, 640, 360),
                new RecordingSettings(),
                () => throw new InvalidOperationException("Recorder must not start in this test."),
                () => "unused.mp4",
                NullLogger<RecordingControlWindow>.Instance);
            try
            {
                FieldInfo frameField = typeof(RecordingControlWindow).GetField(
                    "_regionFrame",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Border frame = Assert.IsAssignableFrom<Border>(frameField.GetValue(window));

                Assert.True(frame.Focusable);
                Assert.Equal(
                    "녹화 영역 테두리 (드래그로 이동)",
                    AutomationProperties.GetName(frame));
                Assert.Contains("방향키", AutomationProperties.GetHelpText(frame), StringComparison.Ordinal);

                AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(frame);
                Assert.NotNull(peer);
                Assert.Equal("녹화 영역 테두리 (드래그로 이동)", peer.GetName());
                Assert.Equal(AutomationControlType.Pane, peer.GetAutomationControlType());
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FinalizingRecording_RejectsPrimaryAndKeyboardRestart(bool stopping, bool completionPending)
    {
        StaTestHost.Run(() =>
        {
            int starts = 0;
            var window = new RecordingControlWindow(
                new RectD(100, 100, 640, 360),
                new RecordingSettings { UseStartDelay = false },
                () => { starts++; throw new InvalidOperationException("Must not restart while finalizing."); },
                () => "unused.mp4",
                NullLogger<RecordingControlWindow>.Instance);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            typeof(RecordingControlWindow).GetField("_stopping", flags)!.SetValue(window, stopping);
            typeof(RecordingControlWindow).GetField("_completionPending", flags)!.SetValue(window, completionPending);
            typeof(RecordingControlWindow).GetField("_finished", flags)!.SetValue(window, true);
            using var source = new HwndSource(new HwndSourceParameters("Recording stop guard test")
            {
                Width = 1, Height = 1, PositionX = -4000, PositionY = -4000, WindowStyle = 0,
            });
            try
            {
                typeof(RecordingControlWindow).GetMethod("OnPrimaryClicked", flags)!.Invoke(window, null);
                foreach (Key key in new[] { Key.Enter, Key.Space })
                {
                    var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
                    typeof(RecordingControlWindow).GetMethod("OnKeyDown", flags)!.Invoke(window, [window, args]);
                    Assert.True(args.Handled);
                }
                Assert.Equal(0, starts);
            }
            finally
            {
                window.CompleteAndClose();
            }
        });
    }

    [Fact]
    public void RecordingFrame_RemainsVisibleDuringCaptureAndCompletionClosesCleanly()
    {
        StaTestHost.Run(() =>
        {
            using var firstFrame = new ManualResetEventSlim();
            var encoder = new RecordingSpyEncoder { FrameWritten = () => firstFrame.Set() };
            var grabber = new RegionFrameGrabber(
                new MyCapture.Platform.Capture.ScreenCaptureEngine(NullLogger<MyCapture.Platform.Capture.ScreenCaptureEngine>.Instance),
                includeCursor: false);
            using var recorder = new RegionRecorder(grabber, _ => encoder, NullLogger.Instance);
            var window = new RecordingControlWindow(
                new RectD(100, 100, 16, 16),
                new RecordingSettings { UseStartDelay = false },
                () => recorder,
                () => "unused.mp4",
                NullLogger<RecordingControlWindow>.Instance) { ShowActivated = false };
            bool closed = false;
            bool finished = false;
            Exception? failure = null;
            window.Closed += (_, _) => closed = true;
            window.RecordingFinished += (_, _) => finished = true;
            window.Failed += (_, args) => failure = args.Exception;
            try
            {
                window.Show();
                window.UpdateLayout();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                typeof(RecordingControlWindow).GetMethod("OnPrimaryClicked", flags)!.Invoke(window, null);
                Assert.True(firstFrame.Wait(TimeSpan.FromSeconds(3)), "recording did not emit a real captured frame");
                Assert.True(window.IsRecording);
                Border frame = (Border)typeof(RecordingControlWindow).GetField("_regionFrame", flags)!.GetValue(window)!;
                Assert.Equal(Visibility.Visible, frame.Visibility);
                Assert.True(frame.IsVisible);
                Assert.False(frame.IsHitTestVisible);
                Assert.Null(frame.Background);

                window.RequestStop();
                var dispatcherFrame = new DispatcherFrame();
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) =>
                {
                    if (finished || failure is not null || DateTime.UtcNow >= deadline)
                    {
                        timer.Stop();
                        dispatcherFrame.Continue = false;
                    }
                };
                timer.Start();
                Dispatcher.PushFrame(dispatcherFrame);
                Assert.Null(failure);
                Assert.True(finished, "recording completion timed out");
                Assert.True(encoder.Completed);
                Assert.False(window.IsRecording);
                Assert.False(closed, "saving status must remain visible until the coordinator completes");
                window.CompleteAndClose();
                Assert.True(closed);
            }
            finally
            {
                window.CompleteAndClose();
            }
        });
    }

    /// <summary>A fake encoder recording the contract, used to keep recorder tests off Media Foundation.</summary>
    private sealed class RecordingSpyEncoder : IVideoEncoder
    {
        public List<double> Timestamps { get; } = [];

        public bool Completed { get; private set; }
        internal Action? FrameWritten { get; init; }

        public int Width => 4;

        public int Height => 4;

        public void WriteFrame(in EncoderFrame frame)
        {
            Timestamps.Add(frame.TimestampMs);
            FrameWritten?.Invoke();
        }

        public void Complete() => Completed = true;

        public void Dispose()
        {
        }
    }
}
