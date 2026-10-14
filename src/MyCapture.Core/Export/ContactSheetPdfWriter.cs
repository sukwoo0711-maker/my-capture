using System.Globalization;
using System.Text;

namespace MyCapture.Core.Export;

/// <summary>An opaque RGB JPEG cell, including any captions, supplied one at a time.</summary>
public sealed record ContactSheetJpeg(byte[] Bytes, int Width, int Height);

/// <summary>PDF 1.4 with JPEG XObjects. Retains only object offsets and one cell, never the document's images.</summary>
public static class ContactSheetPdfWriter
{
    public static void Write(Stream output, ContactSheetOptions options, int itemCount,
        Func<int, ContactSheetJpeg> renderCell, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(renderCell);
        if (!output.CanWrite || (output.CanSeek && (output.Position != 0 || output.Length != 0)))
            throw new ArgumentException("An empty writable stream is required.", nameof(output));
        var layout = new ContactSheetLayout(options);
        int pageCount = layout.PageCount(itemCount);
        int firstImage = 3 + pageCount * 2;
        long[] offsets = new long[firstImage + itemCount];
        long position = 0;
        cancellationToken.ThrowIfCancellationRequested();
        Text("%PDF-1.4\n%MyCapture\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Count {pageCount} /Kids [{string.Join(' ', Enumerable.Range(0, pageCount).Select(p => $"{3 + p * 2} 0 R"))}] >>");
        for (int page = 0; page < pageCount; page++)
        {
            var commands = new StringBuilder();
            var resources = new StringBuilder();
            int end = Math.Min(itemCount, (page + 1) * layout.CellsPerPage);
            for (int item = page * layout.CellsPerPage; item < end; item++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ContactSheetRect cell = layout.Cell(item % layout.CellsPerPage);
                ContactSheetJpeg jpeg = renderCell(item);
                cancellationToken.ThrowIfCancellationRequested();
                if (jpeg.Width <= 0 || jpeg.Height <= 0
                    || jpeg.Width > Math.Ceiling(cell.Width * ContactSheetLayout.ExportDpi / 72)
                    || jpeg.Height > Math.Ceiling(cell.Height * ContactSheetLayout.ExportDpi / 72)
                    || jpeg.Bytes.Length is < 4 or > 32 * 1024 * 1024
                    || jpeg.Bytes[0] != 0xff || jpeg.Bytes[1] != 0xd8 || jpeg.Bytes[^2] != 0xff || jpeg.Bytes[^1] != 0xd9)
                    throw new ArgumentException("The renderer must supply a bounded JPEG cell.", nameof(renderCell));
                int id = firstImage + item;
                Begin(id);
                Text($"<< /Type /XObject /Subtype /Image /Width {jpeg.Width} /Height {jpeg.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Bytes.Length} >>\nstream\n");
                Bytes(jpeg.Bytes);
                Text("\nendstream\nendobj\n");
                resources.Append($"/I{item} {id} 0 R ");
                commands.Append($"q {Number(cell.Width)} 0 0 {Number(cell.Height)} {Number(cell.X)} {Number(layout.PageHeight - cell.Y - cell.Height)} cm /I{item} Do Q\n");
                progress?.Report(item + 1);
            }
            byte[] content = Encoding.ASCII.GetBytes(commands.ToString());
            int pageId = 3 + page * 2;
            Begin(pageId + 1);
            Text($"<< /Length {content.Length} >>\nstream\n");
            Bytes(content);
            Text("endstream\nendobj\n");
            Object(pageId, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Number(layout.PageWidth)} {Number(layout.PageHeight)}] /Resources << /XObject << {resources}>> >> /Contents {pageId + 1} 0 R >>");
        }
        cancellationToken.ThrowIfCancellationRequested();
        long xref = position;
        Text($"xref\n0 {offsets.Length}\n0000000000 65535 f \n");
        for (int id = 1; id < offsets.Length; id++) Text($"{offsets[id].ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xref.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");

        void Bytes(byte[] bytes) { output.Write(bytes); position += bytes.Length; }
        void Text(string value) => Bytes(Encoding.ASCII.GetBytes(value));
        void Begin(int id) { offsets[id] = position; Text($"{id} 0 obj\n"); }
        void Object(int id, string value) { Begin(id); Text(value + "\nendobj\n"); }
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
