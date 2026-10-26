using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WpfBitmapFrame = System.Windows.Media.Imaging.BitmapFrame;

namespace MyCapture.Ocr;

/// <summary>Request-owned pixels for Windows OCR, without PNG compression for opaque captures.</summary>
internal static class WindowsOcrBitmapBridge
{
    // IMemoryBufferByteAccess: https://devblogs.microsoft.com/oldnewthing/20240122-00/?p=109304
    private static readonly Guid MemoryBufferByteAccess = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    internal static Task<SoftwareBitmap> CreateAsync(BitmapSource bitmap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        // The service supplies frozen sources. Keep both pixel copies and the uncommon codec
        // fallback off its caller's dispatcher; do not freeze or mutate an owner-thread image.
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryCopyOpaquePixels(bitmap, cancellationToken)
                ?? await CreateLegacyAsync(bitmap, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <summary>
    /// Copies directly into the destination's native storage. Returning null selects the PNG
    /// path for transparency, palettes, grayscale and embedded color contexts, preserving its
    /// format conversion and premultiplication rounding instead of reimplementing those codecs.
    /// </summary>
    internal static unsafe SoftwareBitmap? TryCopyOpaquePixels(BitmapSource bitmap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        cancellationToken.ThrowIfCancellationRequested();
        PixelFormat format = bitmap.Format;
        if (format != PixelFormats.Bgr32 && format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32)
            return null;
        if (bitmap is WpfBitmapFrame frame && frame.ColorContexts is { Count: > 0 })
            return null;

        int width = bitmap.PixelWidth;
        int height = bitmap.PixelHeight;
        int rowBytes = checked(width * 4);
        var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        try
        {
            bool opaque = true;
            using (BitmapBuffer buffer = software.LockBuffer(BitmapBufferAccessMode.Write))
            using (IMemoryBufferReference reference = buffer.CreateReference())
            {
                Guid iid = MemoryBufferByteAccess;
                // Borrow the native projection while the reference's using scope keeps it alive.
                nint native = ((WinRT.IWinRTObject)reference).NativeObject.ThisPtr;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out nint access));
                try
                {
                    byte* pixels = null;
                    uint capacity = 0;
                    var getBuffer = (delegate* unmanaged[Stdcall]<nint, byte**, uint*, int>)(*(nint**)access)[3];
                    Marshal.ThrowExceptionForHR(getBuffer(access, &pixels, &capacity));
                    BitmapPlaneDescription plane = buffer.GetPlaneDescription(0);
                    long required = checked((long)plane.StartIndex + (long)(height - 1) * plane.Stride + rowBytes);
                    if (pixels == null || plane.StartIndex < 0 || plane.Stride < rowBytes || required > capacity
                        || capacity - plane.StartIndex > int.MaxValue)
                        throw new InvalidOperationException("The OCR bitmap has an invalid native pixel layout.");

                    cancellationToken.ThrowIfCancellationRequested();
                    byte* first = pixels + plane.StartIndex;
                    bitmap.CopyPixels(Int32Rect.Empty, (nint)first, checked((int)(capacity - plane.StartIndex)), plane.Stride);
                    for (int y = 0; y < height && opaque; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        byte* row = first + y * plane.Stride;
                        for (int x = 0; x < width; x++)
                        {
                            if (format == PixelFormats.Bgr32)
                                row[x * 4 + 3] = 255; // Bgr32's fourth byte is unused, not alpha.
                            else if (row[x * 4 + 3] != 255)
                            {
                                opaque = false;
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.Release(access);
                }
            }

            if (!opaque)
            {
                software.Dispose();
                return null;
            }
            software.DpiX = bitmap.DpiX;
            software.DpiY = bitmap.DpiY;
            cancellationToken.ThrowIfCancellationRequested();
            return software;
        }
        catch
        {
            software.Dispose();
            throw;
        }
    }

    // Also the A/B benchmark's unchanged codec baseline. Ownership transfers only on success.
    internal static async Task<SoftwareBitmap> CreateLegacyAsync(BitmapSource bitmap, CancellationToken cancellationToken)
    {
        byte[] png = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
            encoder.Frames.Add(WpfBitmapFrame.Create(bitmap));
            using var memory = new MemoryStream();
            encoder.Save(memory);
            return memory.ToArray();
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream)
            .AsTask(cancellationToken).ConfigureAwait(false);
        SoftwareBitmap software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return software;
        }
        catch
        {
            software.Dispose();
            throw;
        }
    }
}
