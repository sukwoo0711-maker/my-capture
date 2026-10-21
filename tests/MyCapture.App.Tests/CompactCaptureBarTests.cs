using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Capture;
using MyCapture.App.Recording;
using MyCapture.App.Themes;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Core.Settings;
using MyCapture.Core.Themes;
using MyCapture.Platform.Capture;
using MyCapture.Platform.Recording;
using Xunit;
using static MyCapture.App.Tests.CaptureCueEvidenceTests;

namespace MyCapture.App.Tests;

/// <summary>Offscreen presentation and routed commands: no live capture, input injection or persisted settings.</summary>
public sealed class CompactCaptureBarTests
{
    [Theory]
    [InlineData("ko-KR", "일시 정지됨")]
    [InlineData("en-US", "Paused")]
    public void RecordingPillKeepsAccessibleIconCommandsAndCompactPausedTime(string culture, string pausedLabel) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        bool? motion = FluidMotion.AnimationsEnabledOverrideForTest;
        FluidMotion.AnimationsEnabledOverrideForTest = false;
        var window = new RecordingControlWindow(new RectD(100, 100, 640, 360), new RecordingSettings(),
            () => throw new InvalidOperationException("No recorder in this presentation test."), () => "unused.mp4", NullLogger.Instance)
        {
            ReadPauseHotkey = () => new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x50),
        };
        try
        {
            Assert.Equal(UiText.Get("Text_255C28FD0AC2"), AutomationProperties.GetName(window.PrimaryButtonForTest));
            Assert.Equal(UiText.Get("Text_E1C96EF1EA0F"), AutomationProperties.GetName(window.CancelButtonForTest));
            Assert.Equal("Esc", AutomationProperties.GetAcceleratorKey(window.CancelButtonForTest));
            window.PreviewRecordingForTest(false, TimeSpan.FromSeconds(83));
            Border strip = Field<Border>(window, "_controlStrip");
            TextBlock status = Field<TextBlock>(window, "_statusText");
            TextBlock clock = Field<TextBlock>(window, "_timerText");
            SymbolIcon indicator = Field<SymbolIcon>(window, "_recordingIndicator");
            Arrange(strip);
            var pauseCaption = new TextBlock
            {
                Text = "Ctrl+Shift+P", FontSize = ShortcutPresentation.CaptionFontSize,
                FontFamily = window.PauseButtonForTest.FontFamily, FontWeight = FontWeights.Normal,
            };
            pauseCaption.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double pauseWidth = Math.Max(36, Math.Ceiling(pauseCaption.DesiredSize.Width) + 6);
            Assert.Equal(pauseWidth, window.PauseButtonForTest.ActualWidth);
            // The compact pill keeps its clock room plus three 36-DIP targets; only the
            // assigned pause chord needs additional horizontal room, reserved before Alt.
            Assert.Equal(216 + pauseWidth - 36, strip.ActualWidth);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            Assert.Equal(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(clock));
            Assert.Equal("01:23", clock.Text);
            Geometry recordGlyph = Assert.IsAssignableFrom<Geometry>(indicator.Data);
            Assert.False(indicator.HasAnimatedProperties);
            foreach (Button button in new[] { window.PauseButtonForTest, window.PrimaryButtonForTest, window.MoreButtonForTest })
            {
                Assert.IsType<SymbolIcon>(SurfaceShortcutHintTests.PrimaryContent(button));
                Assert.Equal(ReferenceEquals(button, window.PauseButtonForTest) ? pauseWidth : 36, button.ActualWidth);
                Assert.Equal(36, button.ActualHeight);
                Rect bounds = button.TransformToAncestor(strip).TransformBounds(new Rect(button.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= strip.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= strip.ActualHeight);
                var peer = new ButtonAutomationPeer(button);
                Assert.False(string.IsNullOrWhiteSpace(peer.GetName()));
                Assert.False(string.IsNullOrWhiteSpace(peer.GetHelpText()));
                Assert.True(button.Focusable && button.IsTabStop);
            }
            Assert.Contains("Esc", (string)window.PrimaryButtonForTest.ToolTip);
            Assert.Equal(UiText.Get("Text_74C6AEBB2B6D"), AutomationProperties.GetName(window.PrimaryButtonForTest));
            Assert.Equal(UiText.Get("Onboard_RecordMore"), AutomationProperties.GetName(window.MoreButtonForTest));
            Assert.Equal(UiText.Get("RecordingPause_PauseName"), AutomationProperties.GetName(window.PauseButtonForTest));
            Assert.Equal("Esc", AutomationProperties.GetAcceleratorKey(window.PrimaryButtonForTest));
            Assert.Contains("Esc", AutomationProperties.GetHelpText(window.PrimaryButtonForTest));
            Assert.Contains("Ctrl+Shift+P", (string)window.PauseButtonForTest.ToolTip);
            Assert.Contains("Ctrl+Shift+P", AutomationProperties.GetHelpText(window.PauseButtonForTest));
            AutomationPeer clockPeer = UIElementAutomationPeer.CreatePeerForElement(clock);
            Assert.Contains("01:23", clockPeer.GetName());
            Assert.Contains(status.Text, clockPeer.GetName());

            window.PreviewRecordingForTest(true, TimeSpan.FromSeconds(83));
            Arrange(strip);
            Assert.Equal(236 + pauseWidth - 36, strip.ActualWidth);
            Assert.Equal(pausedLabel, status.Text);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Equal(Grid.GetColumn(clock), Grid.GetColumn(status));
            Assert.NotSame(recordGlyph, indicator.Data);
            Assert.False(indicator.HasAnimatedProperties);
            Assert.Contains(pausedLabel, clockPeer.GetName());
            Assert.Contains("Esc", clockPeer.GetHelpText());
            Assert.Equal(UiText.Get("RecordingPause_ResumeName"), new ButtonAutomationPeer(window.PauseButtonForTest).GetName());
            Assert.Equal("Ctrl+Shift+P", AutomationProperties.GetAcceleratorKey(window.PauseButtonForTest));

            window.ShowCompletionStatus(UiText.Get("Text_542965B456C7"));
            Arrange(strip);
            Assert.True(double.IsNaN(window.PrimaryButtonForTest.Width));
            Assert.True(window.PrimaryButtonForTest.ActualWidth >= 108);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);
            Assert.True(strip.ActualWidth > 400);
        }
        finally
        {
            window.CompleteAndClose();
            FluidMotion.AnimationsEnabledOverrideForTest = motion;
        }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void ModesExposeOnlyAvailableCommandsWithinCompactWidthAndUseNeutralSelection(string culture) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        foreach (AppTheme theme in Enum.GetValues<AppTheme>())
        {
            var bar = new CaptureModeBar();
            bar.ApplyPalette(CaptureGuidePalette.Create(theme, null, false));
            bar.Configure(true, true, true, otherActions: true);
            bar.ShowMode(CaptureSelectorMode.Capture);
            bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bar.Arrange(new Rect(bar.DesiredSize));
            // Twelve targets now reserve 36 DIPs each so the Alt caption has two lines;
            // the complete bar still fits within 500 DIPs including separators and chrome.
            Assert.InRange(bar.DesiredSize.Width, 300, 500);
            foreach ((ButtonBase command, string nameKey) in new (ButtonBase, string)[]
                     {
                         (bar.CaptureKindButton, "Onboard_BarCapture"), (bar.RecordKindButton, "Onboard_BarRecord"),
                         (bar.CaptureButton, "CaptureGuide2_Region"), (bar.WindowButton, "CaptureBar_Window"),
                         (bar.FullScreenButton, "CaptureGuide2_FullScreen"), (bar.ScrollButton, "Text_4DBA30197C0A"),
                         (bar.DelayButton, "CaptureBar_Delay"), (bar.TextButton, "CaptureGuide2_Text"),
                         (bar.ColorButton, "CaptureGuide2_Color"), (bar.MoreButton, "Onboard_BarMore"),
                         (bar.HelpButton, "CaptureGuide2_Help"), (bar.CancelButton, "CaptureGuide2_Cancel"),
                     })
            {
                Assert.Equal(Visibility.Visible, command.Visibility);
                Assert.Equal(UiText.Get(nameKey), AutomationProperties.GetName(command));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(command)));
                Assert.NotNull(command.ToolTip);
                Assert.Equal(36, command.ActualWidth);
                Assert.Equal(36, command.ActualHeight);
            }
            Assert.Equal("T", AutomationProperties.GetAcceleratorKey(bar.TextButton));
            Assert.Equal("C", AutomationProperties.GetAcceleratorKey(bar.ColorButton));
            foreach (ButtonBase command in new ButtonBase[] { bar.TextButton, bar.ColorButton, bar.CancelButton })
            {
                string key = AutomationProperties.GetAcceleratorKey(command);
                Assert.EndsWith($"({key})", AutomationProperties.GetHelpText(command), StringComparison.Ordinal);
                Assert.EndsWith($"({key})", Assert.IsType<string>(command.ToolTip), StringComparison.Ordinal);
            }
            Assert.Equal(string.Empty, AutomationProperties.GetAcceleratorKey(bar.CaptureButton));
            foreach (RadioButton selected in new[] { bar.CaptureKindButton, bar.CaptureButton })
            {
                selected.ApplyTemplate();
                var wash = Assert.IsType<Border>(selected.Template.FindName("CheckedLayer", selected));
                var underline = Assert.IsType<Border>(selected.Template.FindName("SelectedUnderline", selected));
                Assert.Same(bar.Resources[CaptureModeBar.HoverKey], wash.Background);
                Assert.NotSame(bar.Resources[CaptureModeBar.AccentKey], wash.Background);
                Assert.Same(bar.Resources["Text.Primary"], underline.Background);
                Assert.Equal(2, underline.Height);
                Assert.Equal(1, underline.Opacity);
                Assert.Same(bar.Resources[CaptureModeBar.TextKey], selected.Foreground);
            }

            bar.Configure(false, false, false, otherActions: true, recording: true);
            bar.ShowMode(CaptureSelectorMode.Capture);
            Assert.True(bar.RecordKindButton.IsChecked);
            Assert.False(bar.CaptureKindButton.IsChecked);
            foreach (FrameworkElement unavailable in new FrameworkElement[]
                     { bar.WindowButton, bar.ScrollButton, bar.DelayButton, bar.TextButton, bar.ColorButton, bar.MoreButton })
                Assert.Equal(Visibility.Collapsed, unavailable.Visibility);
            Assert.Equal(Visibility.Visible, bar.FullScreenButton.Visibility);
            var requests = new List<CaptureSelectorRequest>();
            bar.ActionRequested += requests.Add;
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(bar.CaptureKindButton)).Select();
            Assert.Equal([new CaptureSelectorRequest(CaptureSelectorAction.Capture)], requests);
            Assert.True(bar.RecordKindButton.IsChecked);
        }
    });

    [Fact]
    public void WindowAndRegionSelectionWorkThroughAutomationIncludingInitialRegionChoice() => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = EvidenceView(Desktop(), AppTheme.Glass, "window-hover");
        try
        {
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            Assert.True(bar.WindowButton.IsChecked);
            Assert.NotNull(view.HoverRegionForTest);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(bar.CaptureButton)).Select();
            view.UpdatePointer(false, false);
            Assert.True(bar.CaptureButton.IsChecked);
            Assert.Null(view.HoverRegionForTest);
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(bar.WindowButton)).Select();
            view.UpdatePointer(false, false);
            Assert.True(bar.WindowButton.IsChecked);
            Assert.NotNull(view.HoverRegionForTest);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void RecordingSelectorConfirmsOnlyGeometryAndSwitchesKindWithoutCapturing() => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("en-US");
        CaptureOverlayView MakeView() => new(Desktop(), showMagnifier: false)
        {
            ReadCursor = () => new PointD(300, 300), ReadMonitors = () => Monitors,
            RecordingSelection = true, ShowModeBar = true, OffersOtherActions = true,
        };
        CaptureOverlayView view = MakeView();
        try
        {
            view.InitializePointer();
            Render(view);
            Assert.True(view.ModeBarForTest!.RecordKindButton.IsChecked);
            view.ModeBarForTest.HelpButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Contains("record", view.StartGuideCardForTest!.HelpText);
            RegionSelectionEventArgs? chosen = null;
            view.SelectionConfirmed += (_, selection) => chosen = selection;
            view.ModeBarForTest.FullScreenButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.NotNull(chosen);
            Assert.Equal(new RectD(0, 0, Width, Height), chosen!.BitmapRegion);
            Assert.False(chosen.TextCapture);
        }
        finally { view.ReleaseResources(); }
        CaptureOverlayView switching = MakeView();
        try
        {
            switching.InitializePointer();
            Render(switching);
            var requests = new List<CaptureSelectorRequest>();
            int captures = 0;
            switching.ActionRequested += (_, request) => requests.Add(request);
            switching.SelectionConfirmed += (_, _) => captures++;
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(switching.ModeBarForTest!.CaptureKindButton)).Select();
            Assert.Equal([new CaptureSelectorRequest(CaptureSelectorAction.Capture)], requests);
            Assert.Equal(0, captures);
        }
        finally { switching.ReleaseResources(); }
    });

    [Theory]
    [InlineData("ko-KR")]
    [InlineData("en-US")]
    public void PausingBeforeTheFirstFrameBecomesReadyShowsCompactPausedState(string culture) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        using var firstCaptureEntered = new ManualResetEventSlim();
        using var releaseFirstCapture = new ManualResetEventSlim();
        int captures = 0;
        var grabber = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance), false)
        {
            CaptureFrameForTest = pixels =>
            {
                Interlocked.Increment(ref captures);
                firstCaptureEntered.Set();
                if (!releaseFirstCapture.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("First frame was not released.");
                Array.Fill(pixels, (byte)0xA0);
            },
        };
        var sink = new FirstFrameSink();
        using var recorder = new RegionRecorder(grabber, _ => sink, NullLogger.Instance);
        var window = new RecordingControlWindow(new RectD(100, 100, 640, 360),
            new RecordingSettings { UseStartDelay = false, IncludeCursor = false },
            () => recorder, () => "unused-first-frame.mp4", NullLogger.Instance) { ReadPauseHotkey = () => Hotkey.None };
        RecordingResult? finished = null;
        Exception? failure = null;
        window.RecordingFinished += (_, result) => finished = result;
        window.Failed += (_, args) => failure = args.Exception;
        try
        {
            // Start the real recorder offscreen, with only the acquisition replaced by synthetic pixels.
            window.PrimaryButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(firstCaptureEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(window.IsRecording);
            Assert.False(recorder.IsReady);
            Assert.True(window.TogglePause());
            Assert.True(window.IsPaused && recorder.IsPaused);
            Border strip = Field<Border>(window, "_controlStrip");
            SymbolIcon indicator = Field<SymbolIcon>(window, "_recordingIndicator");
            TextBlock status = Field<TextBlock>(window, "_statusText");
            TextBlock timer = Field<TextBlock>(window, "_timerText");
            Assert.Equal(560, strip.Width);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);
            Geometry pausedGlyph = Assert.IsAssignableFrom<Geometry>(indicator.Data);
            string pausedHelp = UiText.Get("RecordingPause_StatusHelp");
            Assert.Equal(pausedHelp, AutomationProperties.GetHelpText(status));

            releaseFirstCapture.Set();
            WindowDetectOverlayTests.PumpUntil(() => recorder.IsReady && Field<bool>(window, "_readyAnnounced")
                && !window.IsPauseTransitionPending);

            Arrange(strip);
            Assert.Equal(1, Volatile.Read(ref captures));
            Assert.Equal(1, sink.Frames);
            Assert.Null(grabber.ActiveBackend); // Neither GDI nor WGC is opened for this lifecycle test.
            Assert.True(window.IsPaused && recorder.IsPaused);
            Assert.Equal(236 + Math.Max(0, window.PauseButtonForTest.Width - 36), strip.ActualWidth);
            Assert.Equal(Visibility.Visible, indicator.Visibility);
            Assert.Same(pausedGlyph, indicator.Data);
            Assert.False(indicator.HasAnimatedProperties);
            Assert.Equal(UiText.Get("RecordingPause_Status"), status.Text);
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Equal(Grid.GetColumn(timer), Grid.GetColumn(status));
            Assert.Equal("00:00", timer.Text);
            Assert.Equal(pausedHelp, AutomationProperties.GetHelpText(status));
            Assert.Equal(pausedHelp, status.ToolTip);
            AutomationPeer timerPeer = UIElementAutomationPeer.CreatePeerForElement(timer);
            Assert.Contains(status.Text, timerPeer.GetName());
            Assert.Equal(pausedHelp, timerPeer.GetHelpText());
            Assert.Equal(UiText.Get("RecordingPause_ResumeName"), new ButtonAutomationPeer(window.PauseButtonForTest).GetName());
            Assert.Equal(UiText.Get("RecordingPause_ResumeHelp"), AutomationProperties.GetHelpText(window.PauseButtonForTest));
        }
        finally
        {
            releaseFirstCapture.Set();
            if (window.IsRecording)
            {
                window.RequestStop();
                WindowDetectOverlayTests.PumpUntil(() => finished is not null || failure is not null);
            }
            window.CompleteAndClose();
        }
        Assert.Null(failure);
        Assert.True(sink.Completed);
    });

    [Theory]
    [InlineData("click", false)]
    [InlineData("click", true)]
    [InlineData("enter", false)]
    [InlineData("enter", true)]
    [InlineData("automation", false)]
    public void RegionButtonRequestsCaptureOncePerActivation(string activation, bool alreadySelected) => StaTestHost.Run(() =>
    {
        CaptureOverlayView view = EvidenceView(Desktop(), AppTheme.Glass, "window-hover");
        try
        {
            Render(view);
            CaptureModeBar bar = view.ModeBarForTest!;
            bar.ShowMode(CaptureSelectorMode.Capture, windowSelected: !alreadySelected);
            var requests = new List<CaptureSelectorMode>();
            bar.ModeRequested += requests.Add;
            switch (activation)
            {
                case "click":
                    // WPF mouse/Space activation reaches this virtual method; no system input is injected.
                    ClickRadio(bar.CaptureButton);
                    break;
                case "enter":
                    KeyEventArgs key = WindowDetectOverlayTests.KeyArgs(Key.Enter);
                    bar.CaptureButton.RaiseEvent(key);
                    Assert.True(key.Handled);
                    break;
                case "automation":
                    ((ISelectionItemProvider)new RadioButtonAutomationPeer(bar.CaptureButton)).Select();
                    break;
            }
            Assert.Equal([CaptureSelectorMode.Capture], requests);
            Assert.True(bar.CaptureButton.IsChecked);
            Assert.Null(view.HoverRegionForTest);
        }
        finally { view.ReleaseResources(); }
    });

    [Fact]
    public void RegionButtonClickAfterAutomationSelectionStillRequestsCaptureOnce() => StaTestHost.Run(() =>
    {
        var bar = new CaptureModeBar();
        bar.ShowMode(CaptureSelectorMode.Capture, windowSelected: true);
        var requests = new List<CaptureSelectorMode>();
        bar.ModeRequested += requests.Add;
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(bar.CaptureButton)).Select();
        Assert.Equal([CaptureSelectorMode.Capture], requests);
        requests.Clear();
        ClickRadio(bar.CaptureButton);
        Assert.Equal([CaptureSelectorMode.Capture], requests);
    });

    private static void ClickRadio(RadioButton button) => button.GetType()
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

    private sealed class FirstFrameSink : IVideoEncoder
    {
        private int _frames;
        public int Width => 640;
        public int Height => 360;
        public bool Completed { get; private set; }
        internal int Frames => Volatile.Read(ref _frames);
        public void WriteFrame(in EncoderFrame frame) => Interlocked.Increment(ref _frames);
        public void Complete() => Completed = true;
        public void Dispose() { }
    }

    private static void Arrange(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        element.UpdateLayout();
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}
