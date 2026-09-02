using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
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
/// Recording feature wiring that must hold regardless of the machine: the Ctrl+X
/// default, the settings graph, the derived bitrate, the encoder contract exercised
/// through a fake, and the recorder's start/stop guards.
/// </summary>
public sealed class RecordingFeatureTests
{
    [Fact]
    public void RecordRegionHotkey_DefaultsToCtrlX()
    {
        var settings = new HotkeySettings();

        Assert.True(settings.RecordRegion.IsAssigned);
        Assert.Equal(HotkeyModifiers.Control, settings.RecordRegion.Modifiers);
        Assert.Equal(Hotkey.VkX, settings.RecordRegion.VirtualKey);
        Assert.Equal("Ctrl+X", settings.RecordRegion.ToString());
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

    /// <summary>A fake encoder recording the contract, used to keep recorder tests off Media Foundation.</summary>
    private sealed class RecordingSpyEncoder : IVideoEncoder
    {
        public List<double> Timestamps { get; } = [];

        public bool Completed { get; private set; }

        public int Width => 4;

        public int Height => 4;

        public void WriteFrame(in EncoderFrame frame) => Timestamps.Add(frame.TimestampMs);

        public void Complete() => Completed = true;

        public void Dispose()
        {
        }
    }
}
