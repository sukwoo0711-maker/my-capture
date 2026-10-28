using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.App.Pinning;
using Xunit;

namespace MyCapture.App.Tests;

internal static class PinHistoryTestSupport
{
    internal static PinWindow? Restore(PinManager manager) => Await(manager.RestoreLastClosedAsync());

    internal static T Await<T>(Task<T> task)
    {
        Await((Task)task);
        return task.GetAwaiter().GetResult();
    }

    internal static void Await(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        Assert.True(task.IsCompleted, "Pin history operation did not finish in 15 seconds.");
        task.GetAwaiter().GetResult();
    }

    internal static void EqualPixels(BitmapSource expected, BitmapSource actual)
    {
        Assert.Equal(expected.PixelWidth, actual.PixelWidth);
        Assert.Equal(expected.PixelHeight, actual.PixelHeight);
        Assert.Equal(Pixels(expected), Pixels(actual));
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        converted.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }
}
