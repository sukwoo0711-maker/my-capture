using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Capture;
using MyCapture.Core.Primitives;
using Xunit;

namespace MyCapture.App.Tests;

/// <summary>Offscreen snapshot of actual native DIB pixels and the retained bounded chrome.</summary>
internal static class NativeSelectorTestRenderer
{
    internal static RenderTargetBitmap Render(NativeCaptureOverlay owner, NativeSelectorChrome chrome)
    {
        NativeSelectorDib dib = Assert.IsType<NativeSelectorDib>(owner.DisplayDib);
        Assert.NotNull(owner.Frame);
        using var painted = new NativeSelectorDib(owner.Frame!.Bitmap, new SolidColorBrush(Colors.Transparent));
        owner.PaintTo(painted.Dc);
        Assert.True(NativeSelectorInterop.GdiFlush());
        var pixels = new byte[dib.ByteLength];
        Marshal.Copy(painted.Bits, pixels, 0, pixels.Length);
        BitmapSource desktop = BitmapSource.Create(dib.Width, dib.Height, 96, 96,
            PixelFormats.Bgr32, null, pixels, dib.Stride);
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawImage(desktop, new Rect(0, 0, dib.Width, dib.Height));
            foreach (Window window in chrome.Windows)
            {
                if (!chrome.IsReadoutVisibleForTest && chrome.Windows.Count >= 3
                    && ReferenceEquals(window, chrome.Windows[1])) continue;
                FrameworkElement content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                RectD bounds = chrome.BoundsForTest(window);
                Size logicalSize = chrome.LogicalSizeForTest(window);
                double contentScale = chrome.ScaleForTest(window);
                double left = bounds.Left - owner.ScreenBounds.Left, top = bounds.Top - owner.ScreenBounds.Top;
                double width = logicalSize.Width, height = logicalSize.Height;
                DpiScale previousDpi = VisualTreeHelper.GetDpi(content);
                Transform previousTransform = content.LayoutTransform;
                window.Content = null;
                try
                {
                    // The detached root uses target monitor DPI directly, without the live HWND's
                    // compensation transform. Restore both properties before reattaching it.
                    content.LayoutTransform = Transform.Identity;
                    VisualTreeHelper.SetRootDpi(content, new DpiScale(contentScale, contentScale));
                    content.Measure(new Size(width, height));
                    content.Arrange(new Rect(0, 0, width, height));
                    content.UpdateLayout();
                    int pixelWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width));
                    int pixelHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height));
                    var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                        96 * contentScale, 96 * contentScale, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    if (ReferenceEquals(window, chrome.Windows.Last()) && chrome.MagnifierForTest is { } magnifier)
                    {
                        // The Adorner lives outside Border.Child, which remains the retained Image.
                        // Its actual drawing includes the grid, footer and any image corner masks.
                        var overlay = new DrawingVisual();
                        using (DrawingContext overlayContext = overlay.RenderOpen())
                        {
                            Point imageOffset = magnifier.ImageOffsetForTest;
                            overlayContext.PushTransform(new TranslateTransform(imageOffset.X, imageOffset.Y));
                            overlayContext.DrawDrawing(magnifier.DrawingForTest);
                            overlayContext.Pop();
                        }
                        bitmap.Render(overlay);
                    }
                    dc.DrawRectangle(window.Background, null, new Rect(left, top, pixelWidth, pixelHeight));
                    dc.DrawImage(bitmap, new Rect(left, top, pixelWidth, pixelHeight));
                }
                finally
                {
                    VisualTreeHelper.SetRootDpi(content, previousDpi);
                    content.LayoutTransform = previousTransform;
                    window.Content = content;
                }
            }
        }
        var rendered = new RenderTargetBitmap(dib.Width, dib.Height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        return rendered;
    }
}
