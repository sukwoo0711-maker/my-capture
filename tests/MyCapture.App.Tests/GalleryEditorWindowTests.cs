using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Gallery;
using MyCapture.Core.Annotations;
using MyCapture.Core.Primitives;
using MyCapture.Core.Queue;
using MyCapture.Platform.Capture;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>
/// Gallery re-edit must wrap the shared editor without re-parenting it while it is still
/// the window Content — that throws "Specified element is already the logical child of
/// another element" and is what blocked Edit from the library.
/// </summary>
public sealed class GalleryEditorWindowTests
{
    [Fact]
    public void Constructor_WrapsEditorWithoutThrowingWhenARecordIsSupplied()
    {
        RunSta(() =>
        {
            BitmapSource bitmap = Solid(40, 24, 0x88);
            var record = new CaptureRecord
            {
                Width = bitmap.PixelWidth,
                Height = bitmap.PixelHeight,
                CreatedAt = DateTimeOffset.Now,
                MediaKind = CaptureMediaKind.Image,
            };
            var frame = new FrozenFrame(
                bitmap,
                new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight),
                Monitor: null,
                ElapsedMilliseconds: 0);
            var context = new GalleryReeditContext(
                record,
                frame,
                new RectD(0, 0, bitmap.PixelWidth, bitmap.PixelHeight),
                bitmap,
                AnnotationDocument.CreateFor(bitmap.PixelWidth, bitmap.PixelHeight),
                new Dictionary<string, BitmapSource>());

            GalleryEditorWindow? window = null;
            try
            {
                window = new GalleryEditorWindow(context, record: record, retentionHours: () => 168);
                Assert.IsType<DockPanel>(window.Content);
                var root = (DockPanel)window.Content;
                Assert.Equal(2, root.Children.Count);
                Assert.Same(window.Editor, root.Children[1]);
                Assert.Same(window, LogicalTreeHelper.GetParent(root));
            }
            finally
            {
                window?.Close();
            }
        });
    }

    [Fact]
    public void Constructor_LeavesBareEditorWhenNoRecordIsSupplied()
    {
        RunSta(() =>
        {
            BitmapSource bitmap = Solid(16, 12, 0x22);
            var record = new CaptureRecord { Width = 16, Height = 12 };
            var frame = new FrozenFrame(bitmap, new RectD(0, 0, 16, 12), null, 0);
            var context = new GalleryReeditContext(
                record,
                frame,
                new RectD(0, 0, 16, 12),
                bitmap,
                AnnotationDocument.CreateFor(16, 12),
                new Dictionary<string, BitmapSource>());

            GalleryEditorWindow? window = null;
            try
            {
                window = new GalleryEditorWindow(context);
                Assert.Same(window.Editor, window.Content);
            }
            finally
            {
                window?.Close();
            }
        });
    }

    private static BitmapSource Solid(int width, int height, byte value)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = value;
            pixels[offset + 1] = value;
            pixels[offset + 2] = value;
            pixels[offset + 3] = 0xFF;
        }

        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException($"STA body threw: {failure}");
        }
    }
}
