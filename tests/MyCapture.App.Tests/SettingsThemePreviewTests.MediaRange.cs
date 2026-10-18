using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task GalleryVideoPlayer_ShowsStudioRangeRecordingAtFullRange() =>
        RunIsolated(nameof(GalleryVideoPlayer_ShowsStudioRangeRecordingAtFullRange), () => VerifyGalleryVideoRange(fullRange: false));

    [Fact]
    public Task GalleryVideoPlayer_ShowsFullRangeRecordingAtFullRange() =>
        RunIsolated(nameof(GalleryVideoPlayer_ShowsFullRangeRecordingAtFullRange), () => VerifyGalleryVideoRange(fullRange: true));

    [Fact]
    public Task GalleryVideoPlayer_MeasuredShaderReproducesStudioSource() =>
        RunIsolated(nameof(GalleryVideoPlayer_MeasuredShaderReproducesStudioSource), () => VerifyGalleryVideoRange(false, true));

    [Fact]
    public Task GalleryVideoPlayer_MeasuredShaderReproducesFullSource() =>
        RunIsolated(nameof(GalleryVideoPlayer_MeasuredShaderReproducesFullSource), () => VerifyGalleryVideoRange(true, true));

    [Fact]
    public Task GalleryVideoPlayer_StudioUserPlayPausePath() =>
        RunIsolated(nameof(GalleryVideoPlayer_StudioUserPlayPausePath), () => VerifyGalleryVideoRange(false, userPath: true));

    [Fact]
    public Task GalleryVideoPlayer_FullUserPlayPausePath() =>
        RunIsolated(nameof(GalleryVideoPlayer_FullUserPlayPausePath), () => VerifyGalleryVideoRange(true, userPath: true));

    [Fact]
    public Task GalleryVideoPlayer_StudioMeasuredUserPlayPausePath() =>
        RunIsolated(nameof(GalleryVideoPlayer_StudioMeasuredUserPlayPausePath), () => VerifyGalleryVideoRange(false, true, true));

    [Fact]
    public Task GalleryVideoPlayer_FullMeasuredUserPlayPausePath() =>
        RunIsolated(nameof(GalleryVideoPlayer_FullMeasuredUserPlayPausePath), () => VerifyGalleryVideoRange(true, true, true));

    private static void VerifyGalleryVideoRange(bool fullRange, bool forceMeasured = false, bool userPath = false)
    {
        // The player window reads styles from Application resources.
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
        string root = OwnedTestDirectory.Create("mc-gallery-video-range-");
        GalleryVideoPlayerWindow? player = null;
        bool previousForce = MediaElementRangeVerification.ForceMeasuredForTest;
        MediaElementRangeVerification.ForceMeasuredForTest = forceMeasured;
        try
        {
            const int width = 1280, height = 720;
            string clip = userPath
                ? MyCapture.App.Diagnostics.VideoRangeColourPattern.Encode(Path.Combine(root, "clip.mp4"), width, height, fullRange, durationSeconds: 10)
                : RangePatternClip.Encode(Path.Combine(root, "clip.mp4"), width, height, fullRange);
            player = new GalleryVideoPlayerWindow { Left = -10000, Top = -10000 };
            if (!userPath) player.Show();
            Assert.True(player.Open(Guid.NewGuid(), "clip", clip, autoPlay: false));
            if (userPath) player.Show();
            FieldInfo ready = typeof(GalleryVideoPlayerWindow).GetField("_mediaReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (!(bool)ready.GetValue(player)! && DateTime.UtcNow < deadline)
                RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
            Assert.True((bool)ready.GetValue(player)!, "the gallery player did not open the clip");
            RangePatternClip.WaitForVerification(player.MediaForDiagnostic);
            RangePatternClip.Pump(TimeSpan.FromMilliseconds(500));

            var video = (MediaElement)typeof(GalleryVideoPlayerWindow).GetField("_video", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
            // The real player must verify successfully with the shared implicit Window style.
            // Falling back silently would hide a presenter/reference mismatch.
            VideoRangeCorrection correction = RangePatternClip.AssertVerifiedEffect(video, !fullRange);
            if (forceMeasured)
            {
                Assert.True(StudioRangeVideoEffect.HardwareShaderAvailable(), "Real measured shader test requires hardware PS 2.0.");
                Assert.Equal("measured", StudioRangeVideoEffect.VerificationFor(video)!.LastResult!.Kind);
                Assert.IsType<MeasuredRangeVideoEffect>(video.Effect);
            }
            if (userPath)
                Assert.True(StudioRangeVideoEffect.HardwareShaderAvailable(), "Real user-path colour tests require hardware PS 2.0.");
            if (!StudioRangeVideoEffect.HardwareShaderAvailable()
                && !fullRange)
                return;

            var visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
                context.DrawRectangle(new VisualBrush(video) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
            var shown = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            shown.Render(visual);
            RangePatternClip.AssertMatchesSource(shown, width, height, tolerance: fullRange ? 12 : 8, correction: correction,
                verification: StudioRangeVideoEffect.VerificationFor(video)?.LastResult);
            if (userPath)
            {
                Assert.False(player.IsPlayingForDiagnostic);
                SampleUserPath("after verification");
                var button = (Button)typeof(GalleryVideoPlayerWindow).GetField("_playButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
                var invoke = (IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke);
                Press(true);
                SampleUserPath("during playback");
                Press(false);
                SampleUserPath("after pause");
                Press(true);
                Press(false);
                SampleUserPath("after second play/pause");

                void Press(bool playing)
                {
                    double before = video.Position.TotalMilliseconds;
                    invoke.Invoke();
                    Assert.True(RangePatternClip.PumpUntil(() => player.IsPlayingForDiagnostic == playing,
                        TimeSpan.FromSeconds(2)), $"play button did not change state to {playing}");
                    if (playing)
                        Assert.True(RangePatternClip.PumpUntil(() => video.Position.TotalMilliseconds >= before + 150,
                            TimeSpan.FromSeconds(3)), "the play button did not advance the real MediaElement");
                    RangePatternClip.Pump(TimeSpan.FromMilliseconds(300));
                }

                void SampleUserPath(string stage)
                {
                    var verification = StudioRangeVideoEffect.VerificationFor(video)?.LastResult;
                    try
                    {
                        RangePatternClip.AssertVerifiedEffect(video, !fullRange);
                        var frame = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        frame.Render(visual); // Effect stays applied at all four observations.
                        int maximum = 0;
                        for (int patch = 0; patch < RangePatternClip.Patches.Length; patch++)
                        {
                            var (x, y) = RangePatternClip.Centre(patch, width, height);
                            var actual = RangePatternClip.Sample(frame, x, y);
                            var expected = RangePatternClip.Patches[patch];
                            maximum = Math.Max(maximum, Math.Max(Math.Abs(actual.R - expected.R),
                                Math.Max(Math.Abs(actual.G - expected.G), Math.Abs(actual.B - expected.B))));
                        }
                        Console.WriteLine($"gallery full={fullRange}, forced={forceMeasured}, {stage}, source max error={maximum}; {verification?.Details}");
                        Assert.True(maximum <= (fullRange ? 12 : 8), $"{stage}: source max error={maximum}");
                    }
                    catch (Exception ex)
                    {
                        throw new Xunit.Sdk.XunitException($"{stage}: {ex}\n{verification?.Details}\n{verification?.Evidence}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            string patches = "unavailable (player did not open)";
            string effect = player?.MediaForDiagnostic.Effect?.GetType().Name ?? "None";
            if (player is not null)
            {
                try
                {
                    var observation = MyCapture.App.Diagnostics.VideoRangeColourPattern.Observe(player.MediaForDiagnostic);
                    patches = string.Join("; ", observation.Patches.Select((p, i) =>
                        $"{RangePatternClip.Patches[i].Name}=({p.R},{p.G},{p.B},{p.A})"));
                }
                catch (Exception sampleError) { patches = "sampling failed: " + sampleError; }
            }
            var measured = player is null ? null : StudioRangeVideoEffect.VerificationFor(player.MediaForDiagnostic)?.LastResult;
            throw new Xunit.Sdk.XunitException($"{ex}\nPlayer verification: {measured?.Details}; "
                + $"hardware={StudioRangeVideoEffect.HardwareShaderAvailable()}; effect={effect}; patches: {patches}\n{measured?.Evidence}");
        }
        finally
        {
            player?.Close();
            MediaElementRangeVerification.ForceMeasuredForTest = previousForce;
            application.Shutdown();
            OwnedTestDirectory.Delete(root);
        }
    }
}
