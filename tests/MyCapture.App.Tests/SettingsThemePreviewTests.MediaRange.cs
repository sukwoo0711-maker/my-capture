using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Gallery;
using MyCapture.App.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    /// <summary>The library's floating player shows a studio-range recording at full range.</summary>
    [Fact]
    public Task GalleryVideoPlayer_ShowsStudioRangeRecordingAtFullRange() =>
        RunIsolated(nameof(GalleryVideoPlayer_ShowsStudioRangeRecordingAtFullRange), () =>
        {
            // The player window reads styles from Application resources.
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            string root = OwnedTestDirectory.Create("mc-gallery-video-range-");
            GalleryVideoPlayerWindow? player = null;
            try
            {
                const int width = 1280, height = 720;
                string clip = RangePatternClip.Encode(Path.Combine(root, "clip.mp4"), width, height, fullRange: false);
                player = new GalleryVideoPlayerWindow { Left = -10000, Top = -10000 };
                player.Show();
                Assert.True(player.Open(Guid.NewGuid(), "clip", clip, autoPlay: false));
                FieldInfo ready = typeof(GalleryVideoPlayerWindow).GetField("_mediaReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!(bool)ready.GetValue(player)! && DateTime.UtcNow < deadline)
                    RangePatternClip.Pump(TimeSpan.FromMilliseconds(20));
                Assert.True((bool)ready.GetValue(player)!, "the gallery player did not open the clip");
                RangePatternClip.WaitForCalibration();
                RangePatternClip.Pump(TimeSpan.FromMilliseconds(500));

                var video = (MediaElement)typeof(GalleryVideoPlayerWindow).GetField("_video", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
                bool needsExpansion = StudioRangeVideoEffect.Calibration.NeedsExpansion;
                bool hardware = StudioRangeVideoEffect.HardwareShaderAvailable();
                // The real presenter measurement runs on its own UI thread inside an app with the
                // shared implicit Window style; a failure would silently keep the old policy.
                if (hardware)
                    Assert.Null(StudioRangeVideoEffect.Calibration.Failure);
                Assert.Equal(needsExpansion && hardware, video.Effect is StudioRangeVideoEffect);
                if (!hardware && needsExpansion)
                {
                    // Software rendering keeps playback fast instead of correcting the range.
                    Assert.Null(video.Effect);
                    return;
                }

                var visual = new DrawingVisual();
                using (DrawingContext context = visual.RenderOpen())
                    context.DrawRectangle(new VisualBrush(video) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
                var shown = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                shown.Render(visual);
                RangePatternClip.AssertMatchesSource(shown, width, height);
            }
            finally
            {
                player?.Close();
                application.Shutdown();
                // Retain the temporary fixture for device diagnostics; no permanent cleanup.
            }
        });
}
