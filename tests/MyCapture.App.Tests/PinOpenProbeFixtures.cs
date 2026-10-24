using System.Diagnostics;
using System.Security;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyCapture.App.Pinning;
using MyCapture.Core.Pin;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Tests;

internal sealed record PinRendererSample(string Kind, int Repeat, double ParseMs, double RasterMs,
    double TotalMs, int PixelWidth, int PixelHeight, double DpiX, int ThreadId);

internal static class PinOpenFixtures
{
    private static readonly string Code = string.Join('\n', Enumerable.Range(1, 30)
        .Select(index => $"var sample{index:00} = ReadPixel({index * 17}, {index * 9}); // synthetic line {index:00}"));
    private static readonly string CodeHtml = "<pre style=\"background-color:#1E1E1E;color:#DCDCDC\"><code>"
        + SecurityElement.Escape(Code) + "</code></pre>";
    private static readonly string Table = string.Join('\n', Enumerable.Range(1, 24)
        .Select(row => string.Join('\t', Enumerable.Range(1, 12).Select(column => $"R{row:00}C{column:00}"))));

    internal static Task<(PinContent Content, PinRendererSample Sample)> RenderAsync(string kind, int repeat)
    {
        var done = new TaskCompletionSource<(PinContent, PinRendererSample)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = WpfTestThread.Create(() =>
        {
            try
            {
                // Fixture generation is outside the timed PNG decode, as clipboard bytes already exist.
                byte[]? png = kind == "image" ? MakePng() : null;
                long start = Stopwatch.GetTimestamp();
                long parsed = start;
                long rastered;
                PinContent content;
                BitmapSource bitmap;
                if (kind == "image")
                {
                    bitmap = ImageCodec.TryDecodePng(png!) ?? throw new InvalidOperationException("Synthetic PNG did not decode.");
                    rastered = Stopwatch.GetTimestamp();
                    content = PinContent.FromImage(bitmap);
                }
                else if (kind == "code")
                {
                    PinCodeDocument document = ClipboardCodeRenderer.TryParse(Code, CodeHtml)
                        ?? throw new InvalidOperationException("Synthetic 30-line code did not parse.");
                    parsed = Stopwatch.GetTimestamp();
                    bitmap = ClipboardCodeRenderer.Render(document);
                    rastered = Stopwatch.GetTimestamp();
                    content = PinContent.FromCode(bitmap, Code, document);
                }
                else if (kind == "table")
                {
                    bitmap = ClipboardTextRenderer.Render(Table, true);
                    rastered = Stopwatch.GetTimestamp();
                    content = PinContent.FromText(bitmap, Table, true);
                }
                else throw new ArgumentException("Unknown pin fixture: " + kind);
                long end = Stopwatch.GetTimestamp();
                if (!bitmap.IsFrozen) throw new InvalidOperationException("Product renderer must return a frozen raster.");
                done.TrySetResult((content, new PinRendererSample(kind, repeat, PinOpenProbe.Ms(start, parsed),
                    PinOpenProbe.Ms(parsed, rastered), PinOpenProbe.Ms(start, end), bitmap.PixelWidth, bitmap.PixelHeight,
                    bitmap.DpiX, Environment.CurrentManagedThreadId)));
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        thread.Name = "Pin probe synthetic clipboard renderer";
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return FinishAsync();

        async Task<(PinContent, PinRendererSample)> FinishAsync()
        {
            try { return await done.Task.ConfigureAwait(false); }
            finally
            {
                // Join even a failed renderer, so its STA teardown cannot overlap the next operation.
                await Task.Run(thread.Join).ConfigureAwait(false);
            }
        }
    }

    private static byte[] MakePng()
    {
        const int width = 800, height = 500;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null,
            SyntheticImage.PhotoLikePixels(width, height, 17), width * 4);
        bitmap.Freeze();
        return ImageCodec.EncodePng(bitmap, null);
    }
}
