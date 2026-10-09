using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Gallery;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

public sealed partial class SettingsThemePreviewTests
{
    [Fact]
    public Task GalleryVideoPlayer_SeekRangeIsTheContainerDuration() =>
        RunIsolated(nameof(GalleryVideoPlayer_SeekRangeIsTheContainerDuration), () =>
        {
            // The player window reads styles from Application resources.
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string name in new[] { "Tokens", "Symbols", "Controls" })
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/MyCapture;component/Themes/{name}.xaml") });
            string root = TestRecycleBin.CreateTempSubdirectory("mc-gallery-video-duration-").FullName;
            GalleryVideoPlayerWindow? player = null;
            try
            {
                // 10 frames at 15 fps (666.7 ms): WPF reports no NaturalDuration for it, which used
                // to leave the seek slider with a 1 ms range.
                string clip = Path.Combine(root, "clip.mp4");
                var options = new VideoEncoderOptions(clip, 160, 120, 15, VideoEncoderOptions.DeriveBitrate(160, 120, 15));
                byte[] pixels = new byte[160 * 4 * 120];
                using (var encoder = new MediaFoundationVideoEncoder(options, NullLogger.Instance))
                {
                    for (int i = 0; i < 10; i++)
                    {
                        Array.Fill(pixels, (byte)(40 + (i * 15)));
                        encoder.WriteFrame(new EncoderFrame(pixels, 160, 120, 160 * 4, i * 1000.0 / 15));
                    }

                    encoder.Complete();
                }

                double containerMs = MediaFoundationVideoFrameReader.ReadContainerDurationMs(clip);
                player = new GalleryVideoPlayerWindow { Left = -10000, Top = -10000 };
                player.Show();
                Assert.True(player.Open(Guid.NewGuid(), "clip", clip, autoPlay: false));
                FieldInfo ready = typeof(GalleryVideoPlayerWindow).GetField("_mediaReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while (!(bool)ready.GetValue(player)! && DateTime.UtcNow < deadline)
                {
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
                    timer.Tick += (sender, _) => { ((DispatcherTimer)sender!).Stop(); frame.Continue = false; };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                }

                Assert.True((bool)ready.GetValue(player)!, "the gallery player did not open the clip");
                var seek = (Slider)typeof(GalleryVideoPlayerWindow).GetField("_seek", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
                Assert.Equal(containerMs, seek.Maximum);
            }
            finally
            {
                player?.Close();
                TestRecycleBin.DeleteDirectory(root, recursive: true);
            }
        });
}
