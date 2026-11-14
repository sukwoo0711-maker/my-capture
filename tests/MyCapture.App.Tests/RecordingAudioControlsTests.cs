using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.App.Capture;
using MyCapture.Core.Localization;
using MyCapture.Core.Primitives;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class RecordingAudioControlsTests
{
    public static IEnumerable<object[]> Labels()
    {
        foreach (string culture in new[] { "ko-KR", "en-US" })
        foreach (int sources in Enumerable.Range(0, 4))
        foreach (double scale in new[] { 1d, 1.5d, 2d }) yield return [culture, sources, scale];
    }

    [Theory, MemberData(nameof(Labels))]
    public void EffectiveLabelAndMenuButtonFitEveryScale(string culture, int sources, double scale) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage(culture);
        var window = Create();
        try
        {
            window.PreviewAudioSessionForTest(true, Snapshot(sources));
            Border strip = RecordingAudioEvidenceTests.Field<Border>(window, "_controlStrip");
            TextBlock label = RecordingAudioEvidenceTests.Field<TextBlock>(window, "_audioStatusText");
            string[] expected = culture == "ko-KR" ? ["소리 없음", "시스템 소리", "마이크", "시스템+마이크"]
                : ["No audio", "System audio", "Microphone", "System + mic"];
            strip.Width /= scale; strip.Height /= scale;
            Layout(strip);
            Assert.Equal(expected[sources], label.Text);
            var natural = new TextBlock
            {
                Text = label.Text, FontFamily = label.FontFamily, FontSize = label.FontSize,
                FontWeight = label.FontWeight, UseLayoutRounding = label.UseLayoutRounding,
            };
            TextOptions.SetTextFormattingMode(natural, TextOptions.GetTextFormattingMode(label));
            natural.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.True(label.ActualWidth >= natural.DesiredSize.Width,
                $"{culture}/{sources}/{scale}: actual {label.ActualWidth}, natural {natural.DesiredSize.Width}, desired {label.DesiredSize.Width}, layout rounding {label.UseLayoutRounding}.");
            Assert.Equal(TextTrimming.None, label.TextTrimming);
            Rect bounds = label.TransformToAncestor(strip).TransformBounds(new Rect(label.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Right <= strip.ActualWidth);
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= strip.ActualHeight);
            var peer = new ButtonAutomationPeer(window.AudioButtonForTest);
            Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
            Assert.Equal(expected[sources], peer.GetName());
            Assert.True(window.AudioButtonForTest.Focusable);
            Assert.Equal(UiText.Get("RecordingAudio_ChooseSources"), AutomationProperties.GetHelpText(window.AudioButtonForTest));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PreparationPersistsChoicesAndSilentStartLocksOnlyCurrentSession() => StaTestHost.Run(() =>
    {
        var settings = new RecordingSettings();
        var window = Create(settings);
        var saved = new List<(bool System, bool Microphone)>();
        window.AudioPreferencesChanged += (system, microphone) => saved.Add((system, microphone));
        try
        {
            ContextMenu menu = window.CreateAudioMenu();
            Assert.Equal(2, menu.Items.Count);
            Assert.All(menu.Items.Cast<MenuItem>(), item => { Assert.True(item.IsEnabled); Assert.False(item.IsChecked); });
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal([(true, false)], saved);
            Assert.True(settings.RecordSystemAudio);
            Assert.True(((MenuItem)window.CreateAudioMenu().Items[0]).IsChecked);
            window.RefreshAudioPreferences(false, false);
            window.PreviewAudioSessionForTest(false, Snapshot(0));
            menu = window.CreateAudioMenu();
            Assert.Equal(3, menu.Items.Count);
            Assert.All(menu.Items.Cast<MenuItem>(), item => Assert.False(item.IsEnabled));
            Assert.Equal(UiText.Get("RecordingAudio_NextRecording"), ((MenuItem)menu.Items[2]).Header);
            Assert.False(window.SetAudioSources(true, true));
            window.RefreshAudioPreferences(true, true);
            Assert.Equal(UiText.Get("Recording_NoAudio"), AutomationProperties.GetName(window.AudioButtonForTest));
            Assert.Single(saved);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void AudioMenuUsesTheExistingOpaqueCaptureFallback() => StaTestHost.Run(() =>
    {
        Func<bool> original = CaptureWindowExclusion.LayeredWindowProbe;
        RecordingControlWindow? window = null;
        try
        {
            CaptureWindowExclusion.LayeredWindowProbe = static () => false;
            window = Create();
            Assert.False(window.AllowsTransparency);
            ContextMenu menu = window.CreateAudioMenu();
            Assert.IsType<OpaqueCaptureContextMenu>(menu);
            Assert.Same(window.AudioButtonForTest, menu.PlacementTarget);
            Assert.Equal(PlacementMode.Bottom, menu.Placement);
            Assert.All(menu.Items.Cast<MenuItem>(), item => Assert.True(item.IsCheckable && item.Focusable));
        }
        finally { window?.Close(); CaptureWindowExclusion.LayeredWindowProbe = original; }
    });

    [Fact]
    public void UnavailableEncoderShowsLatestRequestedSourcesAndOneFailureMessage() => StaTestHost.Run(() =>
    {
        var window = Create(new RecordingSettings { RecordSystemAudio = true });
        var messages = new List<string>();
        window.AudioFailureMessageShown += messages.Add;
        RecordingAudioDiagnostics unavailable = RecordingAudioSession.InactiveSnapshot(AudioEncoderState.Unavailable, true, false);
        try
        {
            window.PreviewAudioSessionForTest(true, unavailable);
            Assert.Single(messages);
            Assert.True(window.SetAudioSources(false, true));
            window.PresentAudioState(unavailable);
            ContextMenu menu = window.CreateAudioMenu();
            var system = (MenuItem)menu.Items[0]; var microphone = (MenuItem)menu.Items[1];
            Assert.False(system.IsChecked);
            Assert.Equal(UiText.Get("RecordingAudio_System"), system.Header);
            Assert.True(microphone.IsChecked);
            Assert.Equal(UiText.Format("RecordingAudio_Unavailable", UiText.Get("RecordingAudio_Microphone")), microphone.Header);
            Assert.Equal(UiText.Get("Recording_NoAudio"), AutomationProperties.GetName(window.AudioButtonForTest));
            Assert.Single(messages);
            Assert.True(unavailable.System.Status.Enabled);
            Assert.False(unavailable.Microphone.Status.Enabled);
            Assert.All(menu.Items.Cast<MenuItem>(), item => Assert.True(item.IsEnabled));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void StartedWithAudioSwitchesBothSourcesImmediately() => StaTestHost.Run(() =>
    {
        var sources = new FakeSources();
        var clock = new FakeClock();
        var grabber = new RegionFrameGrabber(new ScreenCaptureEngine(NullLogger<ScreenCaptureEngine>.Instance),
            false, false, null, null, (pixels, _, _, _) => Array.Fill(pixels, (byte)60));
        var recorder = new RegionRecorder(grabber, _ => new FakeEncoder(), NullLogger.Instance, clock, sources.Create);
        var window = new RecordingControlWindow(new RectD(100, 100, 32, 24), new RecordingSettings { RecordSystemAudio = true },
            () => recorder, () => System.IO.Path.Combine(OwnedTestDirectory.Create("mc-audio-controls-"), "synthetic.mp4"), NullLogger.Instance);
        try
        {
            window.PrimaryButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Wait(() => recorder.IsReady);
            _ = sources.Get(RecordingAudioSourceKind.System);
            Assert.True(window.SetAudioSources(false, true));
            _ = sources.Get(RecordingAudioSourceKind.Microphone);
            Wait(() => recorder.AudioDiagnostics.System.ActiveObjects == 0);
            window.PresentAudioState(recorder.AudioDiagnostics);
            Assert.Equal(UiText.Get("RecordingAudio_Microphone"), AutomationProperties.GetName(window.AudioButtonForTest));
            Assert.True(window.SetAudioSources(false, false));
            Wait(() => recorder.AudioDiagnostics.Microphone.ActiveObjects == 0);
            window.PresentAudioState(recorder.AudioDiagnostics);
            Assert.Equal(UiText.Get("Recording_NoAudio"), AutomationProperties.GetName(window.AudioButtonForTest));
            Assert.All(window.CreateAudioMenu().Items.Cast<MenuItem>(), item => Assert.True(item.IsEnabled));
            Assert.True(window.SetAudioSources(true, true));
            _ = sources.Get(RecordingAudioSourceKind.System, 2);
            _ = sources.Get(RecordingAudioSourceKind.Microphone, 2);
            window.PresentAudioState(recorder.AudioDiagnostics);
            Assert.Equal(UiText.Get("RecordingAudio_Both"), AutomationProperties.GetName(window.AudioButtonForTest));
        }
        finally { window.Close(); }
        Assert.Equal(0, recorder.AudioDiagnostics.SourceThreadsStillRunning);
        Assert.Equal(0, recorder.AudioDiagnostics.System.ActiveObjects);
        Assert.Equal(0, recorder.AudioDiagnostics.Microphone.ActiveObjects);
    });

    [Fact]
    public void AudioMenuHasKeyboardEntryWithoutStartingTheRecorder() => StaTestHost.Run(() =>
    {
        var window = Create();
        try
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.Left = -30000; window.Top = -30000;
            window.Show();
            _ = SetActiveWindow(new WindowInteropHelper(window).Handle);
            window.AudioButtonForTest.Focus();
            Assert.True(window.AudioButtonForTest.IsKeyboardFocusWithin);
            // Use the product's routed key handling; no SendInput or foreground requirement.
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Space)
                { RoutedEvent = Keyboard.KeyDownEvent };
            window.RaiseEvent(key);
            Assert.False(key.Handled);
            window.AudioButtonForTest.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ContextMenu menu = Assert.IsType<ContextMenu>(window.AudioMenuForTest);
            Assert.True(menu.IsOpen);
            Assert.True(((MenuItem)menu.Items[0]).Focusable);
            menu.IsOpen = false;
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("no endpoint", unchecked((int)0x80070490))]
    [InlineData("unplug", unchecked((int)0x88890004))]
    [InlineData("default changed", unchecked((int)0x88890010))]
    [InlineData("exclusive use", unchecked((int)0x8889000A))]
    [InlineData("format changed", unchecked((int)0x88890008))]
    [InlineData("service missing", unchecked((int)0x88890010))]
    [InlineData("access denied", unchecked((int)0x80070005))]
    [InlineData("timestamp error", unchecked((int)0x80004005))]
    public void FakeSourceFailureShowsOneMessageAndRecoveryIsSilent(string failure, int hresult) => StaTestHost.Run(() =>
    {
        using var language = UiText.UseLanguage("ko-KR");
        var clock = new FakeClock();
        var sources = new FakeSources();
        using var session = new RecordingAudioSession(clock.GetTimestamp(), clock.TimestampFrequency, true, true, sources.Create, clock);
        FakeSource microphone = sources.Get(RecordingAudioSourceKind.Microphone);
        _ = sources.Get(RecordingAudioSourceKind.System);
        var window = Create(new RecordingSettings { RecordSystemAudio = true, RecordMicrophone = true });
        var messages = new List<string>();
        window.AudioFailureMessageShown += messages.Add;
        try
        {
            window.PreviewAudioSessionForTest(true, session.Snapshot());
            microphone.Fail(new COMException(failure, hresult));
            Wait(() => session.Snapshot().Microphone.Status.State == AudioSourceState.Unavailable);
            RecordingAudioDiagnostics failed = session.Snapshot();
            window.PresentAudioState(failed);
            window.PresentAudioState(failed);
            Assert.Single(messages);
            Assert.Contains("녹화는 계속합니다.", messages[0]);
            if (failure == "access denied") Assert.Contains("Windows 설정에서 마이크 접근이 꺼져 있습니다.", messages[0]);
            Assert.Equal("시스템 소리", AutomationProperties.GetName(window.AudioButtonForTest));
            ContextMenu menu = window.CreateAudioMenu();
            var mic = (MenuItem)menu.Items[1];
            Assert.True(mic.IsChecked); Assert.True(mic.IsEnabled);
            Assert.Equal("마이크 · 사용 불가", mic.Header);
            Assert.Equal(AudioSourceState.Capturing, session.Snapshot().System.Status.State);
            FakeSource recovered = sources.Get(RecordingAudioSourceKind.Microphone, 2);
            window.PresentAudioState(session.Snapshot());
            Assert.Equal("시스템+마이크", AutomationProperties.GetName(window.AudioButtonForTest));
            Assert.Equal("마이크", ((MenuItem)window.CreateAudioMenu().Items[1]).Header);
            Assert.Single(messages);
            // The same failure after recovery is still the same failure in this recording.
            recovered.Fail(new COMException(failure, hresult));
            Wait(() => session.Snapshot().Microphone.Status.State == AudioSourceState.Unavailable);
            window.PresentAudioState(session.Snapshot());
            Assert.Single(messages);
        }
        finally
        {
            window.Close();
            session.RequestStop(clock.GetTimestamp()); session.JoinSources();
        }
        Assert.Equal(0, session.Snapshot().SourceThreadsStillRunning);
        Assert.Equal(0, session.Snapshot().System.ActiveObjects);
        Assert.Equal(0, session.Snapshot().Microphone.ActiveObjects);
    });

    internal static RecordingControlWindow Create(RecordingSettings? settings = null) => new(new RectD(100, 100, 640, 360),
        settings ?? new(), () => throw new InvalidOperationException("This fixture cannot open a recorder."),
        () => throw new InvalidOperationException("This fixture cannot create a recording."), NullLogger.Instance);

    internal static RecordingAudioDiagnostics Snapshot(int sources)
    {
        var result = RecordingAudioSession.InactiveSnapshot(AudioEncoderState.Available, (sources & 1) != 0, (sources & 2) != 0);
        RecordingAudioSourceDiagnostics Capturing(RecordingAudioSourceDiagnostics source) => source with
        { Status = source.Status with { State = source.Status.Enabled ? AudioSourceState.Capturing : AudioSourceState.Off } };
        return result with { System = Capturing(result.System), Microphone = Capturing(result.Microphone) };
    }

    internal static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height)); element.UpdateLayout();
    }
    private static void Wait(Func<bool> condition) => Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)));
    private sealed class FakeClock : TimeProvider
    {
        public override long TimestampFrequency => 10_000_000;
        public override long GetTimestamp() => 10_000_000;
    }
    private sealed class FakeSources
    {
        private readonly ConcurrentDictionary<RecordingAudioSourceKind, ConcurrentQueue<FakeSource>> _sources = new();
        internal IRecordingAudioPacketSource Create(RecordingAudioSourceKind kind)
        { var source = new FakeSource(); _sources.GetOrAdd(kind, _ => new()).Enqueue(source); return source; }
        internal FakeSource Get(RecordingAudioSourceKind kind, int number = 1)
        {
            Wait(() => _sources.TryGetValue(kind, out var values) && values.Count >= number);
            FakeSource source = _sources[kind].ToArray()[number - 1];
            Assert.True(source.Started.Wait(TimeSpan.FromSeconds(5))); return source;
        }
    }
    private sealed class FakeSource : IRecordingAudioPacketSource
    {
        internal readonly ManualResetEventSlim Started = new();
        private readonly BlockingCollection<Exception> _failures = new();
        internal void Fail(Exception failure) => _failures.Add(failure);
        public void Run(Action<RecordingAudioPacket> packet, Action<RecordingAudioSourceEvent> state, CancellationToken cancellation)
        { state(RecordingAudioSourceEvent.Opened); Started.Set(); throw _failures.Take(cancellation); }
        public void Dispose() { Started.Dispose(); _failures.Dispose(); }
    }
    private sealed class FakeEncoder : IAudioVideoEncoder
    {
        public AudioEncoderState AudioState => AudioEncoderState.Available;
        public string? AudioUnavailableReason => null;
        public int Width => 32;
        public int Height => 24;
        public void WriteFrame(in EncoderFrame frame) { }
        public void WriteAudio(in RecordingAudioBlock block) { }
        public void Complete() { }
        public void Dispose() { }
    }
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
}
