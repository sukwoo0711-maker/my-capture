using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Settings;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Storage;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Shell;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The optional pause hotkey: it registers and routes like the other optional commands,
/// toggles a running recording through exactly the control window's pause-button path, and
/// does nothing at all while no recording is running.
/// </summary>
public sealed class RecordingPauseHotkeyTests : KoreanCaptionTest
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Hotkey CtrlAltP = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50);

    [Fact]
    public void PauseHotkey_IsOptionalRegistersRoutesAndClonesLikeTheOtherOptionalCommands() => StaTestHost.Run(() =>
    {
        Assert.Contains(GlobalHotkeyCommand.PauseRecording, Enum.GetValues<GlobalHotkeyCommand>());
        var registrar = new FakeRegistrar();
        using var window = new NativeMessageWindow();
        using var service = new GlobalHotkeyService(window, registrar, NullLogger<GlobalHotkeyService>.Instance);
        var pressed = new List<GlobalHotkeyCommand>();
        service.Pressed += (_, e) => pressed.Add(e.Command);

        // Unassigned by default: nothing is claimed from other apps.
        service.Initialize(new HotkeySettings());
        Assert.DoesNotContain(GlobalHotkeyCommand.PauseRecording, service.RegisteredCommands);
        Assert.Null(service.GetAssigned(GlobalHotkeyCommand.PauseRecording));

        var settings = new HotkeySettings { PauseRecording = CtrlAltP };
        Assert.Equal(CtrlAltP, settings.DeepCloneHotkeys().PauseRecording);
        Assert.True(service.Reconfigure(settings).Applied);
        Assert.Equal(CtrlAltP, service.GetAssigned(GlobalHotkeyCommand.PauseRecording));
        Assert.Contains(CtrlAltP, registrar.Held.Values);
        Assert.True(service.PostDiagnosticCommand(GlobalHotkeyCommand.PauseRecording));
        PumpUntil(() => pressed.Count > 0);
        Assert.Equal(GlobalHotkeyCommand.PauseRecording, Assert.Single(pressed));

        // Sharing Ctrl+Shift+X with region recording rolls the whole set back, as for every command.
        HotkeyReconfigureResult collision = service.Reconfigure(
            new HotkeySettings { PauseRecording = new HotkeySettings().RecordRegion });
        Assert.False(collision.Applied);
        Assert.Equal(GlobalHotkeyCommand.PauseRecording, Assert.Single(collision.Failures).Command);
        Assert.Equal(CtrlAltP, service.GetAssigned(GlobalHotkeyCommand.PauseRecording));

        Assert.True(service.Reconfigure(new HotkeySettings()).Applied);
        Assert.Null(service.GetAssigned(GlobalHotkeyCommand.PauseRecording));
        Assert.DoesNotContain(CtrlAltP, registrar.Held.Values);
    });

    [Fact]
    public void Coordinator_PauseHotkeyDoesNothingWithoutARunningRecording() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        string root = OwnedTestDirectory.Create("mc-recording-pause-hotkey-idle-");
        try
        {
            RegionRecordingCoordinator coordinator = NewCoordinator(fixture, root);

            // Idle: nothing to pause, and no session, selection or window is started.
            Assert.False(coordinator.TogglePause());
            Assert.False(coordinator.IsActive);

            // Controls open but the recording has not started yet: still nothing to pause.
            OpenControls(coordinator, fixture.Region);
            RecordingControlWindow controls = Field<RecordingControlWindow>(coordinator, "_controls");
            try
            {
                Assert.False(controls.IsRecording);
                Assert.False(coordinator.TogglePause());
                Assert.False(controls.IsPaused);
                Assert.False(controls.IsPauseTransitionPending);
                Assert.Equal(Visibility.Collapsed, Field<Button>(controls, "_pauseButton").Visibility);
            }
            finally
            {
                controls.Close();
            }

            Assert.False(coordinator.IsActive);
            Assert.False(coordinator.TogglePause());
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void Coordinator_PauseHotkeyTogglesThroughThePauseButtonPath() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        string root = OwnedTestDirectory.Create("mc-recording-pause-hotkey-");
        try
        {
            RegionRecordingCoordinator coordinator = NewCoordinator(fixture, root);
            OpenControls(coordinator, fixture.Region);
            RecordingControlWindow controls = Field<RecordingControlWindow>(coordinator, "_controls");
            try
            {
                Button pause = Field<Button>(controls, "_pauseButton");
                Border frame = Field<Border>(controls, "_regionFrame");
                TextBlock status = Field<TextBlock>(controls, "_statusText");
                Invoke(controls, "OnPrimaryClicked");
                PumpUntil(() => controls.CanCaptureStill);
                RegionRecorder recorder = Field<RegionRecorder>(controls, "_recorder");
                controls.ShowTime = true; // The ⋯ menu's "show time" (formerly the clock check box).
                RecordingWallClockWindow clock = Field<RecordingWallClockWindow>(controls, "_wallClock");

                // Hotkey pause: the capture stops at once, but the in-region badge waits until
                // the capture loop has parked, exactly as for the button.
                Assert.True(coordinator.TogglePause());
                Assert.True(recorder.IsPaused);
                Assert.True(controls.IsPauseTransitionPending);
                Assert.False(pause.IsEnabled);
                Assert.False(clock.IsPaused);
                // A repeated press while the pause is still applying is ignored, not queued.
                Assert.False(coordinator.TogglePause());
                PumpUntil(() => controls.IsPaused && !controls.IsPauseTransitionPending);
                Assert.True(controls.IsRecording);
                Assert.True(pause.IsEnabled);
                Assert.Equal("재개", RecordingControlWindow.ButtonLabel(pause));
                Assert.Equal("녹화 재개", AutomationProperties.GetName(pause));
                Assert.StartsWith("일시 정지됨", status.Text, StringComparison.Ordinal);
                Assert.Equal(status.Text, AutomationProperties.GetName(status));
                Assert.Equal("일시 정지됨", AutomationProperties.GetItemStatus(frame));
                Assert.IsType<LinearGradientBrush>(frame.BorderBrush);
                Assert.True(clock.IsPaused);

                TimeSpan recordedAtPause = recorder.RecordedElapsed;
                SyntheticCaptureFixture.Pump(300);
                Assert.Equal(recordedAtPause, recorder.RecordedElapsed);

                // The button resumes a hotkey pause: both drive one shared state.
                pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => !controls.IsPaused && !controls.IsPauseTransitionPending);
                Assert.False(recorder.IsPaused);
                Assert.False(clock.IsPaused);

                // And the hotkey resumes a button pause, clearing the badge before frames flow.
                pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                PumpUntil(() => controls.IsPaused && !controls.IsPauseTransitionPending);
                Assert.True(clock.IsPaused);
                Assert.True(coordinator.TogglePause());
                Assert.False(clock.IsPaused);
                Assert.True(recorder.IsPaused);
                PumpUntil(() => !controls.IsPaused && !controls.IsPauseTransitionPending);
                Assert.False(recorder.IsPaused);
                Assert.Equal("일시 정지", RecordingControlWindow.ButtonLabel(pause));
                Assert.Equal("녹화 일시 정지", AutomationProperties.GetName(pause));
                Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(frame));
                Assert.IsNotType<LinearGradientBrush>(frame.BorderBrush);
                Assert.DoesNotContain("일시 정지됨", status.Text, StringComparison.Ordinal);
            }
            finally
            {
                controls.Close();
            }

            Assert.False(coordinator.IsActive);
            Assert.False(coordinator.TogglePause());
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void ControlWindow_PauseGuidanceNamesTheShortcutOnlyWhenAssigned() => StaTestHost.Run(() =>
    {
        using var fixture = new SyntheticCaptureFixture();
        using var recorder = new RegionRecorder(
            new RegionFrameGrabber(fixture.Engine, false), _ => new DiscardingEncoder(320, 240), NullLogger.Instance);
        Hotkey assigned = Hotkey.None;
        var window = new RecordingControlWindow(
            fixture.Region,
            new RecordingSettings { UseStartDelay = false, FrameRate = RecordingFrameRate.Fps15 },
            () => recorder,
            () => "unused.mp4",
            NullLogger<RecordingControlWindow>.Instance)
        {
            ShowActivated = false,
            ReadPauseHotkey = () => assigned,
        };
        RecordingResult? finished = null;
        Exception? failure = null;
        window.RecordingFinished += (_, result) => finished = result;
        window.Failed += (_, args) => failure = args.Exception;
        try
        {
            window.Show();
            Button pause = Field<Button>(window, "_pauseButton");
            Assert.False(window.TogglePause());

            Invoke(window, "OnPrimaryClicked");
            PumpUntil(() => window.CanCaptureStill);
            const string pauseHelp = "녹화를 일시 정지합니다. 일시 정지한 구간은 동영상에 포함되지 않습니다.";
            Assert.Equal(pauseHelp, pause.ToolTip);
            Assert.Equal(pauseHelp, AutomationProperties.GetHelpText(pause));
            Assert.Equal(string.Empty, AutomationProperties.GetAcceleratorKey(pause));

            // Assigned in settings while recording: the next state change names the chord.
            assigned = CtrlAltP;
            Assert.True(window.TogglePause());
            PumpUntil(() => window.IsPaused && !window.IsPauseTransitionPending);
            const string resumeHelp = "녹화를 다시 시작합니다. 동영상은 일시 정지한 지점에서 이어집니다. 단축키: Ctrl+Alt+P";
            Assert.Equal(resumeHelp, pause.ToolTip);
            Assert.Equal(resumeHelp, AutomationProperties.GetHelpText(pause));
            Assert.Equal("Ctrl+Alt+P", AutomationProperties.GetAcceleratorKey(pause));

            // Stopping (Ctrl+Shift+X while paused) ends pause toggling for this window.
            window.RequestStop();
            Assert.False(window.TogglePause());
            PumpUntil(() => finished is not null || failure is not null);
            Assert.Null(failure);
            Assert.False(window.TogglePause());
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
    public void ShortcutHelp_ListsThePauseHotkeyNextToRegionRecording() => StaTestHost.Run(() =>
    {
        Func<HotkeySettings> previous = ShortcutHelpWindow.ReadHotkeys;
        try
        {
            foreach ((Hotkey key, string expected) in new[] { (Hotkey.None, "지정 안 됨"), (CtrlAltP, "Ctrl+Alt+P") })
            {
                ShortcutHelpWindow.ReadHotkeys = () => new HotkeySettings { PauseRecording = key };
                var help = new ShortcutHelpWindow();
                try
                {
                    TabControl tabs = ((DockPanel)help.Content).Children.OfType<TabControl>().Single();
                    TabItem capture = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, UiText.Get("Help.Capture")));
                    List<UniformGrid> rows = ((StackPanel)((ScrollViewer)capture.Content).Content).Children.OfType<UniformGrid>().ToList();
                    int record = rows.FindIndex(row => Label(row) == "영역 녹화 시작/중지");
                    Assert.True(record >= 0);
                    UniformGrid pauseRow = rows[record + 1];
                    Assert.Equal("녹화 일시 정지/재개", Label(pauseRow));
                    Assert.Equal(expected, AutomationProperties.GetName(pauseRow.Children[1]));
                }
                finally
                {
                    help.Close();
                }
            }
        }
        finally
        {
            ShortcutHelpWindow.ReadHotkeys = previous;
        }
    });

    private static string? Label(UniformGrid row) => row.Children.OfType<TextBlock>().FirstOrDefault()?.Text;

    private static RegionRecordingCoordinator NewCoordinator(SyntheticCaptureFixture fixture, string root)
    {
        AppPaths paths = AppPaths.CreateForRoot(root);
        var queue = new CaptureQueue(paths, new QueueSettings(), NullLogger<CaptureQueue>.Instance);
        queue.Load();
        var library = new VideoLibraryService(queue, paths, NullLogger<VideoLibraryService>.Instance);
        return new RegionRecordingCoordinator(
            fixture.Engine,
            paths,
            () => new RecordingSettings
            {
                UseStartDelay = false,
                FrameRate = RecordingFrameRate.Fps15,
                IncludeCursor = false,
            },
            NullLoggerFactory.Instance,
            library);
    }

    private static void OpenControls(RegionRecordingCoordinator coordinator, RectD region) =>
        typeof(RegionRecordingCoordinator).GetMethod("OpenControls", Flags)!.Invoke(coordinator, [region]);

    private static T Field<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;

    private static void Invoke(object owner, string method) =>
        owner.GetType().GetMethod(method, Flags)!.Invoke(owner, null);

    private static void PumpUntil(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            SyntheticCaptureFixture.Pump(10);
        }

        Assert.True(predicate(), "The expected recording state was not reached.");
    }

    /// <summary>Fake registration seam: never touches RegisterHotKey, rejects duplicates.</summary>
    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        private readonly Dictionary<int, Hotkey> _held = [];

        internal IReadOnlyDictionary<int, Hotkey> Held => _held;

        public bool TryRegister(int id, Hotkey hotkey, out int errorCode, out string errorMessage)
        {
            errorCode = 0;
            errorMessage = string.Empty;
            if (_held.ContainsValue(hotkey))
            {
                errorCode = 1409;
                errorMessage = "taken";
                return false;
            }

            _held[id] = hotkey;
            return true;
        }

        public void Unregister(int id) => _held.Remove(id);
    }

    private sealed class DiscardingEncoder(int width, int height) : IVideoEncoder
    {
        public int Width => width;

        public int Height => height;

        public void WriteFrame(in EncoderFrame frame)
        {
        }

        public void Complete()
        {
        }

        public void Dispose()
        {
        }
    }
}
