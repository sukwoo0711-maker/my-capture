using System.IO;
using System.Windows.Media.Imaging;
using MyCapture.App.Editing;
using MyCapture.Core.Settings;
using MyCapture.Platform.Imaging;

namespace MyCapture.App.Gallery;

internal static class GalleryImageExport
{
    internal static void CopyTo(string source, Stream output, ImageExportOptions options, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase))
        {
            GalleryExportFiles.CopyToStream(source, output, cancellationToken);
            return;
        }
        using var input = new MemoryStream();
        GalleryExportFiles.CopyToStream(source, input, cancellationToken);
        byte[] png = PreparePng(input.ToArray(), options, cancellationToken);
        output.Write(png);
    }

    internal static byte[] PreparePng(byte[] png, ImageExportOptions options, CancellationToken cancellationToken = default)
    {
        options = options.Normalize();
        cancellationToken.ThrowIfCancellationRequested();
        if (options.LongEdge > 0)
        {
            using var input = new MemoryStream(png, writable: false);
            BitmapSource source = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            source.Freeze();
            if (Math.Max(source.PixelWidth, source.PixelHeight) > options.LongEdge)
            {
                string? url = options.IncludeSourceUrl ? PngSourceUrlMetadata.TryReadSourceUrl(png) : null;
                BitmapSource resized = ImageExportRenderer.Render(source, new() { LongEdge = options.LongEdge }, default);
                png = ImageCodec.EncodePng(resized, url);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return options.IncludeSourceUrl ? png : PngSourceUrlMetadata.StripSourceUrl(png);
    }

    internal static void Copy(string source, string target, ImageExportOptions options, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase))
            GalleryExportFiles.Copy(source, target, cancellationToken);
        else GalleryExportFiles.WriteStreamNew(target, output => CopyTo(source, output, options, cancellationToken), cancellationToken);
    }
}
