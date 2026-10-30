using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyCapture.App.Gallery;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed class GalleryVideoPlayerReadyTests
{
    [Fact]
    public Task RequestPlayBeforeMediaOpenedWaitsForTheSourceProbeAndPlaysWhenReady() => RunIsolated(
        nameof(RequestPlayBeforeMediaOpenedWaitsForTheSourceProbeAndPlaysWhenReady), fixture =>
        {
            fixture.Open();
            fixture.Player.RequestPlay();
            Assert.Empty(fixture.Playback);
            Assert.False(fixture.Player.IsMediaReadyForDiagnostic);

            fixture.RaiseMediaOpened();
            Assert.False(fixture.Probe.Task.IsCompleted);
            Assert.False(fixture.Player.IsMediaReadyForDiagnostic);
            Assert.False(fixture.Player.IsPlayingForDiagnostic);
            Assert.Empty(fixture.Playback);

            fixture.CompleteProbe();
            Assert.True(fixture.Player.IsMediaReadyForDiagnostic);
            Assert.True(fixture.Player.IsPlayingForDiagnostic);
            Assert.Equal(new[] { true }, fixture.Playback);

            fixture.Player.RequestPlay();
            Assert.Equal(new[] { true, true }, fixture.Playback);
        });

    [Fact]
    public Task MediaOpenedWithoutAPlayRequestWaitsForTheProbeAndPauses() => RunIsolated(
        nameof(MediaOpenedWithoutAPlayRequestWaitsForTheProbeAndPauses), fixture =>
        {
            fixture.Open();
            fixture.RaiseMediaOpened();
            Assert.False(fixture.Player.IsMediaReadyForDiagnostic);
            Assert.Empty(fixture.Playback);

            fixture.CompleteProbe();
            Assert.True(fixture.Player.IsMediaReadyForDiagnostic);
            Assert.False(fixture.Player.IsPlayingForDiagnostic);
            Assert.Equal(new[] { false }, fixture.Playback);

            fixture.Player.RequestPlay();
            Assert.True(fixture.Player.IsPlayingForDiagnostic);
            Assert.Equal(new[] { false, true }, fixture.Playback);
        });

    [Fact]
    public Task ClosingWhileMediaOpenedAwaitsTheProbePreventsLatePlayback() => RunIsolated(
        nameof(ClosingWhileMediaOpenedAwaitsTheProbePreventsLatePlayback), fixture =>
        {
            fixture.Open();
            fixture.Player.RequestPlay();
            fixture.RaiseMediaOpened();
            Assert.False(fixture.Probe.Task.IsCompleted);
            Assert.False(fixture.Player.IsMediaReadyForDiagnostic);
            Assert.Empty(fixture.Playback);

            bool closed = false;
            fixture.Player.Closed += (_, _) => closed = true;
            fixture.Player.Close();
            Assert.True(closed);
            fixture.CompleteProbe();

            Assert.False(fixture.Player.IsMediaReadyForDiagnostic);
            Assert.False(fixture.Player.IsPlayingForDiagnostic);
            Assert.Empty(fixture.Playback);
        });

    private static Task RunIsolated(string name, Action<Fixture> verify) => SettingsThemePreviewTests.RunIsolated(name, () =>
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resourceName in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{resourceName}.xaml") });
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            using var fixture = new Fixture();
            verify(fixture);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            application.Shutdown();
        }
    }, typeof(GalleryVideoPlayerReadyTests));

    private sealed class Fixture : IDisposable
    {
        internal GalleryVideoPlayerWindow Player { get; } = new()
        {
            Left = -10000, Top = -10000, Topmost = false, ShowActivated = false,
        };
        // Inline continuations make completion of the async MediaOpened handler observable
        // on this same dispatcher before CompleteProbe returns, including its early return.
        internal TaskCompletionSource<VideoSourceInfo> Probe { get; } = new();
        internal List<bool> Playback { get; } = [];

        internal void Open()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "video-shorter-than-audio.mp4");
            var probedPaths = new List<string>();
            Player.SourceProbeForTest = sourcePath =>
            {
                probedPaths.Add(sourcePath);
                return Probe.Task;
            };
            Player.PlaybackForTest = Playback.Add;
            Guid id = Guid.NewGuid();
            Assert.True(Player.Open(id, "Ready-path fixture", path, autoPlay: false));
            Assert.Equal(id, Player.VideoId);
            Assert.Equal(new[] { path }, probedPaths);
            Assert.Equal(new Uri(path), Player.MediaForDiagnostic.Source);

            // Cancel the codec bootstrap before dispatching any messages. These tests
            // control MediaOpened ordering through the real routed event and subscription.
            Player.MediaForDiagnostic.Close();
            Player.MediaForDiagnostic.Source = null;
        }

        internal void RaiseMediaOpened() => Player.MediaForDiagnostic.RaiseEvent(
            new RoutedEventArgs(MediaElement.MediaOpenedEvent, Player.MediaForDiagnostic));

        internal void CompleteProbe()
        {
            Probe.SetResult(new VideoSourceInfo(1234, StudioRange: false));
            Player.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        public void Dispose() => Player.Close();
    }
}
