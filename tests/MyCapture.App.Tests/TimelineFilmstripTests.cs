using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyCapture.App.Recording;
using MyCapture.Core.Recording;
using MyCapture.Platform.Recording;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// The trim strip's frame thumbnails: decoded from a real MP4 off the UI thread, in order,
/// at the tile's device-pixel size, and never a reason for the editor to fail.
/// </summary>
public sealed class TimelineFilmstripTests
{
    [Fact]
    public void DecodesEveryTileInOrderAtTheDevicePixelSize() => StaTestHost.Run(() =>
    {
        string root = OwnedTestDirectory.Create("mc-filmstrip-");
        try
        {
            string clip = Path.Combine(root, "ramp.mp4");
            WriteBlueRampClip(clip, width: 320, height: 180, fps: 30, frames: 60);
            FilmstripLayout layout = FilmstripLayout.Create(2_000, 320, 180, 500, 40, 30);
            Assert.Equal(8, layout.TileCount);

            int published = 0;
            using TimelineFilmstrip filmstrip = TimelineFilmstrip.Start(
                clip, layout, new DpiScale(1.5, 1.5), Dispatcher.CurrentDispatcher,
                _ => published++, NullLogger.Instance);
            Assert.True(filmstrip.CompletionForTest.Wait(TimeSpan.FromSeconds(30)), "decode did not finish");
            Assert.Equal(layout.TileCount, filmstrip.PublishedCount);
            Assert.True(filmstrip.IsCompleted);

            // Eight tiles are one batch: the strip is told once, on its dispatcher.
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, published);

            Assert.Equal((int)Math.Round(layout.TileWidth * 1.5), filmstrip.PixelWidth);
            Assert.Equal(60, filmstrip.PixelHeight);
            int previousBlue = -8;
            for (int index = 0; index < layout.TileCount; index++)
            {
                BitmapSource tile = Assert.IsAssignableFrom<BitmapSource>(filmstrip.Tile(index));
                Assert.True(tile.IsFrozen, $"tile {index} is not frozen");
                Assert.Equal(filmstrip.PixelWidth, tile.PixelWidth);
                Assert.Equal(filmstrip.PixelHeight, tile.PixelHeight);

                // The clip's blue channel rises with time, so later tiles decode bluer frames.
                int blue = CentreBlue(tile);
                Assert.True(blue + 6 >= previousBlue, $"tile {index} ({blue}) is not later than tile {index - 1} ({previousBlue})");
                previousBlue = blue;
            }

            Assert.True(previousBlue > CentreBlue(filmstrip.Tile(0)!) + 100, "the strip does not span the clip");
            Assert.Null(filmstrip.Tile(layout.TileCount));
        }
        finally
        {
            OwnedTestDirectory.Delete(root);
        }
    });

    [Fact]
    public void MissingClip_LeavesTheStripEmptyWithoutThrowing() => StaTestHost.Run(() =>
    {
        string missing = Path.Combine(Path.GetTempPath(), "mc-filmstrip-missing-" + Guid.NewGuid().ToString("N") + ".mp4");
        FilmstripLayout layout = FilmstripLayout.Create(1_000, 320, 180, 300, 40, 30);
        using TimelineFilmstrip filmstrip = TimelineFilmstrip.Start(
            missing, layout, new DpiScale(1, 1), Dispatcher.CurrentDispatcher,
            _ => Assert.Fail("an unreadable clip must not publish tiles"), NullLogger.Instance);

        Assert.True(filmstrip.CompletionForTest.Wait(TimeSpan.FromSeconds(10)));
        Pump(TimeSpan.FromMilliseconds(30));
        Assert.Equal(0, filmstrip.PublishedCount);
        Assert.Null(filmstrip.Tile(0));
    });

    [Fact]
    public void TrimStripGrowsForTheFilmstripOnlyInWindowsTallEnough() => StaTestHost.Run(() =>
    {
        using var timeline = new TwoLineTimeline();
        timeline.Initialize(10_000, 30);
        timeline.SetDetailVisible(false);
        Assert.False(timeline.IsFullStripForTest);
        Assert.Equal(58, timeline.TrimStripHeight);

        var host = new Window
        {
            Content = timeline, Width = 900, Height = 555, Left = -10000, Top = -10000,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        try
        {
            host.Show();
            Pump(TimeSpan.FromMilliseconds(30));
            host.UpdateLayout();
            Assert.False(timeline.IsFullStripForTest);
            Assert.Equal(58, timeline.TrimStripHeight);

            host.Height = TwoLineTimeline.FullStripWindowHeight;
            Pump(TimeSpan.FromMilliseconds(30));
            host.UpdateLayout();
            Assert.True(timeline.IsFullStripForTest);
            Assert.Equal(74, timeline.TrimStripHeight);
            timeline.SetDetailVisible(true);
            Assert.Equal(60, timeline.TrimStripHeight);

            host.Height = 555;
            Pump(TimeSpan.FromMilliseconds(30));
            host.UpdateLayout();
            Assert.False(timeline.IsFullStripForTest);
            Assert.Equal(44, timeline.TrimStripHeight);
        }
        finally
        {
            host.Close();
        }
    });

    private static void WriteBlueRampClip(string path, int width, int height, int fps, int frames)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        using var encoder = new MediaFoundationVideoEncoder(
            new VideoEncoderOptions(path, width, height, fps, VideoEncoderOptions.DeriveBitrate(width, height, fps)),
            NullLogger.Instance);
        for (int frame = 0; frame < frames; frame++)
        {
            byte blue = (byte)(frame * 255 / (frames - 1));
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = blue;
                pixels[offset + 1] = 40;
                pixels[offset + 2] = 40;
                pixels[offset + 3] = 255;
            }

            encoder.WriteFrame(new EncoderFrame(pixels, width, height, stride, frame * 1000.0 / fps));
        }

        encoder.Complete();
    }

    private static int CentreBlue(BitmapSource tile)
    {
        var pixel = new byte[4];
        tile.CopyPixels(new Int32Rect(tile.PixelWidth / 2, tile.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel[0];
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
