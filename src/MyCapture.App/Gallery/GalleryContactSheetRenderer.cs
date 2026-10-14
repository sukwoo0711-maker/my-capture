using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyCapture.Core.Export;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

internal sealed record GalleryContactSheetItem(string ImagePath, string Title, DateTimeOffset CreatedAt,
    string? SourceApp, string SourceWindow);

internal static class GalleryContactSheetRenderer
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);

    // A single STA job owns its WPF resources; rapid preview changes cannot accumulate decoders.
    internal static async Task<T> RunAsync<T>(Func<T> render, CancellationToken token)
    {
        await RenderGate.WaitAsync(token);
        try
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    T result;
                    try { token.ThrowIfCancellationRequested(); result = render(); }
                    finally { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(token); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }) { IsBackground = true, Name = "Contact sheet renderer" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task;
        }
        finally { RenderGate.Release(); }
    }

    internal static BitmapSource Preview(IReadOnlyList<GalleryContactSheetItem> items, ContactSheetOptions options,
        CultureInfo culture, CancellationToken token)
    {
        var layout = new ContactSheetLayout(options);
        _ = layout.PageCount(items.Count);
        var page = new DrawingVisual();
        using (DrawingContext drawing = page.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, layout.PageWidth, layout.PageHeight));
            for (int index = 0; index < Math.Min(items.Count, layout.CellsPerPage); index++)
            {
                token.ThrowIfCancellationRequested();
                ContactSheetRect cell = layout.Cell(index);
                BitmapSource bitmap = RenderCell(items[index], options, cell, 96, culture, token);
                drawing.DrawImage(bitmap, Rect(cell));
            }
        }
        token.ThrowIfCancellationRequested();
        return Rasterize(page, layout.PageWidth, layout.PageHeight, 96);
    }

    internal static ContactSheetJpeg EncodeCell(GalleryContactSheetItem item, ContactSheetOptions options,
        ContactSheetRect cell, CultureInfo culture, CancellationToken token)
    {
        BitmapSource bitmap = RenderCell(item, options, cell, ContactSheetLayout.ExportDpi, culture, token);
        token.ThrowIfCancellationRequested();
        var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Rgb24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(rgb));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new(stream.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
    }

    private static BitmapSource RenderCell(GalleryContactSheetItem item, ContactSheetOptions options,
        ContactSheetRect cell, int dpi, CultureInfo culture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int longEdge = (int)Math.Ceiling(Math.Max(cell.Width, cell.Height) * dpi / 72);
        BitmapSource image = ImageCodec.TryLoadScaled(item.ImagePath, longEdge)
            ?? throw new IOException("A contact-sheet image could not be read.");
        token.ThrowIfCancellationRequested();
        IReadOnlyList<string> lines = ContactSheetCaptions.Lines(options, item.Title, item.CreatedAt,
            item.SourceApp, item.SourceWindow, culture);
        return ComposeCell(image, cell, lines, dpi, culture);
    }

    internal static BitmapSource ComposeCell(BitmapSource image, ContactSheetRect cell,
        IReadOnlyList<string> lines, int dpi, CultureInfo culture)
    {
        const double padding = 4, lineHeight = 13;
        double captionHeight = lines.Count == 0 ? 0 : lines.Count * lineHeight + padding;
        var bounds = new ContactSheetRect(padding, padding, cell.Width - 2 * padding,
            cell.Height - 2 * padding - captionHeight);
        ContactSheetRect fit = ContactSheetLayout.Fit(image.PixelWidth, image.PixelHeight, bounds);
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, cell.Width, cell.Height));
            drawing.DrawImage(image, Rect(fit));
            for (int index = 0; index < lines.Count; index++)
            {
                var text = new FormattedText(lines[index], culture, FlowDirection.LeftToRight,
                    new Typeface("Malgun Gothic"), 9, Brushes.Black, dpi / 72.0)
                {
                    MaxTextWidth = bounds.Width, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis,
                };
                drawing.DrawText(text, new Point(padding, cell.Height - padding - lines.Count * lineHeight + index * lineHeight));
            }
        }
        return Rasterize(visual, cell.Width, cell.Height, dpi);
    }

    private static BitmapSource Rasterize(Visual visual, double width, double height, int dpi)
    {
        // Drawing coordinates are PDF points (72/in), whereas WPF uses 96 DIP/in.
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi / 72),
            (int)Math.Ceiling(height * dpi / 72), dpi * 96.0 / 72, dpi * 96.0 / 72, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Rect Rect(ContactSheetRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}
